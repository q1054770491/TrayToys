using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 「应用关闭后重新打开 → 音量自动恢复 100%」的常驻守卫。
    ///
    /// 背景：Windows 本身会按「应用 + 设备」记住每个应用的音量，应用重启时自动恢复上次的值。
    /// 本功能反其道而行：只要这个应用曾经被关掉过，下次它一开起来就立刻拉回满音量。
    ///
    /// 实现要点：
    ///   · 每秒枚举一次音频会话（开销很小），常驻运行，不依赖设置窗口是否打开；
    ///   · 用 SessionIdentifier 判断"是不是新出现的会话"（每新建一次会话它都不同）；
    ///   · 用 AppKey（exe 完整路径）判断"是不是同一个应用"—— SessionIdentifier 里带 PID，
    ///     每次启动都会变，不能用来跨会话识别同一应用；
    ///   · 会话消失（应用退出 / 会话过期）⇒ 记入"待重置"名单；
    ///     该应用的新会话出现 ⇒ 立刻把音量设回 100% 并取消静音。
    ///
    /// 注意：「暂停播放」只是会话状态 Active→Inactive，会话仍然存在，不会触发重置；
    /// 只有应用真的退出（会话消失/过期）才算"关闭"。
    /// </summary>
    internal static class SessionResetKeeper
    {
        private const int TickMs = 2000;

        private static Timer timer;
        private static bool running;

        private static readonly HashSet<string> knownSessions = new HashSet<string>();
        private static readonly Dictionary<string, string> sessionAppKey = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> appNames = new Dictionary<string, string>();
        private static readonly Dictionary<string, string> pendingReset = new Dictionary<string, string>();

        /// <summary>启动常驻监听（主程序启动时调用一次）。</summary>
        public static void Start()
        {
            if (running) return;
            try
            {
                Prime(); // 先把已存在的会话记为"已知"，避免把启动瞬间正在播放的音频当成"重开"
                if (timer == null)
                {
                    timer = new Timer();
                    timer.Interval = TickMs;
                    timer.Tick += OnTick;
                }
                timer.Start();
                running = true;
                Logger.Info("应用音量守卫已启动（应用关闭后再打开将自动恢复 100%）");
            }
            catch (Exception ex)
            {
                Logger.Error("启动应用音量守卫失败", ex);
            }
        }

        /// <summary>
        /// **按需启动**：只有当前确实存在"非默认音量"的会话时才需要看守。
        ///
        /// 为什么要这样：守卫靠每秒枚举音频会话来判断"应用被关掉/重开"，而
        /// 枚举一次约 5ms —— 托盘常驻时这属于纯开销。但绝大多数时间所有应用都是
        /// 100% 音量、没有任何东西需要重置，此时完全没有必要轮询：
        ///   · 全部为默认音量 → 不启动，托盘待机时**一次都不枚举**；
        ///   · 有应用被调低音量/静音 → 启动守卫，监测它何时被关闭。
        /// 用户之后通过界面改音量时，由 NotifyVolumeChanged() 重新拉起。
        /// </summary>
        public static void StartIfNeeded()
        {
            try
            {
                if (running) return;
                List<AudioSessionInfo> sessions = AudioSessionManager.GetSessions();
                if (HasNonDefaultVolume(sessions))
                {
                    Logger.Info("检测到有应用处于非默认音量，启动应用音量守卫");
                    Start();
                }
                else
                {
                    Logger.Info("应用音量守卫待命（当前所有应用均为默认音量，不轮询）");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("检查是否需要启动应用音量守卫失败", ex);
            }
        }

        /// <summary>用户通过本程序把某个应用改成非默认音量 → 需要看守它何时被关闭。</summary>
        public static void NotifyVolumeChanged()
        {
            if (running) return;
            Logger.Info("检测到音量被调整，启动应用音量守卫");
            Start();
        }

        /// <summary>是否存在"非默认音量"的会话（音量 &lt; 100% 或已静音）。</summary>
        private static bool HasNonDefaultVolume(List<AudioSessionInfo> sessions)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionInfo s = sessions[i];
                if (s.State == (int)SessionState.Expired) continue;
                if (s.Muted) return true;
                if (s.VolumeControl != null && s.Volume < 0.995f) return true;
            }
            return false;
        }

        /// <summary>停止监听（没事可做时自动调用；程序退出时也会调用）。</summary>
        public static void Stop()
        {
            running = false;
            if (timer != null && timer.Enabled) timer.Stop();
            // 清掉"已知会话"基线：下次启动时由 Prime() 重新建立，
            // 避免留下已经消失的旧标识、导致下次误判成"应用刚被关闭"。
            knownSessions.Clear();
            sessionAppKey.Clear();
        }

        /// <summary>把当前已存在的会话标记为已知。</summary>
        private static void Prime()
        {
            List<AudioSessionInfo> sessions = AudioSessionManager.GetSessions();
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionInfo s = sessions[i];
                if (s.State == (int)SessionState.Expired) continue;
                knownSessions.Add(s.SessionIdentifier);
                sessionAppKey[s.SessionIdentifier] = s.AppKey;
                appNames[s.AppKey] = DisplayName(s);
            }
        }

        private static void OnTick(object sender, EventArgs e)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Logger.Error("应用音量守卫异常，已停止（不影响其它功能）", ex);
                Stop();
            }
        }

        private static void Tick()
        {
            List<AudioSessionInfo> sessions = AudioSessionManager.GetSessions();
            HashSet<string> nowIds = new HashSet<string>();

            // ① 新出现的会话：若该应用在"待重置"名单里，就把音量拉回 100%
            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionInfo s = sessions[i];
                if (s.State == (int)SessionState.Expired) continue;

                string id = s.SessionIdentifier;
                nowIds.Add(id);

                if (knownSessions.Add(id)) // Add 返回 true 表示之前没见过
                {
                    sessionAppKey[id] = s.AppKey;
                    appNames[s.AppKey] = DisplayName(s);

                    string name;
                    if (pendingReset.TryGetValue(s.AppKey, out name))
                    {
                        AudioSessionManager.SetVolume(s, 1f);
                        AudioSessionManager.SetMute(s, false);
                        pendingReset.Remove(s.AppKey);
                        Logger.Info("应用重新打开，音量已恢复 100%: " + (name == null ? s.AppKey : name));
                    }
                }
            }

            // ② 消失的会话：说明应用被关掉了，记入"待重置"名单
            List<string> gone = new List<string>();
            foreach (string id in knownSessions)
            {
                if (!nowIds.Contains(id)) gone.Add(id);
            }
            for (int i = 0; i < gone.Count; i++)
            {
                string id = gone[i];
                string appKey;
                if (sessionAppKey.TryGetValue(id, out appKey))
                {
                    sessionAppKey.Remove(id);
                    if (!pendingReset.ContainsKey(appKey))
                    {
                        string name;
                        appNames.TryGetValue(appKey, out name);
                        pendingReset[appKey] = name;
                        Logger.Info("应用已关闭，下次打开将恢复 100%: " + (name == null ? appKey : name));
                    }
                }
                knownSessions.Remove(id);
            }

            // 没有任何需要看守的事情就暂停自己：没有待重置项、且所有会话都已回到默认音量。
            // 于是一轮"改音量 → 应用关闭 → 重开恢复"走完后，就会自动回到零轮询状态。
            if (pendingReset.Count == 0 && !HasNonDefaultVolume(sessions))
            {
                Logger.Info("应用音量守卫暂停轮询（当前没有需要重置的应用）");
                Stop();
            }
        }

        private static string DisplayName(AudioSessionInfo s)
        {
            return s.IsSystemSounds ? "系统提示音" : s.ProcessName;
        }
    }
}
