using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;

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
        Loaded += (_, _) => UpdateSelectors();
        SetColor(Color.FromArgb(255, 51, 136, 255));
    }

    public Color SelectedColor { get; private set; }

    public static async System.Threading.Tasks.Task<Color?> PickAsync(Window owner, Color initial)
    {
        var window = new ColorPickerWindow();
        window.SetColor(initial);
        return await window.ShowDialog<Color?>(owner);
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
