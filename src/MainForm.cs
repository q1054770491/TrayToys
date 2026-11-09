using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 主窗体（隐藏）：承载全局热键、系统托盘与设备循环切换逻辑。
    /// 支持两类设备：播放（输出）设备 与 输入（录音）设备，各有一个独立快捷键。
    /// </summary>
    internal class MainForm : Form
    {
        private const int HOTKEY_ID = 9001;        // 播放设备切换
        private const int HOTKEY_INPUT_ID = 9002;  // 输入设备切换

        private NotifyIcon tray;
        private bool regPlayback = false;
        private bool regInput = false;

        public MainForm()
        {
            this.ShowInTaskbar = false;
            this.WindowState = FormWindowState.Minimized;
            this.FormBorderStyle = FormBorderStyle.None;
            this.Text = AppInfo.Name;
        }

        /// <summary>
        /// 隐藏主窗口：设 WS_EX_TOOLWINDOW 并去掉 WS_EX_APPWINDOW，
        /// 让它既不进任务栏、也不出现在 Alt+Tab 切换列表里（仅 ShowInTaskbar=false 不够）。
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW
                cp.ExStyle &= ~0x00040000;  // 去掉 WS_EX_APPWINDOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            InitTray();
            RegisterHotkeys();
            ScreenKeeper.Apply(Settings.AllowScreenOff); // 按配置应用防息屏
            ScreenDimmer.Apply(Settings.AllowScreenOff, Settings.DimEnabled,
                Settings.DimSeconds, Settings.DimOpacity, Settings.DimFadeSec); // 按配置应用屏幕变暗
            SessionResetKeeper.StartIfNeeded(); // 按需看守：仅当有应用处于非默认音量时才轮询（否则托盘待机零枚举）
            DeviceBatteryManager.ScanNowAsync(null); // 后台扫一遍：自动发现能读到电量的无线设备（不写死设备，约 1 秒完成，不阻塞启动）
            // 首次运行（本次刚生成默认配置，主键为「无」）→ 自动打开设置，引导用户指定快捷键
            this.BeginInvoke(new MethodInvoker(delegate
            {
                if (Settings.IsFirstRun) OpenSettings();
            }));
        }

        private void InitTray()
        {
            tray = new NotifyIcon();
            tray.Icon = TrayIconHelper.CreateIcon();
            tray.Text = AppInfo.Name + " v" + AppInfo.Version + " · 设备快切 / 音量 / 电量 / 防息屏";
            tray.Visible = true;

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miSettings = new ToolStripMenuItem("设置...");
            miSettings.Click += delegate { OpenSettings(); };
            ToolStripMenuItem miLog = new ToolStripMenuItem("打开日志文件");
            miLog.Click += delegate { OpenLog(); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("退出");
            miExit.Click += delegate { ExitApp(); };
            menu.Items.Add(miSettings);
            menu.Items.Add(miLog);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { OpenSettings(); };
        }

        private void OpenLog()
        {
            try
            {
                if (!System.IO.File.Exists(Logger.LogFilePath))
                {
                    MessageBox.Show("日志文件不存在。\n\n默认不记录日志，如需排查问题请在「设置」里勾选「记录日志」。",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Process.Start(Logger.LogFilePath);
            }
            catch (Exception ex)
            {
                Logger.Error("打开日志文件失败", ex);
            }
        }

        // ==================== 热键注册（播放 + 输入） ====================

        private void RegisterHotkeys()
        {
            UnregisterHotkeys();
            regPlayback = RegisterOne(HOTKEY_ID, Settings.Modifiers, Settings.Key, Settings.DisplayName, "播放设备");
            regInput = RegisterOne(HOTKEY_INPUT_ID, Settings.InputModifiers, Settings.InputKey, Settings.InputDisplayName, "输入设备");
        }

        private bool RegisterOne(int id, uint mods, uint key, string display, string what)
        {
            if (key == HotkeyKeys.None) return false; // 主键为「无」= 不注册热键

            bool ok = NativeMethods.RegisterHotKey(this.Handle, id, mods | NativeMethods.MOD_NOREPEAT, key);
            if (!ok) ok = NativeMethods.RegisterHotKey(this.Handle, id, mods, key);
            if (ok)
            {
                Logger.Info(what + "快捷键注册成功: " + display);
            }
            else
            {
                Logger.Error(what + "快捷键注册失败: " + display + "（可能被其它程序占用）", null);
                MessageBox.Show(what + "快捷键 " + display + " 注册失败，可能被其它程序占用，请在设置中更换组合。",
                    "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return ok;
        }

        private void UnregisterHotkeys()
        {
            if (regPlayback)
            {
                NativeMethods.UnregisterHotKey(this.Handle, HOTKEY_ID);
                regPlayback = false;
            }
            if (regInput)
            {
                NativeMethods.UnregisterHotKey(this.Handle, HOTKEY_INPUT_ID);
                regInput = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id == HOTKEY_ID) CycleDevice(false);
                else if (id == HOTKEY_INPUT_ID) CycleDevice(true);
            }
            base.WndProc(ref m);
        }

        // ==================== 核心切换逻辑（播放/输入共用） ====================

        /// <summary>
        /// 枚举设备 → 过滤出“参与循环”的设备 → 找到当前默认设备 → 切换到下一个 → 弹浮窗。
        /// </summary>
        /// <param name="input">true 处理输入（录音）设备，false 处理播放（输出）设备。</param>
        private void CycleDevice(bool input)
        {
            string what = input ? "输入设备" : "播放设备";
            try
            {
                List<AudioDevice> all = input
                    ? AudioManager.GetRecordingDevices()
                    : AudioManager.GetPlaybackDevices();
                if (all.Count == 0)
                {
                    Logger.Error("切换" + what + "失败: 未找到任何" + what, null);
                    return;
                }

                // 参与循环的设备 = 设置里勾选的；未配置/全不勾 时回退为全部
                List<string> selected = input ? Settings.SelectedInputDevices : Settings.SelectedDevices;
                List<AudioDevice> cycle = new List<AudioDevice>();
                if (selected != null && selected.Count > 0)
                {
                    foreach (AudioDevice d in all)
                        if (selected.Contains(d.Id)) cycle.Add(d);
                }
                if (cycle.Count == 0) cycle = all;

                string current = AudioManager.GetDefaultDeviceId(input);
                int idx = -1;
                for (int i = 0; i < cycle.Count; i++)
                    if (cycle[i].Id == current) { idx = i; break; }

                int next;
                if (idx >= 0) next = (idx + 1) % cycle.Count;
                else next = 0; // 当前设备不在循环列表里，切到第一个勾选设备

                AudioManager.SetDefaultDevice(cycle[next].Id);
                Logger.Info("切换" + what + ": " + (idx >= 0 ? cycle[idx].Name : "当前设备不在循环列表")
                    + " -> " + cycle[next].Name);
                ToastForm.ShowToast((input ? "输入设备已切换为：" : "已切换到：") + cycle[next].Name, Settings.ToastMs);
            }
            catch (Exception ex)
            {
                Logger.Error("切换" + what + "异常", ex);
                ToastForm.ShowToast("切换失败：" + ex.Message, Settings.ToastMs);
            }
        }

        // ==================== 设置窗口（单实例） ====================

        private SettingsForm _settings;

        private void OpenSettings()
        {
            // 单实例：已打开则直接置前聚焦，不重复弹出
            if (_settings != null && !_settings.IsDisposed)
            {
                if (_settings.WindowState == FormWindowState.Minimized)
                    _settings.WindowState = FormWindowState.Normal;
                _settings.Activate();
                _settings.Focus();
                return;
            }

            _settings = new SettingsForm();
            _settings.OnSaved += delegate { RegisterHotkeys(); };
            _settings.FormClosed += delegate(object s, FormClosedEventArgs e)
            {
                if (ReferenceEquals(_settings, s)) _settings = null;
            };
            _settings.Show(this);
            _settings.Activate(); // 打开即聚焦
            _settings.Focus();
        }

        public void ExitApp()
        {
            UnregisterHotkeys();
            ScreenDimmer.Shutdown(); // 撤掉可能的变暗遮罩并停止检测
            SessionResetKeeper.Stop(); // 停止应用音量守卫
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            Logger.Info("程序退出");
            Close();
        }
    }
}
