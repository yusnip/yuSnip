using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ScreenCaptureTool.Core.Capture.Annotations;

namespace ScreenCaptureTool.Controls;

/// <summary>截图与贴图的模糊/马赛克强度共用控件，物理强度换算仍由 BlurIntensityMapper 负责。</summary>
public partial class BlurIntensityControl : UserControl
{
    public Slider Slider => IntensitySlider;
    public TextBlock ValueText => IntensityText;
    public BlurMode Mode { get; private set; } = BlurMode.Mosaic;
    public event EventHandler? ModeChanged;

    public void SyncMode(BlurMode mode)
    {
        Mode = mode;
        BlurButton.Classes.Set("active", mode == BlurMode.Blur);
        MosaicButton.Classes.Set("active", mode == BlurMode.Mosaic);
    }

    private void OnBlurClick(object? sender, RoutedEventArgs e) => Select(BlurMode.Blur, e);
    private void OnMosaicClick(object? sender, RoutedEventArgs e) => Select(BlurMode.Mosaic, e);
    private void Select(BlurMode mode, RoutedEventArgs e)
    {
        e.Handled = true;
        SyncMode(mode);
        ModeChanged?.Invoke(this, EventArgs.Empty);
    }

    public BlurIntensityControl()
    {
        InitializeComponent();
        SyncMode(Mode);
        IntensitySlider.ValueChanged += (_, _) =>
            IntensityText.Text = Math.Round(IntensitySlider.Value).ToString();
    }
}
