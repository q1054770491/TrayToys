using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 屏幕变暗（防息屏的补充功能）：
    /// 在「不允许自动息屏」期间，若持续 N 秒没有鼠标/键盘输入，就把屏幕**渐变**压暗；
    /// 一旦检测到输入，立即**渐变**恢复。属于"屏幕不会自动关闭，但长期无操作时先变暗"的做法。
    ///
    /// 实现要点：
    ///   - 用 GetLastInputInfo 取系统空闲时长（无钩子，不干扰正常输入）；
    ///   - 定时器每 30ms 跑一帧：先算出"这一刻的目标暗度"（到点=目标暗度，否则=0），
    ///     再把当前暗度朝目标值移动（每帧移动 TickMs/fadeMs），从而形成淡入淡出；
    ///     中途反向（例如淡入到一半用户动了鼠标）也会自然地转成淡出；
    ///   - 变暗 = 每个显示器叠一个纯黑遮罩窗（DimForm），只改它的 Opacity；
    ///   - 允许自动息屏时该功能不生效（系统会自己关屏，变暗没有意义）。
    /// </summary>
    internal static class ScreenDimmer
    {
        private const int TickMs = 30;          // 渐变进行中的帧间隔（≈33fps，足够平滑）
        private const int IdleTickMs = 250;     // 稳定状态下的轮询间隔（不做渐变时没必要 33fps）
        private const int ResumeSlackMs = 300;  // 恢复判定余量，避免临界处反复横跳
        private const int DefaultFadeMs = 400;  // 默认渐变时长

        private static Timer timer;
        private static bool active;             // 功能是否生效
        private static int thresholdMs;         // 触发变暗的空闲时长
        private static int fadeMs = DefaultFadeMs;
        private static double targetOpacity;    // 目标暗度 0~1（由设置里的百分比换算）
        private static double currentOpacity;   // 当前暗度 0~1（逐帧推进）
        private static double lastTarget = -1;  // 上一次的目标值（用于日志去重）
        private static bool lastBusy;           // 上一次的"是否有全屏应用"（用于日志去重）
        private static readonly List<DimForm> covers = new List<DimForm>();

        /// <summary>
        /// 按配置应用。
        /// allowScreenOff = true（允许自动息屏）时本功能一律不生效。
        /// </summary>
        /// <param name="seconds">无操作多少秒后开始变暗。</param>
        /// <param name="opacityPct">目标暗度百分比（10~95）。</param>
        /// <param name="fadeSecValue">渐变时长（秒，0 = 立即，不渐变）。</param>
        public static void Apply(bool allowScreenOff, bool dimEnabled, int seconds, int opacityPct, double fadeSecValue)
        {
            try
            {
                if (seconds < 1) seconds = 60;
                thresholdMs = seconds * 1000;
                int pct = (opacityPct >= 10 && opacityPct <= 95) ? opacityPct : 70;
                targetOpacity = pct / 100.0;
                fadeMs = (fadeSecValue >= 0 && fadeSecValue <= 5)
                    ? (int)Math.Round(fadeSecValue * 1000) : DefaultFadeMs;

                active = dimEnabled && !allowScreenOff;
                if (!active)
                {
                    StopTimer();
                    ResetNow();
                    if (dimEnabled && allowScreenOff)
                        Logger.Info("屏幕变暗: 已勾选但暂不生效（当前允许自动息屏）");
                    else
                        Logger.Info("屏幕变暗: 未启用");
                    return;
                }

                StartTimer();
                Logger.Info("屏幕变暗: 已启用（无操作 " + seconds + " 秒后变暗，暗度 " + pct + "%，渐变 "
                    + FadeText + "）");
            }
            catch (Exception ex)
            {
                active = false;
                StopTimer();
                ResetNow();
                Logger.Error("应用屏幕变暗设置失败", ex);
            }
        }

        /// <summary>程序退出前调用：停止检测并立即撤掉遮罩。</summary>
        public static void Shutdown()
        {
            active = false;
            StopTimer();
            ResetNow();
        }

        /// <summary>当前是否处于变暗（含渐变中）。</summary>
        public static bool IsDimmed
        {
            get { return currentOpacity > 0.0; }
        }

        /// <summary>渐变时长的可读文案（秒）。</summary>
        private static string FadeText
        {
            get { return fadeMs > 0 ? (fadeMs / 1000.0).ToString("0.##") + " 秒" : "关闭（立即）"; }
        }

        // ==================== 定时器 ====================

        private static void StartTimer()
        {
            if (timer == null)
            {
                timer = new Timer();
                timer.Interval = TickMs;
                timer.Tick += OnTick;
            }
            if (!timer.Enabled) timer.Start();
        }

        private static void StopTimer()
        {
            if (timer != null && timer.Enabled) timer.Stop();
        }

        /// <summary>每帧：算出目标暗度 → 把当前暗度朝目标推进一格 → 应用到遮罩窗。</summary>
        private static void OnTick(object sender, EventArgs e)
        {
            try
            {
                uint idle = GetIdleMs();

                // 有全屏应用在前台（看视频 / 玩游戏 / PPT 放映）时视为"有活动"：
                // 等效于"刚刚有输入"，因此不会进入变暗。
                //
                // 【重要】只在"尚未变暗"时才做这个检测。原因：我们自己的变暗遮罩本身就是一个
                // 覆盖全屏的顶层窗口，会被 SHQueryUserNotificationState 判定成"全屏应用"——
                // 若不加这个限制，遮罩一显示就会把检测结果顶成"有活动"，于是立刻取消变暗、
                // 遮罩消失、空闲又继续累计……形成来回闪烁的死循环（实测每 65ms 一轮）。
                // 遮罩的退出条件仍然只有"有输入"这一条，逻辑清晰；
                // 而用户要全屏看视频必然伴随鼠标/键盘操作，那时会先恢复、恢复后检测到全屏不再变暗。
                bool busy = currentOpacity <= 0.0 && IsFullscreenBusyCached();
                if (busy != lastBusy)
                {
                    Logger.Info(busy
                        ? "检测到全屏应用（" + FullscreenWatcher.StateName() + "），暂停屏幕变暗"
                        : "全屏应用已退出，恢复屏幕变暗判定");
                    lastBusy = busy;
                }
                if (busy) idle = 0;

                // 到点该暗就暗，有输入（空闲时长回落到阈值以下）就该亮
                bool shouldDim = idle >= (uint)thresholdMs;
                if (currentOpacity > 0.0 && idle + ResumeSlackMs < (uint)thresholdMs)
                    shouldDim = false;
                double target = shouldDim ? targetOpacity : 0.0;

                if (target != lastTarget)
                {
                    if (target > 0.0)
                    {
                        DimForm.HideCursor = true; // 鼠标指针也一起"暗下去"（系统指针不受窗口透明度影响）
                        Logger.Info("屏幕开始变暗（渐变 " + FadeText + "，目标暗度 " + (int)Math.Round(target * 100) + "%）");
                    }
                    else if (lastTarget > 0.0)
                    {
                        DimForm.HideCursor = false; // 一检测到输入，指针立即恢复显示，避免找不到鼠标
                        Logger.Info("屏幕开始恢复（检测到鼠标/键盘输入，渐变 " + FadeText + "）");
                    }
                    lastTarget = target;
                }

                // 每帧推进量：fadeMs 为「从全亮到目标暗度」所需时间
                double step = fadeMs <= 0 ? 1.0 : (double)TickMs / fadeMs;
                if (currentOpacity < target) currentOpacity = Math.Min(target, currentOpacity + step);
                else if (currentOpacity > target) currentOpacity = Math.Max(target, currentOpacity - step);

                if (currentOpacity > 0.0)
                {
                    EnsureCovers();
                    SetOpacity(currentOpacity);
                }
                else
                {
                    CloseCovers();
                }

                // 动态帧率：只有"还在渐变"时才用 30ms（保证平滑）；
                // 稳定状态（全亮、或已到达目标暗度）降到 250ms 轮询即可，
                // 空闲时对 CPU 的占用降到约 1/8。
                int want = (Math.Abs(currentOpacity - target) > 0.001) ? TickMs : IdleTickMs;
                if (timer != null && timer.Interval != want) timer.Interval = want;
            }
            catch (Exception ex)
            {
                Logger.Error("屏幕变暗检测异常，已停止该功能", ex);
                active = false;
                StopTimer();
                ResetNow();
            }
        }

        private static DateTime busyCheckedAt = DateTime.MinValue;
        private static bool busyCached;

        /// <summary>
        /// 全屏检测带 500ms 缓存。
        /// 它要调 shell32 的 SHQueryUserNotificationState，而"是否全屏"不可能 30ms 变一次；
        /// 渐变期间每帧都查纯属浪费。
        /// </summary>
        private static bool IsFullscreenBusyCached()
        {
            if ((DateTime.Now - busyCheckedAt).TotalMilliseconds < 500) return busyCached;
            busyCached = FullscreenWatcher.IsBusy();
            busyCheckedAt = DateTime.Now;
            return busyCached;
        }

        /// <summary>系统距最后一次鼠标/键盘输入的毫秒数。</summary>
        private static uint GetIdleMs()
        {
            NativeMethods.LASTINPUTINFO lii = new NativeMethods.LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.LASTINPUTINFO));
            if (!NativeMethods.GetLastInputInfo(ref lii)) return 0;
            // 32 位 tick 相减，unchecked 自动处理回绕
            return unchecked((uint)Environment.TickCount - lii.dwTime);
        }

        // ==================== 遮罩窗管理 ====================

        private static void EnsureCovers()
        {
            if (covers.Count > 0) return;
            foreach (Screen s in Screen.AllScreens)
            {
                DimForm f = new DimForm(s);
                covers.Add(f);
                f.Show();
            }
            Logger.Info("变暗遮罩已就绪（" + covers.Count + " 个显示器）");
        }

        private static void SetOpacity(double op)
        {
            foreach (DimForm f in covers)
            {
                try
                {
                    if (f.IsDisposed) continue;
                    if (Math.Abs(f.Opacity - op) < 0.004) continue; // 无变化就不动，减少重绘
                    f.Opacity = op;
                }
                catch (Exception ex)
                {
                    Logger.Error("设置变暗遮罩透明度失败", ex);
                }
            }
        }

        private static void CloseCovers()
        {
            if (covers.Count == 0) return;
            foreach (DimForm f in covers.ToArray())
            {
                try
                {
                    if (!f.IsDisposed) f.Close();
                }
                catch (Exception ex)
                {
                    Logger.Error("撤除变暗遮罩失败", ex);
                }
            }
            covers.Clear();
            DimForm.HideCursor = false;
            Logger.Info("屏幕已恢复");
        }

        /// <summary>立即复位（设置变更 / 退出时使用，不做渐变）。</summary>
        private static void ResetNow()
        {
            currentOpacity = 0.0;
            lastTarget = -1;
            DimForm.HideCursor = false;
            CloseCovers();
        }
    }
}
