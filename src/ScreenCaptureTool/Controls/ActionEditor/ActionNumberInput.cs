using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ScreenCaptureTool.Controls.ActionEditor;

/// <summary>
/// 极简统一数值输入框：与普通文本框视觉完全一致，仅保留干净输入区与右侧淡淡单位文字，
/// 去除多余 −/+ 符号按钮，支持直接输入、方向键 ↑/↓ 与聚焦滚轮微调。
/// </summary>
internal sealed class ActionNumberInput : ContinuousBorder
{
    private readonly TextBox _input;
    private readonly int _min;
    private readonly int _max;
    private readonly int _step;
    private readonly bool _allowEmpty;
    private readonly string _emptyToken;
    private readonly int _defaultWhenSteppedFromEmpty;
    private bool _suppress;

    public event Action<string>? ValueCommitted;

    public ActionNumberInput(
        string initialValue,
        int min,
        int max,
        int step = 1,
        string? unit = null,
        string? watermark = null,
        bool allowEmpty = false,
        string emptyToken = "default",
        int defaultWhenSteppedFromEmpty = 0,
        string? tooltip = null)
    {
        _min = min;
        _max = max;
        _step = Math.Max(1, step);
        _allowEmpty = allowEmpty;
        _emptyToken = emptyToken;
        _defaultWhenSteppedFromEmpty = Math.Clamp(defaultWhenSteppedFromEmpty, min, max);

        Classes.Add("actionNumBox");
        Height = 30;
        MinHeight = 30;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(this, tooltip + "（支持直接输入或方向键/滚轮微调）");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        _input = new TextBox
        {
            Classes = { "actionNumInner" },
            Watermark = watermark ?? $"{min}–{max}",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(10, 0, 4, 0),
            MinHeight = 26,
            Height = 26,
        };
        SetValueSilently(initialValue);

        _input.GotFocus += (_, _) => Classes.Add("focused");
        _input.LostFocus += (_, _) =>
        {
            Classes.Remove("focused");
            NormalizeAndCommit();
        };
        _input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; NormalizeAndCommit(); }
            else if (e.Key == Key.Up) { e.Handled = true; Adjust(_step); }
            else if (e.Key == Key.Down) { e.Handled = true; Adjust(-_step); }
        };
        _input.TextChanged += (_, _) =>
        {
            if (_suppress) return;
            EmitIfValidLive();
        };
        PointerWheelChanged += (_, e) =>
        {
            if (!_input.IsFocused) return;
            e.Handled = true;
            Adjust(e.Delta.Y > 0 ? _step : -_step);
        };
        grid.Children.Add(_input);

        if (!string.IsNullOrEmpty(unit))
        {
            var unitText = new TextBlock
            {
                Text = unit,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 10, 0),
                Opacity = 0.72,
            };
            unitText.Classes.Add("muted");
            Grid.SetColumn(unitText, 1);
            grid.Children.Add(unitText);
        }

        Child = grid;
    }

    public void SetNumericValue(int value)
    {
        int clamped = Math.Clamp(value, _min, _max);
        string text = clamped.ToString(CultureInfo.InvariantCulture);
        SetValueSilently(text);
        ValueCommitted?.Invoke(text);
    }

    private void SetValueSilently(string raw)
    {
        _suppress = true;
        if (_allowEmpty && (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, _emptyToken, StringComparison.OrdinalIgnoreCase)))
        {
            _input.Text = "";
        }
        else if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
        {
            _input.Text = Math.Clamp(v, _min, _max).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            _input.Text = _allowEmpty ? "" : _min.ToString(CultureInfo.InvariantCulture);
        }
        _suppress = false;
    }

    private void Adjust(int delta)
    {
        string txt = (_input.Text ?? "").Trim();
        int next;
        if (string.IsNullOrEmpty(txt))
        {
            next = delta > 0 ? Math.Clamp(_defaultWhenSteppedFromEmpty + _step, _min, _max) : _min;
        }
        else if (int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int cur))
        {
            next = Math.Clamp(cur + delta, _min, _max);
        }
        else
        {
            next = _defaultWhenSteppedFromEmpty;
        }
        SetNumericValue(next);
    }

    private void EmitIfValidLive()
    {
        string txt = (_input.Text ?? "").Trim();
        if (string.IsNullOrEmpty(txt))
        {
            if (_allowEmpty) ValueCommitted?.Invoke(_emptyToken);
            return;
        }
        if (int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) && v >= _min && v <= _max)
        {
            ValueCommitted?.Invoke(v.ToString(CultureInfo.InvariantCulture));
        }
    }

    private void NormalizeAndCommit()
    {
        string txt = (_input.Text ?? "").Trim();
        if (string.IsNullOrEmpty(txt))
        {
            if (_allowEmpty)
            {
                ValueCommitted?.Invoke(_emptyToken);
                return;
            }
            SetNumericValue(_min);
            return;
        }

        if (int.TryParse(txt, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
        {
            SetNumericValue(v);
        }
        else if (_allowEmpty)
        {
            SetValueSilently(_emptyToken);
            ValueCommitted?.Invoke(_emptyToken);
        }
        else
        {
            SetNumericValue(_min);
        }
    }
}
