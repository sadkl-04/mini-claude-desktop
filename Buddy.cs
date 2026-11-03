// Claude Buddy: a tiny desktop pet. Single file, builds with the csc.exe that ships with Windows (C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

enum St { Idle, Walk, Code, Think, Coffee, Sleep, Hunt, Happy, Drag, Fall, Dizzy, Eat, Pet, Play, Chase, Dance, Leap, Perch, Crawl, Dangle, Peek, Weave }
enum Eye { Open, Wide, Closed, Happy, Dizzy }
enum ArmPose { Rest, Up, Type, Hold, Wave }

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    public const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_COPYDATA = 0x4A;
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct BLEND { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint Size, Time; }
    [StructLayout(LayoutKind.Sequential)] public struct COPYDATA { public IntPtr Tag; public int Bytes; public IntPtr Data; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLEND blend, int flags);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, ref COPYDATA l, int flags, int ms, out IntPtr result);
}

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr prm, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
}

[ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioMeterInformation
{
    [PreserveSig] int GetPeakValue(out float peak);
}

// Reads only the loudness of the default output device; the sound itself is never captured.
class AudioMeter
{
    const int RenderFlow = 0, MultimediaRole = 1, ClsCtxAll = 23, RefreshTicks = 450;
    IAudioMeterInformation meter;
    int age = RefreshTicks;

    public float Peak()
    {
        // Re-acquired now and then, so switching to headphones is picked up.
        if (++age > RefreshTicks) { age = 0; Acquire(); }
        if (meter == null) return 0;
        try
        {
            float peak;
            if (meter.GetPeakValue(out peak) == 0) return peak;
        }
        catch (COMException) { }
        Release();
        return 0;
    }

    void Acquire()
    {
        Release();
        object en = null;
        IMMDevice device = null;
        try
        {
            en = new MMDeviceEnumerator();
            if (((IMMDeviceEnumerator)en).GetDefaultAudioEndpoint(RenderFlow, MultimediaRole, out device) != 0 || device == null) return;
            Guid iid = typeof(IAudioMeterInformation).GUID;
            object iface;
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out iface) == 0) meter = (IAudioMeterInformation)iface;
        }
        catch (COMException) { }
        catch (InvalidCastException) { }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            if (en != null) Marshal.ReleaseComObject(en);
        }
    }

    void Release()
    {
        if (meter != null) Marshal.ReleaseComObject(meter);
        meter = null;
    }
}

class Pose
{
    public int Face = 1, Legs, Bob, LookX, LookY, Anim, LaptopFrame;
    public Eye Eyes = Eye.Open;
    public ArmPose Arms = ArmPose.Rest;
    public bool Laptop, Mug, Mouth;

    public int Sig()
    {
        unchecked
        {
            int s = Face * 3 + Legs * 7 + Bob * 13 + LookX * 19 + LookY * 23 + Anim * 29 + LaptopFrame * 31;
            s = s * 37 + (int)Eyes;
            s = s * 37 + (int)Arms;
            s = s * 37 + (Laptop ? 1 : 0) + (Mug ? 2 : 0) + (Mouth ? 4 : 0);
            return s;
        }
    }
}

// All drawing is on a coarse pixel grid: the buddy occupies 16x11 cells, u screen pixels each.
static class Painter
{
    public const int GridW = 16, GridH = 11;
    static readonly Brush Body = new SolidBrush(Color.FromArgb(217, 119, 87));
    static readonly Brush Shade = new SolidBrush(Color.FromArgb(190, 96, 66));
    static readonly Brush Dark = new SolidBrush(Color.FromArgb(38, 26, 20));
    static readonly Brush MetalLight = new SolidBrush(Color.FromArgb(150, 156, 166));
    static readonly Brush MetalDark = new SolidBrush(Color.FromArgb(70, 74, 82));
    static readonly Brush ScreenBg = new SolidBrush(Color.FromArgb(24, 26, 32));
    static readonly Brush White = new SolidBrush(Color.FromArgb(240, 236, 228));
    static readonly Brush Coffee = new SolidBrush(Color.FromArgb(92, 58, 36));
    static readonly Brush Steam = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
    static readonly Brush Heart = new SolidBrush(Color.FromArgb(232, 80, 96));
    static readonly Brush Crust = new SolidBrush(Color.FromArgb(200, 150, 90));
    // Alpha 1 is invisible but still receives clicks on a layered window, unlike alpha 0.
    static readonly Brush HitPad = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
    static readonly Brush[] CodeInk = {
        new SolidBrush(Color.FromArgb(126, 211, 135)), new SolidBrush(Color.FromArgb(240, 160, 110)),
        new SolidBrush(Color.FromArgb(120, 180, 240)), new SolidBrush(Color.FromArgb(225, 220, 205))
    };
    static readonly Font BubbleFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
    static readonly Brush BubbleBg = new SolidBrush(Color.FromArgb(244, 34, 32, 30));
    static readonly Brush BubbleInk = new SolidBrush(Color.FromArgb(245, 240, 230));
    static readonly Pen BubbleEdge = new Pen(Color.FromArgb(217, 119, 87), 1.5f);

    public static void Buddy(Graphics g, int ox, int oy, int u, Pose p)
    {
        Action<Brush, int, int, int, int> abs = (b, x, y, w, h) =>
            g.FillRectangle(b, ox + x * u, oy + y * u, w * u, h * u);
        // Accessories and the front arm mirror with the facing direction.
        Action<Brush, int, int, int, int> rel = (b, x, y, w, h) =>
        {
            if (p.Face < 0) x = 15 - (x + w - 1);
            g.FillRectangle(b, ox + x * u, oy + y * u, w * u, h * u);
        };

        int dy = p.Bob + (p.Legs == 5 ? 2 : 0);
        DrawLegs(abs, p, dy);
        if (p.Laptop) DrawLaptop(rel, p);

        abs(Body, 2, 1 + dy, 12, 7);
        abs(Shade, 2, 7 + dy, 12, 1);

        switch (p.Arms)
        {
            case ArmPose.Up:
                abs(Body, 0, 0 + dy + p.Anim % 2, 2, 3);
                abs(Body, 14, 0 + dy + p.Anim % 2, 2, 3);
                break;
            case ArmPose.Type:
                rel(Body, 0, 3 + dy, 2, 2);
                rel(Body, 14, 6 + dy, 2, 2);
                rel(Body, 16, 7 + dy + p.Anim % 2, 3, 1);
                break;
            case ArmPose.Hold:
                rel(Body, 0, 3 + dy, 2, 2);
                rel(Body, 14, 4 + dy, 3, 2);
                rel(White, 17, 2 + dy, 3, 4);
                rel(Coffee, 17, 2 + dy, 3, 1);
                rel(White, 20, 3 + dy, 1, 2);
                rel(Steam, 18, 0 + dy - p.Anim % 2, 1, 1);
                break;
            case ArmPose.Wave:
                bool leftUp = p.Anim % 2 == 0;
                abs(Body, 0, (leftUp ? 0 : 3) + dy, 2, leftUp ? 3 : 2);
                abs(Body, 14, (leftUp ? 3 : 0) + dy, 2, leftUp ? 2 : 3);
                break;
            default:
                abs(Body, 0, 3 + dy, 2, 2);
                abs(Body, 14, 3 + dy, 2, 2);
                break;
        }

        DrawEyes(abs, p, dy);
        if (p.Mouth) abs(Dark, 7, 6 + dy, 2, 1);
    }

    static void DrawLegs(Action<Brush, int, int, int, int> abs, Pose p, int dy)
    {
        int[] xs = { 3, 5, 10, 12 };
        for (int i = 0; i < 4; i++)
        {
            int h = 3 - dy;
            if (p.Legs == 1 && i % 2 == 0) h--;
            if (p.Legs == 2 && i % 2 == 1) h--;
            if (p.Legs == 3 && i % 2 == 0) h--;
            if (p.Legs == 4 && i % 2 == 1) h--;
            if (h > 0) abs(Shade, xs[i], 8 + dy, 1, h);
        }
    }

    static void DrawEyes(Action<Brush, int, int, int, int> abs, Pose p, int dy)
    {
        int lx = p.LookX, y = 3 + dy + p.LookY;
        switch (p.Eyes)
        {
            case Eye.Wide:
                abs(Dark, 4 + lx, y, 2, 2); abs(Dark, 10 + lx, y, 2, 2);
                break;
            case Eye.Closed:
                abs(Dark, 4 + lx, y + 1, 2, 1); abs(Dark, 10 + lx, y + 1, 2, 1);
                break;
            case Eye.Happy:
                foreach (int x in new[] { 4, 9 })
                {
                    abs(Dark, x, y + 1, 1, 1); abs(Dark, x + 1, y, 1, 1); abs(Dark, x + 2, y + 1, 1, 1);
                }
                break;
            case Eye.Dizzy:
                foreach (int x in new[] { 4, 9 })
                {
                    abs(Dark, x, y, 1, 1); abs(Dark, x + 2, y, 1, 1); abs(Dark, x + 1, y + 1, 1, 1);
                    abs(Dark, x, y + 2, 1, 1); abs(Dark, x + 2, y + 2, 1, 1);
                }
                break;
            default:
                abs(Dark, 5 + lx, y, 1, 2); abs(Dark, 10 + lx, y, 1, 2);
                break;
        }
    }

    static void DrawLaptop(Action<Brush, int, int, int, int> rel, Pose p)
    {
        rel(MetalLight, 18, 10, 11, 1);
        rel(MetalDark, 20, 2, 9, 8);
        rel(ScreenBg, 21, 3, 7, 6);
        for (int i = 0; i < 3; i++)
        {
            int seed = (p.LaptopFrame / 18 + i) * 7919;
            int w = 2 + Math.Abs(seed) % 5;
            if (i == 2) w = Math.Min(w, 1 + p.LaptopFrame / 3 % 6);
            rel(CodeInk[Math.Abs(seed / 3) % CodeInk.Length], 21 + (seed % 2 == 0 ? 0 : 1), 3 + i * 2, Math.Min(w, 6), 1);
        }
    }

    public static void Bug(Graphics g, int x, int ground, int u, int frame)
    {
        g.FillRectangle(Dark, x + u / 2, ground - 2 * u, 3 * u, u + u / 2);
        g.FillRectangle(Heart, x + 3 * u, ground - 2 * u, u / 2 + 1, u / 2 + 1);
        int off = frame / 2 % 2;
        g.FillRectangle(Dark, x + off * u / 2, ground - u / 2, u / 2 + 1, u / 2);
        g.FillRectangle(Dark, x + 3 * u - off * u / 2, ground - u / 2, u / 2 + 1, u / 2);
    }

    public static void Pad(Graphics g, Rectangle r) { g.FillRectangle(HitPad, r); }

    public static void Present(IntPtr window, Bitmap frame, int winX, int winY)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr mem = Native.CreateCompatibleDC(screen);
        IntPtr hbmp = frame.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(mem, hbmp);
        var pos = new Native.POINT { X = winX, Y = winY };
        var size = new Native.SIZE { W = frame.Width, H = frame.Height };
        var src = new Native.POINT();
        var blend = new Native.BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
        Native.UpdateLayeredWindow(window, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
        Native.SelectObject(mem, old);
        Native.DeleteObject(hbmp);
        Native.DeleteDC(mem);
        Native.ReleaseDC(IntPtr.Zero, screen);
    }

    public static void Thread(Graphics g, int x1, int y1, int x2, int y2, int width)
    {
        using (var pen = new Pen(Color.FromArgb(235, 170, 176, 188), width)) g.DrawLine(pen, x1, y1, x2, y2);
    }

    // Toys are 5 cells wide; bottom is the pixel row of their lower edge.
    public static void Ball(Graphics g, int x, int bottom, int u, int spin)
    {
        int y = bottom - 5 * u;
        g.FillRectangle(Heart, x + u, y, 3 * u, 5 * u);
        g.FillRectangle(Heart, x, y + u, 5 * u, 3 * u);
        g.FillRectangle(White, x + (spin % 3 + 1) * u, y + u, u, 3 * u);
    }

    public static void Cookie(Graphics g, int x, int bottom, int u)
    {
        int y = bottom - 4 * u;
        g.FillRectangle(Crust, x + u, y, 3 * u, 4 * u);
        g.FillRectangle(Crust, x, y + u, 5 * u, 2 * u);
        g.FillRectangle(Coffee, x + u, y + u, u, u);
        g.FillRectangle(Coffee, x + 3 * u, y + 2 * u, u, u);
    }

    public static void PixelHeart(Graphics g, int x, int y, int s, int alpha)
    {
        using (var b = new SolidBrush(Color.FromArgb(alpha, 232, 80, 96)))
        {
            g.FillRectangle(b, x, y, 2 * s, s); g.FillRectangle(b, x + 3 * s, y, 2 * s, s);
            g.FillRectangle(b, x - s, y + s, 7 * s, 2 * s);
            g.FillRectangle(b, x, y + 3 * s, 5 * s, s);
            g.FillRectangle(b, x + s, y + 4 * s, 3 * s, s);
            g.FillRectangle(b, x + 2 * s, y + 5 * s, s, s);
        }
    }

    public static void Bubble(Graphics g, string text, int cx, int top, int winW, float d, int below = -1)
    {
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        SizeF sz = g.MeasureString(text, BubbleFont, (int)(210 * d));
        int pad = (int)(7 * d), tail = below >= 0 ? 0 : (int)(5 * d);
        int w = Math.Max((int)Math.Ceiling(sz.Width) + pad * 2, (int)(30 * d));
        int h = (int)Math.Ceiling(sz.Height) + pad;
        int x = Math.Max(2, Math.Min(cx - w / 2, winW - w - 3));
        int y = below >= 0 ? below + (int)(4 * d) : Math.Max(2, top - h - tail - (int)(3 * d));
        using (var path = new GraphicsPath())
        {
            int r = (int)(8 * d);
            path.AddArc(x, y, r, r, 180, 90);
            path.AddArc(x + w - r, y, r, r, 270, 90);
            path.AddArc(x + w - r, y + h - r, r, r, 0, 90);
            int tx = Math.Max(x + r, Math.Min(cx, x + w - r - tail));
            path.AddLine(tx + tail, y + h, tx, y + h + tail);
            path.AddLine(tx, y + h + tail, tx - tail / 2, y + h);
            path.AddArc(x, y + h - r, r, r, 90, 90);
            path.CloseFigure();
            g.FillPath(BubbleBg, path);
            g.DrawPath(BubbleEdge, path);
        }
        g.DrawString(text, BubbleFont, BubbleInk, new RectangleF(x + pad, y + pad / 2, w - pad * 2 + 2, h));
        g.SmoothingMode = old;
    }

    // Debug helper: every pose on one sheet, so the art can be checked without watching the desktop.
    public static void Sheet(string path)
    {
        var poses = new List<Pose> {
            new Pose(), new Pose { Eyes = Eye.Closed }, new Pose { Legs = 1 }, new Pose { Legs = 2, LookX = 1 },
            new Pose { Arms = ArmPose.Up, Eyes = Eye.Happy }, new Pose { Arms = ArmPose.Up, Eyes = Eye.Wide, Legs = 3 },
            new Pose { Eyes = Eye.Dizzy }, new Pose { Legs = 5, Eyes = Eye.Closed },
            new Pose { Laptop = true, Arms = ArmPose.Type, LookX = 1, LaptopFrame = 40 },
            new Pose { Laptop = true, Arms = ArmPose.Type, LookX = -1, Face = -1, LaptopFrame = 90, Anim = 1 },
            new Pose { Arms = ArmPose.Hold, Mug = true, Eyes = Eye.Happy },
            new Pose { Arms = ArmPose.Wave, Eyes = Eye.Happy, Legs = 1 }, new Pose { Arms = ArmPose.Wave, Eyes = Eye.Happy, Legs = 2, Anim = 1 },
            new Pose { Mouth = true, Eyes = Eye.Happy }, new Pose { Mouth = true, Eyes = Eye.Wide }
        };
        int u = 5, cw = 34 * u, ch = 16 * u;
        using (var bmp = new Bitmap(cw * 4, ch * 5 + 90))
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(70, 110, 150));
            for (int i = 0; i < poses.Count; i++)
                Buddy(g, i % 4 * cw + 14 * u * (poses[i].Face < 0 ? 1 : 0) + 8, i / 4 * ch + 2 * u, u, poses[i]);
            Bug(g, 3 * cw + 20, 5 * ch - 10, u, 0);
            PixelHeart(g, 3 * cw + 90, 5 * ch - 60, 3, 255);
            Ball(g, 2 * cw + 20, 5 * ch - 10, u, 1);
            Cookie(g, 2 * cw + 80, 5 * ch - 10, u);
            Bubble(g, "fix: теперь точно работает (нет)", 200, ch * 5 + 80, cw * 4, 1.3f);
            bmp.Save(path, ImageFormat.Png);
        }
    }
}

// A corner web built like a real one: spokes first, then a spiral from the rim inwards. P runs from 0 to 1.
class Web
{
    const int Spokes = 9, Rings = 7;
    const double SpokePart = 0.3;
    public readonly int Side;
    public readonly float Radius;
    public double P;

    public Web(int side, float radius)
    {
        Side = side;
        Radius = radius;
    }

    double SpokeLength(int i) { return Radius * (0.86 + 0.14 * Math.Sin(i * 2.3 + 1)); }

    // Distance is measured from the corner: X away from the side wall, Y down from the top.
    PointF OnSpoke(int i, double fraction)
    {
        double a = (i + 0.5) / Spokes * Math.PI / 2, r = SpokeLength(i) * fraction;
        return new PointF((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r));
    }

    double RingFraction(int ring) { return (double)(Rings - ring) / Rings * 0.92; }

    // Threads sag towards the corner between two spokes.
    PointF OnArc(int ring, int from, double t)
    {
        double k = RingFraction(ring);
        PointF a = OnSpoke(from, k), b = OnSpoke(from + 1, k);
        double sag = 1 - 0.1 * Math.Sin(t * Math.PI);
        return new PointF((float)((a.X + (b.X - a.X) * t) * sag), (float)((a.Y + (b.Y - a.Y) * t) * sag));
    }

    static int ArcCount { get { return (Spokes - 1) * Rings; } }

    // The ring, the spoke it leaves from, and how far along. Rings run alternately both ways, so the thread is continuous.
    static void Arc(int n, out int ring, out int from, out bool reverse)
    {
        ring = n / (Spokes - 1);
        reverse = ring % 2 == 1;
        int j = n % (Spokes - 1);
        from = reverse ? Spokes - 2 - j : j;
    }

    public PointF Tip()
    {
        if (P < SpokePart)
        {
            double v = P / SpokePart * Spokes;
            int i = Math.Min(Spokes - 1, (int)v);
            return OnSpoke(i, Math.Min(1, v - i));
        }
        double w = Math.Min((P - SpokePart) / (1 - SpokePart) * ArcCount, ArcCount - 0.001);
        int n = (int)w, ring, from;
        bool reverse;
        Arc(n, out ring, out from, out reverse);
        double t = w - n;
        return OnArc(ring, from, reverse ? 1 - t : t);
    }

    public void Draw(Graphics g, int size, float alpha, float scale)
    {
        Func<PointF, PointF> place = q => new PointF(Side < 0 ? q.X : size - q.X, q.Y);
        using (var under = new Pen(Color.FromArgb((int)(70 * alpha), 30, 32, 44), 2.2f * scale))
        using (var over = new Pen(Color.FromArgb((int)(235 * alpha), 244, 246, 252), 1.1f * scale))
        {
            foreach (Pen pen in new[] { under, over })
            {
                double spokes = Math.Min(1, P / SpokePart) * Spokes;
                for (int i = 0; i < Spokes && i < spokes; i++)
                    g.DrawLine(pen, place(new PointF(0, 0)), place(OnSpoke(i, Math.Min(1, spokes - i))));
                if (P <= SpokePart) continue;
                double w = (P - SpokePart) / (1 - SpokePart) * ArcCount;
                for (int n = 0; n < ArcCount && n < w; n++)
                {
                    int ring, from;
                    bool reverse;
                    Arc(n, out ring, out from, out reverse);
                    double end = Math.Min(1, w - n);
                    var line = new PointF[7];
                    for (int s = 0; s < line.Length; s++)
                    {
                        double t = end * s / (line.Length - 1);
                        line[s] = place(OnArc(ring, from, reverse ? 1 - t : t));
                    }
                    g.DrawLines(pen, line);
                }
            }
        }
    }
}

// Its own window, so the web stays in the corner when the buddy walks off. Clicks always pass through it.
class WebForm : Form
{
    readonly Bitmap bmp;
    readonly Graphics g;
    readonly int size;
    readonly float scale;
    double shownP = -1;
    float shownAlpha = -1;

    public WebForm(int size, float scale)
    {
        this.size = size;
        this.scale = scale;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void Repaint() { shownP = -1; }

    public void Apply(Web web, float alpha, Rectangle work)
    {
        if (!Visible || (Math.Abs(web.P - shownP) < 0.0015 && alpha == shownAlpha)) return;
        shownP = web.P;
        shownAlpha = alpha;
        g.Clear(Color.Transparent);
        web.Draw(g, size, alpha, scale);
        Painter.Present(Handle, bmp, web.Side < 0 ? work.Left : work.Right - size, work.Top);
    }
}

class Particle
{
    public float X, Y, Vx, Vy, Age, Life;
    public string Text;
    public Color Ink;
    public bool Heart;
}

class DeskFolder
{
    public string Name;
    public Rectangle Box;
    public IntPtr Hwnd; // set when this is an application window rather than a desktop folder
}

// Finds folders on the desktop through UI Automation. Only reads what Explorer shows; nothing is opened.
static class Desktop
{
    static IntPtr IconList()
    {
        IntPtr top = IntPtr.Zero;
        foreach (string cls in new[] { "Progman", "WorkerW" })
        {
            while ((top = Native.FindWindowEx(IntPtr.Zero, top, cls, null)) != IntPtr.Zero)
            {
                IntPtr view = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (view == IntPtr.Zero) continue;
                IntPtr list = Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
                if (list != IntPtr.Zero) return list;
            }
        }
        return IntPtr.Zero;
    }

    static bool IsFolder(string name)
    {
        try
        {
            return Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name))
                || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), name));
        }
        catch (ArgumentException) { return false; }
    }

    // Slow enough to stall animation, and UI Automation should not run on the thread owning a window: call off the UI thread.
    public static List<DeskFolder> Folders()
    {
        var found = new List<DeskFolder>();
        try
        {
            IntPtr list = IconList();
            if (list == IntPtr.Zero) return found;
            var isItem = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem);
            foreach (AutomationElement item in AutomationElement.FromHandle(list).FindAll(TreeScope.Children, isItem))
            {
                var info = item.Current;
                if (info.IsOffscreen || info.BoundingRectangle.IsEmpty || !IsFolder(info.Name)) continue;
                System.Windows.Rect r = info.BoundingRectangle;
                found.Add(new DeskFolder { Name = info.Name, Box = new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height) });
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (COMException) { }
        return found;
    }

    // The frontmost ordinary window, or null when it leaves no room above it (maximized, say).
    public static DeskFolder TopWindow(IntPtr self, int minTop)
    {
        for (IntPtr h = Native.GetTopWindow(IntPtr.Zero); h != IntPtr.Zero; h = Native.GetWindow(h, 2))
        {
            if (h == self || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.GetWindowTextLength(h) == 0) continue;
            if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) continue;
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) continue;
            var cls = new StringBuilder(32);
            Native.GetClassName(h, cls, 32);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") continue;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r) || r.R - r.L < 200 || r.B - r.T < 100) continue;
            if (r.T < minTop) return null;
            return new DeskFolder { Name = "окно", Box = Rectangle.FromLTRB(r.L, r.T, r.R, r.B), Hwnd = h };
        }
        return null;
    }

    // False when a window covers the folder: there would be nothing to stand on.
    public static bool Visible(DeskFolder f)
    {
        var p = new Native.POINT { X = f.Box.Left + f.Box.Width / 2, Y = f.Box.Top + f.Box.Height / 3 };
        var cls = new StringBuilder(32);
        Native.GetClassName(Native.GetAncestor(Native.WindowFromPoint(p), 2), cls, 32);
        return cls.ToString() == "Progman" || cls.ToString() == "WorkerW";
    }
}

// `ClaudeBuddy.exe --notify` is run by a Claude Code Stop hook and passes the news to the running buddy.
static class TaskNotice
{
    public static readonly IntPtr Tag = new IntPtr(0x434C4144);

    public static void RunHook()
    {
        string json = "";
        var reader = new Thread(() =>
        {
            using (var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false))) json = input.ReadToEnd();
        });
        reader.IsBackground = true;
        reader.Start();
        reader.Join(2000);
        string project;
        if (Project(json, out project)) Broadcast(project);
    }

    // False when the hook is already re-running: that turn has been reported.
    public static bool Project(string json, out string project)
    {
        project = "";
        if (Regex.IsMatch(json, "\"stop_hook_active\"\\s*:\\s*true")) return false;
        Match m = Regex.Match(json, "\"cwd\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        if (!m.Success) return true;
        try
        {
            string cwd = Regex.Unescape(m.Groups[1].Value).TrimEnd('\\', '/');
            string name = Path.GetFileName(cwd);
            // Sessions without a chosen folder run in generated "scratch-…" folders, whose name says nothing.
            if (!name.StartsWith("scratch-", StringComparison.OrdinalIgnoreCase)) project = name;
        }
        catch (ArgumentException) { }
        return true;
    }

    // Matches on window class too: a folder window named ClaudeBuddy has the same title.
    static void Broadcast(string project)
    {
        IntPtr h = IntPtr.Zero;
        while ((h = Native.FindWindowEx(IntPtr.Zero, h, null, "ClaudeBuddy")) != IntPtr.Zero)
        {
            var cls = new StringBuilder(64);
            Native.GetClassName(h, cls, 64);
            if (cls.ToString().StartsWith("WindowsForms", StringComparison.Ordinal)) Send(h, project);
        }
    }

    public static void Send(IntPtr h, string project)
    {
        IntPtr text = Marshal.StringToHGlobalUni(project);
        try
        {
            var data = new Native.COPYDATA { Tag = Tag, Bytes = project.Length * 2, Data = text };
            IntPtr result;
            Native.SendMessageTimeout(h, Native.WM_COPYDATA, IntPtr.Zero, ref data, 2, 2000, out result);
        }
        finally { Marshal.FreeHGlobal(text); }
    }
}

class Toy
{
    public bool IsBall;
    public double X, Air, Vx, Vy, Age;
}

class Settings
{
    public int Size = 1;
    public bool Quiet, Topmost = true, Sync = true, Greeted, Music = true, Climb = true, Notify = true, Weave = true;
    // Next to the exe: AppData may be redirected per app, and the installer must not write there.
    public static string Dir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
    static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (string line in File.ReadAllLines(FilePath))
            {
                string[] kv = line.Split('=');
                if (kv.Length != 2) continue;
                if (kv[0] == "size") int.TryParse(kv[1], out s.Size);
                if (kv[0] == "quiet") s.Quiet = kv[1] == "1";
                if (kv[0] == "topmost") s.Topmost = kv[1] == "1";
                if (kv[0] == "sync") s.Sync = kv[1] == "1";
                if (kv[0] == "greeted") s.Greeted = kv[1] == "1";
                if (kv[0] == "music") s.Music = kv[1] == "1";
                if (kv[0] == "climb") s.Climb = kv[1] == "1";
                if (kv[0] == "notify") s.Notify = kv[1] == "1";
                if (kv[0] == "weave") s.Weave = kv[1] == "1";
            }
        }
        catch (IOException) { }
        s.Size = Math.Max(0, Math.Min(2, s.Size));
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[] {
                "size=" + Size, "quiet=" + (Quiet ? 1 : 0), "topmost=" + (Topmost ? 1 : 0),
                "sync=" + (Sync ? 1 : 0), "greeted=" + (Greeted ? 1 : 0), "music=" + (Music ? 1 : 0), "climb=" + (Climb ? 1 : 0), "notify=" + (Notify ? 1 : 0), "weave=" + (Weave ? 1 : 0)
            });
        }
        catch (IOException) { }
    }
}

static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "ClaudeBuddy";

    // A logon task, not a Run key: Explorer was seen silently skipping the Run entry at logon.
    public static bool Enabled
    {
        get { return SchTasks("/query /tn " + Name) == 0; }
        set
        {
            if (value) Register();
            else SchTasks("/delete /f /tn " + Name);
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.DeleteValue(Name, false);
        }
    }

    static void Register()
    {
        string xml = TaskXml(WindowsIdentity.GetCurrent().Name, Application.ExecutablePath);
        string file = Path.Combine(Path.GetTempPath(), "ClaudeBuddyTask.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            if (SchTasks("/create /f /tn " + Name + " /xml \"" + file + "\"") != 0)
                throw new InvalidOperationException("schtasks could not create the logon task");
        }
        finally { File.Delete(file); }
    }

    static string TaskXml(string user, string exe)
    {
        string u = SecurityElement.Escape(user), e = SecurityElement.Escape(exe);
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
            "<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + u + "</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>" +
            "<Principals><Principal id=\"Author\"><UserId>" + u + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>" +
            "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>" +
            "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
            "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>6</Priority>" +
            "<RestartOnFailure><Interval>PT1M</Interval><Count>3</Count></RestartOnFailure></Settings>" +
            "<Actions Context=\"Author\"><Exec><Command>" + e + "</Command></Exec></Actions></Task>";
    }

    static int SchTasks(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        using (Process p = Process.Start(psi))
        {
            p.OutputDataReceived += (s, ev) => { };
            p.ErrorDataReceived += (s, ev) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            return p.ExitCode;
        }
    }
}

static class Lines
{
    public static readonly string[] Poke = {
        "Привет! Я тут немножко кодю", "Ой! Щекотно", "Ты что-то хотел(а)?", "Я всё вижу. Особенно твои TODO",
        "Давай отрефакторим что-нибудь?", "Мур. То есть, 200 OK", "Нажат. Обрабатываю нажатие…", "Хорошего дня!"
    };
    public static readonly string[] Annoyed = {
        "Эй, я тут деплою!", "Rate limit: 429, too many tыков", "Сейчас как закоммичу прямо в main!", "Ну всё, я обиделся на 5 секунд"
    };
    public static readonly string[] Code = {
        "fix: теперь точно работает", "// не трогать, само работает", "git commit -m \"ещё чуть-чуть\"", "TODO: понять, что это делает",
        "if (устал) { кофе(); }", "у меня на машине работает", "ещё один console.log…", "while (true) { учусь(); }", "тесты зелёные! почти все"
    };
    public static readonly string[] Think = {
        "Размышляю…", "Вычисляю…", "Вспоминаю синтаксис regex…", "Варю мысль…", "Смотрю на архитектуру…", "Хм-м-м…"
    };
    public static readonly string[] Coffee = { "Кофе — это код, только жидкий", "Заправляюсь", "Ещё глоток и пойду пофикшу всё" };
    public static readonly string[] Caught = { "Баг пофикшен!", "Попался! Пишу регресс-тест", "Минус один баг" };
    public static readonly string[] Escaped = { "Ушёл… Теперь это фича", "Не воспроизводится. Закрываю" };
    public static readonly string[] Dizzy = { "Стек поплыл…", "Segmentation fault в голове", "Ещё раз, но в прод не надо" };
    public static readonly string[] Wake = { "А? Я не спал! Я компилировал", "Пять минуточек…", "Проснулся. Что сломалось?" };
    public static readonly string[] Pet = {
        "Мрр… Обрабатываю поглаживание", "Ещё! Это лучше зелёного CI", "Логирую: меня любят", "Так приятно, что забыл про баг"
    };
    public static readonly string[] Cookie = { "Принимаю все cookies!", "Ням. Контекст пополнен", "Крошки в клавиатуре — это фича" };
    public static readonly string[] Kick = { "Гол в main!", "Лови!", "Рефакторю траекторию", "Мячик ушёл в прод" };
    public static readonly string[] Tag = { "Осалил!", "Попался, курсор!", "Есть контакт" };
    public static readonly string[] Shaken = { "Не тряси, я не снежный шар!", "Стек переполняется!", "Всё, у меня race condition" };
    public static readonly string[] Held = { "Поставь меня, я боюсь высоты…", "Я завис. Как и мой процесс" };
    public static readonly string[] Back = { "С возвращением! Я присмотрел за кодом", "О, ты тут! Я почти не спал" };
    public static readonly string[] Night = { "Поздно уже. Баги подождут до утра", "Ночной деплой? Смело" };
    public static readonly string[] Squish = { "Минус баг! Тебя бы в QA", "Точный клик. Закрываю тикет" };
    public static readonly string[] Dance = { "Билд зелёный, пляшем!", "Деплой прошёл — танцуем!" };
    public static readonly string[] Music = { "О, моя любимая!", "Под такое не покодишь", "Деплою ритм в прод", "Громче! Мне для дела" };
    public static readonly string[] Crawl = { "Иду по периметру. Проверяю рамку", "Гравитация — это рекомендация", "Потолок тоже надо ревьюить", "Тут баги прячутся, я знаю" };
    public static readonly string[] Perch = { "Стою на «{0}». Вид что надо", "«{0}» — крепкая папка", "Я не в «{0}», я на ней" };
    public static readonly string[] Done = { "Готово! Клод закончил", "Клод всё сделал, можно смотреть", "Задача закрыта. Иди проверяй" };
    public static readonly string[] Dangle = { "Человек-Клод спускается", "Вишу. Проверяю требования сверху вниз", "Спускаюсь на уровень абстракции ниже", "Паутинка надёжнее некоторых зависимостей" };
    public static readonly string[] Ride = { "Хороший вид с этого окна", "Подвигай окно, я покатаюсь!", "Сижу. Не закрывай, пожалуйста" };
    public static readonly string[] Found = { "Ай, нашли!", "Как ты меня увидел(а)?!", "Ладно, твоя взяла" };
    public static string WebBack(double p)
    {
        return p < 0.15 ? "Ты быстро! Я только начал плести" : p < 0.7 ? "Смотри, сколько сплёл, пока тебя не было!" : "Пока тебя не было, я сплёл целую паутину!";
    }
    public static readonly string[] Glyphs = { "{ }", "</>", ";", "( )", "=>", "01", "#", "fn", "//", "[ ]", "++" };
}

partial class BuddyForm : Form
{
    readonly Settings cfg = Settings.Load();
    readonly Random rnd = new Random();
    readonly float d;
    int u, charW, charH, winW, winH;
    Rectangle work, bounds;
    Bitmap frameBmp;
    Graphics frameG;

    double x, air, vx, vy, maxFall, targetX, stateTime, stateLen;
    St st = St.Idle;
    int face = 1, frame, lastSig, lookX, clicks;
    bool hover, mouseDown, clickThrough = true, hiddenFs, hiddenByUser, realWork, huntDone;
    Point downPt, grab, lastCursor;
    DateTime clickWindow = DateTime.MinValue, lastChatter = DateTime.MinValue;
    double bugX, bugDir, realStart;
    bool bugOn;
    Toy toy;
    int kicks, tags, petSign, shakeSign;
    double kickCd, chaseCd, chaseVy, petScore, shakeScore;
    bool wide, shaken, heldSaid, highSaid, away, dropping;
    readonly AudioMeter audio = new AudioMeter();
    bool musicOn, musicDance;
    int beatCount;
    double loudTime, silentTime, avgPeak, beatCd, sinceBeat, bounce;
    List<DeskFolder> folders = new List<DeskFolder>();
    DeskFolder perch, leapTarget;
    bool scanning, softFall, tall, rideSaid, dangleToFloor;
    int dangleMode, peekPhase, peekSide; // dangle: 0 reel up, 1 pause on top, 2 down, 3 hang, 4 climb back
    Web web;
    WebForm webForm;
    bool webFast, webReached;
    double webHold;
    float webAlpha = 1;
    double dangleAnchor, dangleStop, dangleWait, swingPhase, swingAmp, swingGoal, peekWait;
    St? queued;
    double queuedLen, leapFromX, leapFromAir, crawlX, crawlY, crawlLeft;
    int crawlEdge; // 0 floor, 1 right wall, 2 ceiling, 3 left wall: counter-clockwise when facing right
    readonly List<Particle> parts = new List<Particle>();
    string bubble;
    double bubbleAge, bubbleLen;
    long lastActivityTicks;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    FileSystemWatcher watcher;
    ToolStripMenuItem miQuiet, miTop, miSync, miMusic, miClimb, miNotify, miWeave, miWebClear, miAuto, miHide;
    ToolStripMenuItem[] miSizes;
    Icon trayIcon;

    public BuddyForm()
    {
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) d = g.DpiX / 96f;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "ClaudeBuddy";
        UseScreen(Screen.PrimaryScreen);
        ApplySize();
        x = work.Right - charW - rnd.Next((int)(120 * d), Math.Max((int)(121 * d), work.Width / 2));
        Location = new Point(-32000, -32000);
        BuildMenu();
        BuildTray();
        StartWatcher();
        timer.Interval = 66;
        timer.Tick += (s, e) => Tick();
        timer.Start();
        Go(St.Happy, 1.0);
        if (!cfg.Greeted)
        {
            Say("Привет! Я Клод. Тыкни меня, потаскай. Правый клик — меню", true);
            cfg.Greeted = true;
            cfg.Save();
        }
        else Say(Greeting(), false);
        AllowDrop = true;
    }

    static string Greeting()
    {
        int h = DateTime.Now.Hour;
        return h < 5 ? "Не спится? Мне тоже" : h < 12 ? "Доброе утро!" : h < 18 ? "Добрый день!" : "Добрый вечер!";
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = new IntPtr(Native.MA_NOACTIVATE); return; }
        if (m.Msg == Native.WM_COPYDATA)
        {
            var data = (Native.COPYDATA)Marshal.PtrToStructure(m.LParam, typeof(Native.COPYDATA));
            if (data.Tag != TaskNotice.Tag) { base.WndProc(ref m); return; }
            string project = Marshal.PtrToStringUni(data.Data, data.Bytes / 2);
            BeginInvoke((Action)(() => TaskDone(project)));
            m.Result = new IntPtr(1);
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTopmost();
    }

    void ApplyTopmost()
    {
        // The web first, so the buddy ends up in front of it.
        if (webForm != null)
            Native.SetWindowPos(webForm.Handle, cfg.Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(Handle, cfg.Topmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void UseScreen(Screen s)
    {
        work = s.WorkingArea;
        bounds = s.Bounds;
    }

    void ApplySize()
    {
        u = Math.Max(2, (int)Math.Round((2 + cfg.Size) * d));
        charW = Painter.GridW * u;
        charH = Painter.GridH * u;
        winW = (int)((wide ? 640 : 320) * d);
        winH = tall ? bounds.Height : (int)((wide ? 240 : 120) * d) + charH;
        if (frameG != null) frameG.Dispose();
        if (frameBmp != null) frameBmp.Dispose();
        frameBmp = new Bitmap(winW, winH, PixelFormat.Format32bppArgb);
        frameG = Graphics.FromImage(frameBmp);
        lastSig = 0;
    }

    void BuildMenu()
    {
        menu.Items.Add("Налить кофе", null, (s, e) => { WakeUp(); Go(St.Coffee, 8); SayFrom(Lines.Coffee, true); });
        menu.Items.Add("Покормить печенькой", null, (s, e) => StartToy(false));
        menu.Items.Add("Попросить писать код", null, (s, e) => { WakeUp(); Go(St.Code, 25); });
        var playMenu = new ToolStripMenuItem("Поиграть");
        playMenu.DropDownItems.Add("Кинуть мячик", null, (s, e) => StartToy(true));
        playMenu.DropDownItems.Add("Догонялки с курсором", null, (s, e) => StartChase());
        playMenu.DropDownItems.Add("Потанцевать", null, (s, e) => { WakeUp(); toy = null; Go(St.Dance, 7); SayFrom(Lines.Dance, true); });
        playMenu.DropDownItems.Add("Запустить баг", null, (s, e) => { WakeUp(); toy = null; Go(St.Hunt, 15); });
        playMenu.DropDownItems.Add("Залезть на папку", null, (s, e) => { WakeUp(); RequestPerch(true); });
        playMenu.DropDownItems.Add("Ползать по краям экрана", null, (s, e) => { WakeUp(); StartCrawl(); });
        playMenu.DropDownItems.Add("Спуститься на паутинке", null, (s, e) => { WakeUp(); StartDangle(); });
        playMenu.DropDownItems.Add("Сесть на окно", null, (s, e) => { WakeUp(); RideWindow(true); });
        playMenu.DropDownItems.Add("Прятки", null, (s, e) => { WakeUp(); StartPeek(); });
        playMenu.DropDownItems.Add("Сплести паутину", null, (s, e) => { WakeUp(); StartWeave(); });
        miWebClear = new ToolStripMenuItem("Убрать паутину", null, (s, e) => ClearWeb());
        playMenu.DropDownItems.Add(miWebClear);
        menu.Items.Add(playMenu);
        menu.Items.Add("Что ты умеешь?", null, (s, e) => Say("Гладь меня курсором, тряси, кидай, тащи на меня файлы. По багу и мячику можно кликать. Брось меня на папку — встану. Под музыку танцую. Игры — в меню", true));
        menu.Items.Add(new ToolStripSeparator());
        miQuiet = Toggle("Тихий режим (спит на месте)", cfg.Quiet, v =>
        {
            cfg.Quiet = v;
            if (v) { bubble = null; toy = null; Go(St.Sleep, 1e9); }
            else PickNext();
        });
        miSync = Toggle("Работает, когда работает Claude Code", cfg.Sync, v => cfg.Sync = v);
        miMusic = Toggle("Танцует под музыку", cfg.Music, v =>
        {
            cfg.Music = v;
            if (!v) StopMusic();
        });
        miClimb = Toggle("Сам лазает по экрану", cfg.Climb, v => cfg.Climb = v);
        miNotify = Toggle("Уведомлять о готовых задачах Claude", cfg.Notify, v => cfg.Notify = v);
        miWeave = Toggle("Плетёт паутину, пока меня нет", cfg.Weave, v => cfg.Weave = v);
        miTop = Toggle("Поверх окон", cfg.Topmost, v => { cfg.Topmost = v; ApplyTopmost(); });
        var sizeMenu = new ToolStripMenuItem("Размер");
        string[] names = { "Маленький", "Средний", "Большой" };
        miSizes = new ToolStripMenuItem[3];
        for (int i = 0; i < 3; i++)
        {
            int idx = i;
            miSizes[i] = new ToolStripMenuItem(names[i], null, (s, e) =>
            {
                cfg.Size = idx;
                cfg.Save();
                ApplySize();
                for (int j = 0; j < 3; j++) miSizes[j].Checked = j == idx;
            });
            miSizes[i].Checked = i == cfg.Size;
            sizeMenu.DropDownItems.Add(miSizes[i]);
        }
        menu.Items.AddRange(new ToolStripItem[] { miQuiet, miSync, miMusic, miClimb, miNotify, miWeave, miTop, sizeMenu });
        menu.Items.Add(new ToolStripSeparator());
        miAuto = new ToolStripMenuItem("Запускать вместе с Windows");
        miAuto.Click += (s, e) => { Autostart.Enabled = !Autostart.Enabled; miAuto.Checked = Autostart.Enabled; };
        menu.Items.Add(miAuto);
        miHide = new ToolStripMenuItem("Спрятать", null, (s, e) => ToggleHidden());
        menu.Items.Add(miHide);
        menu.Items.Add("Выход", null, (s, e) => Close());
        menu.Opening += (s, e) => { miWebClear.Enabled = web != null; miAuto.Checked = Autostart.Enabled; miHide.Text = hiddenByUser ? "Показать" : "Спрятать"; };
    }

    ToolStripMenuItem Toggle(string text, bool on, Action<bool> set)
    {
        var item = new ToolStripMenuItem(text) { Checked = on };
        item.Click += (s, e) => { item.Checked = !item.Checked; set(item.Checked); cfg.Save(); };
        return item;
    }

    void BuildTray()
    {
        using (var bmp = new Bitmap(32, 32, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(bmp))
        {
            Painter.Buddy(g, 0, 5, 2, new Pose());
            IntPtr h = bmp.GetHicon();
            trayIcon = (Icon)Icon.FromHandle(h).Clone();
            Native.DestroyIcon(h);
        }
        tray.Icon = trayIcon;
        tray.Text = "Claude Buddy";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleHidden(); };
    }

    void ToggleHidden()
    {
        hiddenByUser = !hiddenByUser;
        Visible = !hiddenByUser && !hiddenFs;
        SyncWeb();
        if (Visible) lastSig = 0;
    }

    // Only file names and write times are watched; session contents are never read.
    void StartWatcher()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(dir)) return;
        try
        {
            watcher = new FileSystemWatcher(dir, "*.jsonl");
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size;
            FileSystemEventHandler h = (s, e) => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);
            watcher.Changed += h;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    bool ClaudeActive()
    {
        if (!cfg.Sync) return false;
        long t = Interlocked.Read(ref lastActivityTicks);
        return t != 0 && (DateTime.UtcNow.Ticks - t) / TimeSpan.TicksPerSecond < 30;
    }

    // The tray notification is what gets seen: the buddy himself may be behind windows.
    void TaskDone(string project)
    {
        if (!cfg.Notify) return;
        tray.ShowBalloonTip(5000, "Клод закончил задачу", project.Length > 0 ? "Проект: " + project : "Можно смотреть результат", ToolTipIcon.None);
        if (cfg.Quiet) return;
        SayFrom(Lines.Done, false);
        Spawn(x + charW / 2, CharTop(), "✓", Color.FromArgb(126, 211, 135), false);
        if (!Climbing() && st != St.Drag && st != St.Fall) Go(St.Happy, 1.0);
    }

    // ----- input -----

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (!hover && ClickWorld(Cursor.Position)) return;
        mouseDown = true;
        downPt = lastCursor = Cursor.Position;
        grab = new Point(downPt.X - (int)x, downPt.Y - CharTop());
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            Native.SetForegroundWindow(Handle);
            menu.Show(Cursor.Position);
            return;
        }
        if (e.Button != MouseButtons.Left || !mouseDown) return;
        mouseDown = false;
        if (st == St.Drag)
        {
            DeskFolder under = FolderAt(x + charW / 2.0, CharTop() + charH);
            if (under != null) { PerchOn(under); return; }
            softFall = false;
            maxFall = 0;
            vx = Clamp(vx, -1400 * d, 1400 * d);
            vy = Clamp(vy, -1400 * d, 1400 * d);
            Go(St.Fall, 1e9);
            return;
        }
        HandleClick();
    }

    // Things lying around the buddy that can be clicked instead of the buddy himself.
    bool ClickWorld(Point c)
    {
        if (bugOn && BugRect().Contains(c))
        {
            Spawn(bugX, work.Bottom - 3 * u, "+1", Color.FromArgb(126, 211, 135), false);
            SayFrom(Lines.Squish, true);
            Go(St.Happy, 1.0);
            return true;
        }
        if (toy == null || !ToyRect().Contains(c)) return false;
        if (toy.IsBall)
        {
            double side = toy.X + 2.5 * u < c.X ? -1 : 1;
            toy.Vx = side * (260 + rnd.NextDouble() * 240) * d;
            toy.Vy = (340 + rnd.NextDouble() * 200) * d;
        }
        return true;
    }

    Rectangle ToyRect()
    {
        Rectangle r = new Rectangle((int)toy.X, work.Bottom - (int)toy.Air - 5 * u, 5 * u, 5 * u);
        r.Inflate(u, u);
        return r;
    }

    Rectangle BugRect()
    {
        int b = Math.Max(2, u - 1);
        Rectangle r = new Rectangle((int)bugX, work.Bottom - 2 * b, 4 * b, 2 * b);
        r.Inflate(2 * u, 2 * u);
        return r;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        dropping = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effect = dropping ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        dropping = false;
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        dropping = false;
        Taste((string[])e.Data.GetData(DataFormats.FileDrop));
    }

    // Looks only at the name and size. The file is never opened, moved or changed.
    void Taste(string[] paths)
    {
        if (paths == null || paths.Length == 0) return;
        Go(St.Eat, 2.4);
        Say(TasteLine(paths), true);
    }

    static string TasteLine(string[] paths)
    {
        if (paths.Length > 1) return "Целая охапка файлов (" + paths.Length + ")! Хрум. Все целы";
        string name = Path.GetFileName(paths[0].TrimEnd('\\'));
        if (name.Length > 26) name = name.Substring(0, 25) + "…";
        if (Directory.Exists(paths[0])) return name + ": целая папка! Не влезет";
        string size = "";
        try
        {
            long b = new FileInfo(paths[0]).Length;
            size = " (" + (b < 1048576 ? Math.Max(1, b / 1024) + " КБ" : b / 1048576 + " МБ") + ")";
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        string flavour;
        switch (Path.GetExtension(paths[0]).ToLowerInvariant())
        {
            case ".cs": case ".py": case ".js": case ".ts": case ".cpp": case ".java": flavour = "Свежий код, хрустит!"; break;
            case ".png": case ".jpg": case ".jpeg": case ".gif": case ".webp": flavour = "Красиво, но несъедобно."; break;
            case ".pdf": case ".doc": case ".docx": flavour = "Сухо, зато сытно."; break;
            case ".mp3": case ".wav": case ".flac": flavour = "Музыка для желудка."; break;
            case ".zip": case ".rar": case ".7z": flavour = "Сжато, как сухпаёк."; break;
            case ".exe": case ".bat": case ".ps1": flavour = "Острое! Такое не запускаю."; break;
            default: flavour = "Ням, на вкус как байты."; break;
        }
        return name + size + ". " + flavour + " Файл цел";
    }

    void HandleClick()
    {
        DateTime now = DateTime.Now;
        if (now > clickWindow) { clicks = 0; clickWindow = now.AddSeconds(4); }
        clicks++;
        if (st == St.Peek)
        {
            SayFrom(Lines.Found, true);
            Go(St.Happy, 1.0);
            return;
        }
        if (Climbing())
        {
            swingGoal = Math.Min(60 * d, swingGoal + 25 * d);
            SayFrom(Lines.Poke, true);
            Spawn(x + charW / 2, CharTop(), null, Color.Empty, true);
            return;
        }
        if (st == St.Sleep)
        {
            if (cfg.Quiet) { Spawn(x + charW / 2, CharTop(), null, Color.Empty, true); return; }
            SayFrom(Lines.Wake, true);
        }
        else SayFrom(clicks >= 6 ? Lines.Annoyed : Lines.Poke, true);
        Spawn(x + charW / 2, CharTop(), null, Color.Empty, true);
        Go(St.Happy, 1.0);
    }

    void WakeUp()
    {
        if (cfg.Quiet)
        {
            cfg.Quiet = false;
            miQuiet.Checked = false;
            cfg.Save();
        }
    }

    // ----- behaviour -----

    bool Climbing() { return st == St.Leap || st == St.Perch || st == St.Crawl || st == St.Dangle || st == St.Weave; }

    void Go(St s, double len)
    {
        // From up high, anything that happens on the floor waits until he has jumped down.
        if (Climbing() && s != St.Drag && s != St.Fall && s != St.Leap && s != St.Perch && s != St.Crawl && s != St.Dangle && s != St.Weave)
        {
            DropDown();
            queued = s;
            queuedLen = len;
            return;
        }
        queued = null;
        if (s != St.Perch) perch = null;
        if (st == St.Peek && s != St.Peek) x = Clamp(x, work.Left, work.Right - charW);
        if (s == St.Weave)
        {
            if (web == null) web = new Web(rnd.Next(2) == 0 ? -1 : 1, 260 * d);
            webReached = false;
            webAlpha = 1;
            webHold = 120;
            if (webForm != null) webForm.Repaint();
        }
        if (s == St.Dangle)
        {
            double ceiling = work.Height - charH;
            dangleAnchor = Clamp(x + charW / 2.0, work.Left + charW, work.Right - charW);
            dangleMode = air >= ceiling - 1 ? 1 : 0;
            dangleWait = 0.6;
            dangleStop = ceiling * (0.35 + rnd.NextDouble() * 0.35);
            dangleToFloor = false;
            swingPhase = 0;
            swingAmp = swingGoal = 3 * d;
        }
        if (s == St.Peek)
        {
            peekPhase = 0;
            peekSide = x + charW / 2 < work.Left + work.Width / 2 ? -1 : 1;
            face = peekSide;
        }
        if (s == St.Crawl)
        {
            crawlEdge = 0;
            crawlX = x + charW / 2.0;
            crawlY = work.Bottom - charH / 2.0;
            crawlLeft = 2 * (work.Width + work.Height);
        }
        st = s;
        stateTime = 0;
        stateLen = len;
        if (s != St.Fall && s != St.Drag && s != St.Leap && s != St.Perch && s != St.Crawl && s != St.Dangle && s != St.Weave) air = s == St.Happy ? air : 0;
        if (s == St.Code || s == St.Coffee)
        {
            int room = 14 * u;
            if (x + charW + room > work.Right) face = -1;
            else if (x - room < work.Left) face = 1;
        }
        if (s == St.Hunt)
        {
            int side = x < work.Left + 200 * d ? 1 : x > work.Right - charW - 200 * d ? -1 : (rnd.Next(2) == 0 ? -1 : 1);
            bugX = x + side * (charW + 110 * d);
            bugDir = side;
            bugOn = true;
            huntDone = false;
            face = side;
        }
        else bugOn = false;
        if (s != St.Code) realWork = false;
        if (s != St.Dance) musicDance = false;
    }

    void PickNext()
    {
        if (queued.HasValue)
        {
            St next = queued.Value;
            Go(next, queuedLen);
            return;
        }
        if (cfg.Quiet) { Go(St.Sleep, 1e9); return; }
        if (toy != null) { Go(St.Play, 1e9); return; }
        if (ClaudeActive()) { BeginRealWork(); return; }
        if (musicOn) { StartMusicDance(); return; }
        int hour = DateTime.Now.Hour;
        if (hour >= 1 && hour < 5 && rnd.Next(6) == 0) Chatter(Lines.Night);
        if (cfg.Climb)
        {
            int climb = rnd.Next(100);
            if (climb < 5) { StartCrawl(); return; }
            if (climb < 10) { Go(St.Idle, 3); RequestPerch(false); return; }
            if (climb < 13) { StartDangle(); return; }
            if (climb < 16 && RideWindow(false)) return;
        }
        int r = rnd.Next(100);
        if (r < 34) Go(St.Code, 15 + rnd.NextDouble() * 30);
        else if (r < 66) StartWalk();
        else if (r < 78) Go(St.Idle, 3 + rnd.NextDouble() * 5);
        else if (r < 88) { Go(St.Think, 4 + rnd.NextDouble() * 5); Chatter(Lines.Think); }
        else if (r < 93) { Go(St.Coffee, 8); Chatter(Lines.Coffee); }
        else if (r < 97) Go(St.Hunt, 15);
        else Go(St.Sleep, 25 + rnd.NextDouble() * 50);
    }

    void StartToy(bool ball)
    {
        WakeUp();
        double side = x < work.Left + 300 * d ? 1 : x > work.Right - charW - 300 * d ? -1 : (rnd.Next(2) == 0 ? -1 : 1);
        toy = new Toy
        {
            IsBall = ball,
            X = Clamp(x + charW / 2 + side * (130 + rnd.NextDouble() * 110) * d, work.Left, work.Right - 5 * u),
            Air = 150 * d
        };
        kicks = 0;
        kickCd = 0;
        Go(St.Play, 1e9);
    }

    void StartChase()
    {
        WakeUp();
        toy = null;
        tags = 0;
        chaseVy = 0;
        chaseCd = 1.5;
        highSaid = false;
        Go(St.Chase, 30);
        Say("Сейчас догоню твой курсор!", true);
    }

    void DropDown()
    {
        softFall = true;
        maxFall = 0;
        vx = face * 60 * d;
        vy = air > 0 && st == St.Perch ? 260 * d : 0;
        Go(St.Fall, 1e9);
    }

    void StartWeave()
    {
        toy = null;
        if (st == St.Fall || st == St.Drag) return;
        if (web != null && web.P >= 1) ClearWeb();
        webFast = true;
        Go(St.Weave, 1e9);
        Say("Сейчас сплету, смотри", true);
    }

    // Slow enough that a short absence leaves a small web and a long one a whole web; on request, half a minute.
    void StepWeave(double dt)
    {
        web.P = Math.Min(1, web.P + dt / (webFast ? 30 : 600));
        PointF tip = web.Tip();
        double tx = Clamp(web.Side < 0 ? work.Left + tip.X : work.Right - tip.X, work.Left + charW / 2.0, work.Right - charW / 2.0);
        double ty = Math.Max(work.Top + tip.Y, work.Top + charH / 2.0);
        double cx = x + charW / 2.0, cy = work.Bottom - air - charH / 2.0;
        double dx = tx - cx, dy = ty - cy, dist = Math.Sqrt(dx * dx + dy * dy);
        double step = (!webReached ? 600 : webFast ? 520 : 170) * d * dt;
        if (dist <= step) webReached = true;
        else
        {
            cx += dx / dist * step;
            cy += dy / dist * step;
            face = dx >= 0 ? 1 : -1;
        }
        x = cx - charW / 2.0;
        air = work.Bottom - cy - charH / 2.0;
        if (web.P < 1) return;
        if (webFast)
        {
            Say("Готово! Сам бы жил в такой", true);
            DropDown();
        }
        else if (frame % 24 == 0) Spawn(cx, cy, "z", Color.FromArgb(200, 215, 230), false);
    }

    void StepWeb(double dt)
    {
        if (web == null) return;
        if (st == St.Weave || away)
        {
            webAlpha = 1;
            webHold = 120;
        }
        else if ((webHold -= dt) <= 0)
        {
            webAlpha -= (float)(dt / 3);
            if (webAlpha <= 0) { ClearWeb(); return; }
        }
        if (webForm == null && Visible) webForm = new WebForm((int)web.Radius + (int)(10 * d), d);
        SyncWeb();
        if (webForm != null) webForm.Apply(web, webAlpha, work);
    }

    void SyncWeb()
    {
        if (webForm == null) return;
        bool show = web != null && Visible;
        if (show == webForm.Visible) return;
        webForm.Visible = show;
        webForm.Repaint();
        if (show) ApplyTopmost();
    }

    void ClearWeb()
    {
        web = null;
        // A fresh window next time, in case the display scale has changed.
        if (webForm != null) webForm.Close();
        webForm = null;
    }

    void StartDangle()
    {
        toy = null;
        if (st == St.Perch || st == St.Leap)
        {
            DropDown();
            queued = St.Dangle;
            queuedLen = 1e9;
            return;
        }
        if (st == St.Fall || st == St.Drag || st == St.Dangle) return;
        Go(St.Dangle, 1e9);
        SayFrom(Lines.Dangle, false);
    }

    void StepDangle(double dt, Point cur)
    {
        double ceiling = work.Height - charH;
        swingPhase += dt * 2.6;
        swingGoal = Math.Max(3 * d, swingGoal * 0.992);
        if (hover) swingGoal = Math.Min(60 * d, swingGoal + Math.Min(40 * d, Math.Abs(cur.X - lastCursor.X)) * 0.25);
        // Eased, or a quick flick of the mouse would make him jump sideways.
        swingAmp += (swingGoal - swingAmp) * 0.08;
        x = Clamp(dangleAnchor + Math.Sin(swingPhase) * swingAmp - charW / 2.0, bounds.Left, bounds.Right - charW);
        switch (dangleMode)
        {
            case 0:
                air += 320 * d * dt;
                if (air < ceiling) break;
                air = ceiling;
                dangleMode = 1;
                dangleWait = 0.8;
                break;
            case 1:
                dangleWait -= dt;
                if (dangleWait <= 0) dangleMode = 2;
                break;
            case 2:
                if (hover) break;
                air -= 95 * d * dt;
                if (!dangleToFloor && air <= dangleStop)
                {
                    dangleMode = 3;
                    dangleWait = 3 + rnd.NextDouble() * 3;
                    break;
                }
                if (air > 0) break;
                air = 0;
                st = St.Idle;
                PickNext();
                break;
            case 3:
                dangleWait -= dt;
                if (dangleWait > 0) break;
                dangleToFloor = rnd.Next(2) == 0;
                dangleMode = dangleToFloor ? 2 : 4;
                break;
            default:
                air += 150 * d * dt;
                if (air < ceiling) break;
                // Back on top: carries on along the ceiling and down a wall.
                face = rnd.Next(2) == 0 ? -1 : 1;
                Go(St.Crawl, 1e9);
                crawlEdge = 2;
                crawlX = Clamp(x + charW / 2.0, work.Left + charW / 2.0, work.Right - charW / 2.0);
                crawlY = work.Top + charH / 2.0;
                crawlLeft = work.Width + work.Height;
                break;
        }
    }

    bool RideWindow(bool asked)
    {
        DeskFolder w = Desktop.TopWindow(Handle, work.Top + charH + 4);
        if (w != null)
        {
            LeapTo(w);
            return true;
        }
        if (asked) Say("Некуда сесть: окно на весь экран или его нет", true);
        return false;
    }

    void StartPeek()
    {
        toy = null;
        if (Climbing())
        {
            DropDown();
            queued = St.Peek;
            queuedLen = 45;
            return;
        }
        if (st == St.Fall || st == St.Drag) return;
        Go(St.Peek, 45);
        Say("Прячусь! Лови, когда выгляну", true);
    }

    void StepPeek(double dt)
    {
        if (stateTime > stateLen)
        {
            Say("Никто не ищет? Ладно, выхожу сам", false);
            Go(St.Happy, 1.0);
            return;
        }
        double hidden = peekSide < 0 ? work.Left - charW - 2 : work.Right + 2;
        double shown = peekSide < 0 ? work.Left - charW * 0.45 : work.Right - charW * 0.55;
        double step = (peekPhase == 0 ? 220 : 110) * d * dt;
        switch (peekPhase)
        {
            case 0:
                x = MoveTo(x, hidden, step);
                if (x == hidden) HideAgain();
                break;
            case 1:
                peekWait -= dt;
                if (peekWait <= 0) peekPhase = 2;
                break;
            case 2:
                face = -peekSide;
                x = MoveTo(x, shown, step);
                if (x != shown) break;
                peekPhase = 3;
                peekWait = 2.2;
                break;
            case 3:
                peekWait -= dt;
                if (peekWait <= 0) peekPhase = 4;
                break;
            default:
                x = MoveTo(x, hidden, step);
                if (x == hidden) HideAgain();
                break;
        }
    }

    // Out of sight he can come up on either side.
    void HideAgain()
    {
        peekPhase = 1;
        peekWait = 1.5 + rnd.NextDouble() * 2.5;
        peekSide = rnd.Next(2) == 0 ? -1 : 1;
        x = peekSide < 0 ? work.Left - charW - 2 : work.Right + 2;
    }

    static double MoveTo(double from, double to, double step)
    {
        return Math.Abs(to - from) <= step ? to : from + Math.Sign(to - from) * step;
    }

    void StartCrawl()
    {
        toy = null;
        if (st == St.Perch || st == St.Leap)
        {
            DropDown();
            queued = St.Crawl;
            queuedLen = 1e9;
            return;
        }
        if (st == St.Fall || st == St.Drag || st == St.Crawl) return;
        face = rnd.Next(2) == 0 ? -1 : 1;
        Go(St.Crawl, 1e9);
        Say(face > 0 ? "Пошёл по краю, сначала направо" : "Пошёл по краю, сначала налево", false);
    }

    void StepCrawl(double dt)
    {
        if (hover) return;
        double s = 70 * d * dt, half = charW / 2.0;
        crawlLeft -= s;
        bool turn = false;
        switch (crawlEdge)
        {
            case 0: crawlX += face * s; turn = face > 0 ? crawlX >= work.Right - half : crawlX <= work.Left + half; break;
            case 1: crawlY -= face * s; turn = face > 0 ? crawlY <= work.Top + half : crawlY >= work.Bottom - half; break;
            case 2: crawlX -= face * s; turn = face > 0 ? crawlX <= work.Left + half : crawlX >= work.Right - half; break;
            default: crawlY += face * s; turn = face > 0 ? crawlY >= work.Bottom - half : crawlY <= work.Top + half; break;
        }
        if (turn)
        {
            crawlEdge = (crawlEdge + face + 4) % 4;
            if (crawlEdge == 0 || crawlEdge == 2)
            {
                crawlY = crawlEdge == 0 ? work.Bottom - charH / 2.0 : work.Top + charH / 2.0;
                crawlX = Clamp(crawlX, work.Left + half, work.Right - half);
            }
            else
            {
                crawlX = crawlEdge == 1 ? work.Right - charH / 2.0 : work.Left + charH / 2.0;
                crawlY = Clamp(crawlY, work.Top + half, work.Bottom - half);
            }
            Chatter(Lines.Crawl);
        }
        x = crawlX - half;
        air = work.Bottom - (crawlY + charH / 2.0);
        if (cfg.Climb && crawlEdge == 2 && frame % 30 == 0 && rnd.Next(14) == 0) { StartDangle(); return; }
        if (crawlLeft > 0 || crawlEdge != 0) return;
        air = 0;
        st = St.Idle;
        PickNext();
    }

    void RequestPerch(bool asked)
    {
        ScanFolders(() =>
        {
            var open = new List<DeskFolder>();
            foreach (DeskFolder f in folders) if (Desktop.Visible(f)) open.Add(f);
            if (open.Count > 0)
            {
                if (asked || st == St.Idle) LeapTo(open[rnd.Next(open.Count)]);
            }
            else if (asked) Say(folders.Count == 0 ? "Не вижу папок на рабочем столе" : "Папки закрыты окнами. Сверни их, и я залезу", true);
        });
    }

    void ScanFolders(Action done)
    {
        if (scanning) return;
        scanning = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<DeskFolder> found = Desktop.Folders();
            try
            {
                BeginInvoke((Action)(() =>
                {
                    scanning = false;
                    folders = found;
                    if (done != null) done();
                }));
            }
            catch (InvalidOperationException) { }
        });
    }

    DeskFolder FolderAt(double cx, double feet)
    {
        foreach (DeskFolder f in folders)
            if (cx >= f.Box.Left && cx <= f.Box.Right && feet >= f.Box.Top && feet <= f.Box.Bottom) return f;
        return null;
    }

    double PerchX(DeskFolder f) { return Clamp(f.Box.Left + f.Box.Width / 2.0 - charW / 2.0, work.Left, work.Right - charW); }
    double PerchAir(DeskFolder f) { return Math.Max(0, work.Bottom - f.Box.Top); }

    void LeapTo(DeskFolder f)
    {
        if (st == St.Drag) return;
        toy = null;
        leapTarget = f;
        leapFromX = x;
        leapFromAir = air;
        double dist = Math.Sqrt(Math.Pow(PerchX(f) - x, 2) + Math.Pow(PerchAir(f) - air, 2));
        face = PerchX(f) >= x ? 1 : -1;
        Go(St.Leap, Clamp(dist / (900 * d), 0.6, 1.4));
    }

    void StepLeap()
    {
        if (leapTarget == null) { DropDown(); return; }
        double t = Math.Min(1, stateTime / stateLen);
        x = leapFromX + (PerchX(leapTarget) - leapFromX) * t;
        air = leapFromAir + (PerchAir(leapTarget) - leapFromAir) * t + Math.Sin(t * Math.PI) * 70 * d;
        if (t >= 1) PerchOn(leapTarget);
    }

    void PerchOn(DeskFolder f)
    {
        x = PerchX(f);
        air = PerchAir(f);
        vx = vy = 0;
        Go(St.Perch, f.Hwnd != IntPtr.Zero ? 25 + rnd.NextDouble() * 30 : 12 + rnd.NextDouble() * 20);
        perch = f;
        rideSaid = false;
        if (f.Hwnd != IntPtr.Zero)
        {
            SayFrom(Lines.Ride, false);
            return;
        }
        string name = f.Name.Length > 18 ? f.Name.Substring(0, 17) + "…" : f.Name;
        Say(string.Format(Lines.Perch[rnd.Next(Lines.Perch.Length)], name), false);
    }

    void StepPerch()
    {
        if (stateTime > stateLen || perch == null) { DropDown(); return; }
        if (perch.Hwnd != IntPtr.Zero) { FollowWindow(); return; }
        if (frame % 75 != 0) return;
        // The folder may have been moved or deleted under him.
        ScanFolders(() =>
        {
            if (st != St.Perch || perch == null) return;
            foreach (DeskFolder f in folders)
                if (f.Name == perch.Name && f.Box == perch.Box) return;
            DropDown();
        });
    }

    void FollowWindow()
    {
        Native.RECT r;
        if (!Native.IsWindow(perch.Hwnd) || !Native.IsWindowVisible(perch.Hwnd) || Native.IsIconic(perch.Hwnd)
            || !Native.GetWindowRect(perch.Hwnd, out r) || r.T < work.Top + charH + 4)
        {
            DropDown();
            return;
        }
        int dx = r.L - perch.Box.Left, dy = r.T - perch.Box.Top;
        if (dx == 0 && dy == 0) return;
        x = Clamp(x + dx, work.Left, work.Right - charW);
        perch.Box = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
        air = PerchAir(perch);
        if (rideSaid) return;
        rideSaid = true;
        Say("Поехали!", false);
    }

    void StartWalk()
    {
        double dist = (120 + rnd.NextDouble() * 400) * d * (rnd.Next(2) == 0 ? -1 : 1);
        targetX = Math.Max(work.Left, Math.Min(work.Right - charW, x + dist));
        if (Math.Abs(targetX - x) < 40 * d) targetX = x - dist;
        targetX = Math.Max(work.Left, Math.Min(work.Right - charW, targetX));
        face = targetX > x ? 1 : -1;
        Go(St.Walk, 40);
    }

    void BeginRealWork()
    {
        bool fresh = !realWork;
        Go(St.Code, 1e9);
        realWork = true;
        if (fresh)
        {
            realStart = 0;
            Chatter(new[] { "Claude работает — и я работаю!", "О, задача! Подключаюсь" });
        }
    }

    void Tick()
    {
        frame++;
        const double dt = 0.066;
        if (frame % 30 == 0)
        {
            bool fs = FullscreenApp();
            if (fs != hiddenFs)
            {
                hiddenFs = fs;
                Visible = !hiddenFs && !hiddenByUser;
                SyncWeb();
                lastSig = 0;
            }
            if (cfg.Topmost && Visible) ApplyTopmost();
            CheckAway(IdleSeconds());
        }
        if (!Visible) return;
        if (cfg.Music) TrackMusic(audio.Peak(), dt);
        Advance(Cursor.Position, dt);
    }

    void Advance(Point cur, double dt)
    {
        bool needWide = toy != null || bugOn;
        bool needTall = st == St.Dangle;
        if (needWide != wide || needTall != tall) { wide = needWide; tall = needTall; ApplySize(); }
        stateTime += dt;
        Rectangle hit = st == St.Crawl && crawlEdge % 2 == 1
            ? new Rectangle((int)crawlX - charH / 2 - u, (int)crawlY - charW / 2 - u, charH + 2 * u, charW + 2 * u)
            : new Rectangle((int)x - u, CharTop() - u, charW + 2 * u, charH + 2 * u);
        hover = hit.Contains(cur);
        bool overWorld = (toy != null && ToyRect().Contains(cur)) || (bugOn && BugRect().Contains(cur));
        SetClickThrough(!hover && !mouseDown && !overWorld);
        lookX = Math.Abs(cur.X - (x + charW / 2)) < 260 * d && Math.Abs(cur.Y - CharTop()) < 260 * d
            ? (cur.X < x + charW / 2 - 20 * d ? -1 : cur.X > x + charW / 2 + 20 * d ? 1 : 0) : 0;

        if (mouseDown && st != St.Drag && (Math.Abs(cur.X - downPt.X) > 4 * d || Math.Abs(cur.Y - downPt.Y) > 4 * d))
        {
            if (st == St.Dangle) Say("Паутинка порвалась!", true);
            vx = vy = 0;
            shaken = heldSaid = false;
            shakeScore = shakeSign = 0;
            Go(St.Drag, 1e9);
            ScanFolders(null);
        }

        TrackPetting(cur, dt);
        StepToy(dt);
        StepWeb(dt);
        Step(dt, cur);
        StepParticles(dt);
        StepBubble(dt);
        Render();
        lastCursor = cur;
    }

    void Step(double dt, Point cur)
    {
        switch (st)
        {
            case St.Drag:
                vx = (cur.X - lastCursor.X) / dt * 0.6 + vx * 0.4;
                vy = -(cur.Y - lastCursor.Y) / dt * 0.6 + vy * 0.4;
                x = Clamp(cur.X - grab.X, bounds.Left, bounds.Right - charW);
                air = Math.Max(0, work.Bottom - (cur.Y - grab.Y + charH));
                TrackShake(cur, dt);
                break;
            case St.Fall:
                StepFall(dt);
                break;
            case St.Walk:
                if (!hover)
                {
                    x += face * 28 * d * dt;
                    if ((face > 0 && x >= targetX) || (face < 0 && x <= targetX)) PickNext();
                }
                if (st == St.Walk && stateTime > stateLen) PickNext();
                if (st == St.Walk && ClaudeActive() && !cfg.Quiet) BeginRealWork();
                break;
            case St.Code:
                StepCode();
                break;
            case St.Happy:
                air = Math.Abs(Math.Sin(stateTime / stateLen * Math.PI * 2)) * 16 * d;
                if (stateTime >= stateLen) { air = 0; PickNext(); }
                break;
            case St.Hunt:
                StepHunt(dt);
                break;
            case St.Play:
                StepPlay(dt);
                break;
            case St.Leap:
                StepLeap();
                break;
            case St.Perch:
                StepPerch();
                break;
            case St.Crawl:
                StepCrawl(dt);
                break;
            case St.Dangle:
                StepDangle(dt, cur);
                break;
            case St.Peek:
                StepPeek(dt);
                break;
            case St.Weave:
                StepWeave(dt);
                break;
            case St.Chase:
                StepChase(dt, cur);
                break;
            case St.Eat:
                if (frame % 7 == 0) Spawn(x + charW / 2, CharTop() + 5 * u, "·", Color.FromArgb(200, 150, 90), false);
                if (stateTime > stateLen) PickNext();
                break;
            case St.Pet:
                if (frame % 9 == 0) Spawn(x + charW / 2 + rnd.Next(-charW / 3, charW / 3), CharTop(), null, Color.Empty, true);
                if (stateTime > stateLen) PickNext();
                break;
            case St.Dance:
                if (musicDance)
                {
                    air = bounce * 14 * d;
                    face = beatCount / 4 % 2 == 0 ? 1 : -1;
                    break;
                }
                if (frame % 10 == 0) Spawn(x + charW / 2 + rnd.Next(-charW / 2, charW / 2), CharTop(), "♪", Color.FromArgb(240, 180, 130), false);
                face = frame / 15 % 2 == 0 ? 1 : -1;
                air = Math.Abs(Math.Sin(stateTime * 6)) * 7 * d;
                if (stateTime > stateLen) { air = 0; PickNext(); }
                break;
            case St.Sleep:
                if (frame % 24 == 0) Spawn(x + charW * 0.7, CharTop() + u, "z", Color.FromArgb(200, 215, 230), false);
                if (ClaudeActive() && !cfg.Quiet) BeginRealWork();
                else if (stateTime > stateLen) PickNext();
                break;
            case St.Think:
                if (frame % 22 == 0) Spawn(x + charW / 2 + rnd.Next(-charW / 3, charW / 3), CharTop(), rnd.Next(3) == 0 ? "!" : "?", Color.FromArgb(240, 180, 130), false);
                if (stateTime > stateLen) PickNext();
                break;
            default:
                if (st == St.Idle && ClaudeActive() && !cfg.Quiet) { BeginRealWork(); break; }
                if (stateTime > stateLen) PickNext();
                break;
        }
    }

    void StepFall(double dt)
    {
        double before = air;
        vy -= 1500 * d * dt;
        air += vy * dt;
        x += vx * dt;
        if (!softFall && vy < 0)
            foreach (DeskFolder f in folders)
                if (before >= PerchAir(f) && air < PerchAir(f) && x + charW / 2 >= f.Box.Left && x + charW / 2 <= f.Box.Right)
                {
                    PerchOn(f);
                    return;
                }
        maxFall = Math.Max(maxFall, -vy);
        if (x < work.Left) { x = work.Left; vx = -vx * 0.5; }
        if (x > work.Right - charW) { x = work.Right - charW; vx = -vx * 0.5; }
        if (air > 0) return;
        air = 0;
        if (-vy > 220 * d)
        {
            vy = -vy * 0.32;
            vx *= 0.55;
            return;
        }
        vx = vy = 0;
        bool hardLanding = maxFall > 800 * d && !softFall;
        softFall = false;
        if (hardLanding || shaken)
        {
            shaken = false;
            Go(St.Dizzy, 3);
            SayFrom(Lines.Dizzy, true);
        }
        else PickNext();
    }

    void StepCode()
    {
        if (frame % 18 == 0 && rnd.Next(3) == 0)
            Spawn(x + charW / 2 + face * 11 * u, CharTop() + u, Lines.Glyphs[rnd.Next(Lines.Glyphs.Length)], CodeColor(), false);
        if (realWork)
        {
            realStart += 0.066;
            if (!ClaudeActive())
            {
                bool longRun = realStart > 40;
                realWork = false;
                Go(St.Happy, 1.0);
                if (longRun) Chatter(new[] { "Кажется, готово!", "Всё, можно смотреть" });
            }
            return;
        }
        if (frame % 200 == 0) Chatter(Lines.Code);
        if (ClaudeActive() && !cfg.Quiet) { BeginRealWork(); return; }
        if (stateTime > stateLen) PickNext();
    }

    void StepHunt(double dt)
    {
        if (huntDone)
        {
            if (stateTime > stateLen) PickNext();
            return;
        }
        bugX += bugDir * 42 * d * dt;
        if (bugX < work.Left + 5 || bugX > work.Right - 5 * u) bugDir = -bugDir;
        if (frame % 25 == 0 && rnd.Next(3) == 0) bugDir = -bugDir;
        face = bugX > x + charW / 2 ? 1 : -1;
        x = Clamp(x + face * 78 * d * dt, work.Left, work.Right - charW);
        double centre = x + charW / 2;
        if (Math.Abs(bugX - centre) < charW * 0.45)
        {
            Spawn(bugX, work.Bottom - 2 * u, "+1", Color.FromArgb(126, 211, 135), false);
            SayFrom(Lines.Caught, false);
            Go(St.Happy, 1.0);
            return;
        }
        if (stateTime > stateLen)
        {
            SayFrom(Lines.Escaped, false);
            bugOn = false;
            huntDone = true;
            stateLen = stateTime + 1.5;
        }
    }

    // Back-and-forth over the buddy without pressing a button reads as petting; passing by does not.
    void TrackPetting(Point cur, double dt)
    {
        petScore = Math.Max(0, petScore - dt * 0.9);
        if (!hover || mouseDown) { petSign = 0; return; }
        int dx = cur.X - lastCursor.X;
        if (Math.Abs(dx) < 2 * d) return;
        int sign = dx > 0 ? 1 : -1;
        bool reversed = petSign != 0 && sign != petSign;
        petSign = sign;
        if (!reversed) return;
        petScore += 1;
        if (st == St.Pet) { stateLen = stateTime + 1.3; return; }
        if (petScore < 3 || st == St.Drag || st == St.Fall || st == St.Dizzy) return;
        petScore = 0;
        Spawn(x + charW / 2, CharTop(), null, Color.Empty, true);
        if (cfg.Quiet) return;
        if (Climbing()) { SayFrom(Lines.Pet, true); return; }
        Go(St.Pet, 1.6);
        SayFrom(Lines.Pet, true);
    }

    void TrackShake(Point cur, double dt)
    {
        shakeScore = Math.Max(0, shakeScore - dt * 1.2);
        int dx = cur.X - lastCursor.X;
        if (Math.Abs(dx) > 5 * d)
        {
            int sign = dx > 0 ? 1 : -1;
            if (shakeSign != 0 && sign != shakeSign) shakeScore += 1;
            shakeSign = sign;
        }
        if (shakeScore >= 4 && !shaken)
        {
            shaken = true;
            SayFrom(Lines.Shaken, true);
        }
        else if (stateTime > 5 && !heldSaid && !shaken)
        {
            heldSaid = true;
            SayFrom(Lines.Held, true);
        }
    }

    void StepToy(double dt)
    {
        if (toy == null) return;
        toy.Age += dt;
        if (toy.Age > 60)
        {
            toy = null;
            if (st == St.Play) PickNext();
            return;
        }
        toy.Vy -= 1300 * d * dt;
        toy.Air += toy.Vy * dt;
        toy.X += toy.Vx * dt;
        if (toy.Air <= 0)
        {
            toy.Air = 0;
            if (toy.IsBall && -toy.Vy > 110 * d) toy.Vy = -toy.Vy * 0.62;
            else
            {
                toy.Vy = 0;
                toy.Vx = toy.IsBall ? toy.Vx * 0.93 : 0;
            }
        }
        double ceiling = winH - 7 * u;
        if (toy.Air > ceiling) { toy.Air = ceiling; toy.Vy = -Math.Abs(toy.Vy); }
        if (toy.X < work.Left) { toy.X = work.Left; toy.Vx = Math.Abs(toy.Vx) * 0.7; }
        if (toy.X > work.Right - 5 * u) { toy.X = work.Right - 5 * u; toy.Vx = -Math.Abs(toy.Vx) * 0.7; }
        // The window has to hold both the buddy and the toy.
        double leash = winW - charW - 5 * u, gap = toy.X - x;
        if (Math.Abs(gap) > leash)
        {
            toy.X = x + Math.Sign(gap) * leash;
            toy.Vx = -toy.Vx * 0.7;
        }
    }

    void StepPlay(double dt)
    {
        if (toy == null) { PickNext(); return; }
        kickCd -= dt;
        double dist = toy.X + 2.5 * u - (x + charW / 2);
        if (Math.Abs(dist) > charW * 0.3)
        {
            face = dist > 0 ? 1 : -1;
            x = Clamp(x + face * (toy.IsBall ? 125 : 95) * d * dt, work.Left, work.Right - charW);
        }
        if (Math.Abs(dist) > charW * 0.55 + 2.5 * u || toy.Air > charH * 0.8) return;
        if (!toy.IsBall)
        {
            if (toy.Air > 0) return;
            toy = null;
            Go(St.Eat, 2.4);
            SayFrom(Lines.Cookie, false);
            return;
        }
        if (kickCd > 0) return;
        int dir = toy.X < work.Left + 150 * d ? 1 : toy.X > work.Right - 150 * d ? -1 : face;
        toy.Vx = dir * (240 + rnd.NextDouble() * 260) * d;
        toy.Vy = (300 + rnd.NextDouble() * 260) * d;
        kickCd = 0.6;
        kicks++;
        Spawn(toy.X, work.Bottom - toy.Air - 5 * u, "!", Color.FromArgb(240, 180, 130), false);
        if (kicks >= 7)
        {
            toy = null;
            Say("Хорошо поиграли! Мячик в архив", false);
            Go(St.Happy, 1.2);
        }
        else if (rnd.Next(3) == 0) SayFrom(Lines.Kick, false);
    }

    void StepChase(double dt, Point cur)
    {
        chaseCd -= dt;
        double dx = cur.X - (x + charW / 2);
        int above = CharTop() - cur.Y;
        if (Math.Abs(dx) > charW * 0.3)
        {
            face = dx > 0 ? 1 : -1;
            x = Clamp(x + face * 135 * d * dt, work.Left, work.Right - charW);
        }
        if (air > 0 || chaseVy > 0)
        {
            chaseVy -= 1500 * d * dt;
            air = Math.Max(0, air + chaseVy * dt);
            if (air == 0) chaseVy = 0;
        }
        else if (Math.Abs(dx) < charW * 0.6 && above > 0 && above < 90 * d && chaseCd <= 0) chaseVy = 470 * d;
        if (hover && chaseCd <= 0)
        {
            chaseCd = 1.2;
            tags++;
            Spawn(x + charW / 2, CharTop(), "+1", Color.FromArgb(126, 211, 135), false);
            if (tags >= 3)
            {
                Say("Три ноль! Я чемпион", true);
                Go(St.Happy, 1.4);
                return;
            }
            SayFrom(Lines.Tag, true);
        }
        if (above > 300 * d && !highSaid && stateTime > 4)
        {
            highSaid = true;
            Say("Спускайся, я не достаю!", true);
        }
        if (stateTime > stateLen)
        {
            air = 0;
            Say("Ты быстрее. Закрываю как won't fix", true);
            PickNext();
        }
    }

    // A few seconds of steady sound count as music, so notification pings do not start a dance.
    void TrackMusic(float peak, double dt)
    {
        if (peak > 0.015f)
        {
            silentTime = 0;
            loudTime += dt;
        }
        else
        {
            silentTime += dt;
            if (!musicOn && silentTime > 0.8) loudTime = 0;
        }
        if (!musicOn && loudTime >= 3) musicOn = true;
        if (musicOn && silentTime >= 2.5) { StopMusic(); return; }
        if (!musicOn) return;

        TrackBeat(peak, dt);
        if (musicDance || cfg.Quiet) return;
        bool free = st == St.Idle || st == St.Walk || st == St.Think || st == St.Coffee || st == St.Sleep || (st == St.Code && !realWork);
        if (free) StartMusicDance();
    }

    // Jumps on a rise above the running average; heavily compressed tracks have none, so a steady pulse fills in.
    void TrackBeat(float peak, double dt)
    {
        avgPeak += (peak - avgPeak) * 0.08;
        beatCd -= dt;
        sinceBeat += dt;
        bounce *= 0.8;
        if (beatCd > 0 || (peak <= avgPeak * 1.18 && sinceBeat <= 0.55)) return;
        bounce = 1;
        beatCount++;
        beatCd = 0.22;
        sinceBeat = 0;
        if (musicDance && beatCount % 2 == 0)
            Spawn(x + charW / 2 + rnd.Next(-charW / 2, charW / 2), CharTop(), "♪", Color.FromArgb(240, 180, 130), false);
    }

    void StartMusicDance()
    {
        away = false;
        Go(St.Dance, 1e9);
        musicDance = true;
        Chatter(Lines.Music);
    }

    void StopMusic()
    {
        musicOn = false;
        loudTime = 0;
        bounce = 0;
        if (!musicDance) return;
        musicDance = false;
        air = 0;
        PickNext();
    }

    static int IdleSeconds()
    {
        var info = new Native.LASTINPUT { Size = 8 };
        if (!Native.GetLastInputInfo(ref info)) return 0;
        return (int)(((uint)Environment.TickCount - info.Time) / 1000);
    }

    // Dozes off while nobody is at the computer and greets them when they return.
    void CheckAway(int idle)
    {
        if (!away && idle >= 300)
        {
            if (realWork || musicOn || cfg.Quiet || st == St.Drag || st == St.Fall || st == St.Weave) return;
            if (st == St.Sleep && !cfg.Weave) return;
            away = true;
            toy = null;
            if (cfg.Weave)
            {
                webFast = false;
                Go(St.Weave, 1e9);
            }
            else Go(St.Sleep, 1e9);
        }
        else if (away && idle < 2)
        {
            away = false;
            if (st == St.Weave && web != null)
            {
                if (!cfg.Quiet) Say(Lines.WebBack(web.P), true);
                Go(St.Happy, 1.0);
                return;
            }
            if (st != St.Sleep || cfg.Quiet) return;
            Go(St.Happy, 1.0);
            SayFrom(Lines.Back, false);
        }
    }

    Color CodeColor()
    {
        Color[] pal = { Color.FromArgb(126, 211, 135), Color.FromArgb(240, 160, 110), Color.FromArgb(120, 180, 240), Color.FromArgb(225, 220, 205) };
        return pal[rnd.Next(pal.Length)];
    }

    void Spawn(double px, double py, string text, Color ink, bool heart)
    {
        if (parts.Count > 14) return;
        parts.Add(new Particle
        {
            X = (float)px, Y = (float)py, Vx = (float)((rnd.NextDouble() - 0.5) * 14 * d), Vy = -(float)(22 * d),
            Life = 1.8f, Text = text, Ink = ink, Heart = heart
        });
    }

    void StepParticles(double dt)
    {
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            Particle p = parts[i];
            p.Age += (float)dt;
            p.X += p.Vx * (float)dt;
            p.Y += p.Vy * (float)dt;
            if (p.Age > p.Life) parts.RemoveAt(i);
        }
    }

    void Say(string text, bool force)
    {
        if (cfg.Quiet && !force) return;
        bubble = text;
        bubbleAge = 0;
        bubbleLen = 2.2 + text.Length * 0.07;
    }

    void SayFrom(string[] lines, bool force) { Say(lines[rnd.Next(lines.Length)], force); }

    // Unprompted lines are rationed, so the buddy stays background noise.
    void Chatter(string[] lines)
    {
        if ((DateTime.Now - lastChatter).TotalSeconds < 70 || rnd.Next(2) == 0) return;
        lastChatter = DateTime.Now;
        SayFrom(lines, false);
    }

    void StepBubble(double dt)
    {
        if (bubble == null) return;
        bubbleAge += dt;
        if (bubbleAge > bubbleLen) bubble = null;
    }

    void SetClickThrough(bool on)
    {
        if (on == clickThrough) return;
        clickThrough = on;
        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);
    }

    bool FullscreenApp()
    {
        int state;
        if (Native.SHQueryUserNotificationState(out state) == 0 && state >= 2 && state <= 4) return true;
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == Handle) return false;
        var cls = new StringBuilder(64);
        Native.GetClassName(fg, cls, 64);
        string c = cls.ToString();
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
        Native.RECT r;
        if (!Native.GetWindowRect(fg, out r)) return false;
        Rectangle b = Screen.FromHandle(fg).Bounds;
        return r.L <= b.Left && r.T <= b.Top && r.R >= b.Right && r.B >= b.Bottom;
    }

    // ----- drawing -----

    int CharTop() { return work.Bottom - (int)air - charH; }

    static double Clamp(double v, double lo, double hi) { return Math.Max(lo, Math.Min(hi, v)); }

    Pose BuildPose()
    {
        var p = new Pose { Face = face };
        bool blink = frame % 90 < 2;
        p.Eyes = blink ? Eye.Closed : Eye.Open;
        switch (st)
        {
            case St.Walk:
                if (!hover) { p.Legs = 1 + frame / 3 % 2; p.LookX = face; }
                else p.LookX = lookX;
                break;
            case St.Code:
                bool pause = frame / 40 % 4 == 3;
                p.Laptop = true;
                p.Arms = ArmPose.Type;
                p.LookX = face;
                p.LookY = pause ? -1 : 0;
                p.Anim = pause ? 0 : frame / 2;
                p.LaptopFrame = frame;
                break;
            case St.Think:
                p.LookX = frame / 20 % 2 == 0 ? 1 : -1;
                p.LookY = -1;
                break;
            case St.Coffee:
                p.Arms = ArmPose.Hold;
                p.Mug = true;
                p.Anim = frame / 6;
                p.Eyes = frame / 30 % 2 == 0 ? Eye.Closed : Eye.Happy;
                break;
            case St.Sleep:
                p.Legs = 5;
                p.Eyes = Eye.Closed;
                p.Bob = frame / 18 % 2;
                break;
            case St.Hunt:
                p.Legs = 1 + frame / 2 % 2;
                p.LookX = face;
                p.Eyes = Eye.Wide;
                break;
            case St.Happy:
                p.Arms = ArmPose.Up;
                p.Eyes = Eye.Happy;
                p.Anim = frame / 3;
                break;
            case St.Drag:
                p.Arms = ArmPose.Up;
                p.Eyes = shaken ? Eye.Dizzy : Eye.Wide;
                p.Legs = 3 + frame / 3 % 2;
                break;
            case St.Eat:
                p.Mouth = frame / 3 % 2 == 0;
                p.Eyes = Eye.Happy;
                p.Bob = frame / 6 % 2;
                break;
            case St.Pet:
                p.Eyes = Eye.Happy;
                p.Bob = frame / 8 % 2;
                break;
            case St.Play:
                p.Legs = 1 + frame / 2 % 2;
                p.LookX = face;
                break;
            case St.Chase:
                p.Legs = 1 + frame / 2 % 2;
                p.LookX = face;
                p.LookY = -1;
                p.Eyes = Eye.Wide;
                break;
            case St.Leap:
                p.Arms = ArmPose.Up;
                p.Eyes = Eye.Wide;
                p.LookX = face;
                break;
            case St.Weave:
                bool done = web != null && web.P >= 1;
                p.Legs = done ? 0 : 1 + frame / 4 % 2;
                p.Eyes = done ? Eye.Closed : Eye.Open;
                p.LookX = done ? 0 : face;
                if (!webReached) p.Arms = ArmPose.Up;
                break;
            case St.Dangle:
                p.Legs = 1 + frame / 6 % 2;
                p.LookX = lookX;
                if (dangleMode == 3) p.Eyes = frame % 90 < 2 ? Eye.Closed : Eye.Wide;
                break;
            case St.Peek:
                if (peekPhase == 0) { p.Legs = 1 + frame / 2 % 2; p.LookX = face; }
                else
                {
                    p.LookX = -peekSide;
                    p.Eyes = peekPhase == 3 ? Eye.Wide : Eye.Open;
                }
                break;
            case St.Crawl:
                if (!hover)
                {
                    p.Legs = 1 + frame / 3 % 2;
                    p.LookX = face;
                }
                break;
            case St.Dance:
                p.Arms = ArmPose.Wave;
                p.Anim = musicDance ? beatCount : frame / 5;
                p.Legs = 1 + (musicDance ? beatCount : frame / 4) % 2;
                p.Eyes = Eye.Happy;
                break;
            case St.Fall:
                p.Arms = ArmPose.Up;
                p.Eyes = Eye.Wide;
                break;
            case St.Dizzy:
                p.Eyes = Eye.Dizzy;
                p.Bob = frame / 8 % 2;
                break;
            default:
                p.LookX = lookX;
                break;
        }
        if (dropping) { p.Eyes = Eye.Wide; p.Mouth = true; }
        return p;
    }

    void Render()
    {
        Pose pose = BuildPose();
        int charTop = CharTop();
        double centre = x + charW / 2;
        double focus = bugOn ? bugX : toy != null ? toy.X + 2.5 * u : centre;
        int winX = (int)Clamp((centre + focus) / 2 - winW / 2, bounds.Left, bounds.Right - winW);
        int winY = Math.Max(bounds.Top, charTop + charH - winH);
        int revealed = bubble == null ? 0 : Math.Min(bubble.Length, (int)(bubbleAge * 38) + 1);
        int sig;
        unchecked
        {
            sig = 17 + pose.Sig();
            sig = sig * 31 + (int)x;
            sig = sig * 31 + charTop;
            sig = sig * 31 + revealed;
            sig = sig * 31 + (parts.Count > 0 || bugOn || toy != null ? frame : 0);
        }
        if (sig == lastSig) return;
        lastSig = sig;

        Graphics g = frameG;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        if (st == St.Dangle || st == St.Weave)
        {
            int middle = (int)x - winX + charW / 2;
            if (st == St.Dangle) Painter.Thread(g, (int)dangleAnchor - winX, work.Top - winY, middle, charTop - winY + u, Math.Max(1, u / 2));
            g.TranslateTransform(middle, charTop - winY + charH / 2f);
            g.RotateTransform(180);
            Painter.Buddy(g, -charW / 2, -charH / 2, u, pose);
            g.ResetTransform();
        }
        else if (st == St.Crawl && crawlEdge != 0)
        {
            g.TranslateTransform((float)crawlX - winX, (float)crawlY - winY);
            g.RotateTransform(crawlEdge == 1 ? -90 : crawlEdge == 2 ? 180 : 90);
            Painter.Buddy(g, -charW / 2, -charH / 2, u, pose);
            g.ResetTransform();
        }
        else Painter.Buddy(g, (int)x - winX, charTop - winY, u, pose);
        if (bugOn)
        {
            Rectangle bugPad = BugRect();
            bugPad.Offset(-winX, -winY);
            Painter.Pad(g, bugPad);
            Painter.Bug(g, (int)bugX - winX, work.Bottom - winY, Math.Max(2, u - 1), frame);
        }
        if (toy != null)
        {
            int tx = (int)toy.X - winX, ty = work.Bottom - (int)toy.Air - winY;
            if (toy.IsBall)
            {
                Rectangle ballPad = ToyRect();
                ballPad.Offset(-winX, -winY);
                Painter.Pad(g, ballPad);
                Painter.Ball(g, tx, ty, u, (int)Math.Abs(toy.X / (2 * u)));
            }
            else Painter.Cookie(g, tx, ty, u);
        }
        DrawParticles(g, winX, winY);
        if (revealed > 0)
            Painter.Bubble(g, bubble.Substring(0, revealed), (int)x - winX + charW / 2, charTop - winY, winW, d,
                charTop - winY < 60 * d ? charTop - winY + charH : -1);

        Painter.Present(Handle, frameBmp, winX, winY);
    }

    void DrawParticles(Graphics g, int winX, int winY)
    {
        if (parts.Count == 0) return;
        using (var font = new Font("Consolas", 7.5f + cfg.Size, FontStyle.Bold, GraphicsUnit.Point))
        {
            foreach (Particle p in parts)
            {
                int alpha = (int)(255 * Math.Max(0, 1 - p.Age / p.Life));
                if (p.Heart) { Painter.PixelHeart(g, (int)p.X - winX, (int)p.Y - winY, Math.Max(1, u / 2), alpha); continue; }
                using (var b = new SolidBrush(Color.FromArgb(alpha, p.Ink)))
                    g.DrawString(p.Text, font, b, p.X - winX, p.Y - winY);
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tray.Visible = false;
        tray.Dispose();
        timer.Stop();
        if (watcher != null) watcher.Dispose();
        if (webForm != null) webForm.Close();
        base.OnFormClosed(e);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--notify")
        {
            // A hook must never get in Claude's way, whatever goes wrong here.
            try { TaskNotice.RunHook(); }
            catch (Exception ex) { Trace.WriteLine(ex); }
            return;
        }
        Native.SetProcessDPIAware();
        if (args.Length == 2 && args[0] == "--sheet")
        {
            Painter.Sheet(args[1]);
            return;
        }
        if (args.Length == 1 && args[0] == "--autostart")
        {
            Autostart.Enabled = true;
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, "ClaudeBuddy_Singleton", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new BuddyForm());
            GC.KeepAlive(mutex);
        }
    }
}
