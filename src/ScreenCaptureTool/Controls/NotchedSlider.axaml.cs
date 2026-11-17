using System;
using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;

namespace ScreenCaptureTool.Controls;

/// <summary>
/// 凹槽式分段滑块（移植自 Eidos NotchedSlider，对标 PSexp 图片质量滑块风格）：
/// 达到阈值的格子以品牌色填充；支持点击跳格、拖拽连续调节、数字输入精确设置。
/// 颜色全部走主题 Token（未填 SctControlOuterBrush / 已填 SctSegmentedCheckedBgBrush），深浅模式自动适配。
/// </summary>
public partial class NotchedSlider : UserControl
{
    public NotchedSlider()
    {
        InitializeComponent();
        Loaded += (_, _) => RebuildCells();
        SyncInputText();
        // 只允许输入数字（非数字字符直接吞掉）
        InputBox.AddHandler(TextInputEvent, (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Text) || !e.Text.All(char.IsDigit))
                e.Handled = true;
        });
    }

    // ==================== 样式属性 ====================

    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NotchedSlider, double>(nameof(Value), 0d, coerce: CoerceValue);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<NotchedSlider, double>(nameof(Minimum), 0d);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<NotchedSlider, double>(nameof(Maximum), 100d);

    public static readonly StyledProperty<int> CellCountProperty =
        AvaloniaProperty.Register<NotchedSlider, int>(nameof(CellCount), 10);

    public static readonly StyledProperty<string> SuffixProperty =
        AvaloniaProperty.Register<NotchedSlider, string>(nameof(Suffix), string.Empty);

    public static readonly StyledProperty<bool> ShowInputProperty =
        AvaloniaProperty.Register<NotchedSlider, bool>(nameof(ShowInput), true);

    /// <summary>当前值（自动夹取到 [Minimum, Maximum]，并对齐到格子阈值）</summary>
    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>范围下限</summary>
    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    /// <summary>范围上限</summary>
    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>凹槽格数（每格 = (Maximum - Minimum) / CellCount）</summary>
    public int CellCount
    {
        get => GetValue(CellCountProperty);
        set => SetValue(CellCountProperty, value);
    }

    /// <summary>数字输入框后缀（如 "ms" / "px"）</summary>
    public string Suffix
    {
        get => GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    /// <summary>是否显示右侧内嵌数字输入框（大数值排版场景由外部自绘数值，置 False 仅保留格子条）</summary>
    public bool ShowInput
    {
        get => GetValue(ShowInputProperty);
        set => SetValue(ShowInputProperty, value);
    }

    /// <summary>值变化通知（仅值实际变化时触发）。</summary>
    public event EventHandler<double>? ValueChanged;

    private static double CoerceValue(AvaloniaObject o, double v)
    {
        var c = (NotchedSlider)o;
        return Math.Clamp(v, c.Minimum, c.Maximum);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty)
        {
            UpdateCells();
            SyncInputText();
            if (!double.Equals(change.OldValue, change.NewValue))
                ValueChanged?.Invoke(this, (double)change.NewValue!);
        }
        else if (change.Property == MinimumProperty || change.Property == MaximumProperty ||
                 change.Property == CellCountProperty)
        {
            Value = Math.Clamp(Value, Minimum, Maximum);
            RebuildCells();
        }
        else if (change.Property == SuffixProperty)
        {
            SuffixText.Text = Suffix;
        }
        else if (change.Property == ShowInputProperty)
        {
            InputPanel.IsVisible = ShowInput;
        }
    }

    // ==================== 格子构建与刷新 ====================

    // 固定格子尺寸（对标 PSexp 图片质量滑块）：高 20px、宽 4px，宽高比 1:5；
    // 格缝由 Star 列自适应拉伸，轨道通栏两端对齐容器（左对齐标题、右对齐参数值）
    private const double CellHeight = 20;
    private const double CellWidth = 4;

    private void RebuildCells()
    {
        CellsHost.ColumnDefinitions.Clear();
        CellsHost.Children.Clear();

        int count = Math.Max(1, CellCount);
        for (int i = 0; i < count; i++)
            CellsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < count; i++)
        {
            var cell = new ContinuousBorder
            {
                Width = CellWidth,
                Height = CellHeight,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(1),
                Classes = { "notchCell" },
            };
            Grid.SetColumn(cell, i);
            CellsHost.Children.Add(cell);
        }

        CellsHost.Height = CellHeight;
        ClipToBounds = false;
        UpdateCells();
    }

    private void UpdateCells()
    {
        int count = CellsHost.Children.Count;
        if (count == 0) return;

        double span = Maximum - Minimum;
        for (int i = 0; i < count; i++)
        {
            double threshold = GetCellThreshold(i, count);
            bool filled = Value >= threshold - 0.0001;
            var cell = (ContinuousBorder)CellsHost.Children[i];
            if (filled && !cell.Classes.Contains("filled")) cell.Classes.Add("filled");
            else if (!filled && cell.Classes.Contains("filled")) cell.Classes.Remove("filled");
        }
    }

    private double GetCellThreshold(int index, int count)
        => Minimum + (index + 1) * (Maximum - Minimum) / Math.Max(1, count);

    private double Step => (Maximum - Minimum) / Math.Max(1, CellCount);

    private string GetDisplayText() => Math.Round(Value).ToString(CultureInfo.InvariantCulture);

    // ==================== 点击 / 拖拽（按下即跳格，移动即连续吸附到格子阈值） ====================

    private bool _pressed;

    private void CellsHost_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = true;
        e.Pointer.Capture(CellsHost);
        UpdateValueFromPointer(e.GetPosition(CellsHost));
        e.Handled = true;
    }

    private void CellsHost_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed) return;
        UpdateValueFromPointer(e.GetPosition(CellsHost));
        e.Handled = true;
    }

    private void CellsHost_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (CellsHost.IsPointerOver)
            UpdateValueFromPointer(e.GetPosition(CellsHost));
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void CellsHost_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _pressed = false;
    }

    private void UpdateValueFromPointer(Point p)
    {
        double w = CellsHost.Bounds.Width;
        if (w <= 0) return;
        double ratio = Math.Clamp(p.X / w, 0.0, 1.0);
        // 吸附到最近的格子阈值（一格 = Step），拖拽一格一格跳
        Value = Math.Clamp(Minimum + Math.Round(ratio * (Maximum - Minimum) / Step) * Step, Minimum, Maximum);
    }

    // ==================== 数字输入 ====================

    private void SyncInputText()
    {
        if (InputBox.IsFocused) return; // 用户正在编辑时不打扰
        InputBox.Text = GetDisplayText();
    }

    private void InputBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitInput();
            // 回车提交后清除焦点（触发 LostFocus 链路收尾）
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private void InputBox_LostFocus(object? sender, RoutedEventArgs e) => CommitInput();

    private void CommitInput()
    {
        double v = double.TryParse(InputBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out double val)
            ? val : Value;
        Value = Math.Clamp(Math.Round(v), Minimum, Maximum);
        InputBox.Text = GetDisplayText(); // 夹取后回显
    }
}
