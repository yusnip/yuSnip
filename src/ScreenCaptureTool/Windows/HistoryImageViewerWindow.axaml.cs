using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Services;
using ScreenCaptureTool.Utils;

namespace ScreenCaptureTool.Windows;

public partial class HistoryImageViewerWindow : Window
{
    private static HistoryImageViewerWindow? _instance;
    // 静态 OCR 识别框内存缓存：打开过的或历史预取的图片 0 延迟秒开高亮
    private static readonly ConcurrentDictionary<string, List<OcrLineRegion>> _boxCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<HistoryCardItem> _items = new();
    private int _currentIndex = -1;
    private string? _highlightKeyword;
    private int _rotationAngle = 0;
    private double _scale = 1.0;
    private Avalonia.Point _panOffset = new(0, 0);
    private bool _isPanning = false;
    private Avalonia.Point _lastMousePos;
    private Avalonia.Media.Imaging.Bitmap? _currentAvaloniaBitmap;
    private System.Drawing.Bitmap? _currentGdiBitmap;
    private readonly List<RectangleF> _highlightBoxes = new();
    private int _loadVersion = 0;

    public HistoryImageViewerWindow()
    {
        InitializeComponent();
        SetupInteractions();
    }

    public static void ShowFor(IReadOnlyList<HistoryCardItem> items, int selectedIndex, string? highlightKeyword = null)
    {
        if (items == null || items.Count == 0) return;
        if (_instance == null || !_instance.IsVisible) _instance = new HistoryImageViewerWindow();
        _instance._items.Clear();
        _instance._items.AddRange(items);
        _instance._highlightKeyword = string.IsNullOrWhiteSpace(highlightKeyword) ? null : highlightKeyword.Trim();
        _instance.NavigateTo(Math.Clamp(selectedIndex, 0, items.Count - 1));
        _instance.Show();
        _instance.Activate();
    }

    /// <summary>
    /// 后台预取并缓存 OCR 识别框：用户选中历史卡片时立即预热，双击打开时 0 毫秒秒出高亮。
    /// 优先读持久化的行框 sidecar（自动识别时落盘，见 CaptureHistoryStore.TrySetOcrBoxes），
    /// 没有才现场识别；现场识别成功后顺手补写 sidecar，下次（含重启后）直接命中。
    /// </summary>
    public static void PreloadBoxes(string? imagePath)
    {
        if (string.IsNullOrEmpty(imagePath) || !File.Exists(imagePath)) return;
        if (_boxCache.ContainsKey(imagePath)) return;

        // sidecar 命中：零 OCR 开销
        string? id = HistoryIdOfImagePath(imagePath);
        var persisted = id != null ? CaptureHistoryStore.TryGetOcrBoxes(id) : null;
        if (persisted != null)
        {
            _boxCache[imagePath] = persisted;
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var ocrBmp = new System.Drawing.Bitmap(imagePath);
                var ocr = CaptureSession.GetSharedOcrService();
                var boxes = await ocr.ExtractLinesWithBoxesAsync(ocrBmp).ConfigureAwait(false);
                if (boxes != null && boxes.Count > 0)
                {
                    _boxCache[imagePath] = boxes;
                    CaptureHistoryStore.TrySetOcrBoxes(id, boxes);
                }
            }
            catch { }
        });
    }

    /// <summary>历史记录 id：图片文件名（去扩展名）即 id——存储侧按 id 现推图片路径，文件名必然是 id。</summary>
    private static string? HistoryIdOfImagePath(string path)
    {
        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return null; }
    }

    private void SetupInteractions()
    {
        TitleBarDragArea.PointerPressed += (s, e) => { if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };
        BtnMin.Click += (s, e) => WindowState = WindowState.Minimized;
        BtnMax.Click += (s, e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        BtnClose.Click += (s, e) => Close();

        BtnPrev.Click += (s, e) => NavigatePrev();
        BtnNext.Click += (s, e) => NavigateNext();
        BtnRotateLeft.Click += (s, e) => RotateBy(-90);
        BtnRotateRight.Click += (s, e) => RotateBy(90);
        BtnFitWindow.Click += (s, e) => FitToWindow();
        BtnActualSize.Click += (s, e) => SetActualSize();
        BtnPinSticker.Click += (s, e) => PinCurrentToSticker();
        BtnCopyImage.Click += (s, e) => CopyCurrentImage();
        BtnSaveAs.Click += (s, e) => SaveCurrentImageAs();
        TglHighlight.IsCheckedChanged += (s, e) => { CanvasHighlight.IsVisible = TglHighlight.IsChecked == true; };

        ViewportBorder.PointerWheelChanged += OnViewportWheel;
        ViewportBorder.PointerPressed += OnViewportPointerPressed;
        ViewportBorder.PointerMoved += OnViewportPointerMoved;
        ViewportBorder.PointerReleased += OnViewportPointerReleased;

        KeyDown += OnWindowKeyDown;
        SizeChanged += (s, e) => { if (_scale <= 0 || _scale == 1.0) FitToWindow(); else UpdateTransform(); };

        // 首次布局后按真实视口再拟合一次：窗口 Show 之前 Bounds 还是 0，NavigateTo 里的
        // FitToWindow 只能按 1000×700 估算值算 scale，实际窗口更大时图会偏小
        bool initialFitDone = false;
        ViewportBorder.SizeChanged += (s, e) =>
        {
            if (!initialFitDone && e.NewSize.Width > 0)
            {
                initialFitDone = true;
                FitToWindow();
            }
        };
    }

    private void NavigatePrev()
    {
        if (_items.Count == 0) return;
        int next = _currentIndex - 1;
        if (next < 0) next = _items.Count - 1;
        NavigateTo(next);
    }

    private void NavigateNext()
    {
        if (_items.Count == 0) return;
        int next = _currentIndex + 1;
        if (next >= _items.Count) next = 0;
        NavigateTo(next);
    }

    private void NavigateTo(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        _currentIndex = index;
        var card = _items[_currentIndex];
        TxtPageIndicator.Text = $"{_currentIndex + 1} / {_items.Count}";
        TxtTitle.Text = $"{card.Source.SourceDisplayName} - {card.TimeText}";
        TxtMetaInfo.Text = $"{card.Source.Width} × {card.Source.Height} px  ({FormatBytes(card.Source.FileBytes)})";
        _rotationAngle = 0;
        LoadImageFor(card.Source);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        return $"{(bytes / (1024.0 * 1024.0)):F1} MB";
    }

    private void LoadImageFor(CaptureHistoryItem source)
    {
        int version = ++_loadVersion;
        string? path = CaptureHistoryStore.ResolveExistingImagePath(source.Id);

        _currentGdiBitmap?.Dispose();
        _currentGdiBitmap = null;
        _currentAvaloniaBitmap = null;
        CanvasHighlight.Children.Clear();
        PanelMatchToast.IsVisible = false;

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            ImgViewer.Source = null;
            return;
        }

        try
        {
            _currentGdiBitmap = new System.Drawing.Bitmap(path);
            _currentAvaloniaBitmap = BitmapConversion.ToAvaloniaBitmap(_currentGdiBitmap);
            ImgViewer.Source = _currentAvaloniaBitmap;
            ImgViewer.Width = _currentGdiBitmap.Width;
            ImgViewer.Height = _currentGdiBitmap.Height;

            CanvasContainer.Width = _currentGdiBitmap.Width;
            CanvasContainer.Height = _currentGdiBitmap.Height;
            CanvasHighlight.Width = _currentGdiBitmap.Width;
            CanvasHighlight.Height = _currentGdiBitmap.Height;

            FitToWindow();

            // 缓存命中则在当帧 0 毫秒同步高亮；未命中则后台极速提取并进缓存
            if (_boxCache.TryGetValue(path, out var cachedBoxes) && cachedBoxes != null)
            {
                ApplyHighlightBoxes(cachedBoxes, version);
            }
            else
            {
                _ = CheckAndHighlightKeywordAsync(path, version);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("HistoryImageViewerWindow.LoadImageFor 异常: " + ex.Message);
        }
    }

    private void ApplyHighlightBoxes(List<OcrLineRegion> boxes, int version)
    {
        if (version != _loadVersion) return;
        CanvasHighlight.Children.Clear();
        _highlightBoxes.Clear();

        if (string.IsNullOrWhiteSpace(_highlightKeyword) || boxes == null || boxes.Count == 0)
        {
            TglHighlight.IsVisible = false;
            PanelMatchToast.IsVisible = false;
            return;
        }

        string term = _highlightKeyword.Trim();
        var tokens = term.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(t => HistoryQuery.NormalizeForSearch(t).Replace(" ", ""))
                         .Where(t => t.Length > 0)
                         .ToArray();
        if (tokens.Length == 0) return;

        var matches = new List<RectangleF>();
        foreach (var b in boxes)
        {
            string lineText = b.Text ?? "";
            string lineNorm = HistoryQuery.NormalizeForSearch(lineText).Replace(" ", "");
            foreach (var tok in tokens)
            {
                if (lineNorm.IndexOf(tok, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matches.Add(b.Bounds);
                    break;
                }
            }
        }

        if (matches.Count > 0)
        {
            _highlightBoxes.AddRange(matches);
            foreach (var rect in matches)
            {
                var border = new Border
                {
                    Width = Math.Max(rect.Width, 12),
                    Height = Math.Max(rect.Height, 12),
                    Background = new SolidColorBrush(Avalonia.Media.Color.FromArgb(90, 255, 215, 0)),
                    BorderBrush = new SolidColorBrush(Avalonia.Media.Color.FromArgb(235, 255, 140, 0)),
                    BorderThickness = new Thickness(1.8),
                    CornerRadius = new CornerRadius(3),
                    BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Color = Avalonia.Media.Color.FromArgb(120, 255, 165, 0) }),
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(border, rect.Left);
                Canvas.SetTop(border, rect.Top);
                CanvasHighlight.Children.Add(border);
            }

            TglHighlight.IsVisible = true;
            TglHighlight.IsChecked = true;
            CanvasHighlight.IsVisible = true;
            TxtMatchCount.Text = $"找到 {matches.Count} 处匹配「{term}」";
            PanelMatchToast.IsVisible = true;
            _ = Task.Delay(3500).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (version == _loadVersion) PanelMatchToast.IsVisible = false;
            }));
        }
        else
        {
            TglHighlight.IsVisible = false;
            PanelMatchToast.IsVisible = false;
        }
    }

    private async Task CheckAndHighlightKeywordAsync(string imagePath, int version)
    {
        if (string.IsNullOrWhiteSpace(_highlightKeyword)) return;

        try
        {
            List<OcrLineRegion> boxes;
            string? id = HistoryIdOfImagePath(imagePath);
            var persisted = id != null ? CaptureHistoryStore.TryGetOcrBoxes(id) : null;
            if (persisted != null)
            {
                // sidecar 命中：不重新 OCR，直接用持久化的行框（自动识别/上次查看时已落盘）
                boxes = persisted;
                _boxCache[imagePath] = persisted;
            }
            else
            {
                using (var ocrBmp = new System.Drawing.Bitmap(imagePath))
                {
                    var ocr = CaptureSession.GetSharedOcrService();
                    boxes = await ocr.ExtractLinesWithBoxesAsync(ocrBmp).ConfigureAwait(false);
                }

                if (boxes != null && boxes.Count > 0)
                {
                    _boxCache[imagePath] = boxes;
                    // 现场识别的结果也落 sidecar：老记录第一次查看后，之后（含重启）都免 OCR
                    CaptureHistoryStore.TrySetOcrBoxes(id, boxes);
                }
            }

            if (version != _loadVersion) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (version != _loadVersion) return;
                ApplyHighlightBoxes(boxes ?? new List<OcrLineRegion>(), version);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CheckAndHighlightKeywordAsync 失败: " + ex.Message);
        }
    }

    private void RotateBy(int deltaAngle)
    {
        _rotationAngle = (_rotationAngle + deltaAngle) % 360;
        if (_rotationAngle < 0) _rotationAngle += 360;
        FitToWindow();
    }

    private void FitToWindow()
    {
        if (_currentGdiBitmap == null) return;
        double viewW = ViewportBorder.Bounds.Width;
        double viewH = ViewportBorder.Bounds.Height;
        if (viewW <= 0) viewW = Bounds.Width > 0 ? Bounds.Width : 1000;
        if (viewH <= 0) viewH = Bounds.Height > 0 ? Bounds.Height - 36 : 700;

        double imgW = _currentGdiBitmap.Width;
        double imgH = _currentGdiBitmap.Height;
        if (imgW <= 0 || imgH <= 0) return;

        bool isVertical = _rotationAngle == 90 || _rotationAngle == 270;
        double effectiveW = isVertical ? imgH : imgW;
        double effectiveH = isVertical ? imgW : imgH;

        double scaleX = (viewW - 48) / effectiveW;
        double scaleY = (viewH - 80) / effectiveH;
        _scale = Math.Min(Math.Min(scaleX, scaleY), 1.0);
        if (_scale <= 0.05) _scale = 0.05;

        _panOffset = new Avalonia.Point(0, 0);
        UpdateTransform();
    }

    private void SetActualSize()
    {
        if (_currentGdiBitmap == null) return;
        _scale = 1.0;
        _panOffset = new Avalonia.Point(0, 0);
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(_rotationAngle));
        group.Children.Add(new ScaleTransform(_scale, _scale));
        group.Children.Add(new TranslateTransform(_panOffset.X, _panOffset.Y));

        CanvasContainer.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        CanvasContainer.RenderTransform = group;
    }

    private void OnViewportWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_currentGdiBitmap == null) return;

        double delta = e.Delta.Y > 0 ? 1.15 : 0.85;
        double oldScale = _scale;
        double newScale = Math.Clamp(_scale * delta, 0.05, 10.0);
        if (Math.Abs(newScale - oldScale) < 0.001) return;

        _scale = newScale;
        UpdateTransform();
        e.Handled = true;
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(ViewportBorder).Properties;
        if (props.IsLeftButtonPressed)
        {
            if (e.ClickCount == 2)
            {
                if (Math.Abs(_scale - 1.0) < 0.05) FitToWindow(); else SetActualSize();
                e.Handled = true;
                return;
            }

            _isPanning = true;
            _lastMousePos = e.GetPosition(this);
            e.Pointer.Capture(ViewportBorder);
            e.Handled = true;
        }
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;
        var cur = e.GetPosition(this);
        var delta = cur - _lastMousePos;
        _lastMousePos = cur;

        _panOffset = new Avalonia.Point(_panOffset.X + delta.X, _panOffset.Y + delta.Y);
        UpdateTransform();
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void PinCurrentToSticker()
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count) return;
        var card = _items[_currentIndex];
        string? full = CaptureHistoryStore.ResolveExistingImagePath(card.Source.Id);
        if (full == null) return;

        try
        {
            // 判重（2026-09-29 用户口径）：同一条历史已经贴着 → 直接定位，连磁盘都不用再读一遍
            var dupById = StickerDedup.FindSameHistory(card.Source.Id);
            if (dupById != null)
            {
                StickerDedup.Reveal(dupById, "历史查看器贴图");
                return;
            }

            var bmp = CaptureHistoryStore.LoadBitmapDetached(full);
            if (bmp == null) return;
            string? cleanPath = CaptureHistoryStore.ResolveCleanImagePath(card.Source.Id);
            var cleanBmp = CaptureHistoryStore.LoadBitmapDetached(cleanPath);
            var archive = StickerArchiveStore.TryRead(card.Source.Id);

            // 同一张图被存成过多条历史时 Id 各不相同，只能按像素判；命中就别贴第二份（两张位图的所有权
            // 本来要交给贴图窗口，这里没人接就得本地释放）
            var dupByPixels = StickerDedup.FindSameImage(bmp);
            if (dupByPixels != null)
            {
                StickerDedup.Reveal(dupByPixels, "历史查看器贴图");
                try { bmp.Dispose(); } catch { }
                try { cleanBmp?.Dispose(); } catch { }
                return;
            }

            var sticker = new StickerWindow(bmp, cleanAnnotationSource: cleanBmp, annotationShapes: archive?.Shapes, annotationTexts: archive?.Texts);
            sticker.SetHistoryId(card.Source.Id);

            // 落点（2026-09-10）：有存档位置且仍在屏内就贴回原位（与历史窗口右键贴屏同规则）；
            // 否则 Show 后居中到鼠标所在屏。此前什么都不设，窗口落系统默认位——这就是贴图总在左上角的原因
            var wa = ScreenCaptureTool.Platform.MonitorHelper.GetWorkAreaUnion();
            bool positioned = false;
            if (archive != null && archive.State.Width > 0 && wa.Width > 0
                && archive.State.X < wa.Right && archive.State.Y < wa.Bottom
                && archive.State.X + archive.State.Width > wa.Left
                && archive.State.Y + archive.State.Height > wa.Top)
            {
                sticker.Position = new Avalonia.PixelPoint(archive.State.X, archive.State.Y);
                positioned = true;
            }

            sticker.Show();
            sticker.Activate();
            if (!positioned)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(sticker.CenterStickerOnWorkArea, Avalonia.Threading.DispatcherPriority.Loaded);
            }
            StickerFocusManager.SetFocus(sticker);
            StickerToastWindow.ShowAtTop("已贴到屏幕");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PinCurrentToSticker 失败: " + ex.Message);
        }
    }

    private async void CopyCurrentImage()
    {
        if (_currentGdiBitmap == null) return;
        try
        {
            // 与历史窗口复制同一通道：按像素内容自动保 alpha，圆角/透明阴影粘贴到接收端不丢
            // 浏览／关闭窗口可替换或释放原图；后台编码仅持有本次独立快照。
            using var snapshot = ClipboardService.DetachPixels(_currentGdiBitmap);
            if (!await ClipboardService.SetImageAutoAlphaAsync(snapshot)) return;
            StickerToastWindow.ShowAtTop("已复制图片到剪贴板");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("CopyCurrentImage 失败: " + ex.Message);
        }
    }

    private async void SaveCurrentImageAs()
    {
        if (_currentGdiBitmap == null || _currentIndex < 0 || _currentIndex >= _items.Count) return;
        var card = _items[_currentIndex];
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "另存为图片",
                SuggestedFileName = $"yuSnip_{card.Source.Id}.png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG 图片 (*.png)") { Patterns = new[] { "*.png" } },
                    new FilePickerFileType("JPEG 图片 (*.jpg)") { Patterns = new[] { "*.jpg", "*.jpeg" } },
                    new FilePickerFileType("BMP 图片 (*.bmp)") { Patterns = new[] { "*.bmp" } }
                }
            });

            if (file != null)
            {
                string localPath = file.Path.LocalPath;
                // 按所选扩展名落盘（PNG/JPG/BMP），JPG/BMP 无 alpha 时合成白底防四角变黑；
                // 此前写死 ImageFormat.Png，选了 JPG 存出来的也是 PNG 字节
                ImageExportService.Save(_currentGdiBitmap, localPath);
                StickerToastWindow.ShowAtTop("已保存图片");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SaveCurrentImageAs 失败: " + ex.Message);
        }
    }

    // -------------------- 右键菜单 --------------------

    private void OnCtxCopyClick(object? sender, RoutedEventArgs e) => CopyCurrentImage();

    private void OnCtxStickerClick(object? sender, RoutedEventArgs e) => PinCurrentToSticker();

    private void OnCtxSaveAsClick(object? sender, RoutedEventArgs e) => SaveCurrentImageAs();

    private void OnCtxCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnCtxOpenLocationClick(object? sender, RoutedEventArgs e)
    {
        if (_currentIndex < 0 || _currentIndex >= _items.Count) return;
        var src = _items[_currentIndex].Source;
        try
        {
            // 与历史窗口 ResolveRevealPath 同规则：优先用户当初保存的原图，丢失则回退历史目录副本
            string? path = !string.IsNullOrWhiteSpace(src.SavedPath) && File.Exists(src.SavedPath)
                ? src.SavedPath
                : CaptureHistoryStore.ResolveExistingImagePath(src.Id);
            if (path == null)
            {
                StickerToastWindow.ShowAtTop("图片文件已丢失");
                return;
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("HistoryImageViewerWindow.OnCtxOpenLocationClick 失败: " + ex.Message);
            StickerToastWindow.ShowAtTop("无法打开文件位置");
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Left || e.Key == Key.A)
        {
            NavigatePrev();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Right || e.Key == Key.D)
        {
            NavigateNext();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.R)
        {
            // R 顺时针；Shift+R 逆时针
            RotateBy(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -90 : 90);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space)
        {
            FitToWindow();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.C && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            CopyCurrentImage();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.S && (e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            SaveCurrentImageAs();
            e.Handled = true;
            return;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _currentGdiBitmap?.Dispose();
        _currentGdiBitmap = null;
        _currentAvaloniaBitmap = null;
        if (_instance == this) _instance = null;
    }
}
