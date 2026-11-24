using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Kapeyn
{
    sealed class HelpTopic
    {
        public string Title;
        public readonly List<string> Lines = new List<string>();

        public string PlainText
        {
            get { return Title + "\n" + string.Join("\n", Lines.ToArray()).Replace("**", "").Replace("`", ""); }
        }

        public override string ToString() { return Title; } // for screen readers
    }

    // Reads help\help.md (embedded at build time) and keeps the Windows parts.
    static class HelpContent
    {
        public static List<HelpTopic> Load()
        {
            string text;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Kapeyn.help.md"))
            {
                if (s == null) return new List<HelpTopic>();
                using (var r = new StreamReader(s)) text = r.ReadToEnd();
            }
            return Parse(text);
        }

        public static List<HelpTopic> Parse(string text)
        {
            var topics = new List<HelpTopic>();
            HelpTopic current = null;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("%%") || line.StartsWith("[mac] ")) continue;
                if (line.StartsWith("[win] ")) line = line.Substring(6);
                line = Expand(line);
                if (line.StartsWith("# "))
                {
                    current = new HelpTopic { Title = line.Substring(2).Trim() };
                    topics.Add(current);
                }
                else if (current != null)
                {
                    current.Lines.Add(line);
                }
            }
            return topics;
        }

        static string Expand(string s)
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            return s.Replace("{computer}", "PC").Replace("{Computer}", "PC")
                    .Replace("{shortcut}", "Ctrl+Alt+K")
                    .Replace("{tray}", "system tray (bottom right of the taskbar)")
                    .Replace("{rightclick}", "right-click").Replace("{RightClick}", "Right-click")
                    .Replace("{startup}", "Start with Windows")
                    .Replace("{version}", v.Major + "." + v.Minor + "." + v.Build);
        }
    }

    // The Help window: topics (with search) on the left, the selected topic on the right.
    sealed class HelpForm : Form
    {
        static readonly Color Accent = Color.FromArgb(176, 92, 20);
        static readonly Color TipBack = Color.FromArgb(255, 246, 229);

        readonly List<HelpTopic> topics;
        readonly ListBox list;
        readonly TextBox search;
        readonly RichTextBox page;
        readonly Font bodyFont, boldFont, h1Font, h2Font, codeFont;
        readonly float scale;

        public HelpForm(string startTopic)
        {
            topics = HelpContent.Load();
            Font = SystemFonts.MessageBoxFont;
            Text = "Kapeyn Help";
            Icon = IconArt.CreateIcon(32, true);
            StartPosition = FormStartPosition.CenterScreen;
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            ClientSize = new Size((int)(920 * scale), (int)(640 * scale));
            MinimumSize = new Size((int)(640 * scale), (int)(420 * scale));

            string family = Font.FontFamily.Name;
            bodyFont = new Font(family, 10.5f);
            boldFont = new Font(family, 10.5f, FontStyle.Bold);
            h1Font = new Font(family, 18f, FontStyle.Bold);
            h2Font = new Font(family, 12.5f, FontStyle.Bold);
            codeFont = new Font("Consolas", 10f);

            // Left: search + topic list
            var left = new Panel { Dock = DockStyle.Left, Width = (int)(250 * scale), Padding = new Padding((int)(10 * scale)), BackColor = SystemColors.Control };
            search = new TextBox { Dock = DockStyle.Top, Font = bodyFont };
            SetCueBanner(search, "Search help");
            list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = (int)(34 * scale), Font = bodyFont,
                BackColor = SystemColors.Control,
            };
            list.DrawItem += DrawTopic;
            var gap = new Panel { Dock = DockStyle.Top, Height = (int)(8 * scale) };
            var link = new LinkLabel { Text = "Kapeyn on GitHub", Dock = DockStyle.Bottom, AutoSize = false, Height = (int)(28 * scale), TextAlign = ContentAlignment.MiddleLeft };
            link.LinkClicked += delegate { try { System.Diagnostics.Process.Start("https://github.com/jvbalcita/kapeyn"); } catch { } };
            left.Controls.Add(list);
            left.Controls.Add(gap);
            left.Controls.Add(search);
            left.Controls.Add(link);

            // Right: the page
            var right = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window, Padding = new Padding((int)(28 * scale), (int)(20 * scale), (int)(16 * scale), (int)(12 * scale)) };
            page = new RichTextBox
            {
                Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window,
                ScrollBars = RichTextBoxScrollBars.Vertical, DetectUrls = false, TabStop = false, Cursor = Cursors.Arrow,
            };
            right.Controls.Add(page);

            Controls.Add(right);
            Controls.Add(new Panel { Dock = DockStyle.Left, Width = 1, BackColor = SystemColors.ControlDark });
            Controls.Add(left);

            search.TextChanged += delegate { Filter(); };
            list.SelectedIndexChanged += delegate { var t = list.SelectedItem as HelpTopic; if (t != null) Render(t); };
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };

            Filter();
            ShowTopic(startTopic);
        }

        public void ShowTopic(string title)
        {
            if (title == null) return;
            for (int i = 0; i < list.Items.Count; i++)
                if (((HelpTopic)list.Items[i]).Title.Equals(title, StringComparison.OrdinalIgnoreCase)) { list.SelectedIndex = i; return; }
        }

        void Filter()
        {
            string q = search.Text.Trim();
            var selected = list.SelectedItem as HelpTopic;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var t in topics)
                if (q.Length == 0 || t.PlainText.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) list.Items.Add(t);
            list.EndUpdate();
            if (selected != null && list.Items.Contains(selected)) list.SelectedItem = selected;
            else if (list.Items.Count > 0) list.SelectedIndex = 0;
            else { page.Clear(); Append("No topics match \"" + q + "\".", bodyFont, SystemColors.GrayText); }
        }

        void DrawTopic(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? SystemColors.Window : list.BackColor)) e.Graphics.FillRectangle(b, e.Bounds);
            if (sel)
                using (var a = new SolidBrush(Accent))
                    e.Graphics.FillRectangle(a, e.Bounds.Left, e.Bounds.Top + 6, (int)(3 * scale), e.Bounds.Height - 12);
            var t = (HelpTopic)list.Items[e.Index];
            var r = new Rectangle(e.Bounds.Left + (int)(12 * scale), e.Bounds.Top, e.Bounds.Width - (int)(14 * scale), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, t.Title, sel ? boldFont : bodyFont, r, SystemColors.ControlText,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // ---- rendering the help markup ---------------------------------------------------------

        static readonly Regex Numbered = new Regex(@"^(\d+)\.\s+(.*)$");

        void Render(HelpTopic t)
        {
            page.SuspendLayout();
            page.Clear();
            Append(t.Title + "\n", h1Font, Accent, 0, 0, 0);
            Spacer(5);
            bool gap = true;      // just added vertical space
            bool inSteps = false; // previous line was a numbered step
            foreach (string line in t.Lines)
            {
                if (line.Length == 0)
                {
                    if (!gap) Spacer(8);
                    gap = true;
                    inSteps = false;
                    continue;
                }
                Match m;
                if (line.StartsWith("## "))
                {
                    Spacer(gap ? 4 : 12);
                    Append(line.Substring(3) + "\n", h2Font, SystemColors.WindowText, 0, 0, 0);
                    Spacer(3);
                    gap = true;
                    inSteps = false;
                    continue;
                }
                gap = false;
                if (line.StartsWith("- "))
                {
                    Paragraph(14, 14, false, true);
                    Inline(line.Substring(2));
                    EndParagraph();
                }
                else if ((m = Numbered.Match(line)).Success)
                {
                    Paragraph(0, 26, false, false);
                    Append(m.Groups[1].Value + ".\t", boldFont, Accent, -1, -1, -1);
                    Inline(m.Groups[2].Value);
                    EndParagraph();
                    inSteps = true;
                    continue;
                }
                else if (line.StartsWith("> "))
                {
                    Spacer(4);
                    Paragraph(10, 10, true, false);
                    Append("Tip  ", boldFont, Accent, -1, -1, -1);
                    Inline(line.Substring(2));
                    EndParagraph();
                }
                else
                {
                    // A note right under a numbered step belongs to that step: indent it to line up.
                    Paragraph(inSteps ? 26 : 0, inSteps ? 26 : 0, false, false);
                    Inline(line);
                    EndParagraph();
                    if (inSteps) continue;
                }
                inSteps = false;
            }
            page.Select(0, 0);
            page.ResumeLayout();
        }

        readonly Dictionary<int, Font> spacerFonts = new Dictionary<int, Font>();

        /// An empty line of the given height (in points), for tidy vertical spacing.
        void Spacer(int points)
        {
            Font f;
            if (!spacerFonts.TryGetValue(points, out f))
            {
                f = new Font(bodyFont.FontFamily, points);
                spacerFonts[points] = f;
            }
            Append("\n", f, SystemColors.WindowText, 0, 0, 0);
        }

        void Paragraph(int indent, int hanging, bool tip, bool bullet)
        {
            page.Select(page.TextLength, 0);
            page.SelectionBullet = bullet;
            page.SelectionIndent = (int)(indent * scale);
            page.SelectionHangingIndent = bullet ? (int)(10 * scale) : (int)((hanging - indent) * scale);
            page.SelectionBackColor = tip ? TipBack : SystemColors.Window;
            page.SelectionTabs = new[] { (int)(26 * scale) };
            page.SelectionRightIndent = 0;
        }

        void EndParagraph()
        {
            Append("\n", bodyFont, SystemColors.WindowText, -1, -1, -1);
            page.Select(page.TextLength, 0);
            page.SelectionBullet = false;
            page.SelectionIndent = 0;
            page.SelectionHangingIndent = 0;
            page.SelectionBackColor = SystemColors.Window;
        }

        // **bold** and `code`
        void Inline(string text)
        {
            bool bold = false, code = false;
            int i = 0;
            var sb = new System.Text.StringBuilder();
            while (i < text.Length)
            {
                if (!code && i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
                {
                    Flush(sb, bold, code);
                    bold = !bold;
                    i += 2;
                    continue;
                }
                if (text[i] == '`')
                {
                    Flush(sb, bold, code);
                    code = !code;
                    i++;
                    continue;
                }
                sb.Append(text[i]);
                i++;
            }
            Flush(sb, bold, code);
        }

        void Flush(System.Text.StringBuilder sb, bool bold, bool code)
        {
            if (sb.Length == 0) return;
            Append(sb.ToString(), code ? codeFont : bold ? boldFont : bodyFont,
                   code ? Color.FromArgb(150, 60, 0) : SystemColors.WindowText, -1, -1, -1);
            sb.Length = 0;
        }

        /// Appends text with a font/colour. indent/hanging/bullet of -1 keep the current paragraph format.
        void Append(string text, Font font, Color color, int indent, int hanging, int spaceAfter)
        {
            page.Select(page.TextLength, 0);
            page.SelectionFont = font;
            page.SelectionColor = color;
            if (indent >= 0) { page.SelectionIndent = indent; page.SelectionHangingIndent = hanging; page.SelectionBullet = false; }
            page.SelectedText = text;
        }

        void Append(string text, Font font, Color color)
        {
            Append(text, font, color, 0, 0, 0);
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        static void SetCueBanner(TextBox box, string text)
        {
            box.HandleCreated += delegate { SendMessage(box.Handle, 0x1501 /* EM_SETCUEBANNER */, (IntPtr)1, text); };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                bodyFont.Dispose(); boldFont.Dispose(); h1Font.Dispose(); h2Font.Dispose(); codeFont.Dispose();
                foreach (var f in spacerFonts.Values) f.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
