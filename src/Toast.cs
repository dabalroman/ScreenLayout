using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenLayout
{
    // Non-activating card at the bottom of the primary screen: a diagram of the monitors (on = accent),
    // the mode name, a status line, and the hotkeys as keycaps with the current mode highlighted.
    // Painted by hand; sizes are in 96-dpi units scaled by the screen's DPI.
    class Toast : Form
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern bool SystemParametersInfo(int action, int param, ref RECT rect, int winIni);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        static readonly Color Bg = Color.FromArgb(30, 30, 30);
        static readonly Color Fg = Color.FromArgb(255, 255, 255);
        static readonly Color Muted = Color.FromArgb(170, 170, 170);
        static readonly Color Faint = Color.FromArgb(115, 115, 115);
        static readonly Color Accent = Color.FromArgb(96, 205, 255);
        static readonly Color OffFill = Color.FromArgb(44, 44, 44);
        static readonly Color OffEdge = Color.FromArgb(78, 78, 78);
        static readonly Color KeyFill = Color.FromArgb(46, 46, 46);
        static readonly Color Rule = Color.FromArgb(52, 52, 52);
        static readonly Color Warn = Color.FromArgb(255, 160, 140);

        readonly Config cfg;
        readonly ModeDef mode;
        readonly string modeKey;
        string detail;
        bool warn;

        readonly float s; // DPI scale
        readonly Font titleFont, detailFont, keyFont, rowFont, rowBold, labelFont;
        readonly System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer fadeTimer = new System.Windows.Forms.Timer { Interval = 15 };
        bool fadingOut;

        // Layout, computed in Measure()
        float pad, diagH, diagLabelGap, keyW, rowH;
        Dictionary<string, RectangleF> monitorRects = new Dictionary<string, RectangleF>();

        public Toast(Config cfg, string modeKey, string detailText)
        {
            this.cfg = cfg;
            this.modeKey = modeKey;
            mode = cfg.Mode(modeKey);
            detail = detailText ?? "";

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            DoubleBuffered = true;
            Opacity = 0;

            using (Graphics g = CreateGraphics()) s = g.DpiX / 96f;
            titleFont = new Font("Segoe UI Semibold", 22f * s, GraphicsUnit.Pixel);
            detailFont = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel);
            keyFont = new Font("Segoe UI Semibold", 11.5f * s, GraphicsUnit.Pixel);
            rowFont = new Font("Segoe UI", 13f * s, GraphicsUnit.Pixel);
            rowBold = new Font("Segoe UI Semibold", 13f * s, GraphicsUnit.Pixel);
            labelFont = new Font("Segoe UI", 11f * s, GraphicsUnit.Pixel);

            closeTimer.Tick += (o, e) => { closeTimer.Stop(); fadingOut = true; fadeTimer.Start(); };
            fadeTimer.Tick += OnFade;
            Measure();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x80 | 0x8; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref round, 4); // DWMWA_WINDOW_CORNER_PREFERENCE
                int border = 0x00464646; // COLORREF 0x00BBGGRR
                DwmSetWindowAttribute(Handle, 34, ref border, 4); // DWMWA_BORDER_COLOR
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            fadeTimer.Start();
        }

        void OnFade(object sender, EventArgs e)
        {
            if (fadingOut)
            {
                Opacity = Math.Max(0, Opacity - 0.12);
                if (Opacity <= 0) { fadeTimer.Stop(); Close(); }
            }
            else
            {
                Opacity = Math.Min(0.97, Opacity + 0.16);
                if (Opacity >= 0.97) fadeTimer.Stop();
            }
        }

        IEnumerable<ModeDef> HotkeyModes
        {
            get { return cfg.Modes.Where(m => m.Hotkey != null); }
        }

        static string ShortName(string name)
        {
            int sp = name.IndexOf(' ');
            return sp > 0 ? name.Substring(0, sp) : name;
        }

        void Measure()
        {
            pad = 24 * s;
            diagH = 54 * s;
            diagLabelGap = 6 * s;
            rowH = 27 * s;

            // Monitor diagram: rectangles in proportion to their resolution, bottom-aligned.
            var ids = cfg.MonitorOrder.Where(id => cfg.Spec(id) != null).ToList();
            int maxH = ids.Count > 0 ? ids.Max(id => cfg.Spec(id).Height) : 1;
            float k = diagH / maxH, x = pad, diagW = 0;
            monitorRects.Clear();
            foreach (string id in ids)
            {
                MonitorSpec sp = cfg.Spec(id);
                float w = sp.Width * k, h = sp.Height * k;
                monitorRects[id] = new RectangleF(x, pad + diagH - h, w, h);
                x += w + 12 * s;
                diagW = x - 12 * s - pad;
            }

            using (Graphics g = CreateGraphics())
            {
                float titleW = g.MeasureString(Title, titleFont).Width;
                float detailW = g.MeasureString(detail, detailFont).Width;
                keyW = HotkeyModes.Select(m => g.MeasureString(m.Hotkey, keyFont).Width).DefaultIfEmpty(0).Max() + 14 * s;
                float rowW = keyW + 12 * s + HotkeyModes.Select(m => g.MeasureString(m.Name, rowBold).Width).DefaultIfEmpty(0).Max();
                float w = Math.Max(Math.Max(diagW, titleW), Math.Max(detailW, rowW));
                w = Math.Max(w, 300 * s);
                ClientSize = new Size((int)Math.Ceiling(w + 2 * pad), (int)Math.Ceiling(RowsTop + HotkeyModes.Count() * rowH + pad - 4 * s));
            }
        }

        string Title
        {
            get { return mode != null ? mode.Name : modeKey; }
        }

        float TitleTop
        {
            get { return pad + diagH + diagLabelGap + labelFont.Height + 14 * s; }
        }

        float RowsTop
        {
            get { return TitleTop + titleFont.Height + 2 * s + detailFont.Height + 30 * s; }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var centered = new StringFormat { Alignment = StringAlignment.Center };
            var middle = new StringFormat { LineAlignment = StringAlignment.Center };
            var active = mode != null ? new HashSet<string>(mode.Active.Select(a => a.Id)) : new HashSet<string>();

            // Monitors
            foreach (var kv in monitorRects)
            {
                bool on = active.Contains(kv.Key);
                using (GraphicsPath p = Rounded(kv.Value, 4 * s))
                {
                    using (var fill = new SolidBrush(on ? Accent : OffFill)) g.FillPath(fill, p);
                    if (!on) using (var pen = new Pen(OffEdge, 1 * s)) g.DrawPath(pen, p);
                }
                var label = new RectangleF(kv.Value.X - 20 * s, pad + diagH + diagLabelGap, kv.Value.Width + 40 * s, labelFont.Height);
                using (var b = new SolidBrush(on ? Fg : Faint))
                    g.DrawString(ShortName(cfg.MonitorName(kv.Key)), labelFont, b, label, centered);
            }

            // Title + status
            using (var b = new SolidBrush(Fg)) g.DrawString(Title, titleFont, b, pad - 2 * s, TitleTop);
            using (var b = new SolidBrush(warn ? Warn : Muted)) g.DrawString(detail, detailFont, b, pad, TitleTop + titleFont.Height + 2 * s);

            // Divider
            float ruleY = RowsTop - 15 * s;
            using (var pen = new Pen(Rule, 1 * s)) g.DrawLine(pen, pad, ruleY, ClientSize.Width - pad, ruleY);

            // Hotkeys
            float y = RowsTop;
            foreach (ModeDef m in HotkeyModes)
            {
                bool current = m.Key == modeKey;
                var key = new RectangleF(pad, y, keyW, rowH - 6 * s);
                using (GraphicsPath p = Rounded(key, 5 * s))
                {
                    using (var fill = new SolidBrush(KeyFill)) g.FillPath(fill, p);
                    if (current) using (var pen = new Pen(Accent, 1.2f * s)) g.DrawPath(pen, p);
                }
                using (var b = new SolidBrush(current ? Accent : Muted))
                    g.DrawString(m.Hotkey, keyFont, b, key, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                using (var b = new SolidBrush(current ? Fg : Muted))
                    g.DrawString(m.Name, current ? rowBold : rowFont, b, new RectangleF(key.Right + 12 * s, y, ClientSize.Width, key.Height), middle);
                y += rowH;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Reposition();
        }

        // Bottom centre of the primary screen's work area (read fresh: the primary may just have changed).
        public void Reposition()
        {
            var wa = new RECT();
            SystemParametersInfo(0x30, 0, ref wa, 0); // SPI_GETWORKAREA
            Location = new Point(wa.Left + (wa.Right - wa.Left - Width) / 2, wa.Bottom - Height - (int)(64 * s));
        }

        public void SetDetail(string text, bool isWarning)
        {
            detail = text ?? "";
            warn = isWarning;
            Measure();
            Reposition();
            Invalidate();
        }

        public void CloseAfter(int ms)
        {
            closeTimer.Stop();
            closeTimer.Interval = ms;
            closeTimer.Start();
        }

        // Shows a toast for a finished (re)apply; non-blocking, needs a message loop.
        public static void ShowResult(string modeKey, string detailText, bool isWarning, int ms)
        {
            try
            {
                var t = new Toast(Config.Load(Core.LayoutsFile), modeKey, detailText);
                t.warn = isWarning;
                t.Show();
                t.CloseAfter(ms);
            }
            catch (Exception e) { Core.Log("toast: " + e.Message); }
        }
    }
}
