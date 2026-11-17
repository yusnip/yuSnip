using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Services;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 翻译极简浮窗：默认保留译文优先的单区便签，按需开启流式后逐段显示原译文对照。
/// 宽度固定 444（含四周 26px 透明缓冲，保证阴影不被裁），高度贴合内容，超上限由译文区内部滚动。
/// 结果出来即自动写剪贴板，用户不必开窗就能粘贴；双击便签空白处或点右上角图标升级到 <see cref="TranslationResultWindow"/>。
/// 不提供缩小/放大/关闭按钮，也不置顶；是否在失去焦点时自动退场由设置项 <c>TranslateMiniWindowAutoClose</c> 决定。
/// 常驻单例：重复翻译复用同一个窗口刷新内容。
/// </summary>
public partial class TranslateMiniWindow : Window
{
    /// <summary>单例引用：同一时刻只允许一个浮窗，重复弹出走刷新而非叠窗口。</summary>
    private static TranslateMiniWindow? _current;

    /// <summary>内容不足以定位时用的整窗兜底高度（dip）：卡片约 150 + 上下各 26 缓冲。</summary>
    private const double EstimatedHeightDip = 202;

    /// <summary>窗口四周的透明缓冲（dip）：必须与 XAML 外层 Border 的 Padding 一致，否则定位会把卡片整体推开。</summary>
    private const double ShadowPadDip = 26;

    private AppSettings _settings = new();
    private TranslationConfig _config = new();
    private string _sourceText = string.Empty;
    private string _targetText = string.Empty;
    private string _engineName = string.Empty;
    private string _effectiveSourceLang = TranslationLanguage.Auto;
    private string _effectiveTargetLang = TranslationLanguage.ChineseSimplified;
    private string? _detectedSourceLang;
    private TranslationResult? _result;

    private CancellationTokenSource? _cts;
    private bool _closing;
    private bool _dragged;
    private bool _hover;
    private bool _everActivated;
    private bool _copied;
    private string _copiedLabel = "译文";
    private int _retryCount;
    private bool _streaming;
    private bool _translating;
    private int _streamCompleted;
    private readonly List<SelectableTextBlock> _streamTargets = [];
    private readonly List<SelectableTextBlock> _streamTexts = [];
    private readonly StringBuilder _streamText = new();
    private IReadOnlyList<TranslationSourceSpan> _streamSpans = Array.Empty<TranslationSourceSpan>();

    /// <summary>定位锚点（截图选区或贴图的屏幕像素矩形）。</summary>
    private PixelRect _anchor;

    /// <summary>跟随鼠标模式的落点：在弹出/刷新那一刻捕获光标，之后尺寸变化重新定位时沿用，避免便签追著鼠标跑。</summary>
    private PixelPoint? _followOrigin;

    /// <summary>失去焦点是否自动退场：由设置项决定，关掉后便签常驻，直到按 Esc 或双击进编辑窗。</summary>
    private bool AutoCloseOnDeactivate => _settings.TranslateMiniWindowAutoClose;

    /// <summary>是否自动写入剪贴板：由设置项决定，关掉后译文不落地剪贴板，避免顶掉用户原有内容。</summary>
    private bool AutoCopyToClipboard => _settings.TranslateMiniWindowAutoCopy;

    public TranslateMiniWindow()
    {
        InitializeComponent();
        ApplyIcons();

        Closed += OnClosed;
        Opened += OnOpened;

        // 提示窗语义：默认一旦点到别处就退场，可在设置里关掉这个行为
        Activated += OnActivated;
        Deactivated += OnDeactivated;

        // 快捷键走隧道预览：焦点在可选中文字上时 Esc / Ctrl+C 也能收到
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel, true);
    }

    // ── 工厂入口 ──────────────────────────────────────────────

    /// <summary>
    /// 用已识别出的原文弹出极简浮窗（需 UI 线程调用）。
    /// 已有浮窗在场时复用它：换原文、换锚点、重新翻译并闪一下。
    /// </summary>
    /// <param name="sourceText">OCR 得到的完整原文。</param>
    /// <param name="settings">应用设置（决定主题与翻译配置）。</param>
    /// <param name="anchor">定位锚点：截图选区或贴图窗口的屏幕像素矩形。</param>
    public static TranslateMiniWindow ShowForText(string sourceText, AppSettings settings, PixelRect anchor)
    {
        if (_current is { } exist && !exist._closing)
        {
            exist.RefreshWith(sourceText, settings, anchor);
            return exist;
        }

        var win = new TranslateMiniWindow();
        _current = win;
        win._sourceText = sourceText ?? string.Empty;
        win.TxtSource.Text = win._sourceText;
        win.SetupCommon(settings, anchor);

        // 先隐身：Opened 里完成定位后再淡入，避免先闪在左上角
        win.Opacity = 0;
        win.Show();
        return win;
    }

    /// <summary>复用已有浮窗：只刷新内容、重定位与重译，保留窗口本体不重建。</summary>
    private void RefreshWith(string sourceText, AppSettings settings, PixelRect anchor)
    {
        _anchor = anchor;
        _followOrigin = NativeMethods.GetCursorPos(out var cp) ? new PixelPoint(cp.X, cp.Y) : null;
        _settings = settings;
        _config = settings.ToTranslationConfig();
        _engineName = TranslationFlow.GetEngineName(_config);

        _sourceText = sourceText ?? string.Empty;
        _targetText = string.Empty;
        _result = null;
        _retryCount = 0;
        TxtSource.Text = _sourceText;

        ApplyLanguagePair();
        UpdateEngineBadge();

        // 淡出再淡入：给一次"内容已刷新"的视觉确认
        Opacity = 0;
        DispatcherTimer.RunOnce(() => Opacity = 1, TimeSpan.FromMilliseconds(60));

        PositionNearAnchor();
        StartTranslation();
    }

    // ── 初始化 ────────────────────────────────────────────────

    private void SetupCommon(AppSettings settings, PixelRect anchor)
    {
        _settings = settings;
        _anchor = anchor;

        _config = settings.ToTranslationConfig();
        _engineName = TranslationFlow.GetEngineName(_config);

        ApplyLanguagePair();
        UpdateEngineBadge();
    }

    private void ApplyLanguagePair()
    {
        var (effSrc, effTgt, _, detected) = LanguageDetector.ResolveLanguages(
            _sourceText, _config.SourceLanguage, _config.TargetLanguage, _config.SecondaryTargetLanguage);
        _effectiveSourceLang = effSrc;
        _effectiveTargetLang = effTgt;

        if (_config.SourceLanguage == TranslationLanguage.Auto && detected != TranslationLanguage.Auto)
        {
            TxtSourceLang.Text = TranslationLanguage.GetDetectedLanguageDisplayName(detected) + "（检测）";
        }
        else
        {
            TxtSourceLang.Text = TranslationLanguage.GetDisplayName(_config.SourceLanguage);
        }
        TxtTargetLang.Text = TranslationLanguage.GetDisplayName(_effectiveTargetLang);
    }

    private void UpdateEngineBadge()
    {
        bool hasEngine = !string.IsNullOrEmpty(_engineName);
        EngineSep.IsVisible = hasEngine;
        TxtEngine.IsVisible = hasEngine;
        if (hasEngine) TxtEngine.Text = _engineName;
    }

    /// <summary>挂载单色 SVG 图标（几何集中在 TranslateIconGeometry，颜色由样式类承担）。</summary>
    private void ApplyIcons()
    {
        IconOpenEditor.Data = StreamGeometry.Parse(TranslateIconGeometry.OpenInNew);
        IconFooter.Data = StreamGeometry.Parse(TranslateIconGeometry.Check);
        IconWarn.Data = StreamGeometry.Parse(TranslateIconGeometry.Warn);
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        PositionNearAnchor();
        Opacity = 1;
        StartTranslation();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(_current, this)) _current = null;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    // ── 定位：选区正下方 → 空间不足翻上方 → 右侧超出贴边 ──────

    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 高度由内容决定（SizeToContent），展开原文等引起变化时重新贴回锚点；用户拖走过就不再插手
        if (_dragged) return;
        PositionNearAnchor();
    }

    private void PositionNearAnchor()
    {
        try
        {
            // 跟随鼠标优先用弹出时捕获的落点（尺寸变化重定位时不追鼠标）；缺失才现取一次并缓存
            _followOrigin ??= NativeMethods.GetCursorPos(out var pt0) ? new PixelPoint(pt0.X, pt0.Y) : null;
            var cursorPt = _followOrigin.GetValueOrDefault();
            bool hasCursor = _followOrigin.HasValue;
            var screen = PickScreen(hasCursor ? cursorPt : _anchor.Center);

            PixelRect area;
            if (screen != null)
            {
                area = screen.WorkingArea;
                if (area.Width <= 0 || area.Height <= 0) area = screen.Bounds;
            }
            else
            {
                area = new PixelRect(0, 0, 1920, 1080);
            }

            double scale = GetScreenScale(screen);
            double widthDip = Bounds.Width > 0 ? Bounds.Width : 444;
            double heightDip = Bounds.Height > 0 ? Bounds.Height : EstimatedHeightDip;

            int winW = (int)Math.Round(widthDip * scale);
            int winH = (int)Math.Round(heightDip * scale);
            int pad = (int)Math.Round(ShadowPadDip * scale);
            int gap = (int)Math.Round(12 * scale);

            // 只有"看得见的卡片"参与视觉排版，窗口尺寸多出的四周缓冲要在定位时扣掉
            int cardW = Math.Max(1, winW - pad * 2);
            int cardH = Math.Max(1, winH - pad * 2);

            int cx, cy;
            string posMode = AppSettings.NormalizeTranslateMiniWindowPosition(_settings.TranslateMiniWindowPosition);

            if (posMode == "CenterScreen")
            {
                cx = area.X + Math.Max(0, (area.Width - cardW) / 2);
                cy = area.Y + Math.Max(0, (area.Height - cardH) / 2);
            }
            else if (posMode == "BottomCenter")
            {
                // 屏幕中下位置：水平居中，贴近屏幕工作区底部
                int bottomMargin = (int)Math.Round(32 * scale);
                cx = area.X + Math.Max(0, (area.Width - cardW) / 2);
                cy = area.Y + Math.Max(0, area.Height - cardH - bottomMargin);
            }
            else if (hasCursor)
            {
                // 跟随鼠标：卡片左上角落在光标右下方；越界则翻到光标左侧 / 上方
                cx = cursorPt.X + gap;
                cy = cursorPt.Y + gap;
                if (cx + cardW > area.Right) cx = cursorPt.X - cardW - gap;
                if (cy + cardH > area.Bottom) cy = cursorPt.Y - cardH - gap;
            }
            else
            {
                // 兜底（拿不到光标）：贴截图选区下方，空间不足翻上方
                cx = _anchor.X;
                cy = _anchor.Y + _anchor.Height + gap;
                if (cy + cardH > area.Bottom) cy = _anchor.Y - cardH - gap;
            }

            // ③ 兜底：任何档位都不让卡片越界工作区
            int minCx = area.X + gap, minCy = area.Y + gap;
            cx = Math.Clamp(cx, minCx, Math.Max(minCx, area.Right - gap - cardW));
            cy = Math.Clamp(cy, minCy, Math.Max(minCy, area.Bottom - gap - cardH));

            Position = new PixelPoint(cx - pad, cy - pad);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TranslateMiniWindow.PositionNearAnchor 定位异常：" + ex.Message);
        }
    }

    /// <summary>
    /// 取锚点所在屏：优先包含该点的屏，取不到就按中心点距离找最近屏。
    /// 多屏负坐标扩展屏上 ScreenFromPoint 会返回 null，直接回落主屏会把浮窗甩到错误的屏。
    /// </summary>
    private Screen? PickScreen(PixelPoint center)
    {
        var screens = Screens.All;
        if (screens is not { Count: > 0 }) return Screens.Primary;

        foreach (var screen in screens)
        {
            if (screen.Bounds.Contains(center)) return screen;
        }

        return screens
            .OrderBy(s => CenterDistance(s.Bounds, center))
            .FirstOrDefault() ?? Screens.Primary;
    }

    private static long CenterDistance(PixelRect bounds, PixelPoint point)
    {
        long dx = (long)(bounds.X + bounds.Width / 2) - point.X;
        long dy = (long)(bounds.Y + bounds.Height / 2) - point.Y;
        return dx * dx + dy * dy;
    }

    private double GetScreenScale(Screen? screen)
    {
        if (screen != null && screen.Scaling > 0) return screen.Scaling;
        return DesktopScaling > 0 ? DesktopScaling : 1.0;
    }

    // ── 翻译执行 ──────────────────────────────────────────────

    private void StartTranslation()
    {
        if (!_config.IsValid)
        {
            _cts?.Cancel();
            ShowError("CONFIG_ERROR", "未配置翻译服务密钥，请在设置中配置");
            return;
        }
        _ = TranslateAsync(new TranslationService(_config));
    }

    /// <summary>界面线程冻结本次原文与语言；异步结果只更新仍有效的这一次请求。</summary>
    internal Task TranslateAsync(TranslationService translator)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string source = _sourceText, sourceLang = _effectiveSourceLang, targetLang = _effectiveTargetLang, engine = _engineName;
        TxtSource.Text = source;
        _streaming = _settings.TranslateStreamEnabled;
        bool streaming = _streaming;
        _streamSpans = streaming
            ? translator.SupportsDocumentStream ? TranslationService.SplitSentences(source) : new[] { new TranslationSourceSpan(0, source.Length) }
            : Array.Empty<TranslationSourceSpan>();
        _translating = true;
        _targetText = string.Empty;
        _result = null;
        _streamCompleted = 0;
        PrepareStreamRows(_streamSpans.Select(span => source.Substring(span.Start, span.Length)).ToArray());
        SetLoading();
        return Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var r = streaming
                    ? await translator.TranslateTextStreamAsync(source, sourceLang, targetLang, async snapshot =>
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (!ct.IsCancellationRequested && !_closing) ShowDocumentStream(source, snapshot);
                        });
                    }, ct).ConfigureAwait(false)
                    : await translator.TranslateDocumentAsync(source, sourceLang, targetLang, null, ct).ConfigureAwait(false);
                sw.Stop();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || _closing) return;
                    _translating = false;
                    BtnOpenEditor.IsEnabled = true;
                    if (r.Success)
                    {
                        UsageStatistics.RecordTranslate(); // 统计：翻译成功一次
                        _targetText = r.TranslatedText ?? string.Empty;
                        _detectedSourceLang = r.DetectedSourceLanguage;
                        _result = TranslationResult.Ok(source, _targetText, sourceLang,
                            targetLang, _detectedSourceLang, engine, sw.Elapsed.TotalSeconds, r.Pairs);
                        ShowResult();
                    }
                    else
                    {
                        _targetText = _streamText.ToString();
                        _result = TranslationResult.Fail(source, sourceLang,
                            targetLang, engine, r.ErrorCode ?? "ERROR",
                            r.ErrorMessage ?? "翻译失败");
                        _result.TargetText = _targetText;
                        ShowError(_result.ErrorCode!, _result.ErrorMessage!);
                    }
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AppLogger.Error("TranslateMiniWindow 翻译异常", ex);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested || _closing) return;
                    _result = TranslationResult.Fail(source, sourceLang, targetLang, engine, "EXCEPTION", ex.Message);
                    _result.TargetText = _streamText.ToString();
                    ShowError("EXCEPTION", ex.Message);
                });
            }
        });
    }

    private void PrepareStreamRows(IReadOnlyList<string> segments)
    {
        StreamPairs.Children.Clear();
        StreamScroll.Offset = default;
        _streamTargets.Clear();
        _streamTexts.Clear();
        _streamText.Clear();
        foreach (string source in segments)
        {
            var original = new SelectableTextBlock { Text = source, Classes = { "miniStreamSource" } };
            var target = new SelectableTextBlock { Text = "等待翻译…", Classes = { "miniStreamTarget" } };
            foreach (var text in new[] { original, target })
            {
                var selected = new MenuItem { Header = "复制选中内容" };
                selected.Click += (_, _) => { if (!string.IsNullOrEmpty(text.SelectedText)) ClipboardService.SetText(text.SelectedText); };
                var full = new MenuItem { Header = ReferenceEquals(text, original) ? "复制完整原文" : "复制完整译文" };
                full.Click += (_, _) => { if (ReferenceEquals(text, original)) CopySource(); else CopyTarget(); };
                text.ContextMenu = new ContextMenu { Items = { selected, full } };
                _streamTexts.Add(text);
            }
            _streamTargets.Add(target);
            StreamPairs.Children.Add(new StackPanel { Spacing = 2, Children = { original, target } });
        }
    }

    private void ShowDocumentStream(string source, TextTranslationResult snapshot)
    {
        // 句框只负责本地排版，原文始终整篇请求；按响应范围定位，不能把左右行号硬配对。
        if (snapshot.Pairs.Count > 0)
        {
            if (snapshot.Pairs.Any(pair => !_streamSpans.Any(span => span.Start == pair.SourceStart && span.Length == pair.SourceLength)))
            {
                _streamSpans = snapshot.Pairs.Select(pair => new TranslationSourceSpan(pair.SourceStart, pair.SourceLength)).ToArray();
                PrepareStreamRows(_streamSpans.Select(span => source.Substring(span.Start, span.Length)).ToArray());
            }
            foreach (var pair in snapshot.Pairs)
            {
                int index = Enumerable.Range(0, _streamSpans.Count).First(i => _streamSpans[i].Start == pair.SourceStart);
                _streamTargets[index].Text = snapshot.TranslatedText!.Substring(pair.TargetStart, pair.TargetLength);
            }
            _streamCompleted = snapshot.Pairs.Count;
        }
        else if (_streamTargets.Count > 0)
        {
            _streamTargets[0].Text = snapshot.TranslatedText;
            _streamCompleted = 1;
        }
        _streamText.Clear(); _streamText.Append(snapshot.TranslatedText);
        UpdateFooter();
    }

    // ── 三态切换：加载 / 结果 / 错误 ───────────────────────────

    private void SetLoading()
    {
        StreamScroll.MaxHeight = 190;
        SkeletonPanel.IsVisible = !_streaming;
        StreamScroll.IsVisible = _streaming;
        TxtSource.IsVisible = SourceDivider.IsVisible = !_streaming;
        BtnOpenEditor.IsEnabled = !_streaming;
        TxtTarget.IsVisible = false;
        ErrorPanel.IsVisible = false;
        _copied = false;
        _copiedLabel = "译文";
        UpdateFooter();
    }

    private void ShowResult()
    {
        SkeletonPanel.IsVisible = false;
        ErrorPanel.IsVisible = false;
        TxtTarget.IsVisible = !_streaming;
        TxtTarget.Text = _targetText;

        // 提示窗主路径：结果出来即入库剪贴板，用户不用开窗就能粘贴（设置项可关，以免顶掉原有内容）
        if (AutoCopyToClipboard && !string.IsNullOrEmpty(_targetText))
        {
            ClipboardService.SetText(_targetText);
            _copied = true;
        }
        UpdateFooter();

        if (_detectedSourceLang != null && _config.SourceLanguage == TranslationLanguage.Auto)
        {
            TxtSourceLang.Text = TranslationLanguage.GetDetectedLanguageDisplayName(_detectedSourceLang) + "（检测）";
        }
        TxtTargetLang.Text = TranslationLanguage.GetDisplayName(_effectiveTargetLang);
    }

    private void ShowError(string errorCode, string message)
    {
        _translating = false;
        BtnOpenEditor.IsEnabled = true;
        if (_streaming)
        {
            // 保留错误与重试的位置，避免长篇滚动区把页脚挤出 340 DIP 的浮窗上限。
            StreamScroll.MaxHeight = 140;
            _targetText = _streamText.ToString();
            for (int i = _streamCompleted; i < _streamTargets.Count; i++) _streamTargets[i].Text = "尚未翻译";
        }
        SkeletonPanel.IsVisible = false;
        TxtTarget.IsVisible = false;
        ErrorPanel.IsVisible = true;
        TxtError.Text = _retryCount == 0
            ? $"翻译失败 · {message}"
            : $"第 {_retryCount} 次重试仍失败 · {message}";
        TxtError.MaxLines = _streaming ? 2 : 0;
        TxtError.TextTrimming = _streaming ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        if (_streaming) ToolTip.SetTip(TxtError, message);
        _copied = false;
        UpdateFooter();
        AppLogger.Warn($"TranslateMiniWindow 翻译失败 [{errorCode}]：{message}");
    }

    /// <summary>页脚只报当前真正成立的几件事：译文是否已复制、会不会失焦退场、怎么进编辑窗。</summary>
    private void UpdateFooter()
    {
        string hint = (AutoCloseOnDeactivate ? "点击别处自动关闭 · " : "") + "双击打开编辑窗";
        TxtFooter.Text = _streaming && _translating
            ? $"翻译中 · {_streamCompleted}/{_streamTargets.Count} 段" : _copied ? $"{_copiedLabel}已复制 · {hint}" : hint;
        IconFooter.IsVisible = _copied;
    }

    private void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        _cts?.Cancel();

        Opacity = 0;
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(220));
    }

    // ── 事件处理 ──────────────────────────────────────────────

    private void OnRootPointerEntered(object? sender, PointerEventArgs e)
    {
        _hover = true;
    }

    private void OnRootPointerExited(object? sender, PointerEventArgs e)
    {
        _hover = false;
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // 双击空白处（含顶部 meta 行）即升级到编辑窗；落在可选中文字上时不抢，留给双击选词
        if (e.ClickCount == 2 && e.Source is not SelectableTextBlock)
        {
            e.Handled = true;
            OpenEditor();
            return;
        }

        try
        {
            BeginMoveDrag(e);
            _dragged = true;
        }
        catch { }
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        _everActivated = true;
    }

    /// <summary>失焦 200ms 宽限后退出：给右键菜单、拖选文字这类"焦点短暂离开自身"留出缓冲；设置里关掉自动关闭则常驻。</summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 从未拿到过焦点就收到的 Deactivated，是上一个窗口（截图遮罩）收尾时的抢焦点，不能当"用户点开了别处"
        if (!AutoCloseOnDeactivate || _closing || !_everActivated) return;

        DispatcherTimer.RunOnce(() =>
        {
            if (!AutoCloseOnDeactivate || _closing || _hover) return;
            if (IsActive) return;
            if (IsMenuOpen()) return;
            FadeOutAndClose();
        }, TimeSpan.FromMilliseconds(200));
    }

    /// <summary>弹出式菜单挂在本窗口上时，失焦是菜单抢焦点而非用户离开，不能收场。</summary>
    private bool IsMenuOpen()
    {
        return TxtSource.ContextMenu?.IsOpen == true
            || TxtTarget.ContextMenu?.IsOpen == true || _streamTexts.Any(text => text.ContextMenu?.IsOpen == true);
    }

    private void OnRetryClick(object? sender, RoutedEventArgs e)
    {
        _retryCount++;
        StartTranslation();
    }

    // ── 复制与右键菜单事件 ────────────────────────────────────

    private void OnCopySourceClick(object? sender, RoutedEventArgs e)
    {
        CopySource();
    }

    /// <summary>菜单项：无视当前选区，始终复制整篇译文。</summary>
    private void OnCopyFullTargetClick(object? sender, RoutedEventArgs e)
    {
        CopyTarget();
    }

    private void OnCopySelectedSourceClick(object? sender, RoutedEventArgs e)
    {
        string sel = TxtSource.SelectedText;
        if (!string.IsNullOrEmpty(sel))
        {
            ClipboardService.SetText(sel);
            ShowCopiedFeedback("原文");
        }
        else
        {
            CopySource();
        }
    }

    private void OnCopySelectedTargetClick(object? sender, RoutedEventArgs e)
    {
        string sel = TxtTarget.SelectedText;
        if (!string.IsNullOrEmpty(sel))
        {
            ClipboardService.SetText(sel);
            ShowCopiedFeedback("译文");
        }
        else
        {
            CopyTarget();
        }
    }

    private void CopyTarget()
    {
        if (_streaming && _translating) _targetText = _streamText.ToString();
        if (string.IsNullOrEmpty(_targetText)) return;
        ClipboardService.SetText(_targetText);
        ShowCopiedFeedback("译文");
    }

    private void CopySource()
    {
        if (string.IsNullOrEmpty(_sourceText)) return;
        ClipboardService.SetText(_sourceText);
        ShowCopiedFeedback("原文");
    }

    /// <summary>复制反馈：浮窗没有按钮可换图标，反馈只能落在这行页脚上。</summary>
    private void ShowCopiedFeedback(string label)
    {
        _copiedLabel = label;
        _copied = true;
        UpdateFooter();
    }

    /// <summary>图标按钮：升级到编辑主窗口。</summary>
    private void OnOpenEditorClick(object? sender, RoutedEventArgs e)
    {
        OpenEditor();
    }

    /// <summary>升级到编辑主窗口：携带同一份结果，浮窗随即关闭。图标按钮与双击空白处共用。</summary>
    private void OpenEditor()
    {
        if (_closing || (_streaming && _translating)) return;

        var result = _result ?? new TranslationResult
        {
            SourceText = _sourceText,
            TargetText = _targetText,
            SourceLanguage = _config.SourceLanguage,
            TargetLanguage = _config.TargetLanguage,
            DetectedSourceLanguage = _detectedSourceLang,
            Engine = _engineName
        };

        _closing = true;
        _cts?.Cancel();

        var editor = TranslationResultWindow.FromResult(result, _settings);
        editor.Show();
        editor.Activate();
        Close();
    }

    /// <summary>快捷键：Esc 关闭 / Ctrl+C 复制译文 / Ctrl+Shift+C 复制原文。</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = (e.KeyModifiers & KeyModifiers.Control) != 0;
        bool shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FadeOutAndClose();
            return;
        }

        if (e.Key == Key.C && ctrl)
        {
            e.Handled = true;
            if (shift)
            {
                CopySource();
            }
            else if (!string.IsNullOrEmpty(TxtSource.SelectedText))
            {
                ClipboardService.SetText(TxtSource.SelectedText);
                ShowCopiedFeedback("原文");
            }
            else if (!string.IsNullOrEmpty(TxtTarget.SelectedText))
            {
                ClipboardService.SetText(TxtTarget.SelectedText);
                ShowCopiedFeedback("译文");
            }
            else
            {
                CopyTarget();
            }
        }
    }
}
