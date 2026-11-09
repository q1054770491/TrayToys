using System.Drawing;
using System.Drawing.Drawing2D;

namespace TrayToys
{
    /// <summary>在运行时绘制一个简易托盘图标，避免依赖外部 .ico 资源文件。</summary>
    internal static class TrayIconHelper
    {
        public static Icon CreateIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(0x2D, 0x7D, 0xF0)))
                using (Pen pen = new Pen(Color.White, 3))
                {
                    g.FillEllipse(brush, 3, 3, 26, 26);
                    PointF[] pts = new PointF[]
                    {
                        new PointF(13, 13),
                        new PointF(13, 21),
                        new PointF(21, 17)
                    };
                    g.DrawPolygon(pen, pts);
                }
            }
            Icon icon = Icon.FromHandle(bmp.GetHicon());
            bmp.Dispose();
            return icon;
        }
    }
}
