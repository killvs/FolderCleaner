using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace MenuFontTuner
{
    // ===================== 入口 =====================
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length > 0)
            {
                string a0 = args[0];
                if (a0 == "--apply" && args.Length >= 3)
                {
                    try { NsConfig.Apply(args[1], float.Parse(args[2], CultureInfo.InvariantCulture)); Environment.Exit(0); }
                    catch { Environment.Exit(1); }
                }
                if (a0 == "--reset")
                {
                    try { NsConfig.ResetToAuto(); Environment.Exit(0); }
                    catch { Environment.Exit(1); }
                }
                if (a0 == "--zh" || a0 == "--unzh")
                {
                    try { Localizer.Apply(a0 == "--zh"); Environment.Exit(0); }
                    catch (Exception ex) { DumpError(ex); Environment.Exit(1); }
                }
            }

            MainForm f = new MainForm();
            if (args.Length >= 2 && args[0] == "--shot")
            {
                f.Shown += delegate
                {
                    try { f.Activate(); } catch { }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(350);
                    try
                    {
                        Rectangle b = f.Bounds;
                        using (Bitmap bmp = new Bitmap(b.Width, b.Height))
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                            bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    catch (Exception ex) { DumpError(ex); }
                    f.Close();
                };
            }
            Application.Run(f);
        }

        internal static void DumpError(Exception ex)
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "mft_error.txt"), ex.ToString(), Encoding.UTF8); }
            catch { }
        }
    }

    // ===================== 配色 =====================
    internal static class Pal
    {
        public static readonly Color Bg = Color.FromArgb(22, 24, 28);
        public static readonly Color Card = Color.FromArgb(30, 33, 39);
        public static readonly Color Field = Color.FromArgb(40, 44, 51);
        public static readonly Color FieldHi = Color.FromArgb(48, 53, 62);
        public static readonly Color Line = Color.FromArgb(48, 53, 62);
        public static readonly Color LineHi = Color.FromArgb(74, 82, 96);
        public static readonly Color Text = Color.FromArgb(236, 239, 244);
        public static readonly Color Sub = Color.FromArgb(134, 142, 156);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color AccentHi = Color.FromArgb(102, 160, 255);
        public static readonly Color Ok = Color.FromArgb(90, 200, 130);
        public static readonly Color Warn = Color.FromArgb(238, 175, 85);
    }

    // ===================== 自绘控件基类 =====================
    internal class FlatControl : Control
    {
        protected static readonly float S;
        static FlatControl()
        {
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) S = g.DpiY / 96f; }
            catch { }
            if (S <= 0) S = 1f;
        }

        protected bool Hover;
        protected Font Fnt;

        public FlatControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Pal.Bg;
            Cursor = Cursors.Default;
            Fnt = new Font("Microsoft YaHei UI", 12f * S, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        protected int R(float v) { return (int)Math.Round(v * S); }
        protected float F(float v) { return v * S; }

        protected override void OnMouseEnter(EventArgs e) { Hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected static GraphicsPath RoundRect(RectangleF rc, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(rc.Width, rc.Height));
            if (d <= 0) { p.AddRectangle(rc); return p; }
            p.AddArc(rc.X, rc.Y, d, d, 180, 90);
            p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
            p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
            p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ===================== 按钮 =====================
    internal class FlatButton : FlatControl
    {
        public bool Primary;
        private bool _down;

        public FlatButton()
        {
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Pal.Bg);

            RectangleF rc = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            Color bg, fg;
            if (Primary)
            {
                bg = _down ? Pal.Accent : (Hover ? Pal.AccentHi : Pal.Accent);
                fg = Color.White;
            }
            else
            {
                bg = _down ? Pal.FieldHi : (Hover ? Pal.FieldHi : Pal.Field);
                fg = Pal.Text;
            }

            using (GraphicsPath p = RoundRect(rc, F(6)))
            {
                using (SolidBrush b = new SolidBrush(bg)) g.FillPath(b, p);
                if (!Primary)
                    using (Pen pen = new Pen(Hover ? Pal.LineHi : Pal.Line))
                        g.DrawPath(pen, p);
            }

            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                using (SolidBrush tb = new SolidBrush(fg))
                    g.DrawString(Text, Fnt, tb, rc, sf);
            }
        }
    }

    // ===================== 下拉框 =====================
    internal class FlatCombo : FlatControl
    {
        public string[] Items = new string[0];
        private string _value = "";
        public event EventHandler Changed;

        public string Value
        {
            get { return _value; }
            set { if (_value != value) { _value = value; Invalidate(); } }
        }

        public FlatCombo() { Cursor = Cursors.Hand; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Pal.Bg);

            RectangleF rc = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath p = RoundRect(rc, F(6)))
            {
                using (SolidBrush b = new SolidBrush(Hover ? Pal.FieldHi : Pal.Field)) g.FillPath(b, p);
                using (Pen pen = new Pen(Hover ? Pal.LineHi : Pal.Line)) g.DrawPath(pen, p);
            }

            // 箭头
            float ax = Width - F(16), ay = Height / 2f - F(1.5f);
            using (Pen pen = new Pen(Pal.Sub, F(1.6f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, ax - F(4), ay, ax, ay + F(4));
                g.DrawLine(pen, ax, ay + F(4), ax + F(4), ay);
            }

            // 文字：用它自己的字体预览
            string t = string.IsNullOrEmpty(_value) ? "（选择字体）" : _value;
            Font fn = null; bool own = false;
            try { fn = new Font(_value, F(12f), FontStyle.Regular, GraphicsUnit.Pixel); own = true; }
            catch { fn = Fnt; }
            using (StringFormat sf = new StringFormat())
            using (SolidBrush tb = new SolidBrush(Pal.Text))
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                g.DrawString(t, fn, tb, new RectangleF(F(11), 0, Width - F(38), Height), sf);
            }
            if (own) fn.Dispose();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Items == null || Items.Length == 0) return;
            PickerForm p = new PickerForm(Items, _value, Width);
            Point sp = PointToScreen(new Point(0, Height + R(3)));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (sp.Y + p.Height > wa.Bottom) sp.Y = PointToScreen(Point.Empty).Y - p.Height - R(3);
            if (sp.X + p.Width > wa.Right) sp.X = wa.Right - p.Width;
            p.Location = sp;
            p.Show(FindForm());
            p.FormClosed += delegate
            {
                if (p.Picked != null && p.Picked != _value)
                {
                    _value = p.Picked;
                    Invalidate();
                    if (Changed != null) Changed(this, EventArgs.Empty);
                }
            };
        }
    }

    // ===================== 字体下拉弹层 =====================
    internal class PickerForm : Form
    {
        private readonly ListBox _lb;
        public string Picked;
        private static readonly Dictionary<string, Font> Cache = new Dictionary<string, Font>();

        public PickerForm(string[] items, string cur, int width)
        {
            float s = FlatControlScale;
            int rowH = (int)Math.Round(25 * s);

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Pal.Field;
            Width = width;
            Height = Math.Min(items.Length, 11) * rowH + (int)Math.Round(8 * s);

            _lb = new ListBox();
            _lb.BorderStyle = BorderStyle.None;
            _lb.DrawMode = DrawMode.OwnerDrawFixed;
            _lb.ItemHeight = rowH;
            _lb.BackColor = Pal.Field;
            _lb.ForeColor = Pal.Text;
            _lb.IntegralHeight = false;
            _lb.Dock = DockStyle.Fill;
            _lb.Items.AddRange(items);
            _lb.DrawItem += LbDraw;
            _lb.MouseClick += delegate { Commit(); };
            _lb.KeyDown += LbKey;
            Controls.Add(_lb);

            int i = Array.IndexOf(items, cur);
            if (i >= 0) { _lb.SelectedIndex = i; _lb.TopIndex = Math.Max(0, i - 3); }

            Deactivate += delegate { Close(); };
        }

        internal static float FlatControlScale
        {
            get
            {
                try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiY / 96f; }
                catch { return 1f; }
            }
        }

        private void Commit()
        {
            if (_lb.SelectedIndex < 0) return;
            Picked = _lb.SelectedItem.ToString();
            Close();
        }

        private void LbKey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
        }

        private void LbDraw(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            string name = _lb.Items[e.Index].ToString();
            bool sel = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            using (SolidBrush bg = new SolidBrush(sel ? Pal.Accent : Pal.Field))
                e.Graphics.FillRectangle(bg, e.Bounds);

            Font fn = null;
            if (!Cache.TryGetValue(name, out fn))
            {
                try { fn = new Font(name, 12f * e.Graphics.DpiY / 96f, FontStyle.Regular, GraphicsUnit.Pixel); }
                catch { fn = _lb.Font; }
                Cache[name] = fn;
            }
            using (StringFormat sf = new StringFormat())
            using (SolidBrush fg = new SolidBrush(sel ? Color.White : Pal.Text))
            {
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                e.Graphics.DrawString(name, fn, fg, new RectangleF(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 20, e.Bounds.Height), sf);
            }
        }
    }

    // ===================== 数字步进器 =====================
    internal class FlatSpin : FlatControl
    {
        public decimal Value = 18m;
        public decimal Min = 10m;
        public decimal Max = 48m;
        public event EventHandler Changed;

        private int _hot = -1;   // 0 上, 1 下

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Pal.Bg);

            RectangleF rc = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            using (GraphicsPath p = RoundRect(rc, F(6)))
            {
                using (SolidBrush b = new SolidBrush(Hover ? Pal.FieldHi : Pal.Field)) g.FillPath(b, p);
                using (Pen pen = new Pen(Hover ? Pal.LineHi : Pal.Line)) g.DrawPath(pen, p);
            }

            // 数值
            using (StringFormat sf = new StringFormat())
            using (SolidBrush tb = new SolidBrush(Pal.Text))
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(((int)Value).ToString(), Fnt, tb, new RectangleF(0, 0, Width - F(22), Height), sf);
            }

            // 上下箭头
            float cx = Width - F(11);
            DrawArrow(g, cx, Height * 0.32f, true, _hot == 0);
            DrawArrow(g, cx, Height * 0.68f, false, _hot == 1);

            using (Pen sep = new Pen(Pal.Line))
                g.DrawLine(sep, Width - F(22), F(5), Width - F(22), Height - F(5));
        }

        private void DrawArrow(Graphics g, float cx, float cy, bool up, bool hot)
        {
            float w = F(3.6f), h = F(2.4f);
            PointF[] pts = up
                ? new PointF[] { new PointF(cx - w, cy + h), new PointF(cx + w, cy + h), new PointF(cx, cy - h) }
                : new PointF[] { new PointF(cx - w, cy - h), new PointF(cx + w, cy - h), new PointF(cx, cy + h) };
            using (SolidBrush b = new SolidBrush(hot ? Pal.Text : Pal.Sub))
                g.FillPolygon(b, pts);
        }

        private int Zone(Point p)
        {
            if (p.X < Width - F(22)) return -1;
            return p.Y < Height / 2f ? 0 : 1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int z = Zone(e.Location);
            if (z != _hot) { _hot = z; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { _hot = -1; base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            int z = Zone(e.Location);
            if (z == 0) Step(1);
            else if (z == 1) Step(-1);
            base.OnMouseDown(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Step(e.Delta > 0 ? 1 : -1);
            base.OnMouseWheel(e);
        }

        private void Step(int d)
        {
            decimal v = Value + d;
            if (v < Min) v = Min;
            if (v > Max) v = Max;
            if (v != Value)
            {
                Value = v;
                Invalidate();
                if (Changed != null) Changed(this, EventArgs.Empty);
            }
        }
    }

    // ===================== 菜单预览 =====================
    internal class MenuPreview : FlatControl
    {
        public string FaceName = "Microsoft YaHei UI";
        public float SizePx = 18f;

        private static readonly string[] Items = { "查看(V)", "排序方式(O)", "刷新(E)", "复制(C)", "文件管理", "属性(R)" };
        private static readonly string[] Glyphs = { "eye", "sort", "refresh", "copy", "folder", "gear" };

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.Clear(Pal.Card);

            float size = Math.Max(9f, SizePx);
            Font f;
            try { f = new Font(FaceName, size * S, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch { f = new Font("Microsoft YaHei UI", size * S, FontStyle.Regular, GraphicsUnit.Pixel); }

            float fh = f.GetHeight(g);
            float itemH = Math.Max(fh + F(4), Math.Min(fh + F(9), (Height - F(12)) / 4f));
            int n = (int)((Height - F(10)) / itemH);
            if (n > Items.Length) n = Items.Length;
            if (n < 3) n = 3;

            float w = Math.Min(Width - F(24), F(250));
            float h = n * itemH + F(10);
            float x = (Width - w) / 2f;
            float y = (Height - h) / 2f;
            float iconSz = size * S * 0.85f;

            // 柔和投影
            for (int i = 5; i >= 1; i--)
            {
                RectangleF sr = new RectangleF(x - F(0.5f), y + i * F(1.2f), w, h);
                using (GraphicsPath sp = RoundRect(sr, F(8)))
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(9, 0, 0, 0)))
                    g.FillPath(sb, sp);
            }

            RectangleF rc = new RectangleF(x, y, w, h);
            using (GraphicsPath path = RoundRect(rc, F(8)))
            {
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(252, 44, 48, 57))) g.FillPath(sb, path);
                using (Pen p = new Pen(Color.FromArgb(30, 255, 255, 255))) g.DrawPath(p, path);
            }

            float top = y + F(5);
            for (int i = 0; i < n; i++)
            {
                if (i == 3)
                {
                    using (Pen sep = new Pen(Color.FromArgb(34, 255, 255, 255)))
                        g.DrawLine(sep, x + F(12), top - F(1), x + w - F(12), top - F(1));
                }
                float iy = top + i * itemH;

                if (i == 0)
                {
                    using (GraphicsPath hp = RoundRect(new RectangleF(x + F(4), iy, w - F(8), itemH), F(5)))
                    using (SolidBrush hb = new SolidBrush(Color.FromArgb(54, Pal.Accent)))
                        g.FillPath(hb, hp);
                }

                DrawGlyph(g, Glyphs[i], new RectangleF(x + F(13), iy + (itemH - iconSz) / 2f, iconSz, iconSz));
                using (StringFormat sf = new StringFormat())
                using (SolidBrush tb = new SolidBrush(Pal.Text))
                {
                    sf.LineAlignment = StringAlignment.Center;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    g.DrawString(Items[i], f, tb,
                        new RectangleF(x + F(13) + iconSz + F(11), iy, w - iconSz - F(48), itemH), sf);
                }
            }
            f.Dispose();
        }

        private void DrawGlyph(Graphics g, string kind, RectangleF r)
        {
            using (Pen p = new Pen(Color.FromArgb(186, 196, 212), F(1.3f)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(186, 196, 212)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                switch (kind)
                {
                    case "eye":
                        g.DrawEllipse(p, r);
                        g.FillEllipse(b, cx - F(1.6f), cy - F(1.6f), F(3.2f), F(3.2f));
                        break;
                    case "sort":
                        g.DrawLine(p, r.X, r.Y + F(2), r.Right, r.Y + F(2));
                        g.DrawLine(p, r.X, cy, r.Right, cy);
                        g.DrawLine(p, r.X, r.Bottom - F(2), r.Right, r.Bottom - F(2));
                        break;
                    case "refresh":
                        g.DrawArc(p, r, 40, 275);
                        g.FillEllipse(b, r.Right - F(4), r.Y + F(1), F(3.4f), F(3.4f));
                        break;
                    case "copy":
                        g.DrawRectangle(p, r.X, r.Y + F(2.5f), r.Width - F(3), r.Height - F(3));
                        g.DrawLine(p, r.X + F(3), r.Y + F(0.5f), r.Right - F(0.5f), r.Y + F(0.5f));
                        break;
                    case "folder":
                        g.DrawRectangle(p, r.X, r.Y + F(3), r.Width, r.Height - F(4));
                        g.DrawLine(p, r.X, r.Y + F(3), r.X + r.Width * 0.45f, r.Y + F(3));
                        break;
                    default:
                        g.DrawEllipse(p, cx - F(2.2f), cy - F(2.2f), F(4.4f), F(4.4f));
                        g.DrawEllipse(p, r);
                        break;
                }
            }
        }
    }

    // ===================== Nilesoft 配置 =====================
    internal static class NsConfig
    {
        public static readonly string AppDir = @"C:\Program Files\Nilesoft Shell";
        public static string ThemePath { get { return Path.Combine(AppDir, @"imports\theme.nss"); } }
        public static string ImportsDir { get { return Path.Combine(AppDir, "imports"); } }

        public static bool Installed { get { return File.Exists(ThemePath); } }

        public static string ReadName()
        {
            Match m = Regex.Match(ReadText(), "font\\.name\\s*=\\s*\"([^\"]*)\"");
            return m.Success ? m.Groups[1].Value : "Microsoft YaHei UI";
        }

        public static float ReadSize()
        {
            Match m = Regex.Match(ReadText(), "font\\.size\\s*=\\s*([0-9]+(?:\\.[0-9]+)?)");
            if (m.Success) return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            return 0f;
        }

        private static string ReadText()
        {
            if (!File.Exists(ThemePath)) return "";
            return File.ReadAllText(ThemePath, Encoding.UTF8);
        }

        public static void Apply(string name, float size)
        {
            string t = ReadText();
            if (t.Length == 0) throw new IOException("未找到 Nilesoft Shell 配置文件");
            t = SetOrInsert(t, "font\\.name", "font.name=\"" + name + "\"");
            t = SetOrInsert(t, "font\\.size", "font.size=" + size.ToString("0.##", CultureInfo.InvariantCulture));
            if (!Regex.IsMatch(t, "font\\.weight")) t = SetOrInsert(t, null, "font.weight=400");
            File.WriteAllText(ThemePath, t, new UTF8Encoding(true));
        }

        public static void ResetToAuto()
        {
            string t = ReadText();
            if (t.Length == 0) throw new IOException("未找到 Nilesoft Shell 配置文件");
            t = Regex.Replace(t, "[ \\t]*font\\.(name|size|weight)\\s*=\\s*[^\\r\\n]*\\r?\\n?", "");
            File.WriteAllText(ThemePath, t, new UTF8Encoding(true));
        }

        private static string SetOrInsert(string text, string prop, string line)
        {
            if (prop != null && Regex.IsMatch(text, prop + "\\s*="))
                return Regex.Replace(text, prop + "\\s*=\\s*[^\\r\\n]*", line);
            Match m = Regex.Match(text, "theme\\s*\\{");
            if (!m.Success) throw new IOException("配置格式异常（缺少 theme 块）");
            return text.Insert(m.Index + m.Length, "\r\n\t" + line);
        }

        public static void RestartExplorer()
        {
            foreach (Process p in Process.GetProcessesByName("explorer"))
            {
                try { p.Kill(); } catch { }
            }
            System.Threading.Thread.Sleep(900);
            try { Process.Start("explorer.exe"); } catch { }
        }
    }

    // ===================== 汉化模块 =====================
    internal static class Localizer
    {
        private const char Sep = '\u00A6';

        // 每项格式："英文原文整行¦中文译文整行"，按行精确匹配，绝不触碰 cmd= 等参数
        private static readonly string[] Raw = new string[] {
            "menu(where=sel.count>0 type='file|dir|drive|namespace|back' mode=\"multiple\" title='File manage' image=\\uE253)¦menu(where=sel.count>0 type='file|dir|drive|namespace|back' mode=\"multiple\" title='文件管理' image=\\uE253)",
            "item(where=sel.count > 1 title='Copy (@sel.count) items selected' cmd=command.copy(sel(false, \"\\n\")))¦item(where=sel.count > 1 title='复制选中的 @sel.count 项' cmd=command.copy(sel(false, \"\\n\")))",
            "item(mode=\"single\" type='file' separator=\"before\" find='.lnk' title='open file location')¦item(mode=\"single\" type='file' separator=\"before\" find='.lnk' title='打开文件所在位置')",
            "item(mode=\"single\" type=\"file\" title=\"Change extension\" image=\\uE0B5 cmd=if(input(\"Change extension\", \"Type extension\"),¦item(mode=\"single\" type=\"file\" title=\"更改扩展名\" image=\\uE0B5 cmd=if(input(\"更改扩展名\", \"输入扩展名\"),",
            "item(title=\"All\" image=icon.select_all cmd=command.select_all)¦item(title=\"全选\" image=icon.select_all cmd=command.select_all)",
            "item(title=\"Invert\" image=icon.invert_selection cmd=command.invert_selection)¦item(title=\"反向选择\" image=icon.invert_selection cmd=command.invert_selection)",
            "item(title=\"None\" image=icon.select_none cmd=command.select_none)¦item(title=\"取消选择\" image=icon.select_none cmd=command.select_none)",
            "item(type='file|dir|back.dir|drive' title='Take ownership' image=[\\uE194,#f00] admin¦item(type='file|dir|back.dir|drive' title='取得所有权' image=[\\uE194,#f00] admin",
            "menu(title=\"Show/Hide\" image=icon.show_hidden_files)¦menu(title=\"显示/隐藏\" image=icon.show_hidden_files)",
            "item(title=\"System files\" image=inherit cmd='@command.togglehidden')¦item(title=\"系统文件\" image=inherit cmd='@command.togglehidden')",
            "item(title=\"File name extensions\" image=icon.show_file_extensions cmd='@command.toggleext')¦item(title=\"文件扩展名\" image=icon.show_file_extensions cmd='@command.toggleext')",
            "menu(type='file|dir|back.dir' mode=\"single\" title='Attributes' image=icon.properties)¦menu(type='file|dir|back.dir' mode=\"single\" title='属性' image=icon.properties)",
            "item(title='Hidden' checked=io.attribute.hidden(atrr)¦item(title='隐藏' checked=io.attribute.hidden(atrr)",
            "item(title='System' checked=io.attribute.system(atrr)¦item(title='系统' checked=io.attribute.system(atrr)",
            "item(title='Read-Only' checked=io.attribute.readonly(atrr)¦item(title='只读' checked=io.attribute.readonly(atrr)",
            "item(title='Archive' checked=io.attribute.archive(atrr)¦item(title='存档' checked=io.attribute.archive(atrr)",
            "item(title=\"Created\" keys=io.dt.created(sel.path, 'y/m/d') cmd=io.dt.created(sel.path,2000,1,1) vis=label)¦item(title=\"创建时间\" keys=io.dt.created(sel.path, 'y/m/d') cmd=io.dt.created(sel.path,2000,1,1) vis=label)",
            "item(title=\"Modified\" keys=io.dt.modified(sel.path, 'y/m/d') cmd=io.dt.modified(sel.path,2000,1,1) vis=label)¦item(title=\"修改时间\" keys=io.dt.modified(sel.path, 'y/m/d') cmd=io.dt.modified(sel.path,2000,1,1) vis=label)",
            "item(title=\"Accessed\" keys=io.dt.accessed(sel.path, 'y/m/d') cmd=io.dt.accessed(sel.path,2000,1,1) vis=label)¦item(title=\"访问时间\" keys=io.dt.accessed(sel.path, 'y/m/d') cmd=io.dt.accessed(sel.path,2000,1,1) vis=label)",
            "menu(mode=\"single\" type='file' find='.dll|.ocx' separator=\"before\" title='Register Server' image=\\uea86)¦menu(mode=\"single\" type='file' find='.dll|.ocx' separator=\"before\" title='注册服务器' image=\\uea86)",
            "item(title='Register' admin cmd='regsvr32.exe' args='@sel.path.quote' invoke=\"multiple\")¦item(title='注册' admin cmd='regsvr32.exe' args='@sel.path.quote' invoke=\"multiple\")",
            "item(title='Unregister' admin cmd='regsvr32.exe' args='/u @sel.path.quote' invoke=\"multiple\")¦item(title='取消注册' admin cmd='regsvr32.exe' args='/u @sel.path.quote' invoke=\"multiple\")",
            "menu(separator=\"before\" title='New Folder' image=icon.new_folder)¦menu(separator=\"before\" title='新建文件夹' image=icon.new_folder)",
            "modify(find=\"unpin*\" pos=\"bottom\" menu=\"Pin/Unpin\")¦modify(find=\"unpin*\" pos=\"bottom\" menu=\"固定/取消固定\")",
            "modify(find=\"pin*\" pos=\"top\" menu=\"Pin/Unpin\")¦modify(find=\"pin*\" pos=\"top\" menu=\"固定/取消固定\")",
            "modify(where=this.id==id.copy_as_path menu=\"file manage\")¦modify(where=this.id==id.copy_as_path menu=\"文件管理\")",
            "modify(type=\"dir.back|drive.back\" where=this.id==id.customize_this_folder pos=1 sep=\"top\" menu=\"file manage\")¦modify(type=\"dir.back|drive.back\" where=this.id==id.customize_this_folder pos=1 sep=\"top\" menu=\"文件管理\")",
            "menu(mode=\"multiple\" title='&Develop' vis=key.shift() sep=sep.bottom image=\\uE26E)¦menu(mode=\"multiple\" title='开发(&D)' vis=key.shift() sep=sep.bottom image=\\uE26E)",
            "menu(mode=\"single\" title='editors' image=\\uE17A)¦menu(mode=\"single\" title='编辑器' image=\\uE17A)",
            "item(type='file' mode=\"single\" title='Windows notepad' image cmd='@sys.bin\\notepad.exe' args='\"@sel.path\"')¦item(type='file' mode=\"single\" title='记事本' image cmd='@sys.bin\\notepad.exe' args='\"@sel.path\"')",
            "item(title='run' cmd-line='/K dotnet run' image=\\uE149)¦item(title='运行' cmd-line='/K dotnet run' image=\\uE149)",
            "item(title='watch' cmd-line='/K dotnet watch')¦item(title='监视' cmd-line='/K dotnet watch')",
            "item(title='clean' image=\\uE0CE cmd-line='/K dotnet clean')¦item(title='清理' image=\\uE0CE cmd-line='/K dotnet clean')",
            "item(title='build debug' cmd-line='/K dotnet build')¦item(title='生成调试版' cmd-line='/K dotnet build')",
            "item(title='build release' cmd-line='/K dotnet build -c release /p:DebugType=None')¦item(title='生成发布版' cmd-line='/K dotnet build -c release /p:DebugType=None')",
            "menu(mode=\"multiple\" sep=\"both\" title='publish' image=\\ue11f)¦menu(mode=\"multiple\" sep=\"both\" title='发布' image=\\ue11f)",
            "item(title='help' image=\\uE136 cmd-line='/k dotnet -h')¦item(title='帮助' image=\\uE136 cmd-line='/k dotnet -h')",
            "item(title='version' cmd-line='/k dotnet --info')¦item(title='版本' cmd-line='/k dotnet --info')",
            "menu(title='Folder' image=\\uE1F4)¦menu(title='文件夹' image=\\uE1F4)",
            "item(title='Windows' image=inherit cmd=sys.dir)¦item(title='Windows 目录' image=inherit cmd=sys.dir)",
            "item(title='System' image=inherit cmd=sys.bin)¦item(title='System32 目录' image=inherit cmd=sys.bin)",
            "item(title='Program Files' image=inherit cmd=sys.prog)¦item(title='程序 (Program Files)' image=inherit cmd=sys.prog)",
            "item(title='Program Files x86' image=inherit cmd=sys.prog32)¦item(title='程序 (x86)' image=inherit cmd=sys.prog32)",
            "item(title='ProgramData' image=inherit cmd=sys.programdata)¦item(title='程序数据 (ProgramData)' image=inherit cmd=sys.programdata)",
            "item(title='Applications' image=inherit cmd='shell:appsfolder')¦item(title='应用列表' image=inherit cmd='shell:appsfolder')",
            "item(title='Users' image=inherit cmd=sys.users)¦item(title='用户文件夹' image=inherit cmd=sys.users)",
            "item(title='Desktop' image=inherit cmd=user.desktop)¦item(title='桌面' image=inherit cmd=user.desktop)",
            "item(title='Downloads' image=inherit cmd=user.downloads)¦item(title='下载' image=inherit cmd=user.downloads)",
            "item(title='Pictures' image=inherit cmd=user.pictures)¦item(title='图片' image=inherit cmd=user.pictures)",
            "item(title='Documents' image=inherit cmd=user.documents)¦item(title='文档' image=inherit cmd=user.documents)",
            "item(title='Startmenu' image=inherit cmd=user.startmenu)¦item(title='开始菜单' image=inherit cmd=user.startmenu)",
            "item(title='Profile' image=inherit cmd=user.dir)¦item(title='用户主目录' image=inherit cmd=user.dir)",
            "item(title='AppData' image=inherit cmd=user.appdata)¦item(title='应用数据 (AppData)' image=inherit cmd=user.appdata)",
            "item(title='Temp' image=inherit cmd=user.temp)¦item(title='临时文件 (Temp)' image=inherit cmd=user.temp)",
            "item(title='All Control Panel Items' image=\\uE0F3 cmd='shell:::{ED7BA470-8E54-465E-825C-99712043E01C}')¦item(title='所有控制面板项' image=\\uE0F3 cmd='shell:::{ED7BA470-8E54-465E-825C-99712043E01C}')",
            "item(title='system' image=inherit cmd='ms-settings:')¦item(title='系统' image=inherit cmd='ms-settings:')",
            "item(title='about' image=inherit cmd='ms-settings:about')¦item(title='关于' image=inherit cmd='ms-settings:about')",
            "item(title='your-info' image=inherit cmd='ms-settings:yourinfo')¦item(title='账户信息' image=inherit cmd='ms-settings:yourinfo')",
            "item(title='system-info' image=inherit cmd-line='/K systeminfo')¦item(title='系统信息' image=inherit cmd-line='/K systeminfo')",
            "item(title='search' cmd='search-ms:' image=inherit)¦item(title='搜索' cmd='search-ms:' image=inherit)",
            "item(title='usb' image=inherit cmd='ms-settings:usb')¦item(title='USB' image=inherit cmd='ms-settings:usb')",
            "item(title='windows-update' image=inherit cmd='ms-settings:windowsupdate')¦item(title='Windows 更新' image=inherit cmd='ms-settings:windowsupdate')",
            "item(title='windows-defender' image=inherit cmd='ms-settings:windowsdefender')¦item(title='Windows 安全中心' image=inherit cmd='ms-settings:windowsdefender')",
            "menu(title='apps' image=inherit)¦menu(title='应用' image=inherit)",
            "item(title='apps-features' image=inherit cmd='ms-settings:appsfeatures')¦item(title='应用和功能' image=inherit cmd='ms-settings:appsfeatures')",
            "item(title='default-apps' image=inherit cmd='ms-settings:defaultapps')¦item(title='默认应用' image=inherit cmd='ms-settings:defaultapps')",
            "item(title='optional-features' image=inherit cmd='ms-settings:optionalfeatures')¦item(title='可选功能' image=inherit cmd='ms-settings:optionalfeatures')",
            "item(title='startup' image=inherit cmd='ms-settings:startupapps')¦item(title='启动应用' image=inherit cmd='ms-settings:startupapps')",
            "menu(title='personalization' image=inherit)¦menu(title='个性化' image=inherit)",
            "item(title='personalization' image=inherit cmd='ms-settings:personalization')¦item(title='个性化' image=inherit cmd='ms-settings:personalization')",
            "item(title='lockscreen' image=inherit cmd='ms-settings:lockscreen')¦item(title='锁屏界面' image=inherit cmd='ms-settings:lockscreen')",
            "item(title='background' image=inherit cmd='ms-settings:personalization-background')¦item(title='桌面背景' image=inherit cmd='ms-settings:personalization-background')",
            "item(title='colors' image=inherit cmd='ms-settings:colors')¦item(title='颜色' image=inherit cmd='ms-settings:colors')",
            "item(title='themes' image=inherit cmd='ms-settings:themes')¦item(title='主题' image=inherit cmd='ms-settings:themes')",
            "item(title='start' image=inherit cmd='ms-settings:personalization-start')¦item(title='开始' image=inherit cmd='ms-settings:personalization-start')",
            "item(title='taskbar' image=inherit cmd='ms-settings:taskbar')¦item(title='任务栏' image=inherit cmd='ms-settings:taskbar')",
            "menu(title='network' image=inherit)¦menu(title='网络' image=inherit)",
            "item(title='status' image=inherit cmd='ms-settings:network-status')¦item(title='网络状态' image=inherit cmd='ms-settings:network-status')",
            "item(title='ethernet' image=inherit cmd='ms-settings:network-ethernet')¦item(title='以太网' image=inherit cmd='ms-settings:network-ethernet')",
            "item(title='connections' image=inherit cmd='shell:::{7007ACC7-3202-11D1-AAD2-00805FC1270E}')¦item(title='网络连接' image=inherit cmd='shell:::{7007ACC7-3202-11D1-AAD2-00805FC1270E}')",
            "item(title=\"config\" image=\\uE10A cmd='\"@app.cfg\"')¦item(title=\"配置\" image=\\uE10A cmd='\"@app.cfg\"')",
            "item(title=\"manager\" image=\\uE0F3 admin cmd='\"@app.exe\"')¦item(title=\"管理器\" image=\\uE0F3 admin cmd='\"@app.exe\"')",
            "item(title=\"directory\" image=\\uE0E8 cmd='\"@app.dir\"')¦item(title=\"目录\" image=\\uE0E8 cmd='\"@app.dir\"')",
            "item(title=\"docs\" image=\\uE1C4 cmd='https://nilesoft.org/docs')¦item(title=\"文档\" image=\\uE1C4 cmd='https://nilesoft.org/docs')",
            "item(title=\"donate\" image=\\uE1A7 cmd='https://nilesoft.org/donate')¦item(title=\"捐赠\" image=\\uE1A7 cmd='https://nilesoft.org/donate')",
            "menu(title=\"Apps\" image=\\uE254)¦menu(title=\"应用\" image=\\uE254)",
            "item(title='Paint' image=\\uE116 cmd='mspaint')¦item(title='画图' image=\\uE116 cmd='mspaint')",
            "item(title='Calculator' image=\\ue1e7 cmd='calc.exe')¦item(title='计算器' image=\\ue1e7 cmd='calc.exe')",
            "menu(title=title.windows image=\\uE1FB)¦menu(title=\"窗口\" image=\\uE1FB)",
            "item(title=title.cascade_windows cmd=command.cascade_windows)¦item(title=\"层叠窗口\" cmd=command.cascade_windows)",
            "item(title=title.Show_windows_stacked cmd=command.Show_windows_stacked)¦item(title=\"堆叠显示窗口\" cmd=command.Show_windows_stacked)",
            "item(title=title.Show_windows_side_by_side cmd=command.Show_windows_side_by_side)¦item(title=\"并排显示窗口\" cmd=command.Show_windows_side_by_side)",
            "item(title=title.minimize_all_windows cmd=command.minimize_all_windows)¦item(title=\"最小化所有窗口\" cmd=command.minimize_all_windows)",
            "item(title=title.restore_all_windows cmd=command.restore_all_windows)¦item(title=\"还原所有窗口\" cmd=command.restore_all_windows)",
            "item(title=title.task_manager sep=both image=icon.task_manager cmd='taskmgr.exe')¦item(title=\"任务管理器\" sep=both image=icon.task_manager cmd='taskmgr.exe')",
            "item(title=title.taskbar_Settings sep=both image=inherit cmd='ms-settings:taskbar')¦item(title=\"任务栏设置\" sep=both image=inherit cmd='ms-settings:taskbar')",
            "item(vis=key.shift() title=title.exit_explorer cmd=command.restart_explorer)¦item(vis=key.shift() title=\"退出资源管理器\" cmd=command.restart_explorer)",
            "item(title=title.command_prompt tip=tip_run_admin admin=has_admin image cmd='cmd.exe' args='/K TITLE Command Prompt &ver& PUSHD \"@sel.dir\"')¦item(title=title.command_prompt tip='按住 Shift 键以管理员身份运行' admin=has_admin image cmd='cmd.exe' args='/K TITLE 命令提示符 &ver& PUSHD \"@sel.dir\"')",
            "item(title=title.windows_powershell admin=has_admin tip=tip_run_admin image cmd='powershell.exe' args='-noexit -command Set-Location -Path \"@sel.dir\\.\"')¦item(title=title.windows_powershell admin=has_admin tip='按住 Shift 键以管理员身份运行' image cmd='powershell.exe' args='-noexit -command Set-Location -Path \"@sel.dir\\.\"')",
            "item(where=package.exists(\"WindowsTerminal\") title=title.Windows_Terminal tip=tip_run_admin admin=has_admin image='@package.path(\"WindowsTerminal\")\\WindowsTerminal.exe' cmd='wt.exe' arg='-d \"@sel.path\\.\"')¦item(where=package.exists(\"WindowsTerminal\") title=title.Windows_Terminal tip='按住 Shift 键以管理员身份运行' admin=has_admin image='@package.path(\"WindowsTerminal\")\\WindowsTerminal.exe' cmd='wt.exe' arg='-d \"@sel.path\\.\"')",
            "menu(mode=\"multiple\" title=\"Pin/Unpin\" image=icon.pin)¦menu(mode=\"multiple\" title=\"固定/取消固定\" image=icon.pin)",
        };

        private static Dictionary<string, string> _en2zh;
        private static Dictionary<string, string> _zh2en;

        private static readonly string[] Files = new string[] {
            @"imports\file-manage.nss",
            @"imports\modify.nss",
            @"imports\develop.nss",
            @"imports\goto.nss",
            @"imports\taskbar.nss",
            @"imports\terminal.nss",
            "shell.nss"
        };

        private static void Build()
        {
            if (_en2zh != null) return;
            _en2zh = new Dictionary<string, string>();
            _zh2en = new Dictionary<string, string>();
            foreach (string s in Raw)
            {
                int i = s.IndexOf(Sep);
                if (i <= 0) continue;
                string en = s.Substring(0, i);
                string zh = s.Substring(i + 1);
                if (!_en2zh.ContainsKey(en)) _en2zh[en] = zh;
                if (!_zh2en.ContainsKey(zh)) _zh2en[zh] = en;
            }
        }

        public static int Total { get { Build(); return _en2zh.Count; } }

        /// <summary>还能被翻译的行数（0 表示已是中文）</summary>
        public static int PendingCount()
        {
            Build();
            int n = 0;
            foreach (string rel in Files)
            {
                string path = Path.Combine(NsConfig.AppDir, rel);
                if (!File.Exists(path)) continue;
                foreach (string line in ReadLines(path))
                {
                    string c = line.Trim();
                    if (_en2zh.ContainsKey(c)) n++;
                }
            }
            return n;
        }

        public static bool Installed { get { return NsConfig.Installed; } }

        private static string[] ReadLines(string path)
        {
            return File.ReadAllText(path, Encoding.UTF8).Split('\n');
        }

        /// <summary>整目录备份到 %LOCALAPPDATA%\MenuFontTuner\backups\时间戳</summary>
        public static string Backup()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       @"MenuFontTuner\backups");
            string dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(dir);
            if (Directory.Exists(NsConfig.ImportsDir))
                CopyDir(NsConfig.ImportsDir, Path.Combine(dir, "imports"));
            string shell = Path.Combine(NsConfig.AppDir, "shell.nss");
            if (File.Exists(shell)) File.Copy(shell, Path.Combine(dir, "shell.nss"), true);
            return dir;
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
            {
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); } catch { }
            }
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        /// <summary>返回替换行数；needBackup 为 true 时先备份</summary>
        public static int Apply(bool toChinese)
        {
            Build();
            Dictionary<string, string> map = toChinese ? _en2zh : _zh2en;
            bool first = true;
            int total = 0;

            foreach (string rel in Files)
            {
                string path = Path.Combine(NsConfig.AppDir, rel);
                if (!File.Exists(path)) continue;

                byte[] raw = File.ReadAllBytes(path);
                bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
                string text = new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));

                string[] lines = text.Split('\n');
                int changed = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string cr = "";
                    if (line.EndsWith("\r")) { cr = "\r"; line = line.Substring(0, line.Length - 1); }
                    int k = 0;
                    while (k < line.Length && (line[k] == ' ' || line[k] == '\t')) k++;
                    string indent = line.Substring(0, k);
                    string content = line.Substring(k).TrimEnd();
                    string zh;
                    if (map.TryGetValue(content, out zh))
                    {
                        lines[i] = indent + zh + cr;
                        changed++;
                    }
                }

                if (changed > 0)
                {
                    if (first) { Backup(); first = false; }
                    File.WriteAllText(path, string.Join("\n", lines), new UTF8Encoding(bom));
                    total += changed;
                }
            }
            return total;
        }
    }

    // ===================== 主窗体 =====================
    internal class MainForm : Form
    {
        private float _scale;

        private FlatCombo _cmbFont;
        private FlatSpin _spin;
        private FlatButton _btnAuto;
        private FlatButton _btnApply;
        private FlatButton _btnZh;
        private FlatButton _btnClose;
        private MenuPreview _preview;
        private Label _lblStatus;

        private static readonly string[] FontPriority = { "Microsoft YaHei UI", "Microsoft YaHei", "等线", "宋体", "黑体", "Segoe UI" };

        public MainForm()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) _scale = g.DpiY / 96f;
            if (_scale <= 0) _scale = 1f;

            Text = "右键菜单字体";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Pal.Bg;
            ForeColor = Pal.Text;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(Px(540), Px(396));
            TryDoubleBuffer(this);

            BuildUi();
            LoadCurrent();
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(Handle, 19, ref on, 4);
            }
            catch { }
        }

        private int Px(float v) { return (int)Math.Round(v * _scale); }

        private void TryDoubleBuffer(Control c)
        {
            try
            {
                typeof(Control).GetProperty("DoubleBuffered",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .SetValue(c, true, null);
            }
            catch { }
        }

        private void BuildUi()
        {
            Font fUi = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            Font fLabel = new Font("Microsoft YaHei UI", Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            Font fSmall = new Font("Microsoft YaHei UI", Px(11), FontStyle.Regular, GraphicsUnit.Pixel);

            Label lblF = new Label();
            lblF.Text = "字体";
            lblF.Location = new Point(Px(40), Px(96));
            lblF.AutoSize = true;
            lblF.Font = fLabel;
            lblF.ForeColor = Pal.Sub;
            lblF.BackColor = Color.Transparent;

            _cmbFont = new FlatCombo();
            _cmbFont.Location = new Point(Px(76), Px(88));
            _cmbFont.Size = new Size(Px(196), Px(32));
            _cmbFont.Items = GetFontNames();
            _cmbFont.Changed += delegate { UpdatePreview(); };

            Label lblS = new Label();
            lblS.Text = "字号";
            lblS.Location = new Point(Px(288), Px(96));
            lblS.AutoSize = true;
            lblS.Font = fLabel;
            lblS.ForeColor = Pal.Sub;
            lblS.BackColor = Color.Transparent;

            _spin = new FlatSpin();
            _spin.Location = new Point(Px(322), Px(88));
            _spin.Size = new Size(Px(68), Px(32));
            _spin.Changed += delegate { UpdatePreview(); };

            _btnAuto = new FlatButton();
            _btnAuto.Text = "跟随系统";
            _btnAuto.Location = new Point(Px(404), Px(88));
            _btnAuto.Size = new Size(Px(90), Px(32));
            _btnAuto.Click += delegate { DoReset(); };

            _preview = new MenuPreview();
            _preview.Location = new Point(Px(30), Px(158));
            _preview.Size = new Size(Px(480), Px(148));

            _btnApply = new FlatButton();
            _btnApply.Text = "应用并立即生效";
            _btnApply.Primary = true;
            _btnApply.Location = new Point(Px(24), Px(324));
            _btnApply.Size = new Size(Px(172), Px(34));
            _btnApply.Click += delegate { DoApply(); };

            _btnZh = new FlatButton();
            _btnZh.Text = "汉化菜单";
            _btnZh.Location = new Point(Px(206), Px(324));
            _btnZh.Size = new Size(Px(112), Px(34));
            _btnZh.Click += delegate { DoLocalize(); };

            _btnClose = new FlatButton();
            _btnClose.Text = "关闭";
            _btnClose.Location = new Point(Px(450), Px(324));
            _btnClose.Size = new Size(Px(66), Px(34));
            _btnClose.Click += delegate { Close(); };

            _lblStatus = new Label();
            _lblStatus.Location = new Point(Px(26), Px(366));
            _lblStatus.Size = new Size(Px(490), Px(22));
            _lblStatus.Font = fSmall;
            _lblStatus.ForeColor = Pal.Ok;
            _lblStatus.BackColor = Color.Transparent;
            _lblStatus.Text = "";

            Controls.AddRange(new Control[] {
                lblF, _cmbFont, lblS, _spin, _btnAuto,
                _preview,
                _btnApply, _btnZh, _btnClose, _lblStatus
            });
        }

        private string[] GetFontNames()
        {
            List<string> names = new List<string>();
            try
            {
                using (InstalledFontCollection col = new InstalledFontCollection())
                {
                    List<string> all = new List<string>();
                    foreach (FontFamily ff in col.Families)
                    {
                        try { if (ff.IsStyleAvailable(FontStyle.Regular)) all.Add(ff.Name); } catch { }
                    }
                    all.Sort(StringComparer.CurrentCultureIgnoreCase);
                    foreach (string pref in FontPriority)
                        if (all.Contains(pref) && !names.Contains(pref)) names.Add(pref);
                    foreach (string s in all) if (!names.Contains(s)) names.Add(s);
                }
            }
            catch { names.Add("Microsoft YaHei UI"); }
            return names.ToArray();
        }

        private void LoadCurrent()
        {
            string name = NsConfig.ReadName();
            float size = NsConfig.ReadSize();
            if (size <= 0) size = 18f;
            if (size < 10f) size = 10f;
            if (size > 48f) size = 48f;
            _cmbFont.Value = name;
            _spin.Value = (decimal)size;
            UpdatePreview();
            RefreshZhButton();

            if (!NsConfig.Installed)
                ShowStatus("未检测到 Nilesoft Shell，请先安装后再使用", Pal.Warn);
        }

        private void UpdatePreview()
        {
            string face = _cmbFont.Value;
            if (string.IsNullOrEmpty(face)) face = "Microsoft YaHei UI";
            _preview.FaceName = face;
            _preview.SizePx = (float)_spin.Value;
            _preview.Invalidate();
        }

        private void RefreshZhButton()
        {
            bool cn = false;
            try { cn = NsConfig.Installed && Localizer.PendingCount() == 0; }
            catch { }
            _btnZh.Text = cn ? "还原英文" : "汉化菜单";
        }

        private void ShowStatus(string msg, Color c)
        {
            _lblStatus.Text = msg;
            _lblStatus.ForeColor = c;
        }

        private void DoApply()
        {
            string face = _cmbFont.Value;
            if (string.IsNullOrEmpty(face)) { ShowStatus("请先选择字体", Pal.Warn); return; }
            float size = (float)_spin.Value;

            try { NsConfig.Apply(face, size); }
            catch (UnauthorizedAccessException)
            {
                ShowStatus("需要管理员权限，请在弹窗中点“是”…", Pal.Warn);
                Application.DoEvents();
                if (!Elevated("--apply", face, size.ToString("0.##", CultureInfo.InvariantCulture)))
                {
                    ShowStatus("写入失败：管理员授权被取消", Pal.Warn);
                    return;
                }
            }
            catch (Exception ex)
            {
                ShowStatus("写入失败：" + ex.Message, Pal.Warn);
                return;
            }

            ShowStatus("已保存，正在重启资源管理器…", Pal.Ok);
            Application.DoEvents();
            NsConfig.RestartExplorer();
            ShowStatus(string.Format("已生效：{0} {1}px", face, size.ToString("0.##")), Pal.Ok);
        }

        private void DoReset()
        {
            try { NsConfig.ResetToAuto(); }
            catch (UnauthorizedAccessException)
            {
                ShowStatus("需要管理员权限，请在弹窗中点“是”…", Pal.Warn);
                Application.DoEvents();
                if (!Elevated("--reset", null, null)) { ShowStatus("操作被取消", Pal.Warn); return; }
            }
            catch (Exception ex)
            {
                ShowStatus("操作失败：" + ex.Message, Pal.Warn);
                return;
            }
            ShowStatus("已恢复跟随系统，正在重启资源管理器…", Pal.Ok);
            Application.DoEvents();
            NsConfig.RestartExplorer();
            ShowStatus("已恢复自动模式", Pal.Ok);
        }

        private void DoLocalize()
        {
            if (!NsConfig.Installed) { ShowStatus("未检测到 Nilesoft Shell", Pal.Warn); return; }

            bool toChinese;
            try { toChinese = Localizer.PendingCount() > 0; }
            catch { toChinese = true; }

            int n = -1;
            try { n = Localizer.Apply(toChinese); }
            catch (UnauthorizedAccessException)
            {
                ShowStatus("需要管理员权限，请在弹窗中点“是”…", Pal.Warn);
                Application.DoEvents();
                if (!Elevated(toChinese ? "--zh" : "--unzh", null, null))
                {
                    ShowStatus("操作被取消", Pal.Warn);
                    return;
                }
                n = -2; // 已由提权进程完成
            }
            catch (Exception ex)
            {
                ShowStatus("操作失败：" + ex.Message, Pal.Warn);
                return;
            }

            ShowStatus("正在重启资源管理器…", Pal.Ok);
            Application.DoEvents();
            NsConfig.RestartExplorer();
            RefreshZhButton();

            if (n == 0) ShowStatus("没有需要修改的内容", Pal.Warn);
            else if (toChinese) ShowStatus("菜单已汉化，正在生效", Pal.Ok);
            else ShowStatus("已还原为英文菜单", Pal.Ok);
        }

        private bool Elevated(string a1, string a2, string a3)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = Application.ExecutablePath;
                psi.Arguments = a3 == null
                    ? string.Format("\"{0}\"", a1)
                    : string.Format("\"{0}\" \"{1}\" \"{2}\"", a1, a2, a3);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                Process p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (Font ft = new Font("Microsoft YaHei UI", Px(18), FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(Pal.Text))
                g.DrawString("右键菜单字体", ft, b, Px(26), Px(17));

            using (Font fs = new Font("Microsoft YaHei UI", Px(11.5f), FontStyle.Regular, GraphicsUnit.Pixel))
            using (SolidBrush b = new SolidBrush(Pal.Sub))
                g.DrawString("由 Nilesoft Shell 渲染 · 改完点“应用”立即生效", fs, b, Px(26), Px(46));

            DrawCard(g, 22, 64, 496, 80);
            DrawCard(g, 22, 152, 496, 160);
        }

        private void DrawCard(Graphics g, float x, float y, float w, float h)
        {
            RectangleF rc = new RectangleF(Px(x), Px(y), Px(w), Px(h));
            using (GraphicsPath path = FlatControlPath(rc, Px(9)))
            using (SolidBrush b = new SolidBrush(Pal.Card))
                g.FillPath(b, path);
        }

        private static GraphicsPath FlatControlPath(RectangleF rc, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(rc.X, rc.Y, d, d, 180, 90);
            p.AddArc(rc.Right - d, rc.Y, d, d, 270, 90);
            p.AddArc(rc.Right - d, rc.Bottom - d, d, d, 0, 90);
            p.AddArc(rc.X, rc.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
