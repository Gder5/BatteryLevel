using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BatteryLevel
{
    public sealed class Theme
    {
        public bool Dark;
        public Color Background, Card, CardBorder, Text, SubText, Track, Hover, WindowBorder;

        public static readonly Color Good = Color.FromArgb(0x1F, 0xA8, 0x6B);
        public static readonly Color Mid = Color.FromArgb(0xE3, 0x9B, 0x0B);
        public static readonly Color Low = Color.FromArgb(0xE5, 0x48, 0x4D);
        public const int LowThreshold = 20;
        public const int MidThreshold = 50;

        public static Color LevelColor(int level)
        {
            if (level <= LowThreshold) return Low;
            if (level <= MidThreshold) return Mid;
            return Good;
        }

        public static Theme Current()
        {
            var t = new Theme();
            t.Dark = !ReadLight("AppsUseLightTheme");
            if (t.Dark)
            {
                t.Background = Color.FromArgb(0x1F, 0x1F, 0x22);
                t.Card = Color.FromArgb(0x2B, 0x2B, 0x2F);
                t.CardBorder = Color.FromArgb(0x36, 0x36, 0x3B);
                t.Text = Color.FromArgb(0xF2, 0xF2, 0xF4);
                t.SubText = Color.FromArgb(0x9C, 0x9C, 0xA4);
                t.Track = Color.FromArgb(0x3A, 0x3A, 0x40);
                t.Hover = Color.FromArgb(0x33, 0x33, 0x38);
                t.WindowBorder = Color.FromArgb(0x3A, 0x3A, 0x40);
            }
            else
            {
                t.Background = Color.FromArgb(0xF4, 0xF5, 0xF7);
                t.Card = Color.White;
                t.CardBorder = Color.FromArgb(0xE6, 0xE7, 0xEB);
                t.Text = Color.FromArgb(0x1B, 0x1D, 0x22);
                t.SubText = Color.FromArgb(0x6E, 0x72, 0x7A);
                t.Track = Color.FromArgb(0xEC, 0xED, 0xF0);
                t.Hover = Color.FromArgb(0xE6, 0xE8, 0xEC);
                t.WindowBorder = Color.FromArgb(0xD9, 0xDB, 0xE0);
            }
            return t;
        }

        public static bool TaskbarIsLight() { return ReadLight("SystemUsesLightTheme"); }

        static bool ReadLight(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue(name);
                    return v == null || Convert.ToInt32(v) != 0;
                }
            }
            catch { return true; }
        }

        public static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.R * t + b.R * (1 - t)),
                (int)(a.G * t + b.G * (1 - t)),
                (int)(a.B * t + b.B * (1 - t)));
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    public static class IconFactory
    {
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>托盘图标：横向电池，填充长度与颜色反映最低电量。level &lt; 0 表示没有设备。</summary>
        public static Icon CreateTrayIcon(int level, int size)
        {
            bool lightTaskbar = Theme.TaskbarIsLight();
            Color fg = lightTaskbar ? Color.FromArgb(0x1B, 0x1D, 0x22) : Color.White;
            using (var bmp = DrawBattery(level, size, fg))
            {
                IntPtr h = bmp.GetHicon();
                var tmp = Icon.FromHandle(h);
                var icon = (Icon)tmp.Clone();
                tmp.Dispose();
                DestroyIcon(h);
                return icon;
            }
        }

        public static Bitmap DrawBattery(int level, int size, Color outline)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size / 16f;
                float stroke = Math.Max(1.2f, 1.4f * s);
                var body = new RectangleF(0.5f * s + stroke / 2, 3.5f * s, 13.2f * s - stroke, 9f * s);
                using (var pen = new Pen(outline, stroke))
                using (var path = Theme.RoundRect(body, 2.2f * s))
                    g.DrawPath(pen, path);
                using (var nub = new SolidBrush(outline))
                using (var nubPath = Theme.RoundRect(new RectangleF(13.6f * s, 6.2f * s, 2f * s, 3.6f * s), 0.8f * s))
                    g.FillPath(nub, nubPath);

                if (level >= 0)
                {
                    float pad = stroke / 2 + 1.1f * s;
                    var inner = RectangleF.Inflate(body, -pad, -pad);
                    float w = Math.Max(1.5f * s, inner.Width * Math.Min(100, level) / 100f);
                    using (var fill = new SolidBrush(Theme.LevelColor(level)))
                    using (var fp = Theme.RoundRect(new RectangleF(inner.X, inner.Y, w, inner.Height), 1f * s))
                        g.FillPath(fill, fp);
                }
            }
            return bmp;
        }
    }
}
