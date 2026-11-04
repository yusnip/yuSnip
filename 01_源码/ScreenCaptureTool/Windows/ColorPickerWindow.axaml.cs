using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using Avalonia.Automation;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Windows;

public partial class ColorPickerWindow : Window
{
    private double _hue;
    private double _saturation = 1.0;
    private double _value = 1.0;
    private double _alpha = 1.0;
    private bool _updating;
    private PickerDragMode _dragMode;

    private enum PickerDragMode
    {
        None,
        Sv,
        Hue,
        Alpha,
    }

    public ColorPickerWindow()
    {
        InitializeComponent();
        UseWorkspaceAppearance();
        Loaded += (_, _) => UpdateSelectors();
        SetColor(Color.FromArgb(255, 51, 136, 255));

        // 键盘快捷键：Esc 取消，Enter 确定（Tunnel 优先响应）
        AddHandler(KeyDownEvent, (sender, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                Close(SelectedColor);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    public Color SelectedColor { get; private set; }

    /// <summary>所有入口统一使用普通窗口皮；复用原内容与布局，不影响调用方工具栏。</summary>
    private void UseWorkspaceAppearance()
    {
        if (Content is not Border original) return;
        if (this.TryFindResource("WorkspacePickerStyles", ActualThemeVariant, out var resource) && resource is Styles appearance)
            Styles.Add(appearance);

        // 复用原内容树、事件与名称；仅替换绘制容器，不复制调色盘布局或色谱算法。
        Content = null;
        Content = PickerSurface(original, "SctCardFillBrush", "SctRadiusControl");
        foreach (var input in new[] { HexBox, RBox, GBox, BBox })
        {
            if (input.Parent is not Border wrapper || wrapper.Parent is not Panel panel) continue;
            int index = panel.Children.IndexOf(wrapper);
            panel.Children[index] = PickerSurface(wrapper, "SctInputBgBrush", "SctRadiusControl");
        }
        foreach (var button in this.GetLogicalDescendants().OfType<Button>().Where(b => b.Classes.Contains("pickerSwatch")))
            button.Bind(Button.CornerRadiusProperty, new DynamicResourceExtension("SctRadiusSmall"));
        var close = this.GetLogicalDescendants().OfType<Button>().First(b => b.Content is TextBlock { Text: "✕" });
        close.Content = new Avalonia.Controls.Shapes.Path { Classes = { "workspaceIcon" }, Width = 12, Height = 12, Data = Geometry.Parse(WorkspaceIconGeometry.Close) };
        close.Bind(Button.CornerRadiusProperty, new DynamicResourceExtension("SctRadiusControl"));
        AutomationProperties.SetName(close, "关闭");
        ToolTip.SetTip(close, "关闭");
    }

    private static ContinuousBorder PickerSurface(Border original, string background, string radius)
    {
        var child = original.Child;
        original.Child = null;
        var surface = new ContinuousBorder
        {
            Width = original.Width, Height = original.Height,
            MinWidth = original.MinWidth, MinHeight = original.MinHeight,
            MaxWidth = original.MaxWidth, MaxHeight = original.MaxHeight,
            Margin = original.Margin, Padding = original.Padding, BorderThickness = original.BorderThickness,
            HorizontalAlignment = original.HorizontalAlignment, VerticalAlignment = original.VerticalAlignment,
            Child = child,
        };
        surface.Bind(ContinuousBorder.BackgroundProperty, new DynamicResourceExtension(background));
        surface.Bind(ContinuousBorder.BorderBrushProperty, new DynamicResourceExtension("SctBorderBrush"));
        surface.Bind(ContinuousBorder.CornerRadiusProperty, new DynamicResourceExtension(radius));
        return surface;
    }

    public static async System.Threading.Tasks.Task<Color?> PickAsync(Window owner, Color initial, Control? anchor = null)
    {
        var window = new ColorPickerWindow();
        window.SetColor(initial);

        IntPtr ownerHwnd = IntPtr.Zero;
        try
        {
            ownerHwnd = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch { }

        // 弹出模态调色盘前，临时卸载全屏遮罩的持久置顶守卫（否则其 WndProc 会把遮罩抢回顶层）。
        // ⚠ 降置顶必须走 Avalonia 的 Topmost **属性**，不能裸 SetWindowPos(HWND_NOTOPMOST)：
        //    遮罩 XAML Topmost="True" 记在 Avalonia 自己的账本里，裸 p/invoke 降级会被框架在
        //    ShowDialog 禁用 owner 等节点"自愈"回置顶；且 Avalonia 的模态 z 序摆放会把调色盘
        //    插到 owner 所在的（非置顶）带里——遮罩一回顶层，调色盘就被压在全屏遮罩下面，
        //    owner 又被模态禁用，整屏点不动，表现为"卡死"（2026-09-07 实锤：现场 z 序快照里
        //    遮罩 topmost=True / enabled=False，调色盘 topmost=False 排在它后面）。
        //    正确做法：owner.Topmost=false（框架账本一致，不会自愈），调色盘靠自身 XAML
        //    Topmost="True" 或同带内紧随 owner 之上，两条路都必然可见。
        bool hadGuard = ownerHwnd != IntPtr.Zero && WindowHelper.IsEnforceTopmostActive(ownerHwnd);
        if (hadGuard)
        {
            try { WindowHelper.StopEnforceTopmost(ownerHwnd); } catch { }
        }
        bool ownerWasTopmost = owner.Topmost;
        if (ownerWasTopmost) owner.Topmost = false;

        // 计算调色盘物理像素位置：优先就近停靠在 anchor 控件旁，确保完整落在当前活动显示器工作区内
        window.PositionWindow(owner, anchor);

        window.Opened += (_, _) =>
        {
            try
            {
                IntPtr pickerHwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (pickerHwnd != IntPtr.Zero)
                {
                    try { NativeMethods.SetForegroundWindow(pickerHwnd); } catch { }
                }
            }
            catch { }
        };

        try
        {
            return await window.ShowDialog<Color?>(owner);
        }
        finally
        {
            // 对话框关闭后（无论保存、取消还是异常），若 owner 依然处于活动状态，恢复其置顶与
            // 持久置顶守卫。只恢复本轮动过的：守卫只在先前确实存在时重装（避免给从未挂守卫的
            // 窗口——如贴图标注浮窗——凭空装上 EnforceTopmost）。
            if (owner.IsVisible)
            {
                if (ownerWasTopmost) owner.Topmost = true;
                if (hadGuard)
                {
                    try { WindowHelper.EnforceTopmost(ownerHwnd); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// 将调色盘定位在 anchor 控件附近（下方或上方），并严格钳制在当前显示器工作区内；
    /// 若无 anchor，则居中在当前光标所在的显示器工作区中央（消除多显示器虚拟屏幕中心裂缝问题）。
    /// </summary>
    private void PositionWindow(Window owner, Control? anchor)
    {
        try
        {
            double dipW = Width > 0 ? Width : 288;
            double dipH = Height > 0 ? Height : 432;
            double scale = owner.DesktopScaling > 0 ? owner.DesktopScaling : 1.0;
            int pickerW = (int)Math.Round(dipW * scale);
            int pickerH = (int)Math.Round(dipH * scale);

            System.Drawing.Rectangle workArea = MonitorHelper.GetMonitorAtCursor();
            var workAreas = MonitorHelper.GetWorkAreaRects();
            foreach (var wa in workAreas)
            {
                if (wa.IntersectsWith(workArea))
                {
                    workArea = wa;
                    break;
                }
            }

            int targetX, targetY;

            if (anchor != null && anchor.IsLoaded && anchor.GetVisualRoot() != null)
            {
                PixelPoint anchorScreenTopLeft = anchor.PointToScreen(new Point(0, 0));
                PixelPoint anchorScreenBottomLeft = anchor.PointToScreen(new Point(0, anchor.Bounds.Height));
                int anchorW = (int)Math.Round(anchor.Bounds.Width * scale);

                // 水平居中对齐 anchor
                targetX = anchorScreenTopLeft.X + (anchorW - pickerW) / 2;

                // 默认停靠在 anchor 下方（留 4px 间距）
                int gap = (int)Math.Round(4 * scale);
                targetY = anchorScreenBottomLeft.Y + gap;

                // 若下方超出工作区底部，且上方空间充足，则翻转至 anchor 上方
                if (targetY + pickerH > workArea.Bottom && anchorScreenTopLeft.Y - pickerH - gap >= workArea.Top)
                {
                    targetY = anchorScreenTopLeft.Y - pickerH - gap;
                }
            }
            else
            {
                // 无 anchor 或不可见：居中在光标当前所在的监视器工作区
                targetX = workArea.Left + (workArea.Width - pickerW) / 2;
                targetY = workArea.Top + (workArea.Height - pickerH) / 2;
            }

            // 严格夹紧在当前工作区内（四周留 8px 安全边距）
            int margin = (int)Math.Round(8 * scale);
            targetX = Math.Clamp(targetX, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - pickerW - margin));
            targetY = Math.Clamp(targetY, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - pickerH - margin));

            Position = new PixelPoint(targetX, targetY);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("ColorPickerWindow.PositionWindow: 计算定位异常：" + ex.Message);
        }
    }

    private void SetColor(Color color)
    {
        RgbToHsv(color.R, color.G, color.B, out _hue, out _saturation, out _value);
        _alpha = color.A / 255.0;
        UpdateColor(true);
    }

    private void UpdateColor(bool updateInputs)
    {
        Color rgb = HsvToRgb(_hue, _saturation, _value, _alpha);
        SelectedColor = rgb;
        SvPanel.Background = new SolidColorBrush(HsvToRgb(_hue, 1, 1, 1));
        PreviewBorder.Background = new SolidColorBrush(rgb);
        AlphaTrack.Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, rgb.R, rgb.G, rgb.B), 0),
                new GradientStop(Color.FromArgb(255, rgb.R, rgb.G, rgb.B), 1),
            },
        };
        UpdateSelectors();
        if (!updateInputs) return;

        _updating = true;
        HexBox.Text = $"#{rgb.A:X2}{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
        RBox.Text = rgb.R.ToString();
        GBox.Text = rgb.G.ToString();
        BBox.Text = rgb.B.ToString();
        _updating = false;
    }

    private void UpdateSelectors()
    {
        double svW = Math.Max(1.0, SvPanel.Bounds.Width);
        double svH = Math.Max(1.0, SvPanel.Bounds.Height);
        SvSelector.Margin = new Thickness(
            Math.Clamp(_saturation * svW - SvSelector.Width / 2.0, -SvSelector.Width / 2.0, Math.Max(0, svW - SvSelector.Width / 2.0)),
            Math.Clamp((1.0 - _value) * svH - SvSelector.Height / 2.0, -SvSelector.Height / 2.0, Math.Max(0, svH - SvSelector.Height / 2.0)),
            0,
            0);
        HueSelector.Margin = new Thickness(-2, Math.Clamp(_hue / 360.0 * svH - HueSelector.Height / 2.0, 0, Math.Max(0, svH - HueSelector.Height)), 0, 0);
        double alphaW = Math.Max(1.0, AlphaPanel.Bounds.Width);
        AlphaSelector.Margin = new Thickness(Math.Clamp(_alpha * alphaW - AlphaSelector.Width / 2.0, 0, Math.Max(0, alphaW - AlphaSelector.Width)), 0, 0, 0);
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try { BeginMoveDrag(e); } catch { }
    }

    private void OnSvPointer(object? sender, PointerPressedEventArgs e)
    {
        _dragMode = PickerDragMode.Sv;
        e.Pointer.Capture(sender as IInputElement);
        UpdateSvFromPointer(sender, e);
    }

    private void OnSvPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || _dragMode != PickerDragMode.Sv || !e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        UpdateSvFromPointer(sender, e);
    }

    private void UpdateSvFromPointer(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        Point p = e.GetPosition(c);
        _saturation = Math.Clamp(p.X / Math.Max(1, c.Bounds.Width), 0, 1);
        _value = Math.Clamp(1.0 - p.Y / Math.Max(1, c.Bounds.Height), 0, 1);
        UpdateColor(true);
    }

    private void OnHuePointer(object? sender, PointerPressedEventArgs e)
    {
        _dragMode = PickerDragMode.Hue;
        e.Pointer.Capture(sender as IInputElement);
        UpdateHueFromPointer(sender, e);
    }

    private void OnHuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || _dragMode != PickerDragMode.Hue || !e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        UpdateHueFromPointer(sender, e);
    }

    private void UpdateHueFromPointer(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        Point p = e.GetPosition(c);
        _hue = Math.Clamp(p.Y / Math.Max(1, c.Bounds.Height), 0, 1) * 360.0;
        UpdateColor(true);
    }

    private void OnAlphaPointer(object? sender, PointerPressedEventArgs e)
    {
        _dragMode = PickerDragMode.Alpha;
        e.Pointer.Capture(sender as IInputElement);
        UpdateAlphaFromPointer(sender, e);
    }

    private void OnAlphaPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || _dragMode != PickerDragMode.Alpha || !e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) return;
        UpdateAlphaFromPointer(sender, e);
    }

    private void UpdateAlphaFromPointer(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c) return;
        Point p = e.GetPosition(c);
        _alpha = Math.Clamp(p.X / Math.Max(1, c.Bounds.Width), 0, 1);
        UpdateColor(true);
    }

    private void OnPickerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragMode = PickerDragMode.None;
        e.Pointer.Capture(null);
    }

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        string? tag = b.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(tag)) return;
        try { SetColor(Color.Parse(tag)); } catch { }
    }

    private void OnHexTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        string? text = HexBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        try { SetColor(Color.Parse(text)); } catch { }
    }

    private void OnRgbTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_updating) return;
        if (!byte.TryParse(RBox.Text, out byte r)) return;
        if (!byte.TryParse(GBox.Text, out byte g)) return;
        if (!byte.TryParse(BBox.Text, out byte b)) return;
        RgbToHsv(r, g, b, out _hue, out _saturation, out _value);
        UpdateColor(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);

    private void OnOkClick(object? sender, RoutedEventArgs e) => Close(SelectedColor);

    private static Color HsvToRgb(double h, double s, double v, double a)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromArgb(
            (byte)Math.Round(Math.Clamp(a, 0, 1) * 255),
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(byte rb, byte gb, byte bb, out double h, out double s, out double v)
    {
        double r = rb / 255.0;
        double g = gb / 255.0;
        double b = bb / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * (((b - r) / d) + 2);
            else h = 60 * (((r - g) / d) + 4);
        }
        if (h < 0) h += 360;
        s = max == 0 ? 0 : d / max;
        v = max;
    }
}
