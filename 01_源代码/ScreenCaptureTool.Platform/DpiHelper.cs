using System;
using System.Drawing;

namespace ScreenCaptureTool.Platform;

/// <summary>
/// DPI 查询与物理像素 / DIP 换算辅助。
/// </summary>
public static class DpiHelper
{
    public const double DefaultDpi = 96.0;

    public static double GetSystemDpi()
    {
        try
        {
            uint dpi = NativeMethods.GetDpiForSystem();
            return dpi > 0 ? dpi : DefaultDpi;
        }
        catch
        {
            return DefaultDpi;
        }
    }

    public static double GetDpiForWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return GetSystemDpi();

        try
        {
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi : GetSystemDpi();
        }
        catch
        {
            return GetSystemDpi();
        }
    }

    public static double GetSystemScale()
    {
        return DpiToScale(GetSystemDpi());
    }

    public static double GetScaleForWindow(IntPtr hwnd)
    {
        return DpiToScale(GetDpiForWindow(hwnd));
    }

    public static double DpiToScale(double dpi)
    {
        return dpi > 0 ? dpi / DefaultDpi : 1.0;
    }

    public static PointF PhysicalToDip(Point physicalPoint, Point originPhysical, double scale)
    {
        double safeScale = NormalizeScale(scale);
        return new PointF(
            (float)((physicalPoint.X - originPhysical.X) / safeScale),
            (float)((physicalPoint.Y - originPhysical.Y) / safeScale));
    }

    public static RectangleF PhysicalToDip(Rectangle physicalRect, Point originPhysical, double scale)
    {
        double safeScale = NormalizeScale(scale);
        return new RectangleF(
            (float)((physicalRect.X - originPhysical.X) / safeScale),
            (float)((physicalRect.Y - originPhysical.Y) / safeScale),
            (float)(physicalRect.Width / safeScale),
            (float)(physicalRect.Height / safeScale));
    }

    public static Point DipToPhysical(PointF dipPoint, Point originPhysical, double scale)
    {
        double safeScale = NormalizeScale(scale);
        return new Point(
            (int)Math.Round(dipPoint.X * safeScale + originPhysical.X),
            (int)Math.Round(dipPoint.Y * safeScale + originPhysical.Y));
    }

    public static Rectangle DipToPhysical(RectangleF dipRect, Point originPhysical, double scale)
    {
        double safeScale = NormalizeScale(scale);
        int x = (int)Math.Round(dipRect.X * safeScale + originPhysical.X);
        int y = (int)Math.Round(dipRect.Y * safeScale + originPhysical.Y);
        int w = (int)Math.Round(dipRect.Width * safeScale);
        int h = (int)Math.Round(dipRect.Height * safeScale);
        return new Rectangle(x, y, w, h);
    }

    private static double NormalizeScale(double scale)
    {
        return scale > 0 ? scale : 1.0;
    }
}
