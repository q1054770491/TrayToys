using System;

namespace TrayToys
{
    /// <summary>
    /// 检测"是否有全屏应用正在前台运行"（看视频、玩游戏、PPT 放映等）。
    ///
    /// 实现依据：调用 shell32 的 SHQueryUserNotificationState —— 这正是 Windows 用来决定
    /// "现在能不能给用户弹通知"的接口（所以全屏看视频时微信/QQ 的通知不会冒出来）。
    /// 它返回的状态里，**只有 QUNS_ACCEPTS_NOTIFICATIONS 表示用户处于"可被打扰的常规桌面"**；
    /// 其余取值（全屏应用、D3D 全屏、演示模式、锁屏/屏保、初次登录安静时段）都说明
    /// 用户要么正在全屏消费内容、要么不在电脑前。
    ///
    /// 用途：屏幕变暗的空闲判定必须把它算作"有活动"，否则不碰鼠标键盘地看视频时，
    /// GetLastInputInfo 会一直累加空闲时长，从而被误判成"没人用"而把屏幕压暗。
    /// </summary>
    internal static class FullscreenWatcher
    {
        /// <summary>
        /// 是否处于"不应打扰"的状态（有全屏应用 / 演示中 / 用户不在）。
        /// 任何查询失败都返回 false —— 即退回原有行为（允许变暗），绝不让这个检测把变暗功能整体废掉。
        /// </summary>
        public static bool IsBusy()
        {
            int state = QueryState();
            if (state < 0) return false;
            return state != NativeMethods.QUNS_ACCEPTS_NOTIFICATIONS;
        }

        /// <summary>当前状态的可读名称（写日志用）。</summary>
        public static string StateName()
        {
            int state = QueryState();
            switch (state)
            {
                case NativeMethods.QUNS_NOT_PRESENT: return "用户不在（锁屏/屏保）";
                case NativeMethods.QUNS_BUSY: return "全屏应用运行中";
                case NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN: return "全屏 D3D 应用（游戏）";
                case NativeMethods.QUNS_PRESENTATION_MODE: return "演示模式";
                case NativeMethods.QUNS_ACCEPTS_NOTIFICATIONS: return "常规桌面";
                case NativeMethods.QUNS_QUIET_TIME: return "刚登录的安静时段";
                case NativeMethods.QUNS_APP: return "沉浸式应用全屏";
                case -1: return "查询失败";
                default: return "未知状态(" + state + ")";
            }
        }

        /// <summary>查询状态码；失败返回 -1。</summary>
        private static int QueryState()
        {
            try
            {
                int state;
                int hr = NativeMethods.SHQueryUserNotificationState(out state);
                if (hr != 0) return -1; // S_OK == 0
                return state;
            }
            catch
            {
                return -1;
            }
        }
    }
}
