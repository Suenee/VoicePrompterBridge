using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace VPBridgeTray
{
    internal enum UiIconKind
    {
        Start, Stop, Restart, Exit, Settings, Mailboxes, Log, Copy, Regenerate, Running, Stopped, Error, Connected, Disconnected
    }

    internal static class UiIcons
    {
        // Precomputed multi-resolution SUB icon (16, 20, 24 and 32 px).
        // It uses the tuned artwork directly, with no runtime cropping or drawing.
        private const string AppIconIcoBase64 = "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";

        public static Icon CreateAppIcon(int size)
        {
            _ = size;
            byte[] data = Convert.FromBase64String(AppIconIcoBase64);
            using MemoryStream stream = new MemoryStream(data, false);
            using Icon temp = new Icon(stream);
            return (Icon)temp.Clone();
        }

        public static Bitmap Create(UiIconKind kind, int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 20f;

                if (kind == UiIconKind.Start)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(40, 170, 60)))
                        g.FillPolygon(b, new PointF[] { P(5,3,s), P(17,10,s), P(5,17,s) });
                }
                else if (kind == UiIconKind.Stop)
                {
                    using (Brush b = new SolidBrush(Color.FromArgb(220,45,45))) g.FillRectangle(b, 4*s, 4*s, 12*s, 12*s);
                }
                else if (kind == UiIconKind.Restart || kind == UiIconKind.Regenerate)
                {
                    using (Pen p = new Pen(Color.FromArgb(35,120,210), Math.Max(1.5f,2.3f*s)))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawArc(p, 3*s, 3*s, 14*s, 14*s, 45, 285);
                    }
                    using (Brush b = new SolidBrush(Color.FromArgb(35,120,210)))
                        g.FillPolygon(b, new PointF[] { P(15,2,s), P(18,7,s), P(12,7,s) });
                }
                else if (kind == UiIconKind.Exit)
                {
                    using (Pen p = new Pen(Color.FromArgb(200,45,45), Math.Max(1.5f,2.1f*s)))
                    {
                        g.DrawRectangle(p, 3*s, 4*s, 8*s, 12*s);
                        g.DrawLine(p, 9*s, 10*s, 17*s, 10*s);
                    }
                    using (Brush b = new SolidBrush(Color.FromArgb(200,45,45)))
                        g.FillPolygon(b, new PointF[] { P(14,6,s), P(18,10,s), P(14,14,s) });
                }
                else if (kind == UiIconKind.Settings)
                {
                    using (Pen p = new Pen(Color.FromArgb(85,85,85), Math.Max(1.2f,1.8f*s)))
                    {
                        g.DrawEllipse(p, 6*s, 6*s, 8*s, 8*s);
                        for (int i=0;i<8;i++)
                        {
                            double a=i*Math.PI/4;
                            float x1=10*s+(float)Math.Cos(a)*6*s, y1=10*s+(float)Math.Sin(a)*6*s;
                            float x2=10*s+(float)Math.Cos(a)*8*s, y2=10*s+(float)Math.Sin(a)*8*s;
                            g.DrawLine(p,x1,y1,x2,y2);
                        }
                    }
                }
                else if (kind == UiIconKind.Mailboxes)
                {
                    using (Pen p = new Pen(Color.FromArgb(70,105,150), Math.Max(1.2f,1.6f*s)))
                    {
                        g.DrawRectangle(p, 3*s, 4*s, 14*s, 5*s);
                        g.DrawRectangle(p, 3*s, 11*s, 14*s, 5*s);
                        g.DrawLine(p, 12*s, 6.5f*s, 15*s, 6.5f*s);
                        g.DrawLine(p, 12*s, 13.5f*s, 15*s, 13.5f*s);
                    }
                }
                else if (kind == UiIconKind.Log)
                {
                    using (Pen p = new Pen(Color.FromArgb(70,70,70), Math.Max(1.2f,1.6f*s)))
                    {
                        g.DrawRectangle(p, 4*s, 3*s, 12*s, 14*s);
                        g.DrawLine(p, 7*s, 7*s, 14*s, 7*s); g.DrawLine(p, 7*s, 10*s, 14*s, 10*s); g.DrawLine(p, 7*s, 13*s, 12*s, 13*s);
                    }
                }
                else if (kind == UiIconKind.Copy)
                {
                    using (Pen p = new Pen(Color.FromArgb(65,65,65), Math.Max(1.2f,1.5f*s)))
                    {
                        g.DrawRectangle(p, 6*s, 4*s, 9*s, 10*s);
                        g.DrawRectangle(p, 3*s, 7*s, 9*s, 10*s);
                    }
                }
                else
                {
                    Color c = Color.Gray;
                    if (kind == UiIconKind.Running || kind == UiIconKind.Connected) c = Color.FromArgb(35,170,70);
                    if (kind == UiIconKind.Stopped || kind == UiIconKind.Disconnected || kind == UiIconKind.Error) c = Color.FromArgb(215,55,55);
                    using (Brush b = new SolidBrush(c)) g.FillEllipse(b, 4*s, 4*s, 12*s, 12*s);
                }
            }
            return bmp;
        }

        private static PointF P(float x, float y, float s) { return new PointF(x*s,y*s); }
    }
}
