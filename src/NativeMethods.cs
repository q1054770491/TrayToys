using System;
using System.Runtime.InteropServices;

namespace TrayToys
{
    /// <summary>Win32 / User32 互操作：全局热键。</summary>
    internal static class NativeMethods
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_DEVICECHANGE = 0x0219;      // 设备变动（蓝牙连/断、USB 插拔）
        public const int DBT_DEVNODES_CHANGED = 0x0007; // 设备树节点发生变化

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_WIN = 0x0008;
        public const uint MOD_NOREPEAT = 0x4000; // 忽略长按重复触发

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        // ==================== 电源 / 防息屏（kernel32） ====================

        public const uint ES_CONTINUOUS = 0x80000000;       // 持续生效，直到显式清除
        public const uint ES_SYSTEM_REQUIRED = 0x00000001;  // 阻止系统自动睡眠
        public const uint ES_DISPLAY_REQUIRED = 0x00000002; // 阻止屏幕自动关闭

        [DllImport("kernel32.dll")]
        public static extern uint SetThreadExecutionState(uint esFlags);

        // ==================== 输入空闲检测（user32） ====================

        /// <summary>系统最后一次输入（鼠标/键盘）的信息。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime; // 最后一次输入的 tick（32 位）
        }

        /// <summary>获取系统最后一次输入的时间，配合 Environment.TickCount 可算出空闲时长。</summary>
        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        // ==================== 鼠标光标（user32） ====================

        /// <summary>设置当前光标；传 IntPtr.Zero 表示不显示光标。</summary>
        [DllImport("user32.dll")]
        public static extern IntPtr SetCursor(IntPtr hCursor);

        /// <summary>光标信息（诊断用）。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetCursorInfo(ref CURSORINFO pci);

        public const int CURSOR_SHOWING = 0x0001;

        // ==================== 用户通知状态 / 全屏检测（shell32） ====================

        // QUERY_USER_NOTIFICATION_STATE 取值
        public const int QUNS_NOT_PRESENT = 1;              // 用户不在（锁屏 / 屏保 / 切换会话）
        public const int QUNS_BUSY = 2;                     // 全屏应用运行中（含演示模式）
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;  // 全屏 D3D 应用（游戏）独占运行
        public const int QUNS_PRESENTATION_MODE = 4;        // 用户处于演示模式
        public const int QUNS_ACCEPTS_NOTIFICATIONS = 5;    // 常规桌面，可接受通知
        public const int QUNS_QUIET_TIME = 6;               // 刚登录的安静时段
        public const int QUNS_APP = 7;                      // 沉浸式（Modern）应用全屏

        /// <summary>
        /// 查询"用户当前是否可被打扰"。返回 HRESULT（0 = 成功），结果写入 pquns。
        /// 这正是 Windows 用来决定"能不能弹通知"的接口 —— 全屏看视频时通知不冒出来就是它的功劳。
        /// </summary>
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int pquns);
    }
}
