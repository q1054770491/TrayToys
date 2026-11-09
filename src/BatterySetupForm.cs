using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 「添加自定义电量设备」对话框：4 步向导 —— 选接口 → 抓报文 → 选字节 → 测试保存。
    /// 结果写入 BatteryProtocolStore（battery_protocols.json），无需改代码。
    /// </summary>
    internal class BatterySetupForm : Form
    {
        // —— 控件 ——
        private ComboBox cmbInterfaces;
        private RadioButton rbInput;
        private RadioButton rbFeature;
        private ComboBox cmbReportId;
        private Button btnProbe;
        private Button btnRead;
        private FlowLayoutPanel pnlBytes;
        private CheckBox chkDecimal;
        private TextBox txtKnownLevel;
        private Button btnAutoMatch;
        private Label lblSelected;
        private TextBox txtName;
        private Button btnTest;
        private Label lblTestResult;
        private Button btnSave;
        private Button btnCancel;

        // —— 状态 ——
        private List<DeviceBatteryManager.HidInterfaceInfo> interfaces = new List<DeviceBatteryManager.HidInterfaceInfo>();
        private byte[] currentReport;
        private int selectedByte = -1;

        public BatterySetupForm()
        {
            this.Text = "添加自定义电量设备";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterParent;
            this.AutoScaleMode = AutoScaleMode.None; // 必须先关自动缩放，再做像素布局
            this.Font = new Font("Microsoft YaHei UI", 9f);
            this.ClientSize = new Size(520, 560);

            BuildUi();
            LoadInterfaces();
        }

        private void BuildUi()
        {
            // ===== ① 选择接口 =====
            GroupBox gb1 = new GroupBox();
            gb1.Text = "① 选择设备接口";
            gb1.Location = new Point(12, 8);
            gb1.Size = new Size(496, 62);

            cmbInterfaces = new ComboBox();
            cmbInterfaces.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbInterfaces.Location = new Point(12, 22);
            cmbInterfaces.Size = new Size(470, 24);
            cmbInterfaces.SelectedIndexChanged += delegate { OnInterfaceChanged(); };
            gb1.Controls.Add(cmbInterfaces);

            // ===== ② 抓取报文 =====
            GroupBox gb2 = new GroupBox();
            gb2.Text = "② 抓取报文";
            gb2.Location = new Point(12, 78);
            gb2.Size = new Size(496, 170);

            rbInput = new RadioButton();
            rbInput.Text = "Input 报告（设备主动上报）";
            rbInput.Location = new Point(12, 22);
            rbInput.AutoSize = true;
            rbInput.CheckedChanged += delegate { OnModeChanged(); };

            rbFeature = new RadioButton();
            rbFeature.Text = "Feature 报告（主动查询）";
            rbFeature.Location = new Point(224, 22);
            rbFeature.AutoSize = true;
            rbFeature.CheckedChanged += delegate { OnModeChanged(); };

            Label lblRep = new Label();
            lblRep.Text = "ReportID：";
            lblRep.Location = new Point(12, 54);
            lblRep.AutoSize = true;

            cmbReportId = new ComboBox();
            cmbReportId.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbReportId.Location = new Point(88, 50);
            cmbReportId.Size = new Size(94, 24);

            btnProbe = new Button();
            btnProbe.Text = "探测";
            btnProbe.Location = new Point(190, 49);
            btnProbe.Size = new Size(64, 26);
            btnProbe.Click += BtnProbe_Click;

            btnRead = new Button();
            btnRead.Text = "读取报文";
            btnRead.Location = new Point(390, 49);
            btnRead.Size = new Size(92, 26);
            btnRead.Click += BtnRead_Click;

            pnlBytes = new FlowLayoutPanel();
            pnlBytes.Location = new Point(12, 84);
            pnlBytes.Size = new Size(470, 58);
            pnlBytes.AutoScroll = true;
            pnlBytes.WrapContents = false;
            pnlBytes.FlowDirection = FlowDirection.LeftToRight;
            pnlBytes.BorderStyle = BorderStyle.FixedSingle;

            chkDecimal = new CheckBox();
            chkDecimal.Text = "显示十进制";
            chkDecimal.Location = new Point(12, 146);
            chkDecimal.AutoSize = true;
            chkDecimal.Checked = true;
            chkDecimal.CheckedChanged += delegate { RebuildByteButtons(); };

            gb2.Controls.Add(rbInput);
            gb2.Controls.Add(rbFeature);
            gb2.Controls.Add(lblRep);
            gb2.Controls.Add(cmbReportId);
            gb2.Controls.Add(btnProbe);
            gb2.Controls.Add(btnRead);
            gb2.Controls.Add(pnlBytes);
            gb2.Controls.Add(chkDecimal);

            // ===== ③ 选电量字节 =====
            GroupBox gb3 = new GroupBox();
            gb3.Text = "③ 哪个字节是电量？";
            gb3.Location = new Point(12, 256);
            gb3.Size = new Size(496, 104);

            Label lblKnown = new Label();
            lblKnown.Text = "官方软件显示：";
            lblKnown.Location = new Point(12, 26);
            lblKnown.AutoSize = true;

            txtKnownLevel = new TextBox();
            txtKnownLevel.Location = new Point(108, 22);
            txtKnownLevel.Size = new Size(56, 24);

            Label lblPct = new Label();
            lblPct.Text = "%";
            lblPct.Location = new Point(168, 26);
            lblPct.AutoSize = true;

            btnAutoMatch = new Button();
            btnAutoMatch.Text = "自动搜索";
            btnAutoMatch.Location = new Point(192, 22);
            btnAutoMatch.Size = new Size(84, 26);
            btnAutoMatch.Click += BtnAutoMatch_Click;

            lblSelected = new Label();
            lblSelected.Text = "尚未选择字节（可直接点上方字节框手动选）";
            lblSelected.Location = new Point(12, 58);
            lblSelected.Size = new Size(472, 42);
            lblSelected.ForeColor = Color.DimGray;

            gb3.Controls.Add(lblKnown);
            gb3.Controls.Add(txtKnownLevel);
            gb3.Controls.Add(lblPct);
            gb3.Controls.Add(btnAutoMatch);
            gb3.Controls.Add(lblSelected);

            // ===== ④ 测试保存 =====
            GroupBox gb4 = new GroupBox();
            gb4.Text = "④ 测试并保存";
            gb4.Location = new Point(12, 368);
            gb4.Size = new Size(496, 92);

            Label lblName = new Label();
            lblName.Text = "设备名称：";
            lblName.Location = new Point(12, 26);
            lblName.AutoSize = true;

            txtName = new TextBox();
            txtName.Location = new Point(84, 22);
            txtName.Size = new Size(396, 24);

            btnTest = new Button();
            btnTest.Text = "测试读取";
            btnTest.Location = new Point(12, 56);
            btnTest.Size = new Size(84, 26);
            btnTest.Click += BtnTest_Click;

            lblTestResult = new Label();
            lblTestResult.Text = "";
            lblTestResult.Location = new Point(108, 60);
            lblTestResult.Size = new Size(376, 22);

            gb4.Controls.Add(lblName);
            gb4.Controls.Add(txtName);
            gb4.Controls.Add(btnTest);
            gb4.Controls.Add(lblTestResult);

            // ===== 底部按钮 =====
            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(318, 472);
            btnCancel.Size = new Size(90, 30);
            btnCancel.DialogResult = DialogResult.Cancel;

            btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Location = new Point(414, 472);
            btnSave.Size = new Size(90, 30);
            btnSave.Click += BtnSave_Click;

            this.Controls.Add(gb1);
            this.Controls.Add(gb2);
            this.Controls.Add(gb3);
            this.Controls.Add(gb4);
            this.Controls.Add(btnCancel);
            this.Controls.Add(btnSave);
            this.CancelButton = btnCancel;
        }

        // ==================== 数据加载 ====================

        private void LoadInterfaces()
        {
            interfaces = DeviceBatteryManager.EnumerateInterfaces();

            // 按设备名称排序（让同一设备的多个接口挨在一起、找起来更快）；
            // 名称相同的再按 UsagePage 升序，保证顺序稳定不跳动。
            interfaces.Sort(delegate(DeviceBatteryManager.HidInterfaceInfo a, DeviceBatteryManager.HidInterfaceInfo b)
            {
                string na = (a.Product == null || a.Product.Length == 0) ? "未知设备" : a.Product;
                string nb = (b.Product == null || b.Product.Length == 0) ? "未知设备" : b.Product;
                int c = string.Compare(na, nb, StringComparison.CurrentCulture);
                if (c != 0) return c;
                c = a.UsagePage.CompareTo(b.UsagePage);
                if (c != 0) return c;
                return a.Vid.CompareTo(b.Vid);
            });

            cmbInterfaces.Items.Clear();
            for (int i = 0; i < interfaces.Count; i++)
                cmbInterfaces.Items.Add(interfaces[i].DisplayName);

            if (interfaces.Count > 0) cmbInterfaces.SelectedIndex = 0;
            else
            {
                lblSelected.Text = "未找到可配置的 HID 接口（请确认设备已连接）。";
                btnRead.Enabled = false;
                btnProbe.Enabled = false;
            }
        }

        private DeviceBatteryManager.HidInterfaceInfo CurrentInterface
        {
            get
            {
                int i = cmbInterfaces.SelectedIndex;
                if (i < 0 || i >= interfaces.Count) return null;
                return interfaces[i];
            }
        }

        private int SelectedReportId
        {
            get
            {
                int i = cmbReportId.SelectedIndex;
                if (i < 0) return -1;
                string s = cmbReportId.Items[i].ToString();
                int p = s.IndexOf('(');
                if (p < 0) return -1;
                int q = s.IndexOf(')', p);
                if (q < 0) return -1;
                int v;
                if (int.TryParse(s.Substring(p + 1, q - p - 1), out v)) return v;
                return -1;
            }
        }

        // ==================== 事件 ====================

        private void OnInterfaceChanged()
        {
            DeviceBatteryManager.HidInterfaceInfo info = CurrentInterface;
            if (info == null) return;

            bool canInput = info.InputLen > 0;
            bool canFeature = info.FeatureLen > 0;
            rbInput.Enabled = canInput;
            rbFeature.Enabled = canFeature;
            if (!canInput && canFeature) rbFeature.Checked = true;
            else if (canInput && !canFeature) rbInput.Checked = true;

            if (info.Product != null && info.Product.Length > 0)
                txtName.Text = info.Product;

            // 换接口后清空旧报文，避免拿旧字节误配
            currentReport = null;
            selectedByte = -1;
            pnlBytes.Controls.Clear();
            lblSelected.Text = "尚未选择字节（可直接点上方字节框手动选）";
            lblSelected.ForeColor = Color.DimGray;

            OnModeChanged();
        }

        private void OnModeChanged()
        {
            bool feature = rbFeature.Checked;
            cmbReportId.Enabled = feature;
            btnProbe.Enabled = feature;
            if (!feature)
            {
                cmbReportId.Items.Clear();
            }
        }

        private void BtnProbe_Click(object sender, EventArgs e)
        {
            DeviceBatteryManager.HidInterfaceInfo info = CurrentInterface;
            if (info == null || info.FeatureLen <= 0) return;

            btnProbe.Enabled = false;
            btnProbe.Text = "探测…";

            string path = info.Path;
            int fLen = info.FeatureLen;

            Thread t = new Thread(delegate()
            {
                // HID 调用必须包住：设备拔出/休眠时会抛异常，而后台线程的未捕获异常会直接终止进程
                List<int> found;
                try
                {
                    found = DeviceBatteryManager.ProbeFeatureReportIds(path, fLen);
                }
                catch (Exception ex)
                {
                    Logger.Error("探测 Feature ReportID 失败", ex);
                    found = new List<int>();
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        cmbReportId.Items.Clear();
                        if (found.Count == 0)
                        {
                            lblSelected.Text = "该接口没有读出可用的 Feature 报告。";
                            lblSelected.ForeColor = Color.Firebrick;
                        }
                        else
                        {
                            for (int i = 0; i < found.Count; i++)
                                cmbReportId.Items.Add("0x" + found[i].ToString("X2") + " (" + found[i] + ")");
                            cmbReportId.SelectedIndex = 0;
                            lblSelected.Text = "探测到 " + found.Count + " 个可读 ReportID，已选第一个。";
                            lblSelected.ForeColor = Color.DarkGreen;
                        }
                        btnProbe.Enabled = true;
                        btnProbe.Text = "探测";
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void BtnRead_Click(object sender, EventArgs e)
        {
            DeviceBatteryManager.HidInterfaceInfo info = CurrentInterface;
            if (info == null) return;

            bool feature = rbFeature.Checked;
            int rid = SelectedReportId;
            if (feature && rid <= 0)
            {
                lblSelected.Text = "请先点「探测」确定 ReportID。";
                lblSelected.ForeColor = Color.Firebrick;
                return;
            }

            btnRead.Enabled = false;
            btnRead.Text = "读取中…";

            string path = info.Path;
            int inLen = info.InputLen;
            int fLen = info.FeatureLen;

            Thread t = new Thread(delegate()
            {
                byte[] report = null;
                // 同上：HID 读取放 try 内，读不到就按"读取失败"提示，不让异常逃出线程
                try
                {
                    if (feature) report = DeviceBatteryManager.ReadFeatureReport(path, fLen, rid);
                    else report = DeviceBatteryManager.ReadInputReport(path, inLen);
                }
                catch (Exception ex)
                {
                    Logger.Error("读取 HID 报文失败", ex);
                    report = null;
                }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        btnRead.Enabled = true;
                        btnRead.Text = "读取报文";
                        if (report == null || report.Length == 0)
                        {
                            lblSelected.Text = "读取失败：设备没有返回数据（Input 报告可能需要设备活动时才发）。";
                            lblSelected.ForeColor = Color.Firebrick;
                            return;
                        }
                        currentReport = report;
                        selectedByte = -1;
                        RebuildByteButtons();
                        lblSelected.Text = "已读取 " + report.Length + " 字节；请点选或自动匹配电量字节。";
                        lblSelected.ForeColor = Color.DimGray;
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RebuildByteButtons()
        {
            pnlBytes.Controls.Clear();
            if (currentReport == null) return;

            for (int i = 0; i < currentReport.Length; i++)
            {
                int idx = i;
                Button b = new Button();
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Color.Gray;
                b.Size = new Size(54, 52);
                b.Margin = new Padding(1);
                b.Font = new Font("Consolas", 8f);
                b.TextAlign = ContentAlignment.MiddleCenter;

                string txt = "[" + i + "]";
                if (chkDecimal.Checked)
                    txt += "\r\n" + currentReport[i].ToString("X2") + "\r\n" + currentReport[i];
                else
                    txt += "\r\n" + currentReport[i].ToString("X2");
                b.Text = txt;

                if (i == selectedByte)
                {
                    b.BackColor = Color.FromArgb(180, 220, 255);
                    b.FlatAppearance.BorderColor = Color.DodgerBlue;
                }

                b.Click += delegate
                {
                    selectedByte = idx;
                    RebuildByteButtons();
                    UpdateSelectedLabel();
                };
                pnlBytes.Controls.Add(b);
            }
        }

        private void UpdateSelectedLabel()
        {
            if (currentReport == null || selectedByte < 0 || selectedByte >= currentReport.Length)
            {
                lblSelected.Text = "尚未选择字节（可直接点上方字节框手动选）";
                lblSelected.ForeColor = Color.DimGray;
                return;
            }
            int v = currentReport[selectedByte];
            lblSelected.Text = "已选：第 " + (selectedByte + 1) + " 个字节 [索引 " + selectedByte + "] = 0x"
                + v.ToString("X2") + " = " + v;
            lblSelected.ForeColor = Color.DarkGreen;
        }

        private void BtnAutoMatch_Click(object sender, EventArgs e)
        {
            DeviceBatteryManager.HidInterfaceInfo info = CurrentInterface;
            if (info == null) return;

            int known;
            if (!int.TryParse(txtKnownLevel.Text.Trim(), out known) || known < 0 || known > 100)
            {
                lblSelected.Text = "请先在「官方软件显示」里填 0~100 的电量值，再点自动搜索。";
                lblSelected.ForeColor = Color.Firebrick;
                return;
            }

            bool feature = rbFeature.Checked;
            if (feature && info.FeatureLen <= 0)
            {
                lblSelected.Text = "该接口没有 Feature 报告。";
                lblSelected.ForeColor = Color.Firebrick;
                return;
            }
            if (!feature && info.InputLen <= 0)
            {
                lblSelected.Text = "该接口没有 Input 报告。";
                lblSelected.ForeColor = Color.Firebrick;
                return;
            }

            btnAutoMatch.Enabled = false;
            btnAutoMatch.Text = "搜索中…";
            lblSelected.Text = feature
                ? "正在遍历所有 ReportID × 所有字节搜索，请稍候…"
                : "正在读取报文并搜索，请稍候…";
            lblSelected.ForeColor = Color.DimGray;

            string path = info.Path;
            int inLen = info.InputLen;
            int fLen = info.FeatureLen;

            Thread t = new Thread(delegate()
            {
                DeviceBatteryManager.BatterySearchResult res = null;
                byte[] report = null;
                // 同上：搜索与随后的读取都放 try 内，失败时 res 保持 null，由 UI 给出提示
                try
                {
                    res = DeviceBatteryManager.AutoSearchBattery(path, inLen, fLen, feature, known);

                    // 命中后把首个候选所在的报告再读一次，用于界面显示字节
                    if (res.Matches.Count > 0)
                    {
                        DeviceBatteryManager.BatteryMatch m0 = res.Matches[0];
                        report = m0.UseFeature
                            ? DeviceBatteryManager.ReadFeatureReport(path, fLen, m0.ReportId)
                            : DeviceBatteryManager.ReadInputReport(path, inLen);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("自动搜索电量字节失败", ex);
                    res = null;
                }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        btnAutoMatch.Enabled = true;
                        btnAutoMatch.Text = "自动搜索";
                        if (res == null)
                        {
                            lblSelected.Text = "自动搜索失败：请确认设备已连接后重试。";
                            lblSelected.ForeColor = Color.Firebrick;
                            return;
                        }
                        ApplySearchResult(res, report, known, feature);
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>把自动搜索结果落到界面：填 ReportID 下拉框、选中字节、给出提示。</summary>
        private void ApplySearchResult(DeviceBatteryManager.BatterySearchResult res, byte[] report, int known, bool searchedFeature)
        {
            // Feature 模式下顺便把探测到的 ReportID 填进下拉框，省得用户再点「探测」
            if (searchedFeature)
            {
                cmbReportId.Items.Clear();
                for (int i = 0; i < res.ReportIds.Count; i++)
                    cmbReportId.Items.Add("0x" + res.ReportIds[i].ToString("X2") + " (" + res.ReportIds[i] + ")");
            }

            if (res.Matches.Count == 0)
            {
                if (searchedFeature && res.ReportIds.Count == 0)
                    lblSelected.Text = "该接口没有可读的 Feature 报告。";
                else
                    lblSelected.Text = "没找到值为 " + known + " 的字节。请核对官方软件显示的电量。";
                lblSelected.ForeColor = Color.Firebrick;
                return;
            }

            DeviceBatteryManager.BatteryMatch first = res.Matches[0];

            if (searchedFeature)
            {
                for (int i = 0; i < cmbReportId.Items.Count; i++)
                {
                    if (ParseReportIdItem(cmbReportId.Items[i].ToString()) == first.ReportId)
                    {
                        cmbReportId.SelectedIndex = i;
                        break;
                    }
                }
            }

            currentReport = report;
            selectedByte = first.ByteIndex;
            RebuildByteButtons();

            if (res.Matches.Count == 1)
            {
                lblSelected.Text = "找到唯一匹配：" + DescribeMatch(first) + " = " + known + "（已自动选中）";
                lblSelected.ForeColor = Color.DarkGreen;
            }
            else
            {
                string all = "";
                for (int i = 0; i < res.Matches.Count; i++)
                    all += (i > 0 ? "；" : "") + DescribeMatch(res.Matches[i]);
                lblSelected.Text = "找到 " + res.Matches.Count + " 个候选（已选第 1 个）：" + all
                    + "。提示：调一下设备设置（如亮度）再搜一次，跟着变的那个就不是电量。";
                lblSelected.ForeColor = Color.DarkOrange;
            }
        }

        /// <summary>从 "0x06 (6)" 这种下拉项里取出 ReportID。</summary>
        private int ParseReportIdItem(string s)
        {
            int p = s.IndexOf('(');
            if (p < 0) return -1;
            int q = s.IndexOf(')', p);
            if (q < 0) return -1;
            int v;
            return int.TryParse(s.Substring(p + 1, q - p - 1), out v) ? v : -1;
        }

        private string DescribeMatch(DeviceBatteryManager.BatteryMatch m)
        {
            string s = m.UseFeature ? ("ReportID " + m.ReportId + " ") : "Input ";
            s += "第 " + (m.ByteIndex + 1) + " 字节";
            return s;
        }

        private void BtnTest_Click(object sender, EventArgs e)
        {
            DeviceBatteryManager.HidInterfaceInfo info = CurrentInterface;
            if (info == null) return;
            if (selectedByte < 0)
            {
                lblTestResult.Text = "请先选择电量字节。";
                lblTestResult.ForeColor = Color.Firebrick;
                return;
            }

            bool feature = rbFeature.Checked;
            int rid = SelectedReportId;
            if (feature && rid <= 0)
            {
                lblTestResult.Text = "请先探测 ReportID。";
                lblTestResult.ForeColor = Color.Firebrick;
                return;
            }

            string path = info.Path;
            int inLen = info.InputLen;
            int fLen = info.FeatureLen;
            int sb = selectedByte;

            lblTestResult.Text = "测试中…";
            lblTestResult.ForeColor = Color.DimGray;

            Thread t = new Thread(delegate()
            {
                byte[] report = null;
                // 同上：读取放 try 内，失败按"读不到数据"提示
                try
                {
                    report = feature
                        ? DeviceBatteryManager.ReadFeatureReport(path, fLen, rid)
                        : DeviceBatteryManager.ReadInputReport(path, inLen);
                }
                catch (Exception ex)
                {
                    Logger.Error("测试读取电量字节失败", ex);
                    report = null;
                }

                try
                {
                    BeginInvoke(new MethodInvoker(delegate()
                    {
                        if (report == null || sb >= report.Length)
                        {
                            lblTestResult.Text = "测试失败：读不到数据。";
                            lblTestResult.ForeColor = Color.Firebrick;
                            return;
                        }
                        int v = report[sb];
                        lblTestResult.Text = "读取结果：" + v + "%　（与官方软件对比，一致即成功）";
                        lblTestResult.ForeColor = (v >= 0 && v <= 100) ? Color.DarkGreen : Color.Firebrick;
                    }));
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            DeviceBatteryManager.HidInterfaceInfo info = CurrentInterface;
            if (info == null) { MessageBox.Show("请先选择设备接口。", "提示"); return; }
            if (selectedByte < 0) { MessageBox.Show("请先选择电量字节。", "提示"); return; }
            if (txtName.Text.Trim().Length == 0) { MessageBox.Show("请填写设备名称。", "提示"); return; }
            if (rbFeature.Checked && SelectedReportId <= 0)
            {
                MessageBox.Show("Feature 报告需要先探测 ReportID。", "提示");
                return;
            }

            BatteryProtocol p = new BatteryProtocol();
            p.Vid = info.Vid;
            p.Pid = info.Pid;
            p.UsagePage = info.UsagePage;
            p.UseFeature = rbFeature.Checked;
            p.FeatureReportId = rbFeature.Checked ? SelectedReportId : 0;
            p.BatteryByte = selectedByte;
            p.Name = txtName.Text.Trim();

            BatteryProtocolStore.AddOrUpdate(p);
            Logger.Info("已保存自定义电量协议: " + p.Describe());

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
