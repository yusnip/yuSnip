using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 贴图右键扩展与两项交互（2026-09-29）：
/// <list type="bullet">
/// <item>不透明度：Ctrl+滚轮 / 右键「不透明度(xx%)」。两条呈现通路同一语义——分层呈现走
/// <c>UpdateLayeredWindow</c> 的 SourceConstantAlpha，Avalonia 退路走 RootBorder＋阴影层的 Opacity。</item>
/// <item>大小：右键「大小(xx%)」预设档，走「适配屏幕」同一条以内容中心为锚的缩放通路。</item>
/// <item>图像处理：非破坏式，保留一份原图，显示图恒由"原图＋<see cref="StickerImageAdjustState"/>"重新导出，
/// 导出结果就是 <c>_bitmap</c> 本身，所以复制 / 另存 / 历史天然与显示一致。</item>
/// <item>Alt 取色器：按住 Alt 悬停图片贴图显示放大镜＋色值；取色窗鼠标穿透，Alt 拖选文字照常可用。</item>
/// </list>
/// </summary>
public partial class StickerWindow
{
    // =====================================================================
    // 不透明度
    // =====================================================================

    /// <summary>不透明度下限（10%）：再低贴图就看不见、也点不准了。</summary>
    private const double MinStickerOpacity = 0.10;

    /// <summary>Ctrl+滚轮每一格的步进。</summary>
    private const double OpacityWheelStep = 0.05;

    private static readonly int[] OpacityPresets = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10 };
    private static readonly int[] SizePresets = { 25, 50, 75, 100, 125, 150, 200, 300 };

    private double _stickerOpacity = 1.0;

    /// <summary>分层呈现窗的整窗 alpha（见 <see cref="PinFrame.ConstantAlpha"/>）。</summary>
    private byte StickerOpacityAlpha => (byte)Math.Clamp((int)Math.Round(_stickerOpacity * 255), 0, 255);

    private int StickerOpacityPercent => (int)Math.Round(_stickerOpacity * 100);

    private void SetStickerOpacity(double value, bool showToast)
    {
        double v = Math.Clamp(Math.Round(value * 100) / 100.0, MinStickerOpacity, 1.0);
        bool changed = Math.Abs(v - _stickerOpacity) > 0.0001;
        _stickerOpacity = v;
        if (changed)
        {
            // Avalonia 侧：RootBorder 管图片/文字/标注层；阴影层是它的兄弟，单独乘（呈现层接管时它本就是 0）
            if (RootBorder != null) RootBorder.Opacity = v;
            if (ShadowBorder != null && !_pinActive) ShadowBorder.Opacity = v;
            // 分层呈现侧：alpha 进了 Layout，比对不等即重画一帧
            RequestPinSync();
        }
        if (showToast) StickerToastWindow.ShowOrUpdateAtTop($"不透明度 {StickerOpacityPercent}%");
    }

    /// <summary>Ctrl+滚轮：上滚更不透明、下滚更透明。返回 true＝已消费。</summary>
    private bool TryHandleOpacityWheel(PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return false;
        e.Handled = true;
        if (e.Delta.Y == 0) return true;
        SetStickerOpacity(_stickerOpacity + e.Delta.Y * OpacityWheelStep, showToast: true);
        return true;
    }

    // =====================================================================
    // 右键：贴图工具栏 / 大小 / 不透明度 / 图像处理的状态刷新
    // =====================================================================

    private bool _extendedMenuBuilt;

    /// <summary>由 <see cref="UpdateContextMenuState"/> 调用：标题带当前值、当前档染品牌紫、可用性按状态判。</summary>
    private void UpdateExtendedMenuState()
    {
        EnsureExtendedMenuItems();

        bool isImageSticker = _bitmap != null && (TextViewbox == null || !TextViewbox.IsVisible)
                              && !StickerColorSwatch.IsVisible;

        if (MenuAnnotationToolbar != null)
        {
            // 键位读目录（sticker.annotate，默认空格）：2026-09-29 起它与 IsAnnotationToggleKey 判的是同一个绑定，
            // 再写死空格就会出现"设置页改了键、菜单右列还教空格"
            SetMenuGesture(MenuAnnotationToolbar, "sticker.annotate");
            MenuAnnotationToolbar.Classes.Set("on", _isAnnotating);
            MenuAnnotationToolbar.IsEnabled = !_isLocked && !_isMousePenetration && !_isThumbnailMode;
        }

        if (MenuSize != null)
        {
            int pct = (int)Math.Round(_scale * 100);
            MenuSize.Header = $"大小({pct}%)";
            MenuSize.IsEnabled = !_isLocked && !_isThumbnailMode
                                 && !(_needsMarkdownHeightCapture && IsMarkdownSticker);
            double max = GetMaxScale();
            foreach (var o in MenuSize.Items)
            {
                if (o is not MenuItem mi || mi.Tag is not int p) continue;
                MarkPresetItem(mi, Math.Abs(_scale * 100 - p) < 0.5);
                mi.IsEnabled = p / 100.0 <= max + 0.0001;
            }
        }

        if (MenuOpacity != null)
        {
            int pct = StickerOpacityPercent;
            MenuOpacity.Header = $"不透明度({pct}%)";
            MenuOpacity.IsEnabled = !_isLocked;
            foreach (var o in MenuOpacity.Items)
            {
                if (o is MenuItem mi && mi.Tag is int p) MarkPresetItem(mi, p == pct);
            }
        }

        if (MenuImageAdjust != null)
        {
            var st = _imageAdjust;
            MenuImageAdjust.IsVisible = isImageSticker;
            MenuImageAdjust.IsEnabled = !_isLocked && !IsMattingInFlight && !IsTranslateInFlight;
            MenuAdjustGray?.Classes.Set("on", st.Grayscale);
            MenuAdjustInvert?.Classes.Set("on", st.Invert);
            if (MenuAdjustBrightness != null)
            {
                MenuAdjustBrightness.Header = $"亮度({FormatBrightness(st.BrightnessPct)})";
                MenuAdjustBrightness.Classes.Set("on", st.BrightnessPct != 0);
                foreach (var o in MenuAdjustBrightness.Items)
                {
                    if (o is MenuItem mi && mi.Tag is int p) MarkPresetItem(mi, p == st.BrightnessPct);
                }
            }
            if (MenuAdjustResetGeometry != null) MenuAdjustResetGeometry.IsEnabled = !st.IsGeometryIdentity;
            if (MenuAdjustResetAll != null) MenuAdjustResetAll.IsEnabled = !st.IsIdentity;
        }
    }

    private static string FormatBrightness(int pct) => pct > 0 ? $"+{pct}%" : $"{pct}%";

    /// <summary>当前档：左侧挂一枚对勾并染品牌紫（.on 走全局 menuIcon 样式，三态主题同一 Token）。</summary>
    private static void MarkPresetItem(MenuItem mi, bool current)
    {
        mi.Classes.Set("on", current);
        if (mi.Icon is Control icon) icon.IsVisible = current;
    }

    private static MenuItem CreatePresetItem(string header, int tag, EventHandler<RoutedEventArgs> onClick)
    {
        var check = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(OcrIconGeometry.Check),
            IsVisible = false,
        };
        check.Classes.Add("menuIcon");
        var mi = new MenuItem { Header = header, Tag = tag, Icon = check };
        mi.Click += onClick;
        return mi;
    }

    /// <summary>档位子项按预设表生成一次（预设表与判定"当前档"共用同一份数组，不会漂移）。</summary>
    private void EnsureExtendedMenuItems()
    {
        if (_extendedMenuBuilt) return;
        _extendedMenuBuilt = true;

        if (MenuSize != null)
        {
            foreach (int p in SizePresets)
                MenuSize.Items.Add(CreatePresetItem($"{p}%", p, OnSizePresetClick));
        }
        if (MenuOpacity != null)
        {
            foreach (int p in OpacityPresets)
                MenuOpacity.Items.Add(CreatePresetItem($"{p}%", p, OnOpacityPresetClick));
        }
        if (MenuAdjustBrightness != null)
        {
            foreach (int p in StickerImageAdjust.BrightnessPresets)
            {
                string header = p == 0 ? "0%（原始）" : FormatBrightness(p);
                MenuAdjustBrightness.Items.Add(CreatePresetItem(header, p, OnBrightnessPresetClick));
            }
        }
    }

    private void OnAnnotationToolbarClick(object? sender, RoutedEventArgs e)
    {
        if (_isLocked || _isMousePenetration) return;
        ToggleAnnotationMode();
    }

    private void OnSizePresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: int p }) return;
        if (_isLocked || _isThumbnailMode) return;
        double target = Math.Max(0.01, Math.Min(p / 100.0, GetMaxScale()));
        if (Math.Abs(target - _scale) < 0.0001) return;
        ApplyStickerScaleAnchored(target, "大小预设");
    }

    private void OnOpacityPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: int p }) return;
        SetStickerOpacity(p / 100.0, showToast: false);
    }

    // =====================================================================
    // 图像处理（非破坏式）
    // =====================================================================

    private StickerImageAdjustState _imageAdjust;

    /// <summary>图像处理的原图（本窗口持有）。null＝当前未做任何处理，<c>_bitmap</c> 即原图。</summary>
    private System.Drawing.Bitmap? _adjustOriginal;

    /// <summary>本次 <see cref="ReplaceImage"/> 是图像处理自己发起的：不得把处理基线清掉。</summary>
    private bool _applyingImageAdjust;

    private void OnImageAdjustClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        switch (tag)
        {
            case "rotR": ApplyImageAdjust(st => st.RotateClockwise()); break;
            case "rotL": ApplyImageAdjust(st => st.RotateCounterClockwise()); break;
            case "flipH": ApplyImageAdjust(st => st.ToggleFlipScreenH()); break;
            case "flipV": ApplyImageAdjust(st => st.ToggleFlipScreenV()); break;
            case "gray": ApplyImageAdjust(st => st with { Grayscale = !st.Grayscale }); break;
            case "invert": ApplyImageAdjust(st => st with { Invert = !st.Invert }); break;
            case "resetGeo": ApplyImageAdjust(st => st.ResetGeometry()); break;
            case "resetAll": ApplyImageAdjust(_ => StickerImageAdjustState.Identity); break;
        }
    }

    private void OnBrightnessPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: int p }) return;
        ApplyImageAdjust(st => st with { BrightnessPct = p });
    }

    /// <summary>
    /// 按 <paramref name="op"/> 推出新状态，从原图重新导出显示图并原地换图。
    /// <para>标注的处理：图像处理会整幅改写像素（旋转还会改坐标系），矢量图元没法跟着转，
    /// 故动手前先把当前成品（含标注）压平成原图。已处理过、之后又画了新标注的，同样把"处理后＋新标注"
    /// 压平成新的原图、状态归零——此后的"重置"只回到这一版（Toast 明示）。</para>
    /// </summary>
    private void ApplyImageAdjust(Func<StickerImageAdjustState, StickerImageAdjustState> op)
    {
        if (_bitmap == null || StickerColorSwatch.IsVisible || (TextViewbox != null && TextViewbox.IsVisible)) return;
        if (_isLocked) return;
        if (IsMattingInFlight || IsTranslateInFlight)
        {
            StickerToastWindow.ShowAtTop("正在处理中，请稍候…");
            return;
        }

        try
        {
            FinalizeAnnotationForGeometryChange("图像处理");

            bool rebased = false;
            if (_adjustOriginal == null)
            {
                _adjustOriginal = AcquireAnnotationCompositeForExport() ?? CloneArgb(_bitmap);
                _imageAdjust = StickerImageAdjustState.Identity;
            }
            else if (HasAnyAnnotationContent())
            {
                var flat = AcquireAnnotationCompositeForExport();
                if (flat != null)
                {
                    _adjustOriginal.Dispose();
                    _adjustOriginal = flat;
                    _imageAdjust = StickerImageAdjustState.Identity;
                    rebased = true;
                }
            }

            var next = op(_imageAdjust);
            if (next == _imageAdjust && !rebased) return;

            var result = StickerImageAdjust.Apply(_adjustOriginal, next);
            ReplaceWithAdjustedImage(result);
            _imageAdjust = next;

            // 回到单位状态：显示图就是原图的逐像素拷贝，基线不必再留（下次处理从 _bitmap 重新取）
            if (next.IsIdentity)
            {
                _adjustOriginal.Dispose();
                _adjustOriginal = null;
            }

            PersistAdjustedImageToHistory();
            UpdateContextMenuState();
            if (rebased) StickerToastWindow.ShowAtTop("已将标注合入图像");
            AppLogger.Info($"StickerWindow: 图像处理 → rot={next.RotationDeg} flipH={next.FlipH} flipV={next.FlipV} " +
                           $"gray={next.Grayscale} invert={next.Invert} bright={next.BrightnessPct} " +
                           $"size={_sourcePixelWidth}x{_sourcePixelHeight}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("StickerWindow.ApplyImageAdjust 异常", ex);
            StickerToastWindow.ShowAtTop("图像处理失败");
        }
    }

    private static System.Drawing.Bitmap CloneArgb(System.Drawing.Bitmap src)
        => src.Clone(new System.Drawing.Rectangle(0, 0, src.Width, src.Height),
                     System.Drawing.Imaging.PixelFormat.Format32bppArgb);

    /// <summary>换图并清掉一切挂在旧像素上的标注底稿（它们已被压平进原图）。</summary>
    private void ReplaceWithAdjustedImage(System.Drawing.Bitmap result)
    {
        var oldBitmap = _bitmap;
        _applyingImageAdjust = true;
        try
        {
            // 卡片几何（圆角/描边/烘焙阴影带）对旋转、翻转都对称，原样沿用；抠图贴图保持透明底板口径
            if (_isCutout)
                ReplaceImage(result, isCutout: true);
            else
                ReplaceImage(result, isCutout: false,
                    restoredBakedCard: _bakedCardGeometry,
                    restoredHasBakedShadow: _hasBakedShadow,
                    restoredHideCheckerboard: _hideAlphaCheckerboard,
                    // 边距也"原样沿用"：不传的话会退回 _originalBakedShadowPadPx（那是抠图备份的量，
                    // 与本次无关）或默认的 37，把描边那一段边距丢掉，高亮层与垫底就收不进卡片外沿。
                    restoredShadowPadPx: _bakedShadowPadPx);
        }
        finally
        {
            _applyingImageAdjust = false;
        }

        // 截图转入的贴图带着一份"干净底图＋可编辑图元"：它们描述的是处理前的坐标系与像素，
        // 留着的话下次进标注会把旧图元、旧底图重新摆出来。成品已压平进原图，这里一并作废。
        var initial = _initialAnnotationSource;
        _initialAnnotationSource = null;
        _initialAnnotationShapes = null;
        _initialAnnotationTexts = null;
        if (initial != null && !ReferenceEquals(initial, oldBitmap) && !ReferenceEquals(initial, _bitmap))
        {
            Dispatcher.UIThread.Post(() =>
            {
                try { initial.Dispose(); } catch { }
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>历史记录跟着换成处理后的图：历史/存档恢复看到的必须与屏幕上一致。
    /// 干净底图仅在原先就有时覆盖（否则恢复会拿旧底图当标注底稿）。</summary>
    private void PersistAdjustedImageToHistory()
    {
        if (string.IsNullOrEmpty(HistoryId) || _bitmap == null) return;
        string id = HistoryId!;
        try
        {
            CaptureHistoryStore.TryUpdateImage(id, _bitmap, AppSettings.Load());
            if (CaptureHistoryStore.ResolveCleanImagePath(id) != null)
            {
                var clean = CloneArgb(_bitmap);
                System.Threading.Tasks.Task.Run(() =>
                {
                    using (clean) CaptureHistoryStore.SaveCleanImage(id, clean);
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow.PersistAdjustedImageToHistory 失败: " + ex.Message);
        }
    }

    /// <summary>外部换图（翻译 / 抠图 / 恢复原图）＝换了一张新图：处理基线与状态一并作废。</summary>
    private void ResetImageAdjustBaselineForExternalReplace()
    {
        if (_applyingImageAdjust) return;
        _imageAdjust = StickerImageAdjustState.Identity;
        if (_adjustOriginal != null)
        {
            try { _adjustOriginal.Dispose(); } catch { }
            _adjustOriginal = null;
        }
    }

    private void DisposeImageAdjustResources()
    {
        try { _adjustOriginal?.Dispose(); } catch { }
        _adjustOriginal = null;
        CloseColorPicker();
    }

    // =====================================================================
    // 大小：以内容当前屏幕中心为锚缩放（「适配屏幕」同一条通路）
    // =====================================================================

    /// <summary>
    /// 把贴图缩放到 <paramref name="target"/>。刻意不另写一套几何：激活覆写态（幂等）→
    /// <see cref="ApplyScale"/> 只改内容显式宽高 → 以内容当前屏幕中心为锚重钉各层 →
    /// <see cref="CommitWheelGesture"/> 一次原子 SetWindowPos 完成缩放＋归位。
    /// </summary>
    private void ApplyStickerScaleAnchored(double target, string trigger)
    {
        FinalizeAnnotationForGeometryChange(trigger);
        if (_isResizing) EndEdgeResize();
        // 目标尺寸按 _sourcePixel 反解，错配态先收口再算（图片贴图零开销）
        SyncTextSurfaceBeforeGesture();

        double s = SafeScaling;
        double mPx = ShadowMargin * s;

        // 改写内容尺寸前先留一份旧尺寸（Repin 要按它算当前中心）与当前窗口矩形
        double prevW = StickerImage.Width, prevH = StickerImage.Height;
        double wl, wt, contW, contH;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && ScreenCaptureTool.Platform.NativeMethods.GetWindowRect(hwnd, out var rc))
        {
            wl = rc.Left; wt = rc.Top;
            contW = rc.Width - mPx * 2;
            contH = rc.Height - mPx * 2;
        }
        else
        {
            wl = Position.X; wt = Position.Y;
            contW = Width * s - mPx * 2;
            contH = Height * s - mPx * 2;
        }

        ActivateWheelEnvelope(wl + mPx, wt + mPx, contW, contH);
        _scale = target;
        ApplyScale(skipWindowResize: true);
        RepinWheelVisualForNewContentSize(prevW, prevH, StickerImage.Width, StickerImage.Height,
                                          resizeWindow: false);
        WheelDiag($"{trigger} scale={_scale:F3} 内容物理({_wheelImgWpx:F0}x{_wheelImgHpx:F0}) " +
                  $"源图={_sourcePixelWidth}x{_sourcePixelHeight}");
        CommitWheelGesture(trigger);
    }

    // =====================================================================
    // Shift 取色器（2026-09-29 唤出键由 Alt 改为此键，并受设置项 StickerColorPickerEnabled 控制）
    // =====================================================================

    private StickerColorPickerWindow? _colorPicker;
    private DispatcherTimer? _colorPickerTimer;
    private readonly byte[] _pickerBuffer = new byte[StickerColorPickerWindow.SamplePx * StickerColorPickerWindow.SamplePx * 4];
    private byte _pickerR, _pickerG, _pickerB, _pickerA;
    private bool _pickerHasColor;
    private long _pickerLastSampleTick;

    /// <summary>HEX / RGB 显示格式（全体贴图共用一份，切一次处处生效）。</summary>
    private static bool _pickerUseHex = true;

    private bool ColorPickerVisible => _colorPicker != null && _colorPicker.IsVisible;

    /// <summary>
    /// 取色器能否出现（与 Alt 选词同一批前置条件：图片贴图、非标注、非锁定/穿透/缩略图）。
    /// 设置项判据<b>只加在这一处</b>：显示与隐藏的唯一入口是 <see cref="RefreshColorPicker"/>，
    /// 按键分支又必须先 <see cref="ColorPickerVisible"/>，所以关档后 30ms 轮询自然把已开着的取色器收掉。
    /// 读 <see cref="AppSettings.Current"/> 活实例（同 <c>StickerTransparentCheckerboard</c> 的读法）
    /// ⇒ 设置页勾完立即生效，不必重开贴图窗。
    /// </summary>
    private bool CanShowColorPicker =>
        AppSettings.Current.StickerColorPickerEnabled
        && !_isClosed && !_isAnnotating && !_isLocked && !_isMousePenetration && !_isThumbnailMode
        && _bitmap != null && StickerImage != null && StickerImage.IsVisible
        && !StickerColorSwatch.IsVisible && StickerImage.Source is Bitmap;

    /// <summary>
    /// 唤出键的物理按下态。取色器是"按住才存在"的手势，靠 <c>GetAsyncKeyState</c> 读真实键盘态，
    /// 而不是 Avalonia 事件里的修饰键快照（贴图窗没有焦点时收不到 KeyUp，抬手隐藏只能这么兜）。
    /// 修饰键来自贴图快捷键目录；保持按住唤出、松开隐藏，不改成切换式。
    /// </summary>
    private bool IsSummonKeyDown()
        => (ScreenCaptureTool.Platform.NativeMethods.GetAsyncKeyState(ColorPickerSummonVirtualKey()) & 0x8000) != 0;

    private int ColorPickerSummonVirtualKey()
        => StickerGestureModifier("sticker.pickhold", KeyModifiers.Shift) switch
        {
            KeyModifiers.Control => 0x11,
            KeyModifiers.Alt => 0x12,
            _ => 0x10,
        };

    private bool IsColorPickerSummonKey(Key key)
        => Services.BuiltinShortcutsStore.ModifierOfKey(key)
            == StickerGestureModifier("sticker.pickhold", KeyModifiers.Shift);

    private static bool IsLeftButtonPhysicallyDown()
        => (ScreenCaptureTool.Platform.NativeMethods.GetAsyncKeyState(ScreenCaptureTool.Platform.NativeMethods.VK_LBUTTON) & 0x8000) != 0;

    /// <summary>
    /// 统一入口：按"此刻真实状态"决定显示/隐藏并刷新。指针移动、唤出键按下/抬起、轮询计时器都只调它，
    /// 于是状态判定只有这一份（贴图没拿到键盘焦点时收不到 KeyUp，靠轮询兜住隐藏）。
    /// </summary>
    private void RefreshColorPicker()
    {
        if (CaptureWindow.ActiveOverlayHwnd != IntPtr.Zero
            || !CanShowColorPicker || !IsSummonKeyDown() || IsLeftButtonPhysicallyDown()
            || !TryGetCursorImagePixel(out int cursorX, out int cursorY, out int px, out int py))
        {
            HideColorPicker();
            return;
        }

        if (_colorPicker == null)
        {
            _colorPicker = new StickerColorPickerWindow();
            _colorPicker.RequestedThemeVariant = ActualThemeVariant;
        }

        long now = Environment.TickCount64;
        bool due = !_colorPicker.IsVisible || unchecked(now - _pickerLastSampleTick) >= 15;
        if (due)
        {
            _pickerLastSampleTick = now;
            SamplePickerPixels((Bitmap)StickerImage.Source!, px, py);
            var c = Color.FromArgb(255, _pickerR, _pickerG, _pickerB);
            _colorPicker.UpdateContent(_pickerBuffer, c, _pickerHasColor, FormatPickerColor(), $"X:{px}  Y:{py}");
        }

        if (!_colorPicker.IsVisible)
        {
            _colorPicker.RequestedThemeVariant = ActualThemeVariant;
            _colorPicker.ShowAboveStickers();
        }
        _colorPicker.PlaceNearCursor(cursorX, cursorY);

        if (_colorPickerTimer == null)
        {
            _colorPickerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _colorPickerTimer.Tick += (_, _) => RefreshColorPicker();
        }
        if (!_colorPickerTimer.IsEnabled) _colorPickerTimer.Start();
    }

    private void HideColorPicker()
    {
        _colorPickerTimer?.Stop();
        if (_colorPicker != null && _colorPicker.IsVisible)
        {
            try { _colorPicker.Hide(); } catch { }
        }
    }

    /// <summary>遮罩显示时收起贴图自己的取色窗，避免同一枚 Shift 唤出两套浮层。</summary>
    internal static void HideColorPickersForCapture()
    {
        if (Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var window in desktop.Windows.OfType<StickerWindow>().ToArray())
            window.HideColorPicker();
    }

    private void CloseColorPicker()
    {
        _colorPickerTimer?.Stop();
        _colorPickerTimer = null;
        try { _colorPicker?.Close(); } catch { }
        _colorPicker = null;
    }

    /// <summary>光标（屏幕物理像素）→ 显示位图像素。要求光标确实压在本贴图上（叠放的另一张贴图不算）。</summary>
    private bool TryGetCursorImagePixel(out int cursorX, out int cursorY, out int px, out int py)
    {
        cursorX = cursorY = px = py = 0;
        if (!ScreenCaptureTool.Platform.NativeMethods.GetCursorPos(out var cp)) return false;
        cursorX = cp.X; cursorY = cp.Y;

        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return false;
        IntPtr under = ScreenCaptureTool.Platform.NativeMethods.WindowFromPoint(
            new ScreenCaptureTool.Platform.NativeMethods.POINT_INT(cp.X, cp.Y));
        if (under != hwnd && (_pin == null || under != _pin.Handle)) return false;

        if (StickerImage.Source is not Bitmap bmp) return false;
        var ps = bmp.PixelSize;
        double bw = StickerImage.Bounds.Width, bh = StickerImage.Bounds.Height;
        if (ps.Width <= 0 || ps.Height <= 0 || bw <= 0 || bh <= 0) return false;

        // PointToClient 逆算控件自身与祖先的全部渲染变换（缩放补帧倍率、平移），与所见严格一致
        var local = StickerImage.PointToClient(new PixelPoint(cp.X, cp.Y));
        if (local.X < 0 || local.Y < 0 || local.X >= bw || local.Y >= bh) return false;
        px = Math.Clamp((int)Math.Floor(local.X / bw * ps.Width), 0, ps.Width - 1);
        py = Math.Clamp((int)Math.Floor(local.Y / bh * ps.Height), 0, ps.Height - 1);
        return true;
    }

    /// <summary>以 (cx, cy) 为中心取 17×17 像素进放大区（越界格留透明，中心格恒在十字线上），并记下中心色。
    /// 取的是 <c>StickerImage.Source</c>——此刻显示的那一张（含已烤进去的标注与图像处理）。</summary>
    private unsafe void SamplePickerPixels(Bitmap bmp, int cx, int cy)
    {
        const int n = StickerColorPickerWindow.SamplePx;
        const int half = n / 2;
        Array.Clear(_pickerBuffer);
        _pickerHasColor = false;

        var ps = bmp.PixelSize;
        int l = cx - half, t = cy - half;
        int x0 = Math.Max(0, l), y0 = Math.Max(0, t);
        int x1 = Math.Min(ps.Width, l + n), y1 = Math.Min(ps.Height, t + n);
        if (x1 <= x0 || y1 <= y0) return;

        int w = x1 - x0, h = y1 - y0;
        var tmp = new byte[w * h * 4];
        try
        {
            fixed (byte* p = tmp)
            {
                bmp.CopyPixels(new PixelRect(x0, y0, w, h), (IntPtr)p, tmp.Length, w * 4);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("StickerWindow: 取色读像素失败 " + ex.Message);
            return;
        }

        bool rgba = bmp.Format.HasValue && bmp.Format.Value == Avalonia.Platform.PixelFormat.Rgba8888;
        bool unpremul = bmp.AlphaFormat == Avalonia.Platform.AlphaFormat.Unpremul;
        bool opaqueFmt = bmp.AlphaFormat == Avalonia.Platform.AlphaFormat.Opaque;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                byte c0 = tmp[si], c1 = tmp[si + 1], c2 = tmp[si + 2], a = opaqueFmt ? (byte)255 : tmp[si + 3];
                byte b = rgba ? c2 : c0, g = c1, r = rgba ? c0 : c2;
                if (unpremul && a < 255)
                {
                    b = (byte)(b * a / 255); g = (byte)(g * a / 255); r = (byte)(r * a / 255);
                }
                int di = ((y0 - t + y) * n + (x0 - l + x)) * 4;
                _pickerBuffer[di] = b; _pickerBuffer[di + 1] = g; _pickerBuffer[di + 2] = r; _pickerBuffer[di + 3] = a;
            }
        }

        int ci = (half * n + half) * 4;
        byte ca = _pickerBuffer[ci + 3];
        _pickerA = ca;
        if (ca == 0) return;
        // 放大区是预乘的；色值报"像素本身的颜色"，反预乘回直通色
        _pickerB = (byte)Math.Min(255, _pickerBuffer[ci] * 255 / ca);
        _pickerG = (byte)Math.Min(255, _pickerBuffer[ci + 1] * 255 / ca);
        _pickerR = (byte)Math.Min(255, _pickerBuffer[ci + 2] * 255 / ca);
        _pickerHasColor = true;
    }

    private string FormatPickerColor()
    {
        if (!_pickerHasColor) return "透明";
        string s = _pickerUseHex
            ? $"#{_pickerR:X2}{_pickerG:X2}{_pickerB:X2}"
            : $"{_pickerR}, {_pickerG}, {_pickerB}";
        if (_pickerA < 255) s += $"  A:{(int)Math.Round(_pickerA * 100 / 255.0)}%";
        return s;
    }

    /// <summary>
    /// 取色器显示期间的按键：<b>复制色值</b>与<b>切换 HEX/RGB</b> 两枚动作键走改键目录
    /// （<c>sticker.pickcopy</c> / <c>sticker.pickformat</c>，出厂默认 <c>Shift+C</c> / <c>Shift+X</c>）。
    /// 判据不再是"修饰键必须恰好是 Alt"那种硬编码——键既然可配，就得由绑定自己精确比对。
    /// 返回 true＝已消费。
    /// </summary>
    private bool TryHandleColorPickerKey(KeyEventArgs e)
    {
        if (IsColorPickerSummonKey(e.Key))
        {
            // 按下唤出键的那一刻指针可能一动不动：立刻按当前位置出取色器
            RefreshColorPicker();
            return false;
        }
        if (!ColorPickerVisible) return false;

        if (ShortcutMatches("sticker.pickcopy", e))
        {
            e.Handled = true;
            if (!_pickerHasColor)
            {
                StickerToastWindow.ShowAtTop("此处为透明像素");
                return true;
            }
            string text = _pickerUseHex
                ? $"#{_pickerR:X2}{_pickerG:X2}{_pickerB:X2}"
                : $"{_pickerR}, {_pickerG}, {_pickerB}";
            ClipboardService.SetText(text);
            StickerToastWindow.ShowAtTop("已复制颜色 " + text);
            return true;
        }
        if (ShortcutMatches("sticker.pickformat", e))
        {
            e.Handled = true;
            _pickerUseHex = !_pickerUseHex;
            _colorPicker?.SetColorText(FormatPickerColor());
            return true;
        }
        return false;
    }
}
