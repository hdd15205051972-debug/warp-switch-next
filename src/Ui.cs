using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WarpSwitchNext
{
    static class Palette
    {
        public static readonly Color Blue = Color.FromArgb(0, 103, 192);
        public static readonly Color BlueHover = Color.FromArgb(0, 88, 168);
        public static readonly Color Green = Color.FromArgb(16, 137, 62);
        public static readonly Color Orange = Color.FromArgb(202, 95, 0);
        public static readonly Color Red = Color.FromArgb(196, 43, 28);
        public static readonly Color RedHover = Color.FromArgb(170, 34, 22);
        public static readonly Color Gray = Color.FromArgb(118, 118, 118);
        public static readonly Color Text = Color.FromArgb(26, 26, 26);
        public static readonly Color Secondary = Color.FromArgb(96, 96, 96);
        public static readonly Color Tertiary = Color.FromArgb(132, 132, 132);
        public static readonly Color Window = Color.FromArgb(249, 249, 249);
        public static readonly Color Card = Color.White;
        public static readonly Color Border = Color.FromArgb(229, 229, 229);
        public static readonly Color Disabled = Color.FromArgb(200, 200, 200);

        public static Color ForPhase(string phase) =>
            phase == "on" ? Green : phase == "error" ? Orange : phase == "off" ? Gray : Blue;
    }

    static class Ui
    {
        static readonly Dictionary<string, Font> cache = new Dictionary<string, Font>();

        public static Font Font(float size, FontStyle style = FontStyle.Regular) => Get("Microsoft YaHei UI", size, style);
        public static Font Mono(float size) => Get("Consolas", size, FontStyle.Regular);

        static Font Get(string family, float size, FontStyle style)
        {
            var key = family + size + style;
            if (cache.TryGetValue(key, out var f)) return f;
            try
            {
                f = new Font(family, size, style, GraphicsUnit.Point);
                if (!string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase))
                    f = new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Point);
            }
            catch { f = new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Point); }
            cache[key] = f;
            return f;
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>The project's shield, fitted into the given box.</summary>
        public static GraphicsPath Shield(RectangleF box)
        {
            // Designed on a 1024 grid (y down); the shield spans x 274..750, y 224..803.
            float w = 476, h = 579;
            float scale = Math.Min(box.Width / w, box.Height / h);
            float ox = box.X + (box.Width - w * scale) / 2 - 274 * scale;
            float oy = box.Y + (box.Height - h * scale) / 2 - 224 * scale;
            PointF P(float x, float y) => new PointF(ox + x * scale, oy + y * scale);
            var path = new GraphicsPath();
            path.AddLine(P(512, 224), P(750, 320));
            path.AddLine(P(750, 320), P(750, 534));
            path.AddBezier(P(750, 534), P(750, 674), P(630, 749), P(512, 803));
            path.AddBezier(P(512, 803), P(395, 749), P(274, 674), P(274, 534));
            path.AddLine(P(274, 534), P(274, 320));
            path.CloseFigure();
            return path;
        }

        /// <summary>The check mark that sits inside the shield, in the same coordinates.</summary>
        public static PointF[] Check(RectangleF box, out float penWidth)
        {
            float w = 476, h = 579;
            float scale = Math.Min(box.Width / w, box.Height / h);
            float ox = box.X + (box.Width - w * scale) / 2 - 274 * scale;
            float oy = box.Y + (box.Height - h * scale) / 2 - 224 * scale;
            penWidth = 52 * scale;
            return new[] { new PointF(ox + 390 * scale, oy + 514 * scale), new PointF(ox + 485 * scale, oy + 604 * scale), new PointF(ox + 640 * scale, oy + 404 * scale) };
        }

        public static Icon AppIcon()
        {
            using (var s = typeof(Ui).Assembly.GetManifestResourceStream("AppIcon.ico"))
                return s != null ? new Icon(s) : SystemIcons.Application;
        }

        public static Bitmap AppImage(int size)
        {
            using (var s = typeof(Ui).Assembly.GetManifestResourceStream("AppIcon.ico"))
            {
                if (s == null) return new Bitmap(size, size);
                using (var icon = new Icon(s, size, size)) return icon.ToBitmap();
            }
        }

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr handle);

        static readonly Dictionary<string, Icon> trayIcons = new Dictionary<string, Icon>();

        /// <summary>Small tray icon: a shield coloured by connection state, with a check when connected.</summary>
        public static Icon TrayIcon(string phase)
        {
            var key = phase == "on" ? "on" : phase == "error" ? "error" : phase == "off" ? "off" : "busy";
            if (trayIcons.TryGetValue(key, out var cached)) return cached;
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(size, size))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var box = new RectangleF(size * 0.06f, size * 0.03f, size * 0.88f, size * 0.94f);
                    var color = key == "on" ? Palette.Green : key == "error" ? Palette.Orange : key == "off" ? Palette.Gray : Palette.Blue;
                    using (var shield = Shield(box))
                    using (var brush = new SolidBrush(color)) g.FillPath(brush, shield);
                    if (key == "on")
                    {
                        var points = Check(box, out float width);
                        using (var pen = new Pen(Color.White, Math.Max(1.6f, width)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                            g.DrawLines(pen, points);
                    }
                }
                IntPtr handle = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(handle).Clone();
                DestroyIcon(handle);
                trayIcons[key] = icon;
                return icon;
            }
        }
    }

    /// <summary>The large status circle: a soft disc, a ring, and the shield (or a spinner while busy).</summary>
    sealed class StatusRing : Control
    {
        readonly Timer spin = new Timer { Interval = 33 };
        float angle;
        Color tint = Palette.Gray;
        bool busy, connected;

        public StatusRing()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            spin.Tick += (s, e) => { angle = (angle + 9) % 360; Invalidate(); };
            AccessibleRole = AccessibleRole.Graphic;
        }

        public Color Tint { get => tint; set { if (tint != value) { tint = value; Invalidate(); } } }
        public bool Connected { get => connected; set { if (connected != value) { connected = value; Invalidate(); } } }
        public bool Busy
        {
            get => busy;
            set { if (busy == value) return; busy = value; spin.Enabled = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(Width, Height);
            float stroke = Math.Max(2f, s * 0.024f);
            var outer = new RectangleF((Width - s) / 2 + stroke, (Height - s) / 2 + stroke, s - stroke * 2, s - stroke * 2);
            using (var fill = new SolidBrush(Color.FromArgb(26, tint))) g.FillEllipse(fill, outer);
            using (var ring = new Pen(Color.FromArgb(busy ? 60 : 120, tint), stroke)) g.DrawEllipse(ring, outer);
            if (busy)
            {
                using (var arc = new Pen(tint, stroke * 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(arc, outer, angle - 90, 80);
            }
            var box = RectangleF.Inflate(outer, -outer.Width * 0.27f, -outer.Height * 0.25f);
            using (var shield = Ui.Shield(box))
            using (var brush = new SolidBrush(busy ? Color.FromArgb(150, tint) : tint))
                g.FillPath(brush, shield);
            if (connected && !busy)
            {
                var points = Ui.Check(box, out float width);
                using (var pen = new Pen(Color.White, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, points);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) spin.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>A large rounded button with a solid accent colour.</summary>
    sealed class PillButton : Control
    {
        Color accent = Palette.Blue, hoverAccent = Palette.BlueHover;
        bool hover, pressed;

        public PillButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
        }

        public void SetAccent(Color normal, Color hovered)
        {
            if (accent == normal && hoverAccent == hovered) return;
            accent = normal; hoverAccent = hovered; Invalidate();
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? BackColor);
            var color = !Enabled ? Palette.Disabled : (hover || pressed) ? hoverAccent : accent;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var path = Ui.Rounded(r, Height / 2f))
            using (var brush = new SolidBrush(color)) g.FillPath(brush, path);
            if (Focused && ShowFocusCues)
            {
                var inner = RectangleF.Inflate(r, -3, -3);
                using (var path = Ui.Rounded(inner, inner.Height / 2f))
                using (var pen = new Pen(Color.FromArgb(160, Color.White), 1.5f)) g.DrawPath(pen, path);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>An on/off switch. Clicking asks the owner to change the setting; the owner sets Checked.</summary>
    sealed class ToggleSwitch : Control
    {
        bool isOn;
        public event EventHandler Toggle;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.StandardClick, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.CheckButton;
        }

        public bool Checked
        {
            get => isOn;
            set { if (isOn == value) return; isOn = value; AccessibleDescription = value ? "已开启" : "已关闭"; Invalidate(); }
        }

        protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Toggle?.Invoke(this, EventArgs.Empty); }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) OnClick(EventArgs.Empty);
            base.OnKeyUp(e);
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? BackColor);
            var track = new RectangleF(1, 1, Width - 2.5f, Height - 2.5f);
            using (var path = Ui.Rounded(track, track.Height / 2))
            {
                if (isOn)
                {
                    using (var brush = new SolidBrush(Enabled ? Palette.Blue : Palette.Disabled)) g.FillPath(brush, path);
                }
                else
                {
                    using (var brush = new SolidBrush(Color.White)) g.FillPath(brush, path);
                    using (var pen = new Pen(Enabled ? Palette.Secondary : Palette.Disabled, 1.2f)) g.DrawPath(pen, path);
                }
            }
            float knob = track.Height * (isOn ? 0.62f : 0.52f);
            float cx = isOn ? track.Right - track.Height / 2 : track.Left + track.Height / 2;
            float cy = track.Top + track.Height / 2;
            using (var brush = new SolidBrush(isOn ? Color.White : (Enabled ? Palette.Secondary : Palette.Disabled)))
                g.FillEllipse(brush, cx - knob / 2, cy - knob / 2, knob, knob);
            if (Focused && ShowFocusCues)
            {
                using (var pen = new Pen(Palette.Text, 1) { DashStyle = DashStyle.Dot })
                using (var path = Ui.Rounded(RectangleF.Inflate(track, -2.5f, -2.5f), track.Height / 2 - 2.5f)) g.DrawPath(pen, path);
            }
        }
    }

    /// <summary>A white card with rounded corners and thin separators between its rows.</summary>
    sealed class InfoPanel : Panel
    {
        public int Rows { get; set; } = 5;

        public InfoPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Palette.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent?.BackColor ?? Palette.Window);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            float radius = Height * 0.04f + 4;
            using (var path = Ui.Rounded(r, radius))
            {
                using (var brush = new SolidBrush(BackColor)) g.FillPath(brush, path);
                using (var pen = new Pen(Palette.Border)) g.DrawPath(pen, path);
            }
            g.SmoothingMode = SmoothingMode.None;
            float rowHeight = (float)Height / Rows;
            int inset = (int)(Width * 0.04f);
            using (var pen = new Pen(Palette.Border))
                for (int i = 1; i < Rows; i++)
                {
                    int y = (int)Math.Round(rowHeight * i);
                    g.DrawLine(pen, inset, y, Width - inset, y);
                }
        }
    }
}
