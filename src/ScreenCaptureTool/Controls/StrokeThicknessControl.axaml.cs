using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace ScreenCaptureTool.Controls;

/// <summary>截图和贴图共用的标注粗细步进器。</summary>
public partial class StrokeThicknessControl : UserControl
{
    public Slider Slider => SizeSlider;

    public StrokeThicknessControl()
    {
        InitializeComponent();
        SizeSlider.ValueChanged += (_, _) => UpdateValue();
        UpdateValue();
    }

    private void OnDecreaseClick(object? sender, RoutedEventArgs e) => Adjust(-1);
    private void OnIncreaseClick(object? sender, RoutedEventArgs e) => Adjust(1);

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
    }

    private void UpdateValue()
    {
        double value = SizeSlider.Value;
        SizeText.Text = Math.Round(value).ToString();
        DecreaseButton.IsEnabled = value > SizeSlider.Minimum;
        IncreaseButton.IsEnabled = value < SizeSlider.Maximum;
    }
}
