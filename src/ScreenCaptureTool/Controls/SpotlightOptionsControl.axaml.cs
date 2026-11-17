using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Controls;

/// <summary>聚光灯形状、暗幕与描边共用参数；回显不触发用户修改事件。
/// 圆角滑块已拆除（2026-09-12「圆角滑块退役」）、画布四角手柄也于 2026-09-18 退役：
/// 圆角一律在「选中后元素上方浮出的圆角浮条」上调整，圆角记忆由宿主字段承载，本控件不再管圆角。</summary>
public partial class SpotlightOptionsControl : UserControl
{
    public Slider Darkness => DarknessSlider;
    public Slider Stroke => StrokeSlider;
    public TextBlock DarknessValue => DarknessText;
    public TextBlock StrokeValue => StrokeText;
    public Ellipse StrokePreview => StrokeCircle;
    public SpotlightShapeKind Kind { get; private set; }
    public event EventHandler? KindChanged;
    public event EventHandler? StrokeAdjusted;

    public SpotlightOptionsControl()
    {
        InitializeComponent();
        SyncKind(SpotlightShapeKind.Rectangle);
        DarknessSlider.ValueChanged += (_, _) => DarknessText.Text = Math.Round(DarknessSlider.Value).ToString();
        StrokeSlider.ValueChanged += (_, _) => StrokeText.Text = Math.Round(StrokeSlider.Value).ToString();
    }

    public void SyncKind(SpotlightShapeKind kind)
    {
        Kind = kind;
        RectButton.Classes.Set("active", kind == SpotlightShapeKind.Rectangle);
        CircleButton.Classes.Set("active", kind == SpotlightShapeKind.Ellipse);
    }

    private void OnRectClick(object? sender, RoutedEventArgs e) => Select(SpotlightShapeKind.Rectangle, e);
    private void OnCircleClick(object? sender, RoutedEventArgs e) => Select(SpotlightShapeKind.Ellipse, e);
    private void Select(SpotlightShapeKind kind, RoutedEventArgs e)
    {
        e.Handled = true;
        SyncKind(kind);
        KindChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStrokeWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        AdjustStroke(e.Delta.Y);
        e.Handled = true;
    }

    public void AdjustStroke(double delta)
    {
        if (delta == 0) return;
        StrokeSlider.Value = Math.Clamp(Math.Round(StrokeSlider.Value + Math.Sign(delta)), StrokeSlider.Minimum, StrokeSlider.Maximum);
        StrokeAdjusted?.Invoke(this, EventArgs.Empty);
    }
}
