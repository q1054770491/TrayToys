using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>入口：单实例、全局异常兜底、自检模式。</summary>
    internal static class Program
    {
        private static DateTime lastCrashAt = DateTime.MinValue;
        private static string lastCrashKey = "";

        /// <summary>诊断模式用：崩溃时只写文件、不弹框（否则无人值守的测试会卡住）。</summary>
        internal static bool SuppressCrashUi = false;

        /// <summary>
        /// 崩溃提示：同一个错误 5 秒内只弹一次框。
        /// 若某段每秒执行一次的代码持续抛异常，不去重会疯狂弹窗，用户看到的就是
        /// "程序卡死了" —— 反而掩盖了真正的错误信息。
        /// </summary>
        private static void ShowCrashOnce(string title, Exception ex)
        {
            string key = ex == null ? "(null)" : ex.GetType().Name + "|" + ex.Message;
            DateTime now = DateTime.Now;
            bool repeat = (key == lastCrashKey && (now - lastCrashAt).TotalSeconds < 5);
            lastCrashKey = key;
            lastCrashAt = now;
            if (repeat) return;
            if (SuppressCrashUi) return; // 诊断模式：只留文件，不阻塞

            try
            {
                MessageBox.Show(title + "：" + (ex == null ? "未知错误" : ex.Message)
                    + "\n\n崩溃记录已写入（无论是否开启「记录日志」都会写）：\n" + Logger.CrashFilePath,
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 明确让 UI 线程异常走下面的 ThreadException 处理器。
            // 不显式设置时，某些情况下会走系统的 JIT 调试对话框，窗口一闪而过，
            // 用户根本来不及看清程序就没了 —— 这正是"不知道为什么就死掉了"的常见形态。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            // 统一先加载配置（含日志开关、2.4G 电量协议表）。
            // 否则诊断分支里的 Logger.Enabled = true 会被 Settings.Load 里的 Apply 覆盖回配置值。
            Settings.EnsureLoaded();

            // 诊断模式一律关闭会话枚举的短时缓存：
            // 诊断要的是"每次调用都真的去枚举"，否则"设完立即读回"会读到缓存里的旧值。
            if (args.Length > 0 && args[0].StartsWith("--test")) AudioSessionManager.CacheEnabled = false;

            // 兜底：任何未捕获异常都写入崩溃日志（TrayToys_crash.log）并弹友好提示，而不是 JIT 调试对话框。
            // 崩溃日志**不受「记录日志」开关影响**（Logger.Crash 强制落盘），
            // 否则用户没开日志时崩溃将不留任何线索。
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                Logger.Crash("UI 线程未处理异常", e.Exception);
                ShowCrashOnce("发生未处理错误", e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                Logger.Crash("后台线程未处理异常（进程将退出）", ex);
                ShowCrashOnce("程序发生致命错误，即将退出", ex);
            };

            // 性能自检：测高频函数的单次耗时，折算成"每秒占用多少 CPU"
            if (args.Length > 0 && args[0] == "--test-perf")
            {
                Logger.Enabled = true;
                for (int i = 0; i < 3; i++) { AudioSessionManager.GetSessions(); FullscreenWatcher.IsBusy(); } // 预热

                int n = 200;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++) AudioSessionManager.GetSessions();
                sw.Stop();
                double tSessions = sw.Elapsed.TotalMilliseconds / n;

                sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++) FullscreenWatcher.IsBusy();
                sw.Stop();
                double tBusy = sw.Elapsed.TotalMilliseconds / n;

                sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < n; i++) AudioSessionManager.GetDeviceVolume();
                sw.Stop();
                double tVol = sw.Elapsed.TotalMilliseconds / n;

                int cnt = AudioSessionManager.GetSessions().Count;
                Logger.Info("微基准（每项 " + n + " 次，当前会话数 " + cnt + "）:");
                Logger.Info("  GetSessions     = " + tSessions.ToString("0.000") + " ms");
                Logger.Info("  FullscreenWatcher.IsBusy = " + tBusy.ToString("0.000") + " ms");
                Logger.Info("  GetDeviceVolume = " + tVol.ToString("0.000") + " ms");
                Logger.Info("  折算常驻开销: 守卫(1/s) " + (tSessions / 10).ToString("0.00")
                    + "% + 变暗(33/s) " + (tBusy * 33 / 10).ToString("0.00") + "%");
                return;
            }

            // 崩溃兜底自检：在后台线程故意抛一个未捕获异常，
            // 验证「后台线程异常 → 写崩溃日志」这条链路真的有效（否则下次崩溃依然查不到原因）。
            // 注意：.NET 里后台线程的未捕获异常会让进程终止，这里故意如此。
            if (args.Length > 0 && args[0] == "--test-crash")
            {
                SuppressCrashUi = true;
                Logger.Enabled = true;
                System.Threading.Thread t = new System.Threading.Thread(delegate()
                {
                    throw new InvalidOperationException("这是一条测试异常（--test-crash）：用于验证崩溃日志会落盘");
                });
                t.IsBackground = true;
                t.Name = "CrashSelfTest";
                t.Start();
                t.Join(3000);
                return;
            }

            // 自检模式：仅验证浮窗可正常创建（用于发布前验证），不进入主程序
            if (args.Length > 0 && args[0] == "--selftest")
            {
                Logger.Enabled = true; // 诊断模式强制写日志
                try
                {
                    ToastForm t = new ToastForm("已切换到：自检设备（扬声器）", 500);
                    t.Dispose();
                    Logger.Info("自检通过: 浮窗可正常创建");
                    return; // 构造成功 = 通过
                }
                catch (Exception ex)
                {
                    Logger.Error("自检失败: 浮窗创建异常", ex);
                    Environment.Exit(1);
                }
            }

            // 设置窗口测试模式：直接显示设置窗口（可带页签序号，用于验证 UI 渲染），不进入主程序
            if (args.Length > 0 && args[0] == "--test-settings")
            {
                Logger.Enabled = true;
                DeviceBatteryManager.ScanNow(); // 诊断模式下先把电量扫好，便于检查「设备电量」页
                SettingsForm f = new SettingsForm();
                if (args.Length > 1)
                {
                    int idx;
                    if (int.TryParse(args[1], out idx)) f.SelectTab(idx);
                }
                Application.Run(f);
                return;
            }


            // 设备列表模式：仅枚举播放/输入设备并写日志（不切换，用于排查设备名读取问题）
            if (args.Length > 0 && args[0] == "--test-list")
            {
                Logger.Enabled = true; // 诊断模式强制写日志
                try
                {
                    List<AudioDevice> plays = AudioManager.GetPlaybackDevices();
                    Logger.Info("播放设备枚举成功，共 " + plays.Count + " 个:");
                    for (int i = 0; i < plays.Count; i++)
                        Logger.Info("  播放[" + i + "]: " + plays[i].Name + "  (" + plays[i].Id + ")");

                    List<AudioDevice> recs = AudioManager.GetRecordingDevices();
                    Logger.Info("输入设备枚举成功，共 " + recs.Count + " 个:");
                    for (int i = 0; i < recs.Count; i++)
                        Logger.Info("  输入[" + i + "]: " + recs[i].Name + "  (" + recs[i].Id + ")");

                    string defPlay = AudioManager.GetDefaultDeviceId(false);
                    string defRec = AudioManager.GetDefaultDeviceId(true);
                    Logger.Info("当前默认播放设备: " + defPlay);
                    Logger.Info("当前默认输入设备: " + defRec);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("设备枚举失败", ex);
                    Environment.Exit(1);
                }
            }

            // 音频会话模式：枚举所有应用音频会话（音量合成器那套），验证会话 API 是否可用
            if (args.Length > 0 && args[0] == "--test-sessions")
            {
                Logger.Enabled = true; // 诊断模式强制写日志
                try
                {
                    float devVol = AudioSessionManager.GetDeviceVolume();
                    Logger.Info("默认播放设备总音量: " + (int)Math.Round(devVol * 100) + "%");

                    List<AudioSessionInfo> sessions = AudioSessionManager.GetSessions();
                    Logger.Info("音频会话枚举成功，共 " + sessions.Count + " 个:");
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        AudioSessionInfo s = sessions[i];
                        Logger.Info("  [" + i + "] " + s.ProcessName
                            + " | " + s.StateText
                            + " | 音量 " + (int)Math.Round(s.Volume * 100) + "%"
                            + (s.Muted ? "（静音）" : "")
                            + " | pid=" + s.ProcessId
                            + (s.VolumeAccessible ? "" : " | 音量不可控")
                            + " | exe=" + (s.ExePath == null ? "(取不到)" : s.ExePath));
                    }
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("音频会话枚举失败", ex);
                    Environment.Exit(1);
                }
            }

            // 音量写入闭环验证：找一个非系统会话，设 50% → 重新枚举读回 → 恢复原值
            if (args.Length > 0 && args[0] == "--test-session-set")
            {
                Logger.Enabled = true; // 诊断模式强制写日志
                try
                {
                    List<AudioSessionInfo> sessions = AudioSessionManager.GetSessions();
                    AudioSessionInfo target = null;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        if (!sessions[i].IsSystemSounds && sessions[i].VolumeControl != null)
                        {
                            target = sessions[i];
                            break;
                        }
                    }
                    if (target == null)
                    {
                        Logger.Info("闭环验证跳过：当前没有可控制的音频会话");
                        return;
                    }

                    float original = target.Volume;
                    Logger.Info("目标会话: " + target.ProcessName + "，当前音量 " + (int)Math.Round(original * 100) + "%");

                    bool okSet = AudioSessionManager.SetVolume(target, 0.5f);
                    Logger.Info("设置为 50% → 调用" + (okSet ? "成功" : "失败"));

                    float readBack = -1f;
                    List<AudioSessionInfo> after = AudioSessionManager.GetSessions();
                    for (int i = 0; i < after.Count; i++)
                    {
                        if (after[i].AppKey == target.AppKey && after[i].VolumeControl != null)
                        {
                            readBack = after[i].Volume;
                            break;
                        }
                    }
                    Logger.Info("重新枚举读回: " + (readBack < 0f ? "(没找到该会话)" : (int)Math.Round(readBack * 100) + "%"));

                    bool okRestore = AudioSessionManager.SetVolume(target, original);
                    Logger.Info("恢复原值 " + (int)Math.Round(original * 100) + "% → 调用" + (okRestore ? "成功" : "失败"));

                    // 顺便验证静音接口
                    bool okMute = AudioSessionManager.SetMute(target, true);
                    Logger.Info("设置静音 → 调用" + (okMute ? "成功" : "失败"));
                    bool muteBack = false;
                    List<AudioSessionInfo> muted = AudioSessionManager.GetSessions();
                    for (int i = 0; i < muted.Count; i++)
                    {
                        if (muted[i].AppKey == target.AppKey) { muteBack = muted[i].Muted; break; }
                    }
                    Logger.Info("重新枚举读回静音状态: " + (muteBack ? "已静音" : "未静音"));
                    bool okUnmute = AudioSessionManager.SetMute(target, false);
                    Logger.Info("取消静音 → 调用" + (okUnmute ? "成功" : "失败"));
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("音量闭环验证失败", ex);
                    Environment.Exit(1);
                }
            }

            // 按进程名设置会话音量（只设不改回），用于验证"应用重开后重置"逻辑
            if (args.Length > 2 && args[0] == "--test-session-vol")
            {
                Logger.Enabled = true;
                try
                {
                    string wantName = args[1];
                    int pct = int.Parse(args[2]);
                    List<AudioSessionInfo> sessions = AudioSessionManager.GetSessions();
                    int done = 0;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        AudioSessionInfo s = sessions[i];
                        if (s.State == (int)SessionState.Expired) continue;
                        if (s.ProcessName == null) continue;
                        if (s.ProcessName.IndexOf(wantName, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        AudioSessionManager.SetVolume(s, pct / 100f);
                        done++;
                        Logger.Info("已把 " + s.ProcessName + " 的音量设为 " + pct + "%");
                    }
                    if (done == 0) Logger.Info("没找到进程名包含「" + wantName + "」的会话");
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Error("按进程名设置音量失败", ex);
                    Environment.Exit(1);
                }
            }

            // 电量扫描诊断：枚举能读到电量的无线设备（不写死设备，按实际数据来）并写日志
            if (args.Length > 0 && args[0] == "--test-battery")
            {
                Logger.Enabled = true; // 诊断模式强制写日志
                DeviceBatteryManager.ScanNow();
                return;
            }

            // 设备节点状态诊断：原样打印每个带电量属性的设备节点（不去重、不过滤）
            if (args.Length > 0 && args[0] == "--test-btdump")
            {
                Logger.Enabled = true;
                DeviceBatteryManager.DumpBatteryCandidates();
                return;
            }

            // 配置保存诊断：调用一次 Settings.Save()，用于检查 TrayToys.json 里 hidProtocols 的格式
            if (args.Length > 0 && args[0] == "--test-saveconfig")
            {
                Logger.Enabled = true;
                Settings.Save();
                return;
            }

            // 智能识别诊断：--test-identify <VID十六进制> <官方电量>
            // 走「简化界面」那条路：按设备合并接口 → 全自动遍历 → 输出所有候选与耗时
            if (args.Length >= 3 && args[0] == "--test-identify")
            {
                Logger.Enabled = true;
                ushort vid = 0;
                int level = 0;
                try
                {
                    vid = ushort.Parse(args[1], System.Globalization.NumberStyles.HexNumber);
                    level = int.Parse(args[2]);
                }
                catch
                {
                    Logger.Info("用法: --test-identify <VID十六进制> <电量>   例: --test-identify 41E4 100");
                    return;
                }

                List<DeviceBatteryManager.HidDeviceGroup> devs = DeviceBatteryManager.EnumerateDevices();
                Logger.Info("=== 枚举到 " + devs.Count + " 个设备（同名接口已合并）===");
                for (int i = 0; i < devs.Count; i++)
                    Logger.Info("  [" + i + "] " + devs[i].DisplayName);

                for (int i = 0; i < devs.Count; i++)
                {
                    if (devs[i].Vid != vid) continue;

                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    DeviceBatteryManager.IdentifyResult res =
                        DeviceBatteryManager.IdentifyBattery(devs[i], level, null, null);
                    sw.Stop();

                    Logger.Info("");
                    Logger.Info("识别 " + devs[i].DisplayName + "（目标 " + level + "%）: 耗时 "
                        + sw.ElapsedMilliseconds + " ms，检查 " + res.InterfacesChecked + "/"
                        + res.InterfacesTotal + " 个接口");
                    Logger.Info("  候选 " + res.Candidates.Count + " 个:");
                    for (int k = 0; k < res.Candidates.Count; k++)
                    {
                        DeviceBatteryManager.BatteryCandidate c = res.Candidates[k];
                        Logger.Info("    " + c.InterfaceLabel + "  第 " + (c.ByteIndex + 1) + " 字节 = " + c.Value);
                    }
                    if (res.TimedOut.Count > 0)
                        Logger.Info("  未响应接口: " + string.Join("、", res.TimedOut.ToArray()));
                }
                return;
            }

            // 自动搜索诊断：--test-autosearch <VID十六进制> <目标电量>
            if (args.Length >= 3 && args[0] == "--test-autosearch")
            {
                Logger.Enabled = true;
                ushort vid = 0;
                int level = 0;
                try
                {
                    vid = ushort.Parse(args[1], System.Globalization.NumberStyles.HexNumber);
                    level = int.Parse(args[2]);
                }
                catch
                {
                    Logger.Info("用法: --test-autosearch <VID十六进制> <电量>   例: --test-autosearch 41E4 100");
                    return;
                }

                List<DeviceBatteryManager.HidInterfaceInfo> list = DeviceBatteryManager.EnumerateInterfaces();
                for (int i = 0; i < list.Count; i++)
                {
                    DeviceBatteryManager.HidInterfaceInfo info = list[i];
                    if (info.Vid != vid) continue;

                    bool useFeature = info.FeatureLen > 0;
                    Logger.Info("搜索接口: " + info.DisplayName + "  模式=" + (useFeature ? "Feature" : "Input"));

                    DeviceBatteryManager.BatterySearchResult res =
                        DeviceBatteryManager.AutoSearchBattery(info.Path, info.InputLen, info.FeatureLen, useFeature, level);

                    string ids = "";
                    for (int k = 0; k < res.ReportIds.Count; k++)
                        ids += (k > 0 ? "," : "") + "0x" + res.ReportIds[k].ToString("X2");
                    Logger.Info("  可读 ReportID: " + (res.ReportIds.Count == 0 ? "（无）" : ids));
                    Logger.Info("  命中 " + res.Matches.Count + " 处:");
                    for (int k = 0; k < res.Matches.Count; k++)
                    {
                        DeviceBatteryManager.BatteryMatch m = res.Matches[k];
                        Logger.Info("    ReportID=" + m.ReportId + "  第 " + (m.ByteIndex + 1) + " 字节 = " + m.Value);
                    }
                }
                return;
            }

            // Feature 报文诊断：--test-feature <VID十六进制> <ReportID十六进制>
            // 输出原始字节（连读两次），用于对比"设备连接 / 未连接"时报文里哪些字节变化。
            if (args.Length >= 3 && args[0] == "--test-feature")
            {
                Logger.Enabled = true;
                ushort vid = 0;
                int rid = 0;
                try
                {
                    vid = ushort.Parse(args[1], System.Globalization.NumberStyles.HexNumber);
                    rid = int.Parse(args[2], System.Globalization.NumberStyles.HexNumber);
                }
                catch
                {
                    Logger.Info("用法: --test-feature <VID十六进制> <ReportID十六进制>，例：--test-feature 41E4 6");
                    return;
                }

                List<DeviceBatteryManager.HidInterfaceInfo> fw = DeviceBatteryManager.EnumerateInterfaces();
                bool any = false;
                for (int i = 0; i < fw.Count; i++)
                {
                    DeviceBatteryManager.HidInterfaceInfo hi = fw[i];
                    if (hi.Vid != vid || hi.FeatureLen <= 0) continue;
                    any = true;
                    for (int round = 0; round < 2; round++)
                    {
                        byte[] rep = DeviceBatteryManager.ReadFeatureReport(hi.Path, hi.FeatureLen, (byte)rid);
                        if (rep == null) { Logger.Info("[" + hi.DisplayName + "] 第 " + (round + 1) + " 次读取失败"); continue; }
                        string hex = "";
                        for (int k = 0; k < rep.Length; k++) hex += rep[k].ToString("X2") + " ";
                        Logger.Info("[" + hi.DisplayName + "] 第 " + (round + 1) + " 次: " + hex.Trim());
                        System.Threading.Thread.Sleep(800);
                    }
                }
                if (!any) Logger.Info("没找到 VID=" + vid.ToString("X4") + " 且支持 Feature 报告的接口");
                return;
            }

            // 接口探测诊断：枚举可配置接口、探测 Feature ReportID、读一次 Input 报告
            if (args.Length > 0 && args[0] == "--test-probe")
            {
                Logger.Enabled = true;
                List<DeviceBatteryManager.HidInterfaceInfo> list = DeviceBatteryManager.EnumerateInterfaces();
                Logger.Info("=== 枚举到 " + list.Count + " 个可配置接口 ===");
                for (int i = 0; i < list.Count; i++)
                {
                    DeviceBatteryManager.HidInterfaceInfo info = list[i];
                    Logger.Info("[" + i + "] " + info.DisplayName);

                    if (info.FeatureLen > 0)
                    {
                        List<int> ids = DeviceBatteryManager.ProbeFeatureReportIds(info.Path, info.FeatureLen);
                        string s = "";
                        for (int k = 0; k < ids.Count; k++) s += (k > 0 ? "," : "") + "0x" + ids[k].ToString("X2");
                        Logger.Info("    Feature 可读 ReportID: " + (ids.Count == 0 ? "（无）" : s));
                    }

                    if (info.InputLen > 0)
                    {
                        byte[] r = DeviceBatteryManager.ReadInputReport(info.Path, info.InputLen);
                        if (r != null)
                        {
                            string hex = "";
                            for (int k = 0; k < r.Length; k++) hex += r[k].ToString("X2") + " ";
                            Logger.Info("    Input 报告: " + hex.Trim());
                        }
                        else Logger.Info("    Input 报告: 读不到");
                    }
                }
                return;
            }

            // 配置向导诊断：打开「添加设备」窗口，自绘整窗截图后关闭（验证布局是否完整）
            if (args.Length > 0 && args[0] == "--test-batterysetup")
            {
                Logger.Enabled = true;
                BatterySetupForm f = new BatterySetupForm();
                f.Show();
                f.Refresh();
                System.Windows.Forms.Application.DoEvents();
                try
                {
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                        string dir = System.IO.Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
                        string p = System.IO.Path.Combine(dir, "setup_self.png");
                        bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
                        Logger.Info("[诊断] 自绘截图: " + p + "  (" + f.Width + "x" + f.Height + ")");
                    }
                }
                catch (Exception ex) { Logger.Error("[诊断] 自绘失败", ex); }
                f.Close();
                return;
            }

            // 简化版「添加设备」界面诊断：打开并自绘截图（验证布局是否完整）
            if (args.Length > 0 && args[0] == "--test-simpledevice")
            {
                Logger.Enabled = true;
                DeviceSetupSimpleForm f = new DeviceSetupSimpleForm();
                f.Show();
                f.Refresh();
                Application.DoEvents();
                try
                {
                    using (System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(f.Width, f.Height))
                    {
                        f.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, f.Width, f.Height));
                        string dir = System.IO.Path.GetDirectoryName(Application.ExecutablePath);
                        string p = System.IO.Path.Combine(dir, "simple_self.png");
                        bmp.Save(p, System.Drawing.Imaging.ImageFormat.Png);
                        Logger.Info("[诊断] 自绘截图: " + p + "  (" + f.Width + "x" + f.Height + ")");
                    }
                }
                catch (Exception ex) { Logger.Error("[诊断] 自绘失败", ex); }
                f.Close();
                return;
            }

            bool createdNew;
            using (Mutex m = new Mutex(true, AppInfo.Name + "_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    Logger.Info("检测到程序已在运行，本次启动退出");
                    MessageBox.Show(AppInfo.Name + " 已经在运行了。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Logger.Info("程序启动，当前快捷键: " + Settings.DisplayName);
                Application.Run(new MainForm());
            }
        }
    }
}
