using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 设置窗口（六页签，顺序：应用音量 / 播放设备 / 输入设备 / 设备电量 / 系统 / 关于）
    /// Tab1「应用音量」= 正在播放声音的应用（含已暂停）+ 单独调节音量 / 静音 / 初始化；
    /// Tab2「播放设备」= 播放设备切换快捷键 + 参与循环的播放设备；
    /// Tab3「输入设备」= 输入设备切换快捷键 + 参与循环的输入设备；
    /// Tab4「设备电量」= 能向 Windows 上报电量的无线设备（每次启动自动扫描，不写死设备）；
    /// Tab5「系统」= 允许自动息屏开关 + 屏幕自动变暗；
    /// Tab6「关于」= 版本与路径信息 + 记录日志开关 / 删除日志按钮。
    /// </summary>
    internal class SettingsForm : Form
    {
        // —— 播放设备页 ——
        private CheckBox cbCtrl, cbAlt, cbShift, cbWin;
        private ComboBox cbKey;
        private Label lblPreview;
        private CheckedListBox clbDevices;
        private CheckBox cbLog;
        private Label lblLogInfo;

        // —— 输入设备页 ——
        private CheckBox cbInCtrl, cbInAlt, cbInShift, cbInWin;
        private ComboBox cbInKey;
        private Label lblInPreview;
        private CheckedListBox clbInputDevices;

        // —— 系统页 ——
        private CheckBox cbAllowScreenOff;
        private Label lblScreenState;
        private CheckBox cbDimEnabled;
        private NumericUpDown numDimSeconds;
        private NumericUpDown numDimOpacity;
        private NumericUpDown numDimFadeSec;
        private Label lblDimState;
        private Label lblHint;

        // —— 应用音量页 ——
        private FlowLayoutPanel sessionsList;
        private Label lblSysVolume;
        private Label lblNoSessions;
        private Timer sessionsTimer;
        private readonly Dictionary<string, SessionRow> sessionRows = new Dictionary<string, SessionRow>();
        private static readonly Dictionary<string, Image> iconCache = new Dictionary<string, Image>();

        // —— 设备电量页 ——
        private TabPage pageBattery;
        private FlowLayoutPanel batteryList;
        private Label lblBatteryScanTime;
        private Label lblNoBattery;
        // 电量列表是"快照"，设备会在程序运行期间连上/断开（尤其蓝牙），
        // 所以收到设备变动通知时去抖重扫（不做定时轮询）。
        private Timer batteryDebounce;

        private List<uint> playKeys = new List<uint>();
        private List<uint> inputKeys = new List<uint>();
        private TabControl tabs;
        public event Action OnSaved;

        public SettingsForm()
        {
            this.Text = AppInfo.Name + " V" + AppInfo.Version + " - 设置";
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.ClientSize = new Size(460, 504);
            this.Font = new Font("Microsoft YaHei UI", 9f);
            this.AutoScaleMode = AutoScaleMode.None; // 固定像素布局，避免 DPI/字体缩放导致控件错位或渲染异常

            TabControl tabs = new TabControl();
            tabs.Location = new Point(12, 12);
            tabs.Size = new Size(436, 440);
            this.tabs = tabs;

            TabPage pagePlayback = new TabPage("播放设备");
            pagePlayback.UseVisualStyleBackColor = true;
            TabPage pageInput = new TabPage("输入设备");
            pageInput.UseVisualStyleBackColor = true;
            TabPage pageSessions = new TabPage("应用音量");
            pageSessions.UseVisualStyleBackColor = true;
            pageBattery = new TabPage("设备电量");
            pageBattery.UseVisualStyleBackColor = true;
            TabPage pageSystem = new TabPage("系统");
            pageSystem.UseVisualStyleBackColor = true;
            TabPage pageAbout = new TabPage("关于");
            pageAbout.UseVisualStyleBackColor = true;

            BuildPlaybackTab(pagePlayback);
            BuildInputTab(pageInput);
            BuildSessionsTab(pageSessions);
            BuildBatteryTab(pageBattery);
            BuildSystemTab(pageSystem);
            BuildAboutTab(pageAbout);

            // 页签顺序：设备电量 / 播放设备 / 输入设备 / 应用音量 / 系统 / 关于
            tabs.TabPages.Add(pageBattery);
            tabs.TabPages.Add(pagePlayback);
            tabs.TabPages.Add(pageInput);
            tabs.TabPages.Add(pageSessions);
            tabs.TabPages.Add(pageSystem);
            tabs.TabPages.Add(pageAbout);
            this.Controls.Add(tabs);

            // 只在切到对应页签时才刷新，其它页签不占用资源
            tabs.SelectedIndexChanged += delegate
            {
                if (tabs.SelectedTab == pageSessions)
                {
                    RefreshSessions();
                    sessionsTimer.Start();
                }
                else if (sessionsTimer != null)
                {
                    sessionsTimer.Stop();
                }

                // 「设备电量」页：切进来扫一次；之后只靠设备变动通知，不再定时轮询
                if (tabs.SelectedTab == pageBattery) RescanBattery();
            };
            this.FormClosed += delegate
            {
                if (sessionsTimer != null) sessionsTimer.Stop();
                if (batteryDebounce != null) batteryDebounce.Stop();
            };
            // 注意：构造阶段 TabControl 还没有窗口句柄，此时 SelectedIndex == -1、SelectedTab == null，
            // 在这里判断"当前是不是应用音量页"永远不成立。所以初始选中页和刷新的启动都必须推迟到 Shown。
            this.Shown += delegate
            {
                // 首启引导去「播放设备」页设快捷键；当前顺序下它是第 2 个（索引 1）
                if (Settings.IsFirstRun) tabs.SelectedIndex = 1;
                if (tabs.SelectedTab == pageSessions)
                {
                    RefreshSessions();
                    sessionsTimer.Start();
                }
                if (tabs.SelectedTab == pageBattery) RescanBattery();
            };

            // 按钮：应用（保存但不关窗）/ 保存（保存并关窗）/ 取消
            // 「应用」与「保存/取消」刻意留出较大间距，避免误点
            Button btnApply = new Button();
            btnApply.Text = "应用";
            btnApply.Location = new Point(165, 458);
            btnApply.Size = new Size(75, 32);
            btnApply.Click += delegate { ApplyOnly(); };

            Button btnSave = new Button();
            btnSave.Text = "保存";
            btnSave.Location = new Point(280, 458);
            btnSave.Size = new Size(75, 32);
            btnSave.Click += delegate { SaveAndClose(); };

            Button btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(363, 458);
            btnCancel.Size = new Size(75, 32);
            btnCancel.Click += delegate { Close(); };

            lblHint = new Label();
            lblHint.Location = new Point(12, 467);
            lblHint.AutoSize = true;
            lblHint.ForeColor = Color.DimGray;

            this.Controls.Add(lblHint);
            this.Controls.Add(btnApply);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnCancel);

            cbCtrl.Select(); // 打开即可直接操作
        }

        /// <summary>「应用」：写入配置并立即生效，窗口保持打开。</summary>
        private void ApplyOnly()
        {
            if (ApplySettings())
                lblHint.Text = "已应用 " + DateTime.Now.ToString("HH:mm:ss");
        }

        /// <summary>「保存」：写入配置并立即生效，然后关闭窗口。</summary>
        private void SaveAndClose()
        {
            if (ApplySettings()) Close();
        }

        /// <summary>切换到指定页签（供诊断/测试使用）。</summary>
        internal void SelectTab(int index)
        {
            if (tabs != null && index >= 0 && index < tabs.TabPages.Count)
                tabs.SelectedIndex = index;
        }

        // ==================== Tab2：播放设备 ====================
        private void BuildPlaybackTab(TabPage page)
        {
            Label l1 = new Label();
            l1.Text = "切换快捷键：";
            l1.Location = new Point(10, 16);
            l1.AutoSize = true;

            cbCtrl = MakeCheckBox("Ctrl", 92, 13);
            cbAlt = MakeCheckBox("Alt", 162, 13);
            cbShift = MakeCheckBox("Shift", 227, 13);
            cbWin = MakeCheckBox("Win", 287, 13);

            Label l2 = new Label();
            l2.Text = "主键：";
            l2.Location = new Point(10, 50);
            l2.AutoSize = true;

            cbKey = new ComboBox();
            cbKey.DropDownStyle = ComboBoxStyle.DropDownList;
            cbKey.Location = new Point(92, 46);
            cbKey.Width = 130;
            FillKeys(cbKey, playKeys);

            lblPreview = new Label();
            lblPreview.Location = new Point(10, 82);
            lblPreview.AutoSize = true;
            lblPreview.ForeColor = Color.DarkBlue;

            Label l3 = new Label();
            l3.Text = "参与循环的播放设备（勾选的才进入循环，全不勾 = 全部）：";
            l3.Location = new Point(10, 112);
            l3.AutoSize = true;

            clbDevices = new CheckedListBox();
            clbDevices.Location = new Point(10, 136);
            clbDevices.Size = new Size(410, 250);
            clbDevices.CheckOnClick = true;
            clbDevices.IntegralHeight = false;
            LoadDevices(clbDevices, false, Settings.SelectedDevices);

            page.Controls.Add(l1); page.Controls.Add(cbCtrl); page.Controls.Add(cbAlt);
            page.Controls.Add(cbShift); page.Controls.Add(cbWin);
            page.Controls.Add(l2); page.Controls.Add(cbKey);
            page.Controls.Add(lblPreview);
            page.Controls.Add(l3); page.Controls.Add(clbDevices);

            cbCtrl.CheckedChanged += delegate { UpdatePreview(); };
            cbAlt.CheckedChanged += delegate { UpdatePreview(); };
            cbShift.CheckedChanged += delegate { UpdatePreview(); };
            cbWin.CheckedChanged += delegate { UpdatePreview(); };
            cbKey.SelectedIndexChanged += delegate { UpdatePreview(); };

            cbCtrl.Checked = (Settings.Modifiers & NativeMethods.MOD_CONTROL) != 0;
            cbAlt.Checked = (Settings.Modifiers & NativeMethods.MOD_ALT) != 0;
            cbShift.Checked = (Settings.Modifiers & NativeMethods.MOD_SHIFT) != 0;
            cbWin.Checked = (Settings.Modifiers & NativeMethods.MOD_WIN) != 0;
            SelectKey(cbKey, playKeys, Settings.Key);

            UpdatePreview();
        }

        // ==================== Tab3：输入设备 ====================
        private void BuildInputTab(TabPage page)
        {
            Label l1 = new Label();
            l1.Text = "切换快捷键：";
            l1.Location = new Point(10, 16);
            l1.AutoSize = true;

            cbInCtrl = MakeCheckBox("Ctrl", 92, 13);
            cbInAlt = MakeCheckBox("Alt", 162, 13);
            cbInShift = MakeCheckBox("Shift", 227, 13);
            cbInWin = MakeCheckBox("Win", 287, 13);

            Label l2 = new Label();
            l2.Text = "主键：";
            l2.Location = new Point(10, 50);
            l2.AutoSize = true;

            cbInKey = new ComboBox();
            cbInKey.DropDownStyle = ComboBoxStyle.DropDownList;
            cbInKey.Location = new Point(92, 46);
            cbInKey.Width = 130;
            FillKeys(cbInKey, inputKeys);

            lblInPreview = new Label();
            lblInPreview.Location = new Point(10, 82);
            lblInPreview.AutoSize = true;
            lblInPreview.ForeColor = Color.DarkBlue;

            Label l3 = new Label();
            l3.Text = "参与循环的输入设备（麦克风等，勾选的才进入循环，全不勾 = 全部）：";
            l3.Location = new Point(10, 112);
            l3.AutoSize = true;

            clbInputDevices = new CheckedListBox();
            clbInputDevices.Location = new Point(10, 136);
            clbInputDevices.Size = new Size(410, 240);
            clbInputDevices.CheckOnClick = true;
            clbInputDevices.IntegralHeight = false;
            LoadDevices(clbInputDevices, true, Settings.SelectedInputDevices);

            page.Controls.Add(l1); page.Controls.Add(cbInCtrl); page.Controls.Add(cbInAlt);
            page.Controls.Add(cbInShift); page.Controls.Add(cbInWin);
            page.Controls.Add(l2); page.Controls.Add(cbInKey);
            page.Controls.Add(lblInPreview);
            page.Controls.Add(l3); page.Controls.Add(clbInputDevices);

            cbInCtrl.CheckedChanged += delegate { UpdateInputPreview(); };
            cbInAlt.CheckedChanged += delegate { UpdateInputPreview(); };
            cbInShift.CheckedChanged += delegate { UpdateInputPreview(); };
            cbInWin.CheckedChanged += delegate { UpdateInputPreview(); };
            cbInKey.SelectedIndexChanged += delegate { UpdateInputPreview(); };

            cbInCtrl.Checked = (Settings.InputModifiers & NativeMethods.MOD_CONTROL) != 0;
            cbInAlt.Checked = (Settings.InputModifiers & NativeMethods.MOD_ALT) != 0;
            cbInShift.Checked = (Settings.InputModifiers & NativeMethods.MOD_SHIFT) != 0;
            cbInWin.Checked = (Settings.InputModifiers & NativeMethods.MOD_WIN) != 0;
            SelectKey(cbInKey, inputKeys, Settings.InputKey);

            UpdateInputPreview();
        }

        // ==================== Tab1：应用音量 ====================
        private void BuildSessionsTab(TabPage page)
        {
            Label title = new Label();
            title.Text = "正在播放声音的应用（灰色 = 已暂停但仍在前台）";
            title.Location = new Point(10, 10);
            title.AutoSize = true;

            lblSysVolume = new Label();
            lblSysVolume.Location = new Point(10, 32);
            lblSysVolume.AutoSize = true;
            lblSysVolume.ForeColor = Color.DarkBlue;

            sessionsList = new FlowLayoutPanel();
            sessionsList.Location = new Point(10, 56);
            sessionsList.Size = new Size(408, 292);
            sessionsList.AutoScroll = true;
            sessionsList.FlowDirection = FlowDirection.TopDown;
            sessionsList.WrapContents = false;
            sessionsList.BorderStyle = BorderStyle.FixedSingle;
            sessionsList.Padding = new Padding(2);

            lblNoSessions = new Label();
            lblNoSessions.Text = "当前没有应用在播放声音";
            lblNoSessions.Location = new Point(10, 10);
            lblNoSessions.AutoSize = true;
            lblNoSessions.ForeColor = Color.Gray;
            sessionsList.Controls.Add(lblNoSessions);

            Button btnResetAll = new Button();
            btnResetAll.Text = "全部恢复到 100%";
            btnResetAll.Location = new Point(10, 356);
            btnResetAll.Size = new Size(132, 30);
            btnResetAll.Click += delegate { ResetAllSessions(); };

            Label tip = new Label();
            tip.Text = "应用关闭后再打开，音量会自动恢复 100%";
            tip.Location = new Point(150, 362);
            tip.AutoSize = true;
            tip.ForeColor = Color.DimGray;

            page.Controls.Add(title);
            page.Controls.Add(lblSysVolume);
            page.Controls.Add(sessionsList);
            page.Controls.Add(btnResetAll);
            page.Controls.Add(tip);

            sessionsTimer = new Timer();
            sessionsTimer.Interval = 1000;
            sessionsTimer.Tick += delegate { RefreshSessions(); };
            RefreshSessions(); // 构建时先填一次内容
        }

        /// <summary>重新枚举音频会话并同步到列表（只增删改差异，不重建整表，避免滑块闪烁）。</summary>
        private void RefreshSessions()
        {
            try
            {
                float devVol = AudioSessionManager.GetDeviceVolume();
                string volText = "当前系统总音量：" + (int)Math.Round(devVol * 100) + "%";
                if (lblSysVolume.Text != volText) lblSysVolume.Text = volText; // 没变就别设，避免每秒重绘

                List<AudioSessionInfo> sessions = AudioSessionManager.GetSessions();

                // 按 AppKey 分组（同一应用可能有多路会话）
                Dictionary<string, List<AudioSessionInfo>> groups = new Dictionary<string, List<AudioSessionInfo>>();
                List<string> order = new List<string>();
                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionInfo s = sessions[i];
                    if (s.State == (int)SessionState.Expired) continue;
                    string key = s.AppKey;
                    List<AudioSessionInfo> bucket;
                    if (!groups.TryGetValue(key, out bucket))
                    {
                        bucket = new List<AudioSessionInfo>();
                        groups[key] = bucket;
                        order.Add(key);
                    }
                    bucket.Add(s);
                }

                // 移除已消失的应用行
                List<string> gone = new List<string>();
                foreach (KeyValuePair<string, SessionRow> kv in sessionRows)
                {
                    if (!groups.ContainsKey(kv.Key)) gone.Add(kv.Key);
                }
                for (int i = 0; i < gone.Count; i++)
                {
                    SessionRow row = sessionRows[gone[i]];
                    sessionsList.Controls.Remove(row);
                    row.Dispose();
                    sessionRows.Remove(gone[i]);
                }

                // 新增 / 更新
                for (int i = 0; i < order.Count; i++)
                {
                    string key = order[i];
                    List<AudioSessionInfo> group = groups[key];
                    SessionRow row;
                    if (!sessionRows.TryGetValue(key, out row))
                    {
                        row = new SessionRow(group[0], GetAppIcon(group[0]));
                        sessionsList.Controls.Add(row);
                        sessionRows[key] = row;
                    }
                    row.UpdateFrom(group);
                }

                lblNoSessions.Visible = sessionRows.Count == 0;
            }
            catch (Exception ex)
            {
                Logger.Error("刷新应用音量列表失败", ex);
            }
        }

        /// <summary>把所有应用恢复到满音量 100% 并取消静音。</summary>
        private void ResetAllSessions()
        {
            foreach (KeyValuePair<string, SessionRow> kv in sessionRows)
            {
                kv.Value.ResetToDefault();
            }
            RefreshSessions();
        }

        /// <summary>取应用图标（按 exe 路径缓存，避免每秒重复提取）。</summary>
        private static Image GetAppIcon(AudioSessionInfo s)
        {
            string key = s.IsSystemSounds
                ? "@system-sounds"
                : (s.ExePath != null ? s.ExePath.ToLowerInvariant() : "name:" + s.ProcessName);
            Image cached;
            if (iconCache.TryGetValue(key, out cached)) return cached;

            Image img = null;
            try
            {
                if (!s.IsSystemSounds && !string.IsNullOrEmpty(s.ExePath) && File.Exists(s.ExePath))
                {
                    Icon ic = Icon.ExtractAssociatedIcon(s.ExePath);
                    if (ic != null) img = ic.ToBitmap();
                }
            }
            catch
            {
                img = null;
            }
            iconCache[key] = img;
            return img;
        }

        // ==================== Tab4：设备电量 ====================
        private void BuildBatteryTab(TabPage page)
        {
            Label title = new Label();
            title.Text = "能读到电量的无线设备";
            title.Location = new Point(10, 10);
            title.AutoSize = true;

            lblBatteryScanTime = new Label();
            lblBatteryScanTime.Location = new Point(10, 32);
            lblBatteryScanTime.AutoSize = true;
            lblBatteryScanTime.ForeColor = Color.DimGray;

            Button btnRescan = new Button();
            btnRescan.Text = "重新扫描";
            btnRescan.Location = new Point(308, 27);
            btnRescan.Size = new Size(100, 28);
            btnRescan.Click += delegate
            {
                btnRescan.Enabled = false;
                btnRescan.Text = "扫描中…";
                DeviceBatteryManager.ScanNowAsync(delegate
                {
                    if (IsDisposed) return;
                    try
                    {
                        BeginInvoke(new MethodInvoker(delegate
                        {
                            btnRescan.Enabled = true;
                            btnRescan.Text = "重新扫描";
                            RefreshBatteryList();
                        }));
                    }
                    catch { }
                });
            };

            batteryList = new FlowLayoutPanel();
            batteryList.Location = new Point(10, 60);
            batteryList.Size = new Size(408, 252);
            batteryList.AutoScroll = true;
            batteryList.FlowDirection = FlowDirection.TopDown;
            batteryList.WrapContents = false;
            batteryList.BorderStyle = BorderStyle.FixedSingle;
            batteryList.Padding = new Padding(2);

            lblNoBattery = new Label();
            lblNoBattery.Location = new Point(10, 10);
            lblNoBattery.Size = new Size(372, 56);
            lblNoBattery.ForeColor = Color.Gray;
            lblNoBattery.Text = "未发现可读电量的设备。\r\n只有会向 Windows 上报电量的设备才会出现在这里。";
            batteryList.Controls.Add(lblNoBattery);

            // 添加自定义设备：打开简化界面（选设备 → 填官方电量 → 自动识别），
            // 复杂的逐步指定收在其左下角「高级自定义…」里。规则写入 TrayToys.json
            Button btnAdd = new Button();
            btnAdd.Text = "添加设备";
            btnAdd.Location = new Point(10, 320);
            btnAdd.Size = new Size(90, 28);
            btnAdd.Click += delegate
            {
                using (DeviceSetupSimpleForm f = new DeviceSetupSimpleForm())
                {
                    if (f.ShowDialog(this) == DialogResult.OK)
                    {
                        DeviceBatteryManager.ScanNowAsync(delegate
                        {
                            if (IsDisposed) return;
                            try { BeginInvoke(new MethodInvoker(RefreshBatteryList)); } catch { }
                        });
                    }
                }
            };

            // 管理已配置的协议（可删除配错的条目）
            Button btnManage = new Button();
            btnManage.Text = "管理协议";
            btnManage.Location = new Point(106, 320);
            btnManage.Size = new Size(90, 28);
            btnManage.Click += delegate
            {
                using (ProtocolManageForm f = new ProtocolManageForm())
                {
                    f.ShowDialog(this);
                }
                DeviceBatteryManager.ScanNowAsync(delegate
                {
                    if (IsDisposed) return;
                    try { BeginInvoke(new MethodInvoker(RefreshBatteryList)); } catch { }
                });
            };

            Label tip = new Label();
            tip.Location = new Point(10, 354);
            tip.Size = new Size(408, 20);
            tip.ForeColor = Color.DimGray;
            tip.Text = "蓝牙 / 2.4G 设备读不到电量时，可用「添加设备」手动配置。";

            page.Controls.Add(title);
            page.Controls.Add(lblBatteryScanTime);
            page.Controls.Add(btnRescan);
            page.Controls.Add(batteryList);
            page.Controls.Add(btnAdd);
            page.Controls.Add(btnManage);
            page.Controls.Add(tip);

            RefreshBatteryList();
        }

        /// <summary>按当前扫描结果重建设备列表。</summary>
        private void RefreshBatteryList()
        {
            batteryList.SuspendLayout();

            List<Control> stale = new List<Control>();
            foreach (Control c in batteryList.Controls)
            {
                if (c != lblNoBattery) stale.Add(c);
            }
            for (int i = 0; i < stale.Count; i++)
            {
                batteryList.Controls.Remove(stale[i]);
                stale[i].Dispose();
            }

            List<BatteryDevice> devices = DeviceBatteryManager.Devices;
            for (int i = 0; i < devices.Count; i++)
            {
                batteryList.Controls.Add(BuildBatteryRow(devices[i]));
            }

            lblNoBattery.Visible = devices.Count == 0;

            DateTime t = DeviceBatteryManager.LastScanTime;
            lblBatteryScanTime.Text = t == DateTime.MinValue
                ? "尚未扫描"
                : "上次扫描：" + t.ToString("MM-dd HH:mm:ss") + "　　共 " + devices.Count + " 个设备";

            batteryList.ResumeLayout();
        }

        // ==================== 设备电量：按需重扫（纯事件驱动，无定时器） ====================
        // 列表只是"某一刻的快照"。蓝牙设备会在程序运行期间连上/断开，
        // 若只在启动时扫一次，断开后那一行会一直留着（显示的是残留电量）。
        // 现在的刷新时机全部是"事件"：① 切到本页；② 系统设备变动广播；③ 手动重新扫描；
        // ④ 添加设备 / 管理协议返回。页面停留期间完全不轮询。

        /// <summary>「设备电量」页当前是否可见（只有可见时才响应设备变动通知）。</summary>
        private bool BatteryTabVisible()
        {
            return pageBattery != null && tabs != null && tabs.SelectedTab == pageBattery;
        }

        /// <summary>后台重扫并刷新列表（扫描合并由 DeviceBatteryManager 负责）。</summary>
        private void RescanBattery()
        {
            if (IsDisposed) return;
            // 只有"从没扫过"时才显示扫描提示；已有数据时的定时刷新不闪这行（扫描最长可达数秒）
            if (DeviceBatteryManager.LastScanTime == DateTime.MinValue)
                lblBatteryScanTime.Text = "正在扫描…";
            DeviceBatteryManager.ScanNowAsync(delegate
            {
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (IsDisposed) return;
                        RefreshBatteryList();
                    }));
                }
                catch { }
            });
        }

        /// <summary>
        /// 系统报告设备节点变动（蓝牙连接/断开、USB 插拔）。
        /// 这类通知会连着来一串，所以做 1.2 秒去抖，只扫最后一次。
        /// </summary>
        private void OnDeviceNodesChanged()
        {
            if (IsDisposed || !BatteryTabVisible()) return;

            if (batteryDebounce == null)
            {
                batteryDebounce = new Timer();
                batteryDebounce.Interval = 1200;
                batteryDebounce.Tick += delegate
                {
                    batteryDebounce.Stop();
                    if (BatteryTabVisible()) RescanBattery();
                };
            }
            batteryDebounce.Stop();
            batteryDebounce.Start();
        }

        /// <summary>
        /// 接收设备变动广播（蓝牙连接/断开、USB 插拔）。
        /// 设置窗口是顶层窗口，能收到发往 HWND_BROADCAST 的 WM_DEVICECHANGE。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_DEVICECHANGE
                && m.WParam.ToInt32() == NativeMethods.DBT_DEVNODES_CHANGED)
            {
                OnDeviceNodesChanged();
            }
            base.WndProc(ref m);
        }

        /// <summary>一行：设备名 + 电量条 + 百分比（查询式 2.4G 设备额外显示"值最后变化于…"）。</summary>
        private Panel BuildBatteryRow(BatteryDevice dev)
        {
            // 需要显示"最后变化时间"时行高加大，第二行放灰色小字
            bool withHint = dev.ValueMayBeStale && dev.ValueLastChanged != DateTime.MinValue;

            Panel row = new Panel();
            row.Size = new Size(384, withHint ? 50 : 36);
            row.Margin = new Padding(0, 0, 0, 2);

            Label name = new Label();
            name.Text = dev.Name;
            name.Location = new Point(2, withHint ? 5 : 9);
            name.Size = new Size(214, 18);
            name.AutoEllipsis = true;

            ProgressBar bar = new ProgressBar();
            bar.Location = new Point(222, withHint ? 7 : 10);
            bar.Size = new Size(100, 14);
            bar.Minimum = 0;
            bar.Maximum = 100;
            bar.Value = dev.Level;

            Label pct = new Label();
            pct.Text = dev.Level + "%";
            pct.Location = new Point(326, withHint ? 6 : 9);
            pct.Size = new Size(44, 18);
            pct.TextAlign = ContentAlignment.MiddleRight;
            if (dev.Level <= 20) pct.ForeColor = Color.Firebrick;

            row.Controls.Add(name);
            row.Controls.Add(bar);
            row.Controls.Add(pct);

            if (withHint)
            {
                Label hint = new Label();
                hint.Text = "值最后变化：" + BatteryValueTracker.Describe(dev.ValueLastChanged)
                    + "（查询式设备，可能不是实时值）";
                hint.Location = new Point(2, 26);
                hint.Size = new Size(372, 16);
                hint.Font = new Font("Microsoft YaHei UI", 8f);
                hint.ForeColor = Color.Gray;
                row.Controls.Add(hint);
            }

            return row;
        }

        // ==================== Tab5：系统 ====================
        private void BuildSystemTab(TabPage page)
        {
            cbAllowScreenOff = MakeCheckBox("允许自动息屏", 10, 16);
            cbAllowScreenOff.Size = new Size(140, 24);

            Label h1 = new Label();
            h1.Text = "取消勾选后：屏幕不会自动关闭，系统也不会自动进入睡眠（保持常亮）。";
            h1.Location = new Point(10, 48);
            h1.AutoSize = true;

            Label h2 = new Label();
            h2.Text = "适合看视频、演示、长时间任务；仅在程序运行期间生效，退出后恢复系统默认。";
            h2.Location = new Point(10, 70);
            h2.AutoSize = true;
            h2.ForeColor = Color.DimGray;

            lblScreenState = new Label();
            lblScreenState.Location = new Point(10, 96);
            lblScreenState.AutoSize = true;
            lblScreenState.ForeColor = Color.DarkBlue;

            Label sep = new Label();
            sep.BorderStyle = BorderStyle.Fixed3D;
            sep.Location = new Point(10, 126);
            sep.Size = new Size(406, 2);

            cbDimEnabled = MakeCheckBox("屏幕自动变暗", 10, 138);
            cbDimEnabled.Size = new Size(140, 24);

            Label d0 = new Label();
            d0.Text = "无操作";
            d0.Location = new Point(34, 176);
            d0.AutoSize = true;

            numDimSeconds = new NumericUpDown();
            numDimSeconds.Location = new Point(90, 172);
            numDimSeconds.Size = new Size(64, 25);
            numDimSeconds.Minimum = 5;
            numDimSeconds.Maximum = 3600;
            numDimSeconds.Increment = 5;

            Label d1 = new Label();
            d1.Text = "秒后变暗，鼠标/键盘有输入立即恢复。";
            d1.Location = new Point(162, 176);
            d1.AutoSize = true;

            Label d2 = new Label();
            d2.Text = "变暗程度";
            d2.Location = new Point(34, 210);
            d2.AutoSize = true;

            numDimOpacity = new NumericUpDown();
            numDimOpacity.Location = new Point(90, 206);
            numDimOpacity.Size = new Size(64, 25);
            numDimOpacity.Minimum = 20;
            numDimOpacity.Maximum = 95;
            numDimOpacity.Increment = 5;

            Label d3 = new Label();
            d3.Text = "%（越大越暗）";
            d3.Location = new Point(162, 210);
            d3.AutoSize = true;

            Label f0 = new Label();
            f0.Text = "渐变时长";
            f0.Location = new Point(34, 244);
            f0.AutoSize = true;

            numDimFadeSec = new NumericUpDown();
            numDimFadeSec.Location = new Point(90, 240);
            numDimFadeSec.Size = new Size(64, 25);
            numDimFadeSec.DecimalPlaces = 1;
            numDimFadeSec.Minimum = 0M;
            numDimFadeSec.Maximum = 5M;
            numDimFadeSec.Increment = 0.1M;

            Label f1 = new Label();
            f1.Text = "秒（0 = 立即不渐变）";
            f1.Location = new Point(162, 244);
            f1.AutoSize = true;

            lblDimState = new Label();
            lblDimState.Location = new Point(10, 284);
            lblDimState.AutoSize = true;
            lblDimState.ForeColor = Color.DarkBlue;

            Label d4 = new Label();
            d4.Text = "变暗仅在「不允许自动息屏」时生效（已允许息屏时系统会自行关屏）。";
            d4.Location = new Point(10, 312);
            d4.AutoSize = true;
            d4.ForeColor = Color.DimGray;

            Label d5 = new Label();
            d5.Text = "检测到全屏应用（看视频、玩游戏、PPT 放映）时不会变暗。";
            d5.Location = new Point(10, 334);
            d5.AutoSize = true;
            d5.ForeColor = Color.DimGray;

            page.Controls.Add(cbAllowScreenOff);
            page.Controls.Add(h1);
            page.Controls.Add(h2);
            page.Controls.Add(lblScreenState);
            page.Controls.Add(sep);
            page.Controls.Add(cbDimEnabled);
            page.Controls.Add(d0); page.Controls.Add(numDimSeconds); page.Controls.Add(d1);
            page.Controls.Add(d2); page.Controls.Add(numDimOpacity); page.Controls.Add(d3);
            page.Controls.Add(f0); page.Controls.Add(numDimFadeSec); page.Controls.Add(f1);
            page.Controls.Add(lblDimState);
            page.Controls.Add(d4);
            page.Controls.Add(d5);

            cbAllowScreenOff.Checked = Settings.AllowScreenOff;
            cbDimEnabled.Checked = Settings.DimEnabled;
            numDimSeconds.Value = Clamp(Settings.DimSeconds, (double)numDimSeconds.Minimum, (double)numDimSeconds.Maximum);
            numDimOpacity.Value = Clamp(Settings.DimOpacity, (double)numDimOpacity.Minimum, (double)numDimOpacity.Maximum);
            numDimFadeSec.Value = Clamp(Settings.DimFadeSec, (double)numDimFadeSec.Minimum, (double)numDimFadeSec.Maximum);

            cbAllowScreenOff.CheckedChanged += delegate { UpdateScreenState(); };
            cbDimEnabled.CheckedChanged += delegate { UpdateScreenState(); };
            numDimSeconds.ValueChanged += delegate { UpdateScreenState(); };
            numDimOpacity.ValueChanged += delegate { UpdateScreenState(); };
            numDimFadeSec.ValueChanged += delegate { UpdateScreenState(); };
            UpdateScreenState();
        }

        /// <summary>渐变时长的可读文案（秒）。</summary>
        private static string FadeText(double sec)
        {
            return sec > 0 ? sec.ToString("0.#") + " 秒" : "关闭（立即）";
        }

        private void UpdateScreenState()
        {
            bool allow = cbAllowScreenOff.Checked;
            lblScreenState.Text = "当前状态：" + ScreenKeeper.StateText(allow);

            // 变暗只在「不允许自动息屏」时有意义
            bool usable = !allow;
            cbDimEnabled.Enabled = usable;
            bool on = cbDimEnabled.Checked && usable;
            numDimSeconds.Enabled = on;
            numDimOpacity.Enabled = on;
            numDimFadeSec.Enabled = on;

            if (!usable)
                lblDimState.Text = "变暗状态：不生效（当前允许自动息屏，系统会自行关屏）";
            else if (!cbDimEnabled.Checked)
                lblDimState.Text = "变暗状态：未启用";
            else
                lblDimState.Text = "变暗状态：无操作 " + (int)numDimSeconds.Value + " 秒后变暗，暗度 "
                    + (int)numDimOpacity.Value + "%，渐变 " + FadeText((double)numDimFadeSec.Value);
        }

        private static decimal Clamp(double value, double min, double max)
        {
            if (value < min) return (decimal)min;
            if (value > max) return (decimal)max;
            return (decimal)value;
        }

        // ==================== Tab6：关于 ====================
        private void BuildAboutTab(TabPage page)
        {
            Label title = new Label();
            title.Text = AppInfo.Name + "  V" + AppInfo.Version;
            title.Location = new Point(16, 16);
            title.AutoSize = true;
            title.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);

            // 一行定位 + 一行功能清单：程序现在不止切设备了，描述要覆盖全部功能
            Label desc = new Label();
            desc.Text = "常驻托盘的 Windows 小工具集，按快捷键在设备之间循环切换。";
            desc.Location = new Point(16, 46);
            desc.AutoSize = true;
            desc.ForeColor = Color.DimGray;

            Label desc2 = new Label();
            desc2.Text = "音频设备快切 · 按应用调音量 · 无线设备电量 · 防息屏与屏幕变暗";
            desc2.Location = new Point(16, 66);
            desc2.AutoSize = true;
            desc2.Font = new Font("Microsoft YaHei UI", 8.5f);
            desc2.ForeColor = Color.Gray;

            string logSize = "（暂无）";
            try
            {
                if (File.Exists(Logger.LogFilePath))
                {
                    long len = new FileInfo(Logger.LogFilePath).Length;
                    logSize = len < 1024 ? len + " 字节" : (len / 1024.0).ToString("0.0") + " KB";
                }
            }
            catch { }

            int playCount = -1, inCount = -1;
            try { playCount = AudioManager.GetPlaybackDevices().Count; } catch { }
            try { inCount = AudioManager.GetRecordingDevices().Count; } catch { }

            int y = 96;   // 上方有两行描述，信息区要相应下移
            page.Controls.Add(title); // 修复：原先创建了却没加到页面上，导致关于页顶部一直空白
            page.Controls.Add(desc);
            page.Controls.Add(desc2);
            y = AddInfo(page, "程序文件：", Application.ExecutablePath, y);
            y = AddInfo(page, "配置文件：", Settings.FilePath, y);
            y = AddInfo(page, "日志文件：", Logger.LogFilePath + "  " + logSize, y);
            y = AddInfo(page, "播放设备：", "快捷键 " + Settings.DisplayName
                + "；参与循环 " + SelectedCount(Settings.SelectedDevices)
                + "；系统共 " + CountText(playCount), y);
            y = AddInfo(page, "输入设备：", "快捷键 " + Settings.InputDisplayName
                + "；参与循环 " + SelectedCount(Settings.SelectedInputDevices)
                + "；系统共 " + CountText(inCount), y);
            y = AddInfo(page, "自动息屏：", ScreenKeeper.StateText(Settings.AllowScreenOff), y);
            y = AddInfo(page, "屏幕变暗：", DimStateText(), y);

            // ---- 日志设置（V1.8 起从「播放设备」页移到这里）----
            Label sepLog = new Label();
            sepLog.BorderStyle = BorderStyle.Fixed3D;
            sepLog.Location = new Point(16, y + 2);
            sepLog.Size = new Size(392, 2);

            cbLog = MakeCheckBox("记录日志", 16, y + 12);
            cbLog.Size = new Size(90, 24);
            cbLog.Checked = Settings.LogEnabled;

            Button btnDeleteLog = new Button();
            btnDeleteLog.Text = "删除日志文件";
            btnDeleteLog.Location = new Point(112, y + 8);
            btnDeleteLog.Size = new Size(110, 30);
            btnDeleteLog.Click += delegate { DeleteLog(); };

            lblLogInfo = new Label();
            lblLogInfo.Location = new Point(232, y + 13);
            lblLogInfo.AutoSize = true;
            lblLogInfo.ForeColor = Color.DimGray;

            page.Controls.Add(sepLog);
            page.Controls.Add(cbLog);
            page.Controls.Add(btnDeleteLog);
            page.Controls.Add(lblLogInfo);
        }

        private static string DimStateText()
        {
            if (!Settings.DimEnabled) return "未启用";
            if (Settings.AllowScreenOff) return "不生效（当前允许自动息屏）";
            return "无操作 " + Settings.DimSeconds + " 秒后变暗，暗度 " + Settings.DimOpacity + "%，渐变 "
                + FadeText(Settings.DimFadeSec);
        }

        private static string CountText(int n)
        {
            return n < 0 ? "读取失败" : n + " 个";
        }

        private static string SelectedCount(List<string> sel)
        {
            if (sel == null || sel.Count == 0) return "全部设备";
            return sel.Count + " 个";
        }

        private static int AddInfo(TabPage page, string caption, string value, int y)
        {
            Label cap = new Label();
            cap.Text = caption;
            cap.Location = new Point(16, y);
            cap.AutoSize = true;

            Label val = new Label();
            val.Text = value;
            val.Location = new Point(16, y + 19);
            val.AutoSize = true;
            val.ForeColor = Color.DimGray;

            page.Controls.Add(cap);
            page.Controls.Add(val);
            return y + 38;
        }

        // ==================== 通用辅助 ====================

        /// <summary>创建复选框：固定尺寸 + 系统原生绘制，确保任何环境下都能渲染出来。</summary>
        private static CheckBox MakeCheckBox(string text, int x, int y)
        {
            CheckBox cb = new CheckBox();
            cb.Text = text;
            cb.Location = new Point(x, y);
            cb.Size = new Size(60, 24);
            cb.AutoSize = false;
            cb.FlatStyle = FlatStyle.System;   // 用系统原生绘制复选框，避免自绘不显示
            cb.BackColor = SystemColors.Control;
            return cb;
        }

        /// <summary>填充主键下拉框：第 1 项为「无」（键码 0），其余为常用键。</summary>
        private void FillKeys(ComboBox box, List<uint> codes)
        {
            box.Items.Add("无");
            codes.Add(HotkeyKeys.None);
            foreach (KeyValuePair<uint, string> kv in HotkeyKeys.All)
            {
                box.Items.Add(kv.Value);
                codes.Add(kv.Key);
            }
        }

        private void SelectKey(ComboBox box, List<uint> codes, uint key)
        {
            int idx = codes.IndexOf(key);
            if (idx >= 0) box.SelectedIndex = idx;
            else if (box.Items.Count > 0) box.SelectedIndex = 0; // 默认「无」
        }

        private uint KeyOf(ComboBox box, List<uint> codes)
        {
            return (box.SelectedIndex >= 0 && box.SelectedIndex < codes.Count)
                ? codes[box.SelectedIndex] : HotkeyKeys.None;
        }

        private void LoadDevices(CheckedListBox list, bool input, List<string> selected)
        {
            try
            {
                List<AudioDevice> devices = input
                    ? AudioManager.GetRecordingDevices()
                    : AudioManager.GetPlaybackDevices();
                bool hasSelection = selected != null && selected.Count > 0;
                foreach (AudioDevice d in devices)
                {
                    int i = list.Items.Add(d);
                    if (!hasSelection || selected.Contains(d.Id))
                        list.SetItemChecked(i, true);
                }
                if (list.Items.Count == 0)
                    list.Items.Add(input ? "（未检测到输入设备）" : "（未检测到播放设备）", false);
            }
            catch (Exception ex)
            {
                Logger.Error("设置窗口加载设备列表失败（输入=" + input + "）", ex);
                list.Items.Add("（设备列表加载失败，详见日志）", false);
            }
        }

        private static uint ModsOf(bool ctrl, bool alt, bool shift, bool win)
        {
            uint mods = 0;
            if (ctrl) mods |= NativeMethods.MOD_CONTROL;
            if (alt) mods |= NativeMethods.MOD_ALT;
            if (shift) mods |= NativeMethods.MOD_SHIFT;
            if (win) mods |= NativeMethods.MOD_WIN;
            return mods;
        }

        private static string ModsText(uint mods)
        {
            string s = "";
            if ((mods & NativeMethods.MOD_CONTROL) != 0) s += "Ctrl+";
            if ((mods & NativeMethods.MOD_ALT) != 0) s += "Alt+";
            if ((mods & NativeMethods.MOD_SHIFT) != 0) s += "Shift+";
            if ((mods & NativeMethods.MOD_WIN) != 0) s += "Win+";
            return s;
        }

        private void UpdatePreview()
        {
            if (KeyOf(cbKey, playKeys) == HotkeyKeys.None)
            {
                lblPreview.Text = "当前快捷键：（未设置）";
                return;
            }
            uint mods = ModsOf(cbCtrl.Checked, cbAlt.Checked, cbShift.Checked, cbWin.Checked);
            lblPreview.Text = "当前快捷键：" + ModsText(mods) + HotkeyKeys.GetName(KeyOf(cbKey, playKeys));
        }

        private void UpdateInputPreview()
        {
            if (KeyOf(cbInKey, inputKeys) == HotkeyKeys.None)
            {
                lblInPreview.Text = "当前快捷键：（未设置）";
                return;
            }
            uint mods = ModsOf(cbInCtrl.Checked, cbInAlt.Checked, cbInShift.Checked, cbInWin.Checked);
            lblInPreview.Text = "当前快捷键：" + ModsText(mods) + HotkeyKeys.GetName(KeyOf(cbInKey, inputKeys));
        }

        private void DeleteLog()
        {
            try
            {
                if (File.Exists(Logger.LogFilePath))
                {
                    File.Delete(Logger.LogFilePath);
                    lblLogInfo.Text = "日志已删除";
                }
                else
                {
                    lblLogInfo.Text = "日志文件不存在";
                }
            }
            catch (Exception ex)
            {
                lblLogInfo.Text = "删除失败：" + ex.Message;
            }
        }

        private static List<string> CheckedIds(CheckedListBox list)
        {
            List<string> ids = new List<string>();
            foreach (object item in list.CheckedItems)
            {
                AudioDevice d = item as AudioDevice;
                if (d != null) ids.Add(d.Id);
            }
            return ids;
        }

        /// <summary>把界面上的设置写入 TrayToys.json 并立即生效。返回是否成功。</summary>
        private bool ApplySettings()
        {
            // 播放设备
            Settings.Modifiers = ModsOf(cbCtrl.Checked, cbAlt.Checked, cbShift.Checked, cbWin.Checked);
            Settings.Key = KeyOf(cbKey, playKeys);
            Settings.SelectedDevices = CheckedIds(clbDevices);

            // 输入设备
            Settings.InputModifiers = ModsOf(cbInCtrl.Checked, cbInAlt.Checked, cbInShift.Checked, cbInWin.Checked);
            Settings.InputKey = KeyOf(cbInKey, inputKeys);
            Settings.SelectedInputDevices = CheckedIds(clbInputDevices);

            Settings.LogEnabled = cbLog.Checked;
            Settings.AllowScreenOff = cbAllowScreenOff.Checked;
            Settings.DimEnabled = cbDimEnabled.Checked;
            Settings.DimSeconds = (int)numDimSeconds.Value;
            Settings.DimOpacity = (int)numDimOpacity.Value;
            Settings.DimFadeSec = (double)numDimFadeSec.Value;

            Settings.Save();
            Logger.Enabled = Settings.LogEnabled;      // 立即生效
            ScreenKeeper.Apply(Settings.AllowScreenOff); // 立即生效
            ScreenDimmer.Apply(Settings.AllowScreenOff, Settings.DimEnabled,
                Settings.DimSeconds, Settings.DimOpacity, Settings.DimFadeSec); // 立即生效

            Logger.Info("设置已更新: 播放 " + Settings.DisplayName + "（" + Settings.SelectedDevices.Count + " 个设备）"
                + "，输入 " + Settings.InputDisplayName + "（" + Settings.SelectedInputDevices.Count + " 个设备）"
                + "，记录日志 " + (Settings.LogEnabled ? "开" : "关")
                + "，允许自动息屏 " + (Settings.AllowScreenOff ? "是" : "否")
                + "，屏幕变暗 " + DimStateText());
            if (OnSaved != null) OnSaved();
            return true;
        }
    }
}
