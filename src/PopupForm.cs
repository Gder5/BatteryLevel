using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BatteryLevel
{
    /// <summary>从托盘弹出的电量面板，完全自绘。</summary>
    sealed class PopupForm : Form
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int WM_DEVICECHANGE = 0x0219;
        public const string WindowTitle = "BatteryLevel · 设备电量";
        public static readonly int WM_SHOWME = RegisterWindowMessage("BatteryLevel.ShowPanel");
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessage(string msg);

        // 逻辑尺寸（96 DPI）
        const int LogicalWidth = 340, Pad = 16, HeaderH = 56, CardH = 74, Gap = 8, EmptyH = 140;

        readonly float scale;
        readonly string iconFontName;
        Font fTitle, fSub, fName, fKind, fPercent, fGlyph, fGlyphSmall, fGlyphLarge;
        Theme theme;
        List<DeviceBattery> devices = new List<DeviceBattery>();
        DateTime updated = DateTime.MinValue;
        bool scanning = true;
        Rectangle refreshRect;
        bool hoverRefresh;

        // 卡片命中测试与重命名
        readonly List<Rectangle> cardRects = new List<Rectangle>();
        readonly List<Rectangle> nameRects = new List<Rectangle>();
        ContextMenuStrip cardMenu;
        ToolStripMenuItem miRename, miReset;
        DeviceBattery menuDevice;
        TextBox editor;
        DeviceBattery editing;

        public DateTime LastHidden = DateTime.MinValue;
        public event EventHandler DeviceRenamed;
        public event EventHandler RefreshRequested;
        public event EventHandler DevicesChanged;
        public event EventHandler ShowRequested;

        public PopupForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            Text = WindowTitle;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            iconFontName = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

            const string ui = "Microsoft YaHei UI";
            fTitle = new Font(ui, 12f, FontStyle.Bold);
            fSub = new Font(ui, 8.5f);
            fName = new Font(ui, 10f);
            fKind = new Font(ui, 8.5f);
            fPercent = new Font("Segoe UI Semibold", 16f);
            fGlyph = new Font(iconFontName, 14f);
            fGlyphSmall = new Font(iconFontName, 10.5f);
            fGlyphLarge = new Font(iconFontName, 22f);

            ApplyTheme();
        }

        static bool FontExists(string name)
        {
            using (var f = new Font(name, 10f)) return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
        }

        int S(float v) { return (int)Math.Round(v * scale); }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW：不出现在 Alt+Tab
                cp.ClassStyle |= 0x20000;    // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwm();
        }

        void ApplyDwm()
        {
            if (!IsHandleCreated) return;
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, 4);
                int dark = theme.Dark ? 1 : 0;
                DwmSetWindowAttribute(Handle, 20, ref dark, 4);
                int border = ColorTranslator.ToWin32(theme.WindowBorder);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { } // Win10 无此 API 时忽略
        }

        public void ApplyTheme()
        {
            theme = Theme.Current();
            BackColor = theme.Background;
            ApplyDwm();
            Invalidate();
        }

        public void SetScanning()
        {
            scanning = true;
            Invalidate();
        }

        public void SetDevices(List<DeviceBattery> list)
        {
            devices = list;
            updated = DateTime.Now;
            scanning = false;
            if (Visible) PlaceNearTray(); else Invalidate();
        }

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        Timer focusWatch;
        DateTime shownAt;
        bool wasForeground;

        public void ShowPanel()
        {
            PlaceNearTray();
            Show();
            Activate();
            SetForegroundWindow(Handle);
            // 系统有时不允许抢占前台（此时收不到失焦事件），因此轮询前台窗口兜底：
            // 曾在前台后又失去 → 收起；始终未进入前台 → 8 秒后收起
            if (focusWatch == null)
            {
                focusWatch = new Timer();
                focusWatch.Interval = 500;
                focusWatch.Tick += delegate
                {
                    if (!Visible) { focusWatch.Stop(); return; }
                    if (GetForegroundWindow() == Handle) { wasForeground = true; return; }
                    if (wasForeground || (DateTime.Now - shownAt).TotalSeconds > 8) HidePanel();
                };
            }
            shownAt = DateTime.Now;
            wasForeground = false;
            focusWatch.Start();
        }

        void PlaceNearTray()
        {
            int count = Math.Max(devices.Count, 0);
            int body = count == 0 ? S(EmptyH) : count * S(CardH) + (count - 1) * S(Gap);
            int w = S(LogicalWidth);
            int h = S(Pad) + S(HeaderH) + body + S(Pad);

            var screen = Screen.FromPoint(Cursor.Position);
            var wa = screen.WorkingArea;
            var b = screen.Bounds;
            h = Math.Min(h, wa.Height - S(24));
            int m = S(12);
            int x, y;
            if (wa.Left > b.Left) { x = wa.Left + m; y = wa.Bottom - h - m; }          // 任务栏在左
            else if (wa.Top > b.Top) { x = wa.Right - w - m; y = wa.Top + m; }        // 任务栏在上
            else { x = wa.Right - w - m; y = wa.Bottom - h - m; }                     // 底部 / 右侧
            Bounds = new Rectangle(x, y, w, h);
            Invalidate();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            HidePanel();
        }

        void HidePanel()
        {
            if (!Visible) return;
            EndRename(true);
            if (focusWatch != null) focusWatch.Stop();
            Hide();
            LastHidden = DateTime.Now;
            Memory.Trim();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (editor != null) { base.OnKeyDown(e); return; }   // 正在编辑名称，按键交给输入框
            if (e.KeyCode == Keys.Escape) HidePanel();
            else if (e.KeyCode == Keys.F5) Raise(RefreshRequested);
            base.OnKeyDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool h = refreshRect.Contains(e.Location);
            if (h != hoverRefresh) { hoverRefresh = h; Cursor = h ? Cursors.Hand : Cursors.Default; Invalidate(refreshRect); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverRefresh) { hoverRefresh = false; Cursor = Cursors.Default; Invalidate(refreshRect); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && refreshRect.Contains(e.Location)) Raise(RefreshRequested);
            else if (e.Button == MouseButtons.Left && editor != null) EndRename(true);
            else if (e.Button == MouseButtons.Right)
            {
                int i = HitCard(e.Location);
                if (i >= 0) ShowCardMenu(i, e.Location);
            }
            base.OnMouseClick(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                int i = HitCard(e.Location);
                if (i >= 0) BeginRename(devices[i]);
            }
            base.OnMouseDoubleClick(e);
        }

        int HitCard(Point p)
        {
            for (int i = 0; i < cardRects.Count && i < devices.Count; i++)
                if (cardRects[i].Contains(p)) return i;
            return -1;
        }

        void ShowCardMenu(int i, Point p)
        {
            if (cardMenu == null)
            {
                cardMenu = new ContextMenuStrip();
                miRename = new ToolStripMenuItem("重命名…", null, delegate { if (menuDevice != null) BeginRename(menuDevice); });
                miReset = new ToolStripMenuItem("恢复默认名称", null, delegate
                {
                    if (menuDevice == null) return;
                    Aliases.Remove(menuDevice.Id);
                    Raise(DeviceRenamed);
                });
                cardMenu.Items.Add(miRename);
                cardMenu.Items.Add(miReset);
            }
            menuDevice = devices[i];
            miReset.Enabled = !string.IsNullOrEmpty(Aliases.Get(menuDevice.Id));
            miReset.ToolTipText = menuDevice.DefaultName;
            cardMenu.Show(this, p);
        }

        void BeginRename(DeviceBattery d)
        {
            EndRename(true);
            int i = devices.IndexOf(d);
            if (i < 0 || i >= nameRects.Count) return;
            var r = nameRects[i];
            editing = d;
            editor = new TextBox();
            editor.BorderStyle = BorderStyle.FixedSingle;
            editor.Font = fName;
            editor.BackColor = theme.Card;
            editor.ForeColor = theme.Text;
            editor.MaxLength = 40;
            editor.Text = d.Name;
            editor.Bounds = new Rectangle(r.X - S(4), r.Y - S(3), r.Width + S(4), editor.PreferredHeight);
            editor.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; EndRename(true); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; EndRename(false); }
            };
            editor.LostFocus += delegate { BeginInvoke(new Action(delegate { EndRename(true); })); };
            Controls.Add(editor);
            editor.Focus();
            editor.SelectAll();
        }

        void EndRename(bool commit)
        {
            if (editor == null) return;
            var box = editor; var d = editing;
            editor = null; editing = null;
            if (commit)
            {
                var text = box.Text.Trim();
                // 输入为空或与默认名相同，即视为清除别名
                if (text.Length == 0 || text == d.DefaultName) Aliases.Remove(d.Id);
                else if (text != d.Name) Aliases.Set(d.Id, text);
            }
            Controls.Remove(box);
            box.Dispose();
            Raise(DeviceRenamed);
        }

        void Raise(EventHandler h) { if (h != null) h(this, EventArgs.Empty); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DEVICECHANGE) Raise(DevicesChanged);
            else if (m.Msg == WM_SHOWME && WM_SHOWME != 0) Raise(ShowRequested);
            base.WndProc(ref m);
        }

        // ───────────────────────────── 绘制 ─────────────────────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(theme.Background);

            int W = ClientSize.Width;
            int px = S(Pad);

            // 标题区
            TextRenderer.DrawText(g, "设备电量", fTitle, new Point(px - S(1), px), theme.Text, TextFormatFlags.NoPadding);
            string sub;
            if (scanning && updated == DateTime.MinValue) sub = "正在扫描设备…";
            else if (scanning) sub = "正在刷新…";
            else sub = (devices.Count == 0 ? "未发现设备" : devices.Count + " 台设备") + " · " + updated.ToString("HH:mm") + " 更新";
            TextRenderer.DrawText(g, sub, fSub, new Point(px, px + S(26)), theme.SubText, TextFormatFlags.NoPadding);

            int bs = S(32);
            refreshRect = new Rectangle(W - px - bs + S(4), px + S(2), bs, bs);
            if (hoverRefresh)
                using (var hb = new SolidBrush(theme.Hover))
                    g.FillEllipse(hb, refreshRect);
            TextRenderer.DrawText(g, "", fGlyphSmall, refreshRect, theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int y = px + S(HeaderH);
            var area = new Rectangle(px, y, W - 2 * px, 0);
            cardRects.Clear();
            nameRects.Clear();

            if (devices.Count == 0)
            {
                area.Height = S(EmptyH);
                DrawEmpty(g, area);
                return;
            }

            foreach (var d in devices)
            {
                var r = new Rectangle(area.X, y, area.Width, S(CardH));
                if (r.Bottom > ClientSize.Height - S(4)) break;
                cardRects.Add(r);
                DrawCard(g, r, d);
                y += S(CardH) + S(Gap);
            }
        }

        void FillCard(Graphics g, Rectangle r)
        {
            using (var path = Theme.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10)))
            using (var b = new SolidBrush(theme.Card))
            using (var p = new Pen(theme.CardBorder, 1f))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }

        void DrawCard(Graphics g, Rectangle r, DeviceBattery d)
        {
            FillCard(g, r);
            Color lc = Theme.LevelColor(d.Level);

            // 图标圆
            int dia = S(42);
            var ic = new Rectangle(r.X + S(14), r.Y + (r.Height - dia) / 2, dia, dia);
            using (var b = new SolidBrush(Theme.Blend(lc, theme.Card, theme.Dark ? 0.22f : 0.13f)))
                g.FillEllipse(b, ic);
            TextRenderer.DrawText(g, Glyph(d.Kind), fGlyph, ic, lc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int tx = ic.Right + S(14);
            int right = r.Right - S(16);

            // 百分比（右侧）
            string pct = d.Level.ToString();
            var pctSize = TextRenderer.MeasureText(g, pct, fPercent, Size.Empty, TextFormatFlags.NoPadding);
            var unitSize = TextRenderer.MeasureText(g, "%", fKind, Size.Empty, TextFormatFlags.NoPadding);
            int pctTop = r.Y + S(13);
            int unitX = right - unitSize.Width;
            int pctX = unitX - pctSize.Width - S(2);
            TextRenderer.DrawText(g, pct, fPercent, new Point(pctX, pctTop), theme.Text, TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "%", fKind, new Point(unitX, pctTop + pctSize.Height - unitSize.Height - S(3)), theme.SubText, TextFormatFlags.NoPadding);

            // 名称与类型
            int nameW = Math.Max(S(40), pctX - tx - S(10));
            var nameRect = new Rectangle(tx, r.Y + S(14), nameW, S(22));
            nameRects.Add(nameRect);
            if (d != editing)
                TextRenderer.DrawText(g, d.Name, fName, nameRect, theme.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            string kind = KindLabel(d);
            bool low = !d.IsSystem && !d.Charging && d.Level <= Theme.LowThreshold;
            if (low) kind += " · 电量低";
            TextRenderer.DrawText(g, kind, fKind, new Rectangle(tx, r.Y + S(36), nameW, S(18)),
                low ? lc : theme.SubText,
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            // 进度条
            int barH = S(5);
            var bar = new RectangleF(tx, r.Bottom - S(17), right - tx, barH);
            using (var tp = Theme.RoundRect(bar, barH / 2f))
            using (var tb = new SolidBrush(theme.Track))
                g.FillPath(tb, tp);
            float fw = Math.Max(barH, bar.Width * Math.Min(100, d.Level) / 100f);
            using (var fp = Theme.RoundRect(new RectangleF(bar.X, bar.Y, fw, bar.Height), barH / 2f))
            using (var fb = new SolidBrush(lc))
                g.FillPath(fb, fp);
        }

        void DrawEmpty(Graphics g, Rectangle r)
        {
            FillCard(g, r);
            var gl = new Rectangle(r.X, r.Y + S(20), r.Width, S(40));
            TextRenderer.DrawText(g, "", fGlyphLarge, gl, theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            string t1 = scanning ? "正在扫描…" : "暂无可读取电量的设备";
            TextRenderer.DrawText(g, t1, fName, new Rectangle(r.X, r.Y + S(70), r.Width, S(22)), theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, "支持向 Windows 上报电量的蓝牙设备", fKind, new Rectangle(r.X, r.Y + S(96), r.Width, S(18)), theme.SubText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        }

        static string Glyph(DeviceKind k)
        {
            switch (k)
            {
                case DeviceKind.Mouse: return "";
                case DeviceKind.Keyboard: return "";
                case DeviceKind.Headset: return "";
                case DeviceKind.Speaker: return "";
                case DeviceKind.Gamepad: return "";
                case DeviceKind.Phone: return "";
                case DeviceKind.Pen: return "";
                case DeviceKind.Laptop: return "";
                default: return "";
            }
        }

        public static string KindLabel(DeviceBattery d)
        {
            if (d.Kind == DeviceKind.Laptop) return d.Charging ? "电源已接通" : "使用电池";
            string kind;
            switch (d.Kind)
            {
                case DeviceKind.Mouse: kind = "鼠标"; break;
                case DeviceKind.Keyboard: kind = "键盘"; break;
                case DeviceKind.Headset: kind = "耳机"; break;
                case DeviceKind.Speaker: kind = "音箱"; break;
                case DeviceKind.Gamepad: kind = "手柄"; break;
                case DeviceKind.Phone: kind = "手机"; break;
                case DeviceKind.Pen: kind = "触控笔"; break;
                default: kind = "设备"; break;
            }
            var s = string.IsNullOrEmpty(d.Connection) ? kind : kind + " · " + d.Connection;
            if (d.Charging) s += " · 充电中";
            return s;
        }
    }
}
