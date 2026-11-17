using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ScreenCaptureTool.Controls;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Services;

/// <summary>
/// <see cref="TextAnnotationController"/> 的参数面板半边：字号·粗斜·描边·底色（圆角/内距/透明度）
/// 两枚 Popup 与一枚字号 <c>MenuFlyout</c> 的控件抓取、事件绑定与回写。与图层状态机同属一个类是因为两侧共享同一批样式字段
/// （<c>_currentTextSize</c> / <c>_hasStroke</c> / <c>_textFillOpacity</c> …），拆成两个类只会
/// 凭空造一层公开接口，违背「逐字搬、行为逐位不变」。
///
/// 面板控件一律经 <c>_host.FindTextPanelControl&lt;T&gt;(name)</c> 按名取——截图侧在窗口命名域，
/// 贴图侧在标注浮窗的复刻面板里，**两侧控件名必须一致**（同步义务见 docs/设计规范.md §7.5）。
/// </summary>
public sealed partial class TextAnnotationController
{
    /// <summary>初始化文本工具所有UI控件引用并绑定事件</summary>
    public void InitTextToolControls()
    {
        _textToolsPanel = _host.FindTextPanelControl<StackPanel>("TextToolsPanel");
        _textSizeHost = _host.FindTextPanelControl<Grid>("TextSizeHost");
        _btnTextBold = _host.FindTextPanelControl<Button>("BtnTextBold");
        _btnTextItalic = _host.FindTextPanelControl<Button>("BtnTextItalic");
        _btnTextStroke = _host.FindTextPanelControl<Button>("BtnTextStroke");
        _btnTextBackground = _host.FindTextPanelControl<Button>("BtnTextBackground");
        _btnTargetStrokeColor = _host.FindTextPanelControl<Button>("BtnTargetStrokeColor");
        _btnTargetStrokeText = _host.FindTextPanelControl<Button>("BtnTargetStrokeText");
        _btnTargetBackgroundColor = _host.FindTextPanelControl<Button>("BtnTargetBackgroundColor");
        _btnTargetBackgroundText = _host.FindTextPanelControl<Button>("BtnTargetBackgroundText");
        _textBackgroundPopup = _host.FindTextPanelControl<Popup>("TextBackgroundPopup");
        _textStrokePopup = _host.FindTextPanelControl<Popup>("TextStrokePopup");
        _textSizeValueText = _host.FindTextPanelControl<TextBlock>("TextSizeValueText");
        _textStrokeSizeValue = _host.FindTextPanelControl<TextBlock>("TextStrokeSizeValue");
        _textFillOpacityValue = _host.FindTextPanelControl<TextBlock>("TextFillOpacityValue");
        _textFillCornerValue = _host.FindTextPanelControl<TextBlock>("TextFillCornerValue");
        _textFillPaddingValue = _host.FindTextPanelControl<TextBlock>("TextFillPaddingValue");
        _chkStrokeEnable = _host.FindTextPanelControl<CheckBox>("ChkStrokeEnable");
        _chkBackgroundEnable = _host.FindTextPanelControl<CheckBox>("ChkBackgroundEnable");
        _sliderStrokeSize = _host.FindTextPanelControl<Slider>("SliderStrokeSize");
        _sliderFillOpacity = _host.FindTextPanelControl<Slider>("SliderFillOpacity");
        _sliderFillCorner = _host.FindTextPanelControl<Slider>("SliderFillCorner");
        _sliderFillPadding = _host.FindTextPanelControl<Slider>("SliderFillPadding");

        // 字号区域滚轮/点击事件（菜单改为首次点击时懒建，消除建窗期间 10 档的 eager 构造耗时）
        if (_textSizeHost != null)
        {
            _textSizeHost.PointerWheelChanged += OnTextSizeHostWheel;
            _textSizeHost.PointerPressed += OnTextSizeHostPressed;
        }

        // 描边 Popup 事件
        if (_chkStrokeEnable != null)
            _chkStrokeEnable.IsCheckedChanged += (_, _) => ToggleStrokeEnable();
        if (_sliderStrokeSize != null)
            _sliderStrokeSize.ValueChanged += (_, _) => OnStrokeSizeSliderChanged();

        // 填充 Popup 事件
        if (_chkBackgroundEnable != null)
            _chkBackgroundEnable.IsCheckedChanged += (_, _) => ToggleBackgroundEnable();
        if (_sliderFillOpacity != null)
            _sliderFillOpacity.ValueChanged += (_, _) => OnFillOpacitySliderChanged();
        if (_sliderFillCorner != null)
            _sliderFillCorner.ValueChanged += (_, _) => OnFillCornerSliderChanged();
        if (_sliderFillPadding != null)
            _sliderFillPadding.ValueChanged += (_, _) => OnFillPaddingSliderChanged();
    }

    /// <summary>字号下拉的固定候选档。滚轮的智能步进（2/4/8）会踩出表外的值，
    /// 由 <see cref="RebuildTextSizeItems"/> 每次打开时就地并入，故这里不必预留密集档位。
    /// 档数刻意压在 10 行——菜单不套滚动，超出这个数就要在短屏上重演"列表被裁"。</summary>
    private static readonly int[] TextSizePresets =
        { 8, 12, 16, 20, 24, 28, 32, 48, 64, 96 };

    /// <summary>字号下拉：走设计规范 §6.12 的菜单唯一配方（<see cref="MenuFlyout"/>），
    /// 不再自造外壳——行高、悬浮底色、圆角、明暗三态一律由全局 <c>MenuItem</c> 样式给。
    /// 宽度与对齐走全局两条变体（<c>compact</c> 窄条 + <c>centered</c> 居中，见 <see cref="MenuFlyoutPresenterStyle"/>）。</summary>
    internal void EnsureTextSizeMenu()
    {
        if (_textSizeMenu != null || _textSizeHost == null) return;
        _textSizeMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
    }

    /// <summary>
    /// 每次打开前重铺条目（10～11 行，代价可忽略，换来的是"列表永远等于当前状态"，
    /// 不必维护一份与 <c>_currentTextSize</c> 同步的高亮标记）：
    /// ① 滚轮值不在候选表里时并入并排序，否则读到的数字在列表里找不到自己那一行；
    /// ② 当前档那一行挂 <c>current</c> 类，由全局样式染成品牌紫（§6.12：颜色是唯一的状态表达，
    /// 不用 ✓ 也不用自造底色）；
    /// ③ 铺完把首条目交给 <see cref="MenuFlyoutPresenterStyle"/>，让菜单按窄条档出宽并把档名居中。
    /// </summary>
    private void RebuildTextSizeItems()
    {
        if (_textSizeMenu is not { } menu) return;
        menu.Items.Clear();

        int current = (int)Math.Round(_currentTextSize);
        List<int> sizes = TextSizePresets.ToList();
        if (!sizes.Contains(current))
        {
            sizes.Add(current);
            sizes.Sort();
        }

        foreach (int size in sizes)
        {
            int pickedSize = size;
            var item = new MenuItem { Header = size + " px" };
            item.Classes.Set("current", size == current);
            item.Click += (_, _) => SetCurrentTextSize(pickedSize);
            menu.Items.Add(item);
        }

        if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first)
            MenuFlyoutPresenterStyle.Attach(first, MenuFlyoutPresenterStyle.Compact, MenuFlyoutPresenterStyle.Centered);
    }

    internal void OnTextSizeHostPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (_textSizeHost == null) return;
        EnsureTextSizeMenu();
        if (_textSizeMenu == null) return;
        if (_textStrokePopup != null) _textStrokePopup.IsOpen = false;
        if (_textBackgroundPopup != null) _textBackgroundPopup.IsOpen = false;
        // 宿主是 Grid 不是 Button，弹出/收起得自己翻；Hide() 只认已弹出的 flyout
        // （对从未弹出过的 flyout 调 Hide 不保证是空操作，故一律先看 IsOpen）。
        if (_textSizeMenu.IsOpen) _textSizeMenu.Hide();
        else
        {
            RebuildTextSizeItems();
            _textSizeMenu.ShowAt(_textSizeHost);
        }
    }

    internal void CloseTextPopups()
    {
        if (_textSizeMenu is { IsOpen: true }) _textSizeMenu.Hide();
        if (_textStrokePopup != null) _textStrokePopup.IsOpen = false;
        if (_textBackgroundPopup != null) _textBackgroundPopup.IsOpen = false;
    }

    /// <summary>更新文本工具面板 UI 状态</summary>
    internal void UpdateTextPanelUi()
    {
        try
        {
            if (_textSizeValueText != null)
                _textSizeValueText.Text = _currentTextSize.ToString("0");
        }
        catch { }

        try
        {
            // 按钮 active 状态（对齐旧版 IsChecked → 样式切换）
            SetClassActive(_btnTextBold, _isTextBold);
            SetClassActive(_btnTextItalic, _isTextItalic);
            SetClassActive(_btnTextStroke, _hasStroke);
            SetClassActive(_btnTextBackground, _hasBackground);
            SetClassActive(_btnTargetStrokeColor, _colorTargetMode == 2);
            SetClassActive(_btnTargetStrokeText, _colorTargetMode == 0);
            SetClassActive(_btnTargetBackgroundColor, _colorTargetMode == 1);
            SetClassActive(_btnTargetBackgroundText, _colorTargetMode == 0);

            // 描边 Popup 状态
            if (_sliderStrokeSize != null && _sliderStrokeSize.Value != _textStrokeThickness)
                _sliderStrokeSize.Value = _textStrokeThickness;
            if (_textStrokeSizeValue != null)
                _textStrokeSizeValue.Text = _textStrokeThickness.ToString("0");
            if (_chkStrokeEnable != null && _chkStrokeEnable.IsChecked != _hasStroke)
                _chkStrokeEnable.IsChecked = _hasStroke;

            // 填充 Popup 状态
            if (_sliderFillOpacity != null && Math.Abs(_sliderFillOpacity.Value - _textFillOpacity) > 0.1)
                _sliderFillOpacity.Value = _textFillOpacity;
            if (_textFillOpacityValue != null)
                _textFillOpacityValue.Text = ((int)_textFillOpacity).ToString();

            if (_sliderFillCorner != null && Math.Abs(_sliderFillCorner.Value - _textFillCornerRadius) > 0.1)
                _sliderFillCorner.Value = _textFillCornerRadius;
            if (_textFillCornerValue != null)
                _textFillCornerValue.Text = ((int)_textFillCornerRadius).ToString();

            if (_sliderFillPadding != null && Math.Abs(_sliderFillPadding.Value - _textFillPadding) > 0.1)
                _sliderFillPadding.Value = _textFillPadding;
            if (_textFillPaddingValue != null)
                _textFillPaddingValue.Text = ((int)_textFillPadding).ToString();
        }
        catch { }
    }

    internal static void SetClassActive(Button? btn, bool active)
    {
        if (btn == null) return;
        if (active && !btn.Classes.Contains("active"))
            btn.Classes.Add("active");
        else if (!active && btn.Classes.Contains("active"))
            btn.Classes.Remove("active");
    }

    internal void OnTextBoldClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestTextSnapshot();
        _isTextBold = !_isTextBold;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    internal void OnTextItalicClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        RequestTextSnapshot();
        _isTextItalic = !_isTextItalic;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    internal void OnTextStrokeClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_textStrokePopup != null)
        {
            if (_textStrokePopup.IsOpen)
            {
                _textStrokePopup.IsOpen = false;
            }
            else
            {
                // 互斥：关闭填充 Popup
                if (_textBackgroundPopup != null && _textBackgroundPopup.IsOpen)
                    _textBackgroundPopup.IsOpen = false;
                _textStrokePopup.IsOpen = true;
                SetColorTarget(2); // 描边色
                if (_chkStrokeEnable != null)
                    _chkStrokeEnable.IsChecked = _hasStroke;
            }
        }
        UpdateTextPanelUi();
    }

    internal void OnTextBackgroundClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        // 与旧版工具按钮体验对齐：点击“填充背景”按钮即启用填充；弹窗只用于细调颜色/透明度/圆角/内边距。
        if (!_hasBackground)
        {
            RequestTextSnapshot();
            _hasBackground = true;
            if (_chkBackgroundEnable != null) _chkBackgroundEnable.IsChecked = true;
            ApplyTextSettingsToSelected();
            AppLogger.Info("CaptureWindow.TextBackground: 点击按钮启用填充，selectedText=" + (_selectedText != null));
        }

        if (_textBackgroundPopup != null)
        {
            if (_textBackgroundPopup.IsOpen)
            {
                _textBackgroundPopup.IsOpen = false;
            }
            else
            {
                // 互斥：关闭描边 Popup
                if (_textStrokePopup != null && _textStrokePopup.IsOpen)
                    _textStrokePopup.IsOpen = false;
                _textBackgroundPopup.IsOpen = true;
                SetColorTarget(1); // 填充色
                if (_chkBackgroundEnable != null)
                    _chkBackgroundEnable.IsChecked = _hasBackground;
            }
        }
        UpdateTextPanelUi();
    }

    internal void OnColorTargetButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tagStr && int.TryParse(tagStr, out int mode))
        {
            SetColorTarget(mode);
        }
    }

    internal void SetColorTarget(int mode)
    {
        _colorTargetMode = mode;
        UpdateTextPanelUi();
        _host.NotifyTextPanelNeedsRefresh();
    }

    internal void ToggleStrokeEnable()
    {
        RequestTextSnapshot();
        if (_chkStrokeEnable != null)
            _hasStroke = _chkStrokeEnable.IsChecked == true;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    internal void ToggleBackgroundEnable()
    {
        RequestTextSnapshot();
        if (_chkBackgroundEnable != null)
            _hasBackground = _chkBackgroundEnable.IsChecked == true;
        AppLogger.Info("CaptureWindow.TextBackground: 切换填充=" + _hasBackground + ", selectedText=" + (_selectedText != null));
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    internal void OnStrokeSizeSliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderStrokeSize != null)
        {
            _textStrokeThickness = _sliderStrokeSize.Value;
            if (_textStrokeSizeValue != null)
                _textStrokeSizeValue.Text = _textStrokeThickness.ToString("0");
        }
        ApplyTextSettingsToSelected();
    }

    internal void OnFillOpacitySliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderFillOpacity != null)
        {
            _textFillOpacity = _sliderFillOpacity.Value;
            if (_textFillOpacityValue != null)
                _textFillOpacityValue.Text = ((int)_textFillOpacity).ToString();
        }
        ApplyTextSettingsToSelected();
    }

    internal void OnFillCornerSliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderFillCorner != null)
        {
            _textFillCornerRadius = _sliderFillCorner.Value;
            if (_textFillCornerValue != null)
                _textFillCornerValue.Text = ((int)_textFillCornerRadius).ToString();
        }
        ApplyTextSettingsToSelected();
    }

    internal void OnFillPaddingSliderChanged()
    {
        RequestTextSnapshot();
        if (_sliderFillPadding != null)
        {
            _textFillPadding = _sliderFillPadding.Value;
            if (_textFillPaddingValue != null)
                _textFillPaddingValue.Text = ((int)_textFillPadding).ToString();
        }
        ApplyTextSettingsToSelected();
    }

    internal void OnTextSizeHostWheel(object? sender, PointerWheelEventArgs e)
    {
        // 智能步进：<24=2, 24-48=4, >48=8（对齐旧版）
        double step = 2;
        if (_currentTextSize >= 48) step = 8;
        else if (_currentTextSize >= 24) step = 4;

        double newSize = _currentTextSize + (e.Delta.Y > 0 ? step : -step);
        SetCurrentTextSize(newSize);
        e.Handled = true;
    }

    internal void SetCurrentTextSize(double size)
    {
        RequestTextSnapshot();
        _currentTextSize = size;
        if (_currentTextSize < 8) _currentTextSize = 8;
        if (_currentTextSize > 200) _currentTextSize = 200;
        UpdateTextPanelUi();
        ApplyTextSettingsToSelected();
    }

    internal void ApplyTextSettingsToSelected()
    {
        if (_selectedText != null)
        {
            _selectedText.FontSize = _currentTextSize;
            _selectedText.IsBold = _isTextBold;
            _selectedText.IsItalic = _isTextItalic;
            _selectedText.HasStroke = _hasStroke;
            _selectedText.HasBackground = _hasBackground;
            _selectedText.FontFamily = _currentTextFont;
            _selectedText.StrokeThickness = _textStrokeThickness;
            _selectedText.FillOpacity = _textFillOpacity;
            _selectedText.FillCornerRadius = _textFillCornerRadius;
            _selectedText.FillPadding = _textFillPadding;

            if (_colorTargetMode == 0) _selectedText.Color = _textForegroundColor;
            else if (_colorTargetMode == 1) _selectedText.FillColor = _textFillColor;
            else if (_colorTargetMode == 2) _selectedText.StrokeColor = _textStrokeColor;

            UpdateTextView(_selectedText);
        }
    }
}
