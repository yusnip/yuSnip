using System;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;

namespace ScreenCaptureTool.Controls;

/// <summary>共用粗细参数的取值与滚轮步进，宿主负责将颜色和工具状态反馈到预览。</summary>
public partial class StrokeThicknessControl : UserControl
{
    public Slider Slider => SizeSlider;
    public TextBlock ValueText => SizeText;
    public Ellipse Preview => SizePreview;
    public event EventHandler? WheelAdjusted;

    public StrokeThicknessControl()
    {
        InitializeComponent();
        SizeSlider.ValueChanged += (_, _) => SizeText.Text = Math.Round(SizeSlider.Value).ToString();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        Adjust(e.Delta.Y);
        e.Handled = true;
    }

    public void Adjust(double delta)
    {
        if (delta == 0) return;
        SizeSlider.Value = Math.Clamp(Math.Round(SizeSlider.Value + Math.Sign(delta)), SizeSlider.Minimum, SizeSlider.Maximum);
        WheelAdjusted?.Invoke(this, EventArgs.Empty);
    }
}
