using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 「添加电量设备」——简化界面（默认入口）。
    ///
    /// 用户只需要三步：选设备 → 填官方软件显示的电量 → 点「自动识别」。
    /// 接口、ReportID、第几个字节全部由程序自己试出来：
    ///   · 同名设备的多个接口合并成一条（不要求用户理解"接口"）；
    ///   · 命中 1 个候选 → 自动选中；多个 → 列出来让用户挑；
    ///   · 复杂的逐步指定，挪到左下角「高级自定义…」（即原来的 4 步向导）。
    ///
    /// 协议写入 TrayToys.json 的 hidProtocols，格式与高级向导完全一致。
    /// </summary>
    internal class DeviceSetupSimpleForm : Form
    {
        private ComboBox cmbDevices;
        private TextBox txtLevel;
        private TextBox txtName;
        private Button btnIdentify;
        private Button btnSave;
        private Button btnCancel;
        private Button btnAdvanced;
        private ProgressBar progress;
        private Label lblStatus;
        private Label lblTip;
        private ListView lv;

        private List<DeviceBatteryManager.HidDeviceGroup> devices = new List<DeviceBatteryManager.HidDeviceGroup>();
        private bool running;
        private bool cancelRequested;

        public DeviceSetupSimpleForm()
        {
            this.Text = "添加电量设备";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.AutoScaleMode = AutoScaleMode.None; // 必须先关自动缩放，再做像素布局
            this.Font = new Font("Microsoft YaHei UI", 9f);
            this.ClientSize = new Size(554, 490);

            BuildUi();
            LoadDevices();

            // 关窗时让后台识别尽快停下（它每检查一个接口都会看这个标志）
            this.FormClosing += delegate { cancelRequested = true; };
        }

        private void BuildUi()
        {
            // ===== 设备（同名合并）=====
            Label l1 = new Label();
            l1.Text = "设备";
            l1.Location = new Point(14, 18);
            l1.Size = new Size(44, 18);

            cmbDevices = new ComboBox();
            cmbDevices.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbDevices.Location = new Point(64, 14);
            cmbDevices.Size = new Size(476, 26);
            cmbDevices.SelectedIndexChanged += delegate { OnDeviceChanged(); };

            // ===== 官方电量 + 自动识别 =====
            Label l2 = new Label();
            l2.Text = "官方软件显示电量";
            l2.Location = new Point(14, 56);
            l2.Size = new Size(130, 18);

            txtLevel = new TextBox();
            txtLevel.Text = ""; // 不给默认值：必须由用户按官方软件的实际读数填写
            txtLevel.Location = new Point(150, 52);
            txtLevel.Size = new Size(56, 26);
            txtLevel.TextAlign = HorizontalAlignment.Center;

            Label l3 = new Label();
            l3.Text = "%";
            l3.Location = new Point(212, 56);
            l3.Size = new Size(20, 18);

            btnIdentify = new Button();
            btnIdentify.Text = "自动识别";
            btnIdentify.Location = new Point(424, 50);
            btnIdentify.Size = new Size(116, 30);
            btnIdentify.Click += BtnIdentify_Click;

            // ===== 进度 + 状态 =====
            progress = new ProgressBar();
            progress.Location = new Point(14, 90);
            progress.Size = new Size(526, 12);
            progress.Minimum = 0;
            progress.Maximum = 1;

            lblStatus = new Label();
            lblStatus.Location = new Point(14, 108);
            lblStatus.Size = new Size(526, 18);
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.AutoEllipsis = true;

            // ===== 候选列表 =====
            lv = new ListView();
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.MultiSelect = false;
            lv.HideSelection = false;
            lv.GridLines = false;
            lv.Location = new Point(14, 130);
            lv.Size = new Size(526, 222);
            lv.Columns.Add("接口", 170);
            lv.Columns.Add("方式", 90);
            lv.Columns.Add("ReportID", 90);
            lv.Columns.Add("字节位置", 90);
            lv.Columns.Add("当前值", 76);

            lblTip = new Label();
            lblTip.Location = new Point(14, 358);
            lblTip.Size = new Size(526, 44);
            lblTip.ForeColor = Color.DimGray;

            // ===== 设备名称 =====
            Label l4 = new Label();
            l4.Text = "设备名称";
            l4.Location = new Point(14, 412);
            l4.Size = new Size(66, 18);

            txtName = new TextBox();
            txtName.Location = new Point(84, 408);
            txtName.Size = new Size(240, 26);

            // ===== 底部按钮 =====
            // 「保存 / 取消」的尺寸与主设置窗口保持一致（75×32，间距 8，右侧留 14），
            // 让两层界面看起来是同一套样式；「高级自定义…」文字更长，宽度单独给 110，高度对齐。
            const int SmallW = 75;
            const int BtnH = 32;
            const int BtnY = 444;

            btnAdvanced = new Button();
            btnAdvanced.Text = "高级自定义…";
            btnAdvanced.Location = new Point(14, BtnY);
            btnAdvanced.Size = new Size(110, BtnH);
            btnAdvanced.Click += delegate
            {
                using (BatterySetupForm f = new BatterySetupForm())
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        this.DialogResult = DialogResult.OK;
                        this.Close();
                    }
                }
            };

            btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Location = new Point(382, BtnY);
            btnSave.Size = new Size(SmallW, BtnH);
            btnSave.Click += BtnSave_Click;

            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(465, BtnY);
            btnCancel.Size = new Size(SmallW, BtnH);
            btnCancel.Click += delegate { cancelRequested = true; this.Close(); };
            this.CancelButton = btnCancel;

            this.Controls.Add(l1);
            this.Controls.Add(cmbDevices);
            this.Controls.Add(l2);
            this.Controls.Add(txtLevel);
            this.Controls.Add(l3);
            this.Controls.Add(btnIdentify);
            this.Controls.Add(progress);
            this.Controls.Add(lblStatus);
            this.Controls.Add(lv);
            this.Controls.Add(lblTip);
            this.Controls.Add(l4);
            this.Controls.Add(txtName);
            this.Controls.Add(btnAdvanced);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnCancel);

            // 电量没填之前不让点「自动识别」——避免用户空着点下去再被打断
            btnIdentify.Enabled = false;
            txtLevel.TextChanged += delegate
            {
                btnIdentify.Enabled = txtLevel.Text.Trim().Length > 0 && cmbDevices.SelectedIndex >= 0;
            };
        }

        // ==================== 数据加载 ====================

        private void LoadDevices()
        {
            devices = DeviceBatteryManager.EnumerateDevices();
            cmbDevices.Items.Clear();
            for (int i = 0; i < devices.Count; i++)
                cmbDevices.Items.Add(devices[i].DisplayName);

            if (devices.Count > 0)
            {
                cmbDevices.SelectedIndex = 0;
                OnDeviceChanged();
            }
            else
            {
                lblStatus.Text = "未找到可配置的 HID 设备（请确认设备已连接）。";
                btnIdentify.Enabled = false;
                btnSave.Enabled = false;
            }
        }

        private DeviceBatteryManager.HidDeviceGroup CurrentDevice
        {
            get
            {
                int i = cmbDevices.SelectedIndex;
                if (i < 0 || i >= devices.Count) return null;
                return devices[i];
            }
        }

        private void OnDeviceChanged()
        {
            DeviceBatteryManager.HidDeviceGroup dev = CurrentDevice;
            if (dev == null) return;

            txtName.Text = dev.Product; // 默认用产品名，允许改

            BatteryProtocol exist = FindExisting(dev);
            if (exist != null)
            {
                lblStatus.ForeColor = Color.DarkOrange;
                lblStatus.Text = "该设备已配置（" + exist.Name + "），重新识别会覆盖原规则。";
            }
            else
            {
                lblStatus.ForeColor = Color.DimGray;
                lblStatus.Text = "";
            }
            lv.Items.Clear();
            lblTip.Text = "";

            // 切换设备后按当前输入状态刷新「自动识别」可用性
            btnIdentify.Enabled = txtLevel.Text.Trim().Length > 0;
        }

        private static BatteryProtocol FindExisting(DeviceBatteryManager.HidDeviceGroup dev)
        {
            List<BatteryProtocol> list = BatteryProtocolStore.Protocols;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Vid == (int)dev.Vid && list[i].Pid == (int)dev.Pid) return list[i];
            return null;
        }

        // ==================== 自动识别 ====================

        private void BtnIdentify_Click(object sender, EventArgs e)
        {
            if (running) return;

            DeviceBatteryManager.HidDeviceGroup dev = CurrentDevice;
            if (dev == null) { MessageBox.Show("请先选择设备。", "提示"); return; }

            int level;
            if (!int.TryParse(txtLevel.Text.Trim(), out level) || level < 0 || level > 100)
            {
                MessageBox.Show("请填写官方软件显示的电量（0~100 的整数）。", "提示");
                txtLevel.Focus();
                return;
            }

            running = true;
            cancelRequested = false;
            btnIdentify.Enabled = false;
            btnIdentify.Text = "识别中…";
            btnSave.Enabled = false;
            progress.Maximum = 1;
            progress.Value = 0;
            lv.Items.Clear();
            lblTip.Text = "";
            lblTip.ForeColor = Color.DimGray;
            lblStatus.ForeColor = Color.DimGray;
            lblStatus.Text = "准备中…";

            Thread t = new Thread(delegate()
            {
                DeviceBatteryManager.IdentifyResult res = null;
                try
                {
                    res = DeviceBatteryManager.IdentifyBattery(dev, level,
                        delegate(int done, int total, string stage)
                        {
                            if (IsDisposed) return;
                            try
                            {
                                BeginInvoke(new MethodInvoker(delegate()
                                {
                                    progress.Maximum = Math.Max(total, 1);
                                    progress.Value = Math.Min(done, progress.Maximum);
                                    lblStatus.Text = stage;
                                }));
                            }
                            catch { }
                        },
                        delegate { return cancelRequested; });
                }
                catch (Exception ex)
                {
                    Logger.Error("自动识别电量失败", ex);
                }

                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate() { FinishIdentify(res, level); }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Name = "IdentifyBattery";
            t.Start();
        }

        private void FinishIdentify(DeviceBatteryManager.IdentifyResult res, int level)
        {
            running = false;
            btnIdentify.Enabled = true;
            btnIdentify.Text = "自动识别";
            btnSave.Enabled = true;
            progress.Value = progress.Maximum;

            if (res == null)
            {
                lblStatus.ForeColor = Color.Firebrick;
                lblStatus.Text = "识别失败（内部错误，详见日志）。";
                return;
            }

            lv.BeginUpdate();
            lv.Items.Clear();
            for (int i = 0; i < res.Candidates.Count; i++)
            {
                DeviceBatteryManager.BatteryCandidate c = res.Candidates[i];
                ListViewItem it = new ListViewItem("0x" + c.UsagePage.ToString("X2"));
                it.SubItems.Add(c.UseFeature ? "Feature" : "Input");
                it.SubItems.Add(c.UseFeature ? ("0x" + c.ReportId.ToString("X2")) : "—");
                it.SubItems.Add("第 " + (c.ByteIndex + 1) + " 字节");
                it.SubItems.Add(c.Value.ToString());
                it.Tag = c;
                lv.Items.Add(it);
            }
            lv.EndUpdate();

            // 未响应的接口只在"一个候选都没找到"时才提 —— 有结果时它只是噪音
            // （设备静止时很多 Input 接口本来就一个字都不发，属正常）
            string timeoutText = "";
            string timeoutHint = "";
            if (res.TimedOut.Count > 0)
            {
                timeoutHint = "　其中 " + res.TimedOut.Count + " 个接口没有数据（设备静止时属正常）";
            }

            if (res.Candidates.Count == 0)
            {
                if (res.TimedOut.Count > 0)
                    timeoutText = "　（" + res.TimedOut.Count + " 个接口无数据："
                        + string.Join("、", res.TimedOut.ToArray()) + "）";

                lblStatus.ForeColor = Color.Firebrick;
                lblStatus.Text = "没有找到等于 " + level + "% 的字节。" + timeoutText;
                lblTip.Text = "建议依次确认：① 官方软件此刻显示的确实是 " + level + "%；"
                    + "② 动一下设备（按个键 / 移动鼠标）让它上报一次后再点识别；"
                    + "③ 若始终找不到，该设备可能不主动上报电量，可用左下角「高级自定义…」逐项排查。";
                lblTip.ForeColor = Color.DimGray;
                return;
            }

            lv.Items[0].Selected = true;
            lv.Select();

            if (res.Candidates.Count == 1)
            {
                lblStatus.ForeColor = Color.DarkGreen;
                lblStatus.Text = "找到唯一候选，已自动选中，直接保存即可。" + timeoutHint;
                lblTip.Text = "保存后，「设备电量」页会立即显示该设备的电量。";
                lblTip.ForeColor = Color.DimGray;
            }
            else
            {
                lblStatus.ForeColor = Color.DarkOrange;
                lblStatus.Text = "找到 " + res.Candidates.Count + " 个候选项，请选一个：" + timeoutHint;
                lblTip.Text = "满电时多个字节都等于 " + level + " 属正常（值相同无法区分）。"
                    + "若判断不出来：先改动一项无关设置（如键盘背光亮度），再点一次「自动识别」——"
                    + "跟着变的那一项就不是电量。";
                lblTip.ForeColor = Color.DimGray;
            }
        }

        // ==================== 保存 ====================

        private void BtnSave_Click(object sender, EventArgs e)
        {
            DeviceBatteryManager.HidDeviceGroup dev = CurrentDevice;
            if (dev == null) { MessageBox.Show("请先选择设备。", "提示"); return; }

            if (lv.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先点「自动识别」，并在候选里选一项。", "提示");
                return;
            }
            DeviceBatteryManager.BatteryCandidate c =
                lv.SelectedItems[0].Tag as DeviceBatteryManager.BatteryCandidate;
            if (c == null) { MessageBox.Show("请重新识别后再保存。", "提示"); return; }

            string name = txtName.Text.Trim();
            if (name.Length == 0)
            {
                MessageBox.Show("请填写设备名称。", "提示");
                txtName.Focus();
                return;
            }

            BatteryProtocol p = new BatteryProtocol();
            p.Vid = dev.Vid;
            p.Pid = dev.Pid;
            p.UsagePage = c.UsagePage;
            p.UseFeature = c.UseFeature;
            p.FeatureReportId = c.UseFeature ? c.ReportId : 0;
            p.BatteryByte = c.ByteIndex;
            p.Name = name;

            // 同一设备只保留一条规则：先清掉该 VID:PID 的旧条目
            // （之前可能在别的接口上配过，留着会变成两条互相干扰）
            RemoveAllForDevice(dev.Vid, dev.Pid);
            BatteryProtocolStore.AddOrUpdate(p);

            Logger.Info("已保存电量识别规则: " + p.Describe());

            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private static void RemoveAllForDevice(ushort vid, ushort pid)
        {
            List<BatteryProtocol> list = BatteryProtocolStore.Protocols;
            bool removed = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Vid == (int)vid && list[i].Pid == (int)pid)
                {
                    list.RemoveAt(i);
                    removed = true;
                }
            }
            if (removed) BatteryProtocolStore.Save();
        }
    }
}
