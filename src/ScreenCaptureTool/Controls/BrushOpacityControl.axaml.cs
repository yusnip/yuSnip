using System;
using Avalonia.Controls;

namespace ScreenCaptureTool.Controls;

/// <summary>复用截图画笔的不透明度参数；所有宿主共享取值范围、步进与数值显示。</summary>
public partial class BrushOpacityControl : UserControl
{
    public Slider Slider => OpacitySlider;
    public TextBlock ValueText => OpacityText;

    public BrushOpacityControl()
    {
        InitializeComponent();
        OpacitySlider.ValueChanged += (_, _) =>
            OpacityText.Text = Math.Round(OpacitySlider.Value).ToString();
    }
}
