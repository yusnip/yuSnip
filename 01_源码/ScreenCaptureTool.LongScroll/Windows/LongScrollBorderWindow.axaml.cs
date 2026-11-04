using System;
using System.Drawing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.LongScroll.Windows;

/// <summary>长截图选区边框窗口。复用主截图 SelectionLayer，窗口仅覆盖选区周围一小圈，鼠标穿透，不抢焦点。</summary>
public partial class LongScrollBorderWindow : Window
{
    private const int BorderPaddingPx = 28;
    private const int InteriorHitThroughInsetPx = 12;

    private readonly Rectangle _region;
    private readonly DispatcherTimer _toastTimer;
    private PixelPoint _windowOrigin;

    public LongScrollBorderWindow()
        : this(Rectangle.Empty)
    {
    }

    public LongScrollBorderWindow(Rectangle region)
    {
        _region = region.Width > 0 && region.Height > 0 ? region : new Rectangle(100, 100, 320, 240);
        InitializeComponent();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.IsVisible = false;
        };

        Opened += (_, _) =>
        {
            double scale = DesktopScaling > 0 ? DesktopScaling : 1.0;
            LayoutRegionWindow(scale);
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

    public void ShowMaxHeightToast()
    {
        Dispatcher.UIThread.Post(() =>
        {
            double scale = DesktopScaling > 0 ? DesktopScaling : 1.0;
            double padding = BorderPaddingPx / scale;
            double width = _region.Width / scale;
            double height = _region.Height / scale;

            ToastText.MaxWidth = Math.Max(80, width - 24);
            ToastBorder.Measure(Avalonia.Size.Infinity);
            Avalonia.Size desired = ToastBorder.DesiredSize;
            Canvas.SetLeft(ToastBorder, Math.Max(padding + 8, padding + (width - desired.Width) / 2.0));
            Canvas.SetTop(ToastBorder, Math.Max(padding + 8, padding + (height - desired.Height) / 2.0));
            ToastBorder.IsVisible = true;
            _toastTimer.Stop();
            _toastTimer.Start();
        });
    }

    private void LayoutRegionWindow(double scale)
    {
        int paddingPx = BorderPaddingPx;
        _windowOrigin = new PixelPoint(_region.Left - paddingPx, _region.Top - paddingPx);
        Position = _windowOrigin;

        Width = (_region.Width + paddingPx * 2) / scale;
        Height = (_region.Height + paddingPx * 2) / scale;

        double padding = paddingPx / scale;
        double regionWidth = _region.Width / scale;
        double regionHeight = _region.Height / scale;
        double windowWidth = Math.Max(1.0, Width);
        double windowHeight = Math.Max(1.0, Height);

        SelectionLayer.Width = windowWidth;
        SelectionLayer.Height = windowHeight;
        SelectionLayer.Region = new Rect(padding, padding, regionWidth, regionHeight);
        SelectionLayer.ShowMask = false;
        SelectionLayer.ShowBorderAndHandles = true;
        Canvas.SetLeft(SelectionLayer, 0);
        Canvas.SetTop(SelectionLayer, 0);
    }

    private void ApplyHollowWindowRegion(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int outerWidth = Math.Max(1, _region.Width + BorderPaddingPx * 2);
            int outerHeight = Math.Max(1, _region.Height + BorderPaddingPx * 2);
            int holeLeft = Math.Max(0, BorderPaddingPx + InteriorHitThroughInsetPx);
            int holeTop = Math.Max(0, BorderPaddingPx + InteriorHitThroughInsetPx);
            int holeRight = Math.Min(outerWidth, BorderPaddingPx + _region.Width - InteriorHitThroughInsetPx);
            int holeBottom = Math.Min(outerHeight, BorderPaddingPx + _region.Height - InteriorHitThroughInsetPx);

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
