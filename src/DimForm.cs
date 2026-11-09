using System;
using System.Drawing;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 屏幕变暗遮罩：覆盖单个显示器的全屏纯黑窗体。
    /// 透明度（Opacity）由 ScreenDimmer 逐帧推进，从而实现淡入 / 淡出的渐变效果。
    ///
    /// 扩展样式 WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：
    ///   - 不抢焦点（ShowWithoutActivation），不影响当前正在操作的窗口；
    ///   - 不进任务栏、不出现在 Alt+Tab 列表。
    /// 遮罩不设 WS_EX_TRANSPARENT，因此暗屏期间鼠标点击不会穿透到下层窗口（避免误操作），
    /// 点击本身会被判定为「有输入」，随即自动渐显恢复。
    /// </summary>
    internal class DimForm : Form
    {
        /// <summary>
        /// 是否隐藏鼠标指针（由 ScreenDimmer 控制）。
        ///
        /// 为什么需要它：Windows 的鼠标指针是系统在**所有窗口之上**独立绘制的，属于"硬件光标"层，
        /// 不受窗口 Opacity 影响 —— 屏幕压暗后那个白色箭头依然全亮，看起来非常突兀。
        /// 遮罩覆盖整个屏幕，鼠标一定位于本窗体上，因此在这里处理 WM_SETCURSOR / WM_MOUSEMOVE
        /// 把光标设为 NULL，即可让指针也"一起暗下去"；一旦检测到输入（ScreenDimmer 会立即把
        /// HideCursor 置回 false），指针马上恢复，不会让人找不到鼠标。
        /// </summary>
        internal static bool HideCursor;

        private const int WM_SETCURSOR = 0x0020;
        private const int WM_MOUSEMOVE = 0x0200;

        internal DimForm(Screen screen)
        {
            this.Text = "TrayToys 屏幕变暗"; // 不显示，仅便于调试识别
            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            this.BackColor = Color.Black;
            this.Bounds = screen.Bounds;
            this.Opacity = 0.0; // 初始全透明，由 ScreenDimmer 逐帧升到目标暗度
        }

        protected override void WndProc(ref Message m)
        {
            if (HideCursor)
            {
                if (m.Msg == WM_SETCURSOR)
                {
                    NativeMethods.SetCursor(IntPtr.Zero);
                    m.Result = (IntPtr)1; // 已处理，阻止系统再设回默认光标
                    return;
                }
                if (m.Msg == WM_MOUSEMOVE)
                {
                    NativeMethods.SetCursor(IntPtr.Zero);
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>显示时不激活（不抢当前窗口焦点）。</summary>
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                cp.ExStyle &= ~0x00040000;              // 去掉 WS_EX_APPWINDOW
                return cp;
            }
        }
    }
}
