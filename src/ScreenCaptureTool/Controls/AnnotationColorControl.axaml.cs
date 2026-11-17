using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace ScreenCaptureTool.Controls;

/// <summary>标注内容颜色的共享色板；这些颜色属于输出图像内容，不随界面主题替换。</summary>
public partial class AnnotationColorControl : UserControl
{
    public Border Preview => ColorPreview;
    public event Action<Color>? ColorSelected;
    public event Action<Control>? CustomColorRequested;

    public AnnotationColorControl()
    {
        InitializeComponent();
        foreach (string hex in new[] { "#FFFF5555", "#FFFFCC00", "#FF77CC44", "#FF0099FF", "#FFFFFFFF", "#FF999999", "#FF000000" })
        {
            var color = Color.Parse(hex);
            var sample = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(color) };
            if (color == Colors.White)
            {
                sample.BorderBrush = new SolidColorBrush(Color.Parse("#FFD0D0D0"));
                sample.BorderThickness = new Thickness(1);
            }
            var button = new Button { Content = sample, Tag = hex };
            button.Classes.Add("swatch");
            button.Click += (_, _) => ColorSelected?.Invoke(color);
            Swatches.Items.Add(button);
        }
    }

    private void OnCustomClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CustomColorRequested?.Invoke(CustomButton);
    }
}
