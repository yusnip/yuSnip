using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 贴图窗口：显示一张截图结果，支持拖动、滚轮缩放、复制和关闭。
/// </summary>
public partial class StickerWindow : Window
{
    private readonly System.Drawing.Bitmap _bitmap;
    private readonly Avalonia.Media.Imaging.Bitmap _source;
    private double _scale = 1.0;

    /// <summary>截图原始区域的物理像素宽度；贴图初次显示时按此尺寸还原。</summary>
    private int _sourcePixelWidth;
    /// <summary>截图原始区域的物理像素高度；贴图初次显示时按此尺寸还原。</summary>
    private int _sourcePixelHeight;
    /// <summary>是否已经完成一次按屏幕原始尺寸定位（Opened 之后才可靠读取 DPI）。</summary>
    private bool _initialSizeApplied;

    /// <summary>阴影显示开关（Y 键切换）。true=显示阴影，false=隐藏。默认显示。</summary>
    private bool _shadowVisible = true;
    /// <summary>当前是否处于焦点状态（焦点时阴影为蓝色，否则灰黑）。</summary>
    private bool _isFocused;
    /// <summary>正在拖动窗口期间（忽略 Deactivated 误触发的失焦）。</summary>
    private bool _isDragging;

    private readonly string? _textContent;
    private readonly TextLayoutInfo? _layoutInfo;
    private bool _isTextSelectable;

    public StickerWindow()
        : this(CreateDesignTimeBitmap(), null, null)
    {
    }

    public StickerWindow(System.Drawing.Bitmap bitmap, string? textContent = null, TextLayoutInfo? layoutInfo = null)
    {
        if (bitmap == null) throw new ArgumentNullException(nameof(bitmap));

        _bitmap = new System.Drawing.Bitmap(bitmap);
        _source = ToAvaloniaBitmap(_bitmap);
        _sourcePixelWidth = _bitmap.Width;
        _sourcePixelHeight = _bitmap.Height;
        _textContent = textContent;
        _layoutInfo = layoutInfo;

        InitializeComponent();

        if (!string.IsNullOrEmpty(_textContent) && _layoutInfo != null && _layoutInfo.HtmlLines != null && _layoutInfo.HtmlLines.Count > 0)
        {
            StickerTextBlock.IsVisible = true;
            StickerImage.IsVisible = false;

            // Set native background
            SetNativeBackground();

            // Populate TextBlock Inlines from HtmlLines
            PopulateTextBlockInlines();

            // Set font family and context menu
            bool isCode = TextStickerRenderer.IsCodeOrFormatted(_textContent);
            StickerTextBlock.FontFamily = new Avalonia.Media.FontFamily(isCode ? "Consolas" : "Microsoft YaHei UI");
            StickerTextBlock.ContextMenu = RootBorder.ContextMenu;

            if (MenuSelectableText != null) MenuSelectableText.IsVisible = true;
            if (MenuSeparator != null) MenuSeparator.IsVisible = true;

            // Tunneling handlers for text block pointer interactions
            StickerTextBlock.AddHandler(PointerPressedEvent, OnTextBlockPointerPressed, RoutingStrategies.Tunnel);
            StickerTextBlock.AddHandler(PointerWheelChangedEvent, OnTextBlockPointerWheelChanged, RoutingStrategies.Tunnel);
            StickerTextBlock.AddHandler(PointerMovedEvent, OnTextBlockPointerMoved, RoutingStrategies.Tunnel);
        }
        else
        {
            StickerTextBlock.IsVisible = false;
            StickerImage.IsVisible = true;
            StickerImage.Source = _source;
        }

        // 窗口句柄创建前 DesktopScaling 不可靠，先用 1.0 占位，Opened 事件里再精确还原尺寸。
        ApplyScale();

        Opened += OnWindowOpened;
        KeyDown += OnWindowKeyDown;
        Deactivated += OnWindowDeactivated;
        PositionChanged += OnPositionChanged;
        Closed += OnClosed;
    }

    private void SetNativeBackground()
    {
        try
        {
            if (_bitmap.Width > 8 && _bitmap.Height > 8)
            {
                System.Drawing.Color gdiColor = _bitmap.GetPixel(8, 8);
                var avaloniaColor = Avalonia.Media.Color.FromArgb(255, gdiColor.R, gdiColor.G, gdiColor.B);
                RootBorder.Background = new Avalonia.Media.SolidColorBrush(avaloniaColor);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 设置 RootBorder 背景颜色失败: " + ex.Message);
        }
    }

    private void PopulateTextBlockInlines()
    {
        var textBlock = StickerTextBlock;
        if (textBlock == null || _layoutInfo == null || _layoutInfo.HtmlLines == null) return;

        textBlock.Inlines?.Clear();
        bool isFirstLine = true;
        foreach (var line in _layoutInfo.HtmlLines)
        {
            if (!isFirstLine)
            {
                textBlock.Inlines?.Add(new LineBreak());
            }
            isFirstLine = false;

            foreach (var run in line.Runs)
            {
                var avaloniaColor = Avalonia.Media.Color.FromArgb(255, run.Color.R, run.Color.G, run.Color.B);
                var textRun = new Run
                {
                    Text = run.Text,
                    Foreground = new Avalonia.Media.SolidColorBrush(avaloniaColor),
                    FontWeight = run.IsBold ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal,
                    FontStyle = run.IsItalic ? Avalonia.Media.FontStyle.Italic : Avalonia.Media.FontStyle.Normal
                };
                textBlock.Inlines?.Add(textRun);
            }
        }
    }

    /// <summary>
    /// 设置贴图 Image 左上角在屏幕的目标位置（物理像素）。
    /// 调用方传入"希望图片出现在哪"，本方法自动把窗口左上角左移/上移阴影边距，
    /// 使 Image 精确落在目标位置（窗口本身比 Image 大一圈容纳阴影）。
    /// 在 Opened 后调用最准确（此时 DesktopScaling 可靠）。
    /// </summary>
    public void SetStickerScreenPosition(Avalonia.PixelPoint imageTopLeft)
    {
        double s = SafeScaling;
        int marginPx = (int)Math.Round(ShadowMargin * s);
        // 窗口左上角 = Image 目标位置 - 阴影边距，使居中的 Image 落在目标位置。
        Position = new Avalonia.PixelPoint(imageTopLeft.X - marginPx, imageTopLeft.Y - marginPx);
    }

    /// <summary>窗口是否仍有效（未关闭）。供 StickerFocusManager 判断弱引用是否存活。</summary>
    internal static bool IsAlive(StickerWindow? window) => window != null;

    /// <summary>由 StickerFocusManager 调用：本贴图获得焦点，阴影变蓝（若阴影开关开着）。</summary>
    internal void NotifyFocused()
    {
        _isFocused = true;
        ApplyShadow();
    }

    /// <summary>由 StickerFocusManager 调用：本贴图失去焦点，阴影变灰黑（若阴影开关开着）。</summary>
    internal void NotifyUnfocused()
    {
        _isFocused = false;
        ApplyShadow();
    }

    /// <summary>
    /// 根据（阴影开关, 焦点态）重算 RootBorder.BoxShadow。
    /// 阴影开关关 → 无阴影；开关开 + 焦点 → 蓝；开关开 + 非焦点 → 灰黑。
    /// </summary>
    private void ApplyShadow()
    {
        if (ShadowBorder == null) return;
        if (!_shadowVisible)
        {
            ShadowBorder.BoxShadow = default(Avalonia.Media.BoxShadows);
            return;
        }
        ShadowBorder.BoxShadow = _isFocused ? StickerStyling.FocusedShadow : StickerStyling.UnfocusedShadow;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        // 打开后窗口句柄已创建，DesktopScaling 可靠；按截图原始物理像素尺寸还原贴图，
        // 使其与屏幕上原截图区域完全重合（贴在原位置、保持同样大小）。
        if (!_initialSizeApplied)
        {
            _initialSizeApplied = true;
            ApplyScale();
        }
        ApplyShadow();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        // 拖动期间系统可能短暂 Deactivate 本窗口，此时不应失焦。
        if (_isDragging) return;
        StickerFocusManager.ClearFocus(this);
    }

    private void OnPositionChanged(object? sender, EventArgs e)
    {
        // PositionChanged 在拖动结束时也会触发；这里用作拖动结束的兜底信号。
        if (_isDragging) _isDragging = false;
    }

    /// <summary>当前 DPI 缩放的容错版本（DesktopScaling 在窗口句柄未创建时可能为 0）。</summary>
    private double SafeScaling => DesktopScaling > 0 ? DesktopScaling : 1.0;

    /// <summary>RootGrid 的外边距（各方向像素），为阴影预留渲染空间。</summary>
    private const double ShadowMargin = 16;

    private void ApplyScale()
    {
        // 截图位图是物理像素，窗口 Width/Height 是 DIP（设备无关像素）。
        // 物理像素 ÷ DPI 缩放 = DIP，这样贴图在屏幕上的物理尺寸与原截图区域一致。
        double s = SafeScaling;
        double width = Math.Max(20.0, _sourcePixelWidth * _scale / s);
        double height = Math.Max(20.0, _sourcePixelHeight * _scale / s);

        StickerImage.Width = width;
        StickerImage.Height = height;

        if (StickerTextBlock != null)
        {
            StickerTextBlock.FontSize = 18 * _scale / s;
            StickerTextBlock.Padding = new Avalonia.Thickness(16 * _scale / s);
        }

        if (StickerTextBlock != null && StickerTextBlock.IsVisible)
        {
            Width = double.NaN;
            Height = double.NaN;
        }
        else
        {
            // 窗口尺寸 = Image 尺寸 + 两侧阴影边距，给阴影留出渲染空间。
            Width = width + ShadowMargin * 2;
            Height = height + ShadowMargin * 2;
        }
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            e.Handled = true;
            Close();
            return;
        }

        // 单击：先获得焦点（阴影变蓝），再启动拖动。
        StickerFocusManager.SetFocus(this);

        e.Handled = true;
        _isDragging = true; // 拖动期间忽略 Deactivated 误触发
        try { BeginMoveDrag(e); } catch { _isDragging = false; }
        _isDragging = false;
    }

    private void OnTextBlockPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(StickerTextBlock);
        bool isBlankSpace = IsPointInBlankSpace(point.Position.X, point.Position.Y);

        if (isBlankSpace)
        {
            if (point.Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount >= 2)
                {
                    e.Handled = true;
                    Close();
                    return;
                }

                StickerFocusManager.SetFocus(this);
                e.Handled = true;
                _isDragging = true;
                try { BeginMoveDrag(e); } catch { _isDragging = false; }
                _isDragging = false;
            }
        }
        else
        {
            // Clicked on text: set focus to the window, but don't mark Handled so TextBlock can handle selection
            if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed)
            {
                StickerFocusManager.SetFocus(this);
            }
        }
    }

    private bool IsPointInBlankSpace(double x, double y)
    {
        if (_layoutInfo == null || _layoutInfo.LineWidths == null || _layoutInfo.LineWidths.Count == 0)
        {
            return true;
        }

        double s = SafeScaling;
        double physicalX = x * s / _scale;
        double physicalY = y * s / _scale;

        const double padding = 16.0;

        if (physicalY < padding) return true;

        float lineHeight = _layoutInfo.LineHeight;
        if (lineHeight <= 0) return true;

        int lineIndex = (int)((physicalY - padding) / lineHeight);
        if (lineIndex < 0 || lineIndex >= _layoutInfo.LineWidths.Count)
        {
            return true;
        }

        float lineWidth = _layoutInfo.LineWidths[lineIndex];
        if (physicalX < padding || physicalX > (padding + lineWidth))
        {
            return true;
        }

        return false;
    }

    private void OnRootPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0) _scale *= 1.1;
        else if (e.Delta.Y < 0) _scale /= 1.1;

        _scale = Math.Clamp(_scale, 0.2, 5.0);
        ApplyScale();
        e.Handled = true;
    }

    private void OnTextBlockPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        OnRootPointerWheelChanged(sender, e);
    }

    private void OnTextBlockPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isTextSelectable) return;

        PointerPoint point = e.GetCurrentPoint(StickerTextBlock);
        bool isBlankSpace = IsPointInBlankSpace(point.Position.X, point.Position.Y);
        StickerTextBlock.Cursor = isBlankSpace 
            ? new Cursor(StandardCursorType.Arrow) 
            : new Cursor(StandardCursorType.Ibeam);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            // Esc：焦点贴图失焦（符合需求）；已非焦点则关闭（保留原便捷关闭）。
            if (_isFocused) StickerFocusManager.ClearFocus(this);
            else Close();
            return;
        }

        // Y：仅焦点贴图有效，切换阴影显示开关。
        if (e.Key == Key.Y && _isFocused)
        {
            e.Handled = true;
            _shadowVisible = !_shadowVisible;
            ApplyShadow();
            App.RaiseStickerShadowToast(_shadowVisible, this);
        }
    }

    private void OnCopyImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardService.SetImage(_bitmap);
            AppLogger.Info("StickerWindow: 贴图已复制到剪贴板");
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow: 复制贴图失败", ex);
        }
    }

    private async void OnSaveImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为贴图",
                SuggestedFileName = "Sticker_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG Image") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG Image") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("Bitmap Image") { Patterns = new[] { "*.bmp" } },
                },
            });

            string? path = file?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                ImageExportService.Save(_bitmap, path);
                AppLogger.Info("StickerWindow: 贴图已另存为 " + path);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow: 另存贴图失败", ex);
        }
    }

    private void OnSelectableTextClick(object? sender, RoutedEventArgs e)
    {
        _isTextSelectable = !_isTextSelectable;

        if (MenuSelectableText != null)
        {
            MenuSelectableText.Header = _isTextSelectable ? "✓ 文本可选择" : "文本可选择";
        }

        if (StickerTextBlock != null)
        {
            StickerTextBlock.IsHitTestVisible = _isTextSelectable;
            if (!_isTextSelectable)
            {
                StickerTextBlock.ClearSelection();
            }
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 关闭时若是焦点贴图，清除焦点管理器引用。
        StickerFocusManager.ClearFocus(this);
        try { _source.Dispose(); } catch { }
        try { _bitmap.Dispose(); } catch { }
    }

    private static System.Drawing.Bitmap CreateDesignTimeBitmap()
    {
        var bitmap = new System.Drawing.Bitmap(1, 1, PixelFormat.Format32bppArgb);
        bitmap.SetPixel(0, 0, Color.Transparent);
        return bitmap;
    }

    private static Avalonia.Media.Imaging.Bitmap ToAvaloniaBitmap(System.Drawing.Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        return new Avalonia.Media.Imaging.Bitmap(ms);
    }
}
