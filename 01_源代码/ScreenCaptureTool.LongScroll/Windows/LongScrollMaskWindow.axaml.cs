using System;
using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll.Windows;

/// <summary>长截图遮罩窗口。复用主截图 SelectionLayer，仅显示遮罩；原生窗口区域挖空选区，避免阻塞框内滚动。</summary>
public partial class LongScrollMaskWindow : Window
{
    private readonly Rectangle _region;
    private readonly Rectangle _virtualScreen;

    public LongScrollMaskWindow()
        : this(Rectangle.Empty)
    {
    }

    public LongScrollMaskWindow(Rectangle region)
    {
        _region = region.Width > 0 && region.Height > 0 ? region : new Rectangle(100, 100, 320, 240);
        _virtualScreen = MonitorHelper.GetVirtualScreenRect();
        InitializeComponent();

        Opened += (_, _) =>
        {
            double scale = DesktopScaling > 0 ? DesktopScaling : 1.0;
            LayoutMask(scale);
            IntPtr hwnd = GetWindowHwnd();
            ApplyHollowWindowRegion(hwnd);
            WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: true);
            WindowHelper.MakeHitTestTransparent(hwnd);
            WindowHelper.DisableWindowChromeEffects(hwnd);
        };
    }

    public void EnsureClickThrough()
    {
        IntPtr hwnd = GetWindowHwnd();
        try
        {
            ApplyHollowWindowRegion(hwnd);
            WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: true);
            WindowHelper.MakeHitTestTransparent(hwnd);
            WindowHelper.DisableWindowChromeEffects(hwnd);
        }
        catch
        {
        }
    }

    private void LayoutMask(double scale)
    {
        Position = new PixelPoint(_virtualScreen.Left, _virtualScreen.Top);
        Width = _virtualScreen.Width / scale;
        Height = _virtualScreen.Height / scale;

        SelectionLayer.Width = Math.Max(1.0, Width);
        SelectionLayer.Height = Math.Max(1.0, Height);
        SelectionLayer.Region = new Rect(
            (_region.Left - _virtualScreen.Left) / scale,
            (_region.Top - _virtualScreen.Top) / scale,
            _region.Width / scale,
            _region.Height / scale);
        SelectionLayer.ShowMask = true;
        SelectionLayer.ShowBorderAndHandles = false;
    }

    private void ApplyHollowWindowRegion(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int outerWidth = Math.Max(1, _virtualScreen.Width);
            int outerHeight = Math.Max(1, _virtualScreen.Height);
            int holeLeft = Math.Clamp(_region.Left - _virtualScreen.Left, 0, outerWidth);
            int holeTop = Math.Clamp(_region.Top - _virtualScreen.Top, 0, outerHeight);
            int holeRight = Math.Clamp(_region.Right - _virtualScreen.Left, 0, outerWidth);
            int holeBottom = Math.Clamp(_region.Bottom - _virtualScreen.Top, 0, outerHeight);
            if (holeRight <= holeLeft || holeBottom <= holeTop) return;

            IntPtr outer = NativeMethods.CreateRectRgn(0, 0, outerWidth, outerHeight);
            IntPtr inner = NativeMethods.CreateRectRgn(holeLeft, holeTop, holeRight, holeBottom);
            if (outer == IntPtr.Zero || inner == IntPtr.Zero)
            {
                if (outer != IntPtr.Zero) NativeMethods.DeleteObject(outer);
                if (inner != IntPtr.Zero) NativeMethods.DeleteObject(inner);
                return;
            }

            NativeMethods.CombineRgn(outer, outer, inner, NativeMethods.RGN_DIFF);
            NativeMethods.DeleteObject(inner);
            int result = NativeMethods.SetWindowRgn(hwnd, outer, true);
            if (result == 0)
            {
                NativeMethods.DeleteObject(outer);
            }
        }
        catch
        {
        }
    }

    private IntPtr GetWindowHwnd()
    {
        try
        {
            return TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
