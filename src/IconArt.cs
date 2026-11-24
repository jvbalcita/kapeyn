using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Kapeyn
{
    // Coffee cup icon: steaming amber = awake, grey outline = off.
    static class IconArt
    {
        static readonly Color Amber = Color.FromArgb(255, 222, 128, 44);
        static readonly Color Grey = Color.FromArgb(255, 140, 140, 140);

        public static Bitmap Draw(int size, bool on)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                // Artwork is designed on a 32x32 grid.
                float s = size / 32f;
                g.ScaleTransform(s, s);
                g.TranslateTransform(1.5f, 0f);

                Color c = on ? Amber : Grey;
                float stroke = size <= 16 ? 3.2f : 2.6f; // a bit thicker at tiny sizes so it stays readable

                using (var pen = new Pen(c, stroke))
                using (var brush = new SolidBrush(c))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;

                    // handle (right half only, so it doesn't show inside the hollow "off" cup)
                    g.DrawArc(pen, 17.5f, 14.5f, 8.5f, 8f, -100f, 200f);

                    // cup body: flat top, rounded bottom
                    using (var body = RoundedBottom(3f, 12f, 18f, 15f, 5f))
                    {
                        if (on) g.FillPath(brush, body);
                        else g.DrawPath(pen, body);
                    }

                    // saucer
                    g.DrawLine(pen, 1.5f, 29.5f, 23.5f, 29.5f);

                    // steam
                    if (on)
                    {
                        using (var steam = new Pen(c, stroke * 0.8f))
                        {
                            steam.StartCap = LineCap.Round;
                            steam.EndCap = LineCap.Round;
                            for (int i = 0; i < 3; i++)
                            {
                                float x = 7f + i * 5f;
                                g.DrawBezier(steam, x, 9.5f, x - 2.2f, 7f, x + 2.2f, 4.5f, x, 2f);
                            }
                        }
                    }
                }
            }
            return bmp;
        }

        static GraphicsPath RoundedBottom(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddLine(x, y, x + w, y);
            p.AddLine(x + w, y, x + w, y + h - r);
            p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
            p.AddLine(x + w - r, y + h, x + r, y + h);
            p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Icon CreateIcon(int size, bool on)
        {
            using (var bmp = Draw(size, on))
            {
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
                }
                finally { Native.DestroyIcon(h); }
            }
        }

        // Multi-size .ico with PNG-compressed frames (Vista+).
        public static void SaveIco(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
            var frames = new List<byte[]>();
            foreach (int sz in sizes)
            {
                using (var bmp = Draw(sz, true))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    frames.Add(ms.ToArray());
                }
            }

            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((ushort)0);            // reserved
                w.Write((ushort)1);            // type: icon
                w.Write((ushort)sizes.Length); // count
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                    w.Write(dim); w.Write(dim);
                    w.Write((byte)0); w.Write((byte)0); // palette, reserved
                    w.Write((ushort)1);                 // planes
                    w.Write((ushort)32);                // bpp
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
            }
        }
    }
}
