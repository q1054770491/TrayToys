using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TrayToys
{
    /// <summary>
    /// 不可聚焦、2 秒后自动关闭的浮窗（单例：连续触发时只更新文字并重置计时）。
    /// WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW：不可被鼠标点击选中、不出现在任务栏与 Alt+Tab。
    /// </summary>
    internal class ToastForm : Form
    {
        private static ToastForm current;
        private System.Windows.Forms.Timer timer;
        private string display;
        private int durationMs;
        private int elapsed;

        /// <param name="display">浮窗要显示的完整文案，例如「已切换到：XX」。</param>
        internal ToastForm(string display, int durationMs)
        {
            this.display = display;
            this.durationMs = durationMs;
            this.elapsed = 0;

            this.FormBorderStyle = FormBorderStyle.None;
            this.ShowInTaskbar = false;
            this.TopMost = true;
            this.StartPosition = FormStartPosition.Manual;
            // WinForms 窗体不支持带透明度的 BackColor；改为：不透明底色 + TransparencyKey 实现圆角外透明
            this.BackColor = Color.Magenta;
            this.TransparencyKey = Color.Magenta;
            this.ForeColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 11f);

            ResizeToText();
            Position();
            this.Opacity = 0;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 30;
            timer.Tick += OnTick;
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

        public static void ShowToast(string text, int durationMs)
        {
            try
            {
                if (current != null && !current.IsDisposed)
                {
                    current.UpdateText(text, durationMs);
                    return;
                }
                current = new ToastForm(text, durationMs);
                current.FormClosed += delegate(object s, FormClosedEventArgs e)
                {
                    if (ReferenceEquals(current, s)) current = null;
                };
                current.Show();
            }
            catch (Exception ex)
            {
                // 浮窗只是提示，任何失败都不影响设备切换，但记录下来便于排查
                Logger.Error("浮窗显示失败: " + text, ex);
            }
        }

        private void UpdateText(string display, int durationMs)
        {
            this.display = display;
            this.durationMs = durationMs;
            this.elapsed = 0;
            this.Opacity = 0;
            ResizeToText();
            Position();
            Invalidate();
            if (timer != null && !timer.Enabled) timer.Start();
        }

        private void ResizeToText()
        {
            Size sz = TextRenderer.MeasureText(display, this.Font);
            this.Width = sz.Width + 36;
            if (this.Width < 200) this.Width = 200;
            this.Height = 44;
        }

        private void Position()
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            this.Left = area.Right - this.Width - 16;
            this.Top = area.Bottom - this.Height - 16;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            timer.Start();
        }

        private void OnTick(object sender, EventArgs e)
        {
            const int fade = 200; // 淡入淡出时长(ms)
            int fadeOutStart = fade + durationMs;
            elapsed += 30;

            if (elapsed < fade)
            {
                this.Opacity = (double)elapsed / fade;
            }
            else if (elapsed < fadeOutStart)
            {
                this.Opacity = 1.0;
            }
            else
            {
                this.Opacity = 1.0 - (double)(elapsed - fadeOutStart) / fade;
                if (this.Opacity <= 0.0)
                {
                    timer.Stop();
                    Close();
                }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(45, 45, 45)))
            {
                g.FillPath(bg, path);
            }
            TextRenderer.DrawText(g, display, this.Font,
                new Rectangle(0, 0, Width, Height), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
