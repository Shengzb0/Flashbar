using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace FlashBar
{
    // 胶囊绘制
    internal sealed partial class PillForm
    {
        private static readonly Color Orange = Color.FromArgb(0xF7, 0xA2, 0x1B);
        private static readonly Color Ink = Color.FromArgb(0x1C, 0x1C, 0x1C);
        private const string FontName = "Microsoft YaHei UI";

        private static int Px(float v, float s) { return (int)Math.Round(v * s); }
        private static int LineHeight(float s) { return Math.Max(3, Px(4, s)); }
        private static Size CollapsedSize(float s) { return new Size(Px(120, s), Px(12, s)); }

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            GraphicsPath gp = new GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private static Bitmap NewCanvas(int w, int h, out Graphics g)
        {
            Bitmap bmp = new Bitmap(Math.Max(1, w), Math.Max(1, h), PixelFormat.Format32bppPArgb);
            g = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            return bmp;
        }

        private static Bitmap DrawCollapsed(float s)
        {
            Size sz = CollapsedSize(s);
            Graphics g;
            Bitmap bmp = NewCanvas(sz.Width, sz.Height, out g);
            using (g)
            {
                // alpha=1 的底，扩大鼠标可悬停区域但几乎不可见
                using (SolidBrush hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0)))
                    g.FillRectangle(hit, 0, 0, sz.Width, sz.Height);
                int lh = LineHeight(s);
                RectangleF line = new RectangleF(0, (sz.Height - lh) / 2, sz.Width, lh);
                using (GraphicsPath gp = Round(line, lh / 2f))
                using (SolidBrush b = new SolidBrush(Orange))
                    g.FillPath(b, gp);
            }
            return bmp;
        }

        private static Bitmap DrawExpanded(AttentionItem it, int extra, float s)
        {
            int h = Px(36, s), padL = Px(10, s), icon = Px(20, s), gap = Px(8, s), padR = Px(14, s);
            int maxW = Px(520, s), minW = Px(140, s);
            string text = it.AppName;
            if (!string.IsNullOrEmpty(it.Title) && it.Title != it.AppName) text += " · " + it.Title;
            string badge = extra > 0 ? "+" + extra : null;

            using (Font font = new Font(FontName, 13 * s, FontStyle.Regular, GraphicsUnit.Pixel))
            using (Font bfont = new Font(FontName, 12 * s, FontStyle.Bold, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                sf.LineAlignment = StringAlignment.Center;

                float textW, badgeW = 0;
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                {
                    pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    textW = pg.MeasureString(text, font, 10000, sf).Width + 2;
                    if (badge != null) badgeW = pg.MeasureString(badge, bfont, 1000, sf).Width + Px(14, s);
                }
                int fixedW = padL + icon + gap + padR + (badge != null ? (int)badgeW + gap : 0);
                int avail = maxW - fixedW;
                if (textW > avail) textW = avail;
                int w = Math.Max(minW, fixedW + (int)Math.Ceiling(textW));

                Graphics g;
                Bitmap bmp = NewCanvas(w, h, out g);
                using (g)
                {
                    RectangleF body = new RectangleF(0.5f, 0.5f, w - 1, h - 1);
                    using (GraphicsPath gp = Round(body, h / 2f))
                    using (SolidBrush fill = new SolidBrush(Orange))
                    using (Pen edge = new Pen(Color.FromArgb(90, 120, 60, 0), Math.Max(1f, s)))
                    {
                        g.FillPath(fill, gp);
                        g.DrawPath(edge, gp);
                    }
                    Rectangle ir = new Rectangle(padL, (h - icon) / 2, icon, icon);
                    DrawIcon(g, it, ir, s);

                    using (SolidBrush ink = new SolidBrush(Ink))
                    {
                        RectangleF tr = new RectangleF(ir.Right + gap, 0, textW, h);
                        g.DrawString(text, font, ink, tr, sf);
                        if (badge != null)
                        {
                            int bh = Px(20, s);
                            RectangleF br = new RectangleF(w - padR - badgeW + Px(4, s), (h - bh) / 2f, badgeW, bh);
                            using (GraphicsPath bp = Round(br, bh / 2f))
                            using (SolidBrush bb = new SolidBrush(Color.FromArgb(48, 0, 0, 0)))
                                g.FillPath(bb, bp);
                            using (StringFormat c = new StringFormat(sf))
                            {
                                c.Alignment = StringAlignment.Center;
                                g.DrawString(badge, bfont, ink, br, c);
                            }
                        }
                    }
                }
                return bmp;
            }
        }

        private static void DrawIcon(Graphics g, AttentionItem it, Rectangle r, float s)
        {
            if (it.Icon != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(it.Icon, r);
                return;
            }
            using (SolidBrush b = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillEllipse(b, r);
            string ch = string.IsNullOrEmpty(it.AppName) ? "?" : it.AppName.Substring(0, 1);
            using (Font f = new Font(FontName, 11 * s, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush ink = new SolidBrush(Ink))
            using (StringFormat c = new StringFormat())
            {
                c.Alignment = StringAlignment.Center;
                c.LineAlignment = StringAlignment.Center;
                g.DrawString(ch, f, ink, r, c);
            }
        }
    }
}
