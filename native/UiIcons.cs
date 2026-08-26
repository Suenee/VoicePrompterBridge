using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace VPBridgeTray
{
    internal enum UiIconKind
    {
        Start, Stop, Restart, Exit, Settings, Mailboxes, Log, Copy, Regenerate, Running, Stopped, Error, Connected, Disconnected
    }

    internal static class UiIcons
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon CreateAppIcon(int size)
        {
            Bitmap bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;

                using (Pen bridge = new Pen(Color.FromArgb(60, 110, 150), Math.Max(1.4f, 2.4f * s)))
                {
                    bridge.StartCap = LineCap.Round;
                    bridge.EndCap = LineCap.Round;
                    g.DrawLine(bridge, 4*s, 23*s, 28*s, 23*s);
                    g.DrawLine(bridge, 8*s, 23*s, 8*s, 14*s);
                    g.DrawLine(bridge, 24*s, 23*s, 24*s, 14*s);
                    g.DrawArc(bridge, 8*s, 7*s, 16*s, 15*s, 180, 180);
                }

                PointF[] wave = new PointF[] {
                    new PointF(3*s,16*s), new PointF(7*s,16*s), new PointF(10*s,10*s),
                    new PointF(14*s,21*s), new PointF(18*s,11*s), new PointF(21*s,16*s),
                    new PointF(29*s,16*s)
                };
                using (Pen p = new Pen(Color.FromArgb(25, 145, 230), Math.Max(1.6f, 2.6f * s)))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, wave);
                }
            }

            IntPtr handle = bmp.GetHicon();
            try
            {
                using (Icon temp = Icon.FromHandle(handle))
                    return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(handle);
                bmp.Dispose();
            }
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
