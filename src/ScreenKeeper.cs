using System;

namespace TrayToys
{
    /// <summary>
    /// 屏幕 / 电源保持：控制系统是否允许自动息屏（防息屏开关）。
    /// 通过 kernel32 的 SetThreadExecutionState 实现，作用在当前线程（本程序的主线程会一直存活），
    /// 程序退出后系统恢复默认行为。
    /// </summary>
    internal static class ScreenKeeper
    {
        /// <summary>
        /// 应用设置。
        /// allowScreenOff = true  → 恢复系统默认（允许自动息屏）；
        /// allowScreenOff = false → 阻止屏幕自动关闭、系统自动睡眠。
        /// </summary>
        public static void Apply(bool allowScreenOff)
        {
            try
            {
                uint flags;
                if (allowScreenOff)
                {
                    // 清除之前的持续状态，交回系统默认行为
                    flags = NativeMethods.ES_CONTINUOUS;
                }
                else
                {
                    flags = NativeMethods.ES_CONTINUOUS
                          | NativeMethods.ES_SYSTEM_REQUIRED
                          | NativeMethods.ES_DISPLAY_REQUIRED;
                }

                uint prev = NativeMethods.SetThreadExecutionState(flags);
                if (prev == 0)
                {
                    Logger.Error("设置防息屏失败: SetThreadExecutionState 返回 0（allowScreenOff="
                        + allowScreenOff + "）", null);
                }
                else
                {
                    Logger.Info(allowScreenOff ? "已恢复允许自动息屏" : "已开启防息屏（阻止屏幕关闭/系统睡眠）");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("设置防息屏异常", ex);
            }
        }

        /// <summary>当前设置下的状态描述，用于界面显示。</summary>
        public static string StateText(bool allowScreenOff)
        {
            return allowScreenOff ? "允许自动息屏（系统默认）" : "已阻止自动息屏（保持常亮）";
        }
    }
}
