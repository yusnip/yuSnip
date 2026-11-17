using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Ai;
using ScreenCaptureTool.Core.CloudSync;
using ScreenCaptureTool.Core.Deps;
using ScreenCaptureTool.Core.History;
using ScreenCaptureTool.Core.Translation;
using ScreenCaptureTool.Controls;

namespace ScreenCaptureTool.Windows;

/// <summary>设置窗口分页枚举。</summary>
public enum SettingsPage
{
    Basic,
    Interact,
    Annot,
    Actions,
    Translate,
    Ai,
    CloudSync,
    // 分页独立显示，懒加载按目标页匹配，不依赖枚举的声明顺序。
    Deps,
    Toolbar,
    Stats,
    About,
    // 新页追加以保持已有分页的枚举值不变；导航位置由 XAML 决定。
    Overview,
}

/// <summary>
/// 设置窗口：独立分页、即时搜索与自动保存，读写 <see cref="AppSettings"/>。
/// 支持热键录制并在保存后通知宿主重新注册全局热键。
/// </summary>
public partial class SettingsWindow : Window
{
    // Win32 修饰键位
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private AppSettings _settings = new AppSettings();
    private bool _isNavigating;
    private SettingsPage _activePage = SettingsPage.Overview;
    private readonly Dictionary<SettingsPage, double> _pageOffsets = new();
    private int _pageChangeVersion;
    private bool _isLoading;
    private bool _isTogglingAdminMode;
    /// <summary>上一次自动保存时的开机启动值：仅该值变化时才走自启协调器（避免每次保存都拉起 schtasks 子进程）。</summary>
    private bool _lastAutosaveRunOnStartup;

    // —— 分页按需懒加载标志：仅在滚动或切换到对应分页时才实例化复杂控件 ——
    private bool _actionsLoaded;
    private bool _aiLoaded;
    private bool _toolbarLoaded;
    private bool _statsLoaded;

    // —— 快捷键与动作页状态 ——
    // 列表直接在内存中维护，发生变动时实时落盘并通知宿主重新注册热键
    private List<ScreenCaptureTool.Core.Actions.ActionDefinition> _actionsDraft = new();

    // 内置快捷键（截图/贴图窗口内按键）列表与录制状态
    private List<Services.BuiltinShortcutsStore.BuiltinShortcut> _builtinShortcutsDraft = new();
    private Services.BuiltinShortcutsStore.BuiltinShortcut? _recordingBuiltin;

    private GoogleDriveSyncService? _gdriveService;
    private bool _cloudSyncBusy;
    internal bool HasManualCloudOperation => _cloudSyncBusy;
    private Services.CloudAutoSyncService? _automaticCloud;
    private CloudSyncReport? _shownCloudReport;
    private bool _gdriveConnected;

    // —— 浮动通知与概览摘要 ——
    private readonly bool _runtimeEnabled;
    private bool _overviewSubscribed;
    private bool _overviewClosed;
    private DispatcherTimer? _overviewClock;
    private CancellationTokenSource? _overviewUpdateCts;
    private Core.Update.UpdateInfo? _overviewUpdateInfo;
    private bool _overviewUpdateAttempted;
    private bool _overviewUpdateBusy;
    // CTS 取消上一条的淡出计时；世代号用于 await 之后判定"这条延时是否已被新提示取代"，
    // 防止过期的延时把更新的消息清空（用 DispatcherTimer 则每条退出路径都要手工 Stop）
    private CancellationTokenSource? _statusCts;
    private int _statusGen;

    // 自定义工具栏分页：布局（token 列表，"|" 为分隔线）与拖拽状态
    private List<string> _toolbarDraft = new List<string>();
    private bool _toolbarDragging;
    private int _toolbarDragFromIndex = -1;
    private string? _toolbarDragPoolToken;
    private readonly ListReorderBehavior _toolbarDrag = new(6, manhattan: true);
    private Control? _toolbarDragSourceItem;
    private Avalonia.Controls.Shapes.Rectangle? _toolbarDropPlaceholder;

    /// <summary>动作表发生修改后触发，宿主据此全量重注册动作热键。</summary>
    public event EventHandler? ActionsChanged;

    private readonly SettingsPage? _initialPage;

    public SettingsWindow(bool initialNavigateToStats = false, SettingsPage? initialPage = null)
        : this(initialNavigateToStats, initialPage, initializeRuntime: true)
    {
    }

    /// <summary>离屏验收使用真实窗口与导航，但不读写配置、不连接账户、不启动后台服务。</summary>
    internal SettingsWindow(bool initialNavigateToStats, SettingsPage? initialPage, bool initializeRuntime)
    {
        _runtimeEnabled = initializeRuntime;
        _initialPage = initialPage ?? (initialNavigateToStats ? SettingsPage.Stats : null);
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnWeatherSearchFollowupPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnWeatherSearchFollowupReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => ResetWeatherSearchClick();
        Classes.Set("reduceMotion", !SystemMotionEnabled());
        if (!initializeRuntime)
        {
            _isLoading = true;
            SetNavActiveOnly(_initialPage ?? SettingsPage.Overview);
            BuildSearchIndex();
            AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            return;
        }
        _toolbarDrag.Canceled += ResetToolbarDrag;

        // 1. 尺寸自适应提前执行：在 Native HWND 建立前锁定最终尺寸与居中位置，
        // 彻底消除 Show 之后的窗口二次跳变与重新排版。
        FitWindowToScreen();

        // 2. 数据与图标预载：在 Show 之前完成 UI 控件赋值，首帧绘制即呈现完整内容。
        // 色板须先建好，LoadSettingsToUi 里的 UpdateSelectionBorderColorUi 才有对象可挂选中态
        BuildSelectionColorChips();
        LoadSettingsToUi();
        UpdateAppIcons();

        Opened += OnWindowOpened;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty || e.Property == IsVisibleProperty) UpdateOverviewMotion();
        };
        Classes.CollectionChanged += (_, _) => UpdateOverviewMotion();
        Activated += (_, _) =>
        {
            Classes.Set("reduceMotion", !SystemMotionEnabled());
            if (_activePage == SettingsPage.Overview) RefreshOverviewUi();
        };
        Closed += OnWindowClosed;

        SetupAutoSaveEvents();
        _automaticCloud = (Application.Current as App)?.AutoCloudSync;
        if (_automaticCloud != null) _automaticCloud.Changed += UpdateAutomaticCloudUi;
        UpdateAutomaticCloudUi();

        // 大数值仪表盘：滑块值变化 → 同步大数值显示并实时保存
        NsdElementDwellMs.ValueChanged += (_, _) =>
        {
            SyncDwellMsUi();
            AutoSaveSettings();
        };
        NsdElementDwellDeadband.ValueChanged += (_, _) =>
        {
            SyncDwellDeadbandUi();
            AutoSaveSettings();
        };

        // 点击参数面板内非输入框区域（滑块/文字等不可聚焦控件）时主动收起输入焦点，
        // 否则 TextBox 焦点不会转移，看起来一直处于输入状态
        PnlElementDwell.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not TextBox)
                TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // 同上思路的全窗口版本：点击任意非输入控件区域时释放焦点，搜索栏底线随之从聚焦紫线
        // 退回发丝灰线（Avalonia 点击 TextBlock/ContinuousBorder 等不可聚焦控件不会转移 TextBox 焦点）；
        // 点击命令浮层内部（结果行选择/滚动）时保留焦点，避免跳转前输入框提前失焦变灰
        AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is TextBox) return;
            if (IsSourceInSearchPopup(e.Source)) return;
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        // 录制热键时在窗口级别捕获按键
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        
        BuildSearchIndex();

        this.PropertyChanged += (s, e) =>
        {
            if (e.Property == ActualThemeVariantProperty)
            {
                UpdateAppIcons();
                if (_actionsLoaded)
                {
                    RenderBuiltinShortcutList();
                    RenderActionList();
                }
            }
        };
    }

    /// <summary>主题感知取色：显式携带当前主题变体解析；失败时按深浅色取兜底色。</summary>
    private IBrush ThemeBrush(string key, string lightHex, string darkHex)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
            return brush;
        bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        return new SolidColorBrush(Color.Parse(isDark ? darkHex : lightHex));
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (_initialPage.HasValue)
        {
            NavigateToPage(_initialPage.Value);
        }
        if (_activePage == SettingsPage.Overview) RefreshOverviewUi();
        UpdateOverviewMotion();
    }

    private static Avalonia.Controls.WindowIcon? _cachedDarkIcon;
    private static Avalonia.Controls.WindowIcon? _cachedLightIcon;
    private static Avalonia.Media.Imaging.Bitmap? _cachedDarkBitmap;
    private static Avalonia.Media.Imaging.Bitmap? _cachedLightBitmap;

    private void UpdateAppIcons()
    {
        try
        {
            bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            if (isDark)
            {
                _cachedDarkIcon ??= new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/Black.ico")));
                _cachedDarkBitmap ??= new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/Black.ico")));
                this.Icon = _cachedDarkIcon;
                if (ImgSidebarLogo != null) ImgSidebarLogo.Source = _cachedDarkBitmap;
                if (ImgOverviewLogo != null) ImgOverviewLogo.Source = _cachedDarkBitmap;
                if (ImgAppLogo != null)
                {
                    ImgAppLogo.Source = _cachedDarkBitmap;
                }
            }
            else
            {
                _cachedLightIcon ??= new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/White.ico")));
                _cachedLightBitmap ??= new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri("avares://yuSnip/Assets/White.ico")));
                this.Icon = _cachedLightIcon;
                if (ImgSidebarLogo != null) ImgSidebarLogo.Source = _cachedLightBitmap;
                if (ImgOverviewLogo != null) ImgOverviewLogo.Source = _cachedLightBitmap;
                if (ImgAppLogo != null)
                {
                    ImgAppLogo.Source = _cachedLightBitmap;
                }
            }

            if (_toolbarLoaded && _toolbarDraft.Count > 0 && ToolbarButtonPool != null)
            {
                RenderToolbarPage();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.UpdateAppIcons 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 按当前屏幕工作区（DIP）钳制窗口尺寸，避免在小屏 + 高 DPI 缩放下窗口超出屏幕、按钮被遮挡。
    /// Avalonia 用 DIP 作单位，这里把物理工作区换算回 DIP 后再限制宽高。
    /// </summary>
    private void FitWindowToScreen()
    {
        try
        {
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen == null) return;

            PixelRect area = screen.WorkingArea;
            if (area.Width <= 0 || area.Height <= 0) area = screen.Bounds;

            double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
            double availableWidthDip = area.Width / scale;
            double availableHeightDip = area.Height / scale;

            // 留出窗口外边距（ContinuousBorder Margin=12）和一点安全余量
            double maxWidthDip = Math.Max(MinWidth, availableWidthDip - 24);
            double maxHeightDip = Math.Max(MinHeight, availableHeightDip - 24);

            double targetWidth = Math.Min(Width, maxWidthDip);
            double targetHeight = Math.Min(Height, maxHeightDip);

            bool changed = false;
            if (Math.Abs(targetWidth - Width) > 0.5) { Width = targetWidth; changed = true; }
            if (Math.Abs(targetHeight - Height) > 0.5) { Height = targetHeight; changed = true; }

            // 尺寸被钳制后重新居中到工作区，避免偏出屏幕
            if (changed)
            {
                int pixelW = (int)Math.Round(targetWidth * scale);
                int pixelH = (int)Math.Round(targetHeight * scale);
                int x = area.X + Math.Max(0, (area.Width - pixelW) / 2);
                int y = area.Y + Math.Max(0, (area.Height - pixelH) / 2);
                Position = new PixelPoint(x, y);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.FitWindowToScreen 失败：" + ex.Message);
        }
    }

    /// <summary>三态分段按钮（关闭 / 开启-按住 Alt / 始终显示）回填，参考线与十字准星共用一份实现。</summary>
    private static void FillTriStateMode(RadioButton? off, RadioButton? holdAlt, RadioButton? always, string mode)
    {
        if (off == null) return;
        string m = AppSettings.NormalizeGuidelineMode(mode);
        off.IsChecked = m == AppSettings.GuidelineModeOff;
        if (holdAlt != null) holdAlt.IsChecked = m == AppSettings.GuidelineModeHoldAlt;
        if (always != null) always.IsChecked = m == AppSettings.GuidelineModeAlways;
    }

    /// <summary>三态分段按钮读值；三项都未选中（尚未回填）时保留原值不写坏配置。</summary>
    private static string ReadTriStateMode(RadioButton? off, RadioButton? holdAlt, RadioButton? always, string current)
    {
        if (off?.IsChecked == true) return AppSettings.GuidelineModeOff;
        if (always?.IsChecked == true) return AppSettings.GuidelineModeAlways;
        if (holdAlt?.IsChecked == true) return AppSettings.GuidelineModeHoldAlt;
        return current;
    }

    private void LoadSettingsToUi()
    {
        ReloadAllFromDisk();

        HideStatus();
        SetNavActiveOnly(_initialPage ?? SettingsPage.Overview);
    }

    private void ApplySettingsToUi(AppSettings s)
    {
        TxtSaveDirectory.Text = s.SaveDirectory ?? string.Empty;
        TxtFileNameTemplate.Text = s.FileNameTemplate ?? "Screenshot_{yyyyMMdd_HHmmss}";

        string themeMode = s.ThemeMode ?? "跟随系统";
        RadThemeLight.IsChecked = themeMode == "浅色";
        RadThemeDark.IsChecked = themeMode == "暗色";
        RadThemeSystem.IsChecked = themeMode != "浅色" && themeMode != "暗色";

        RadCopyToClipboardOn.IsChecked = s.CopyToClipboardAfterSave;
        RadCopyToClipboardOff.IsChecked = !s.CopyToClipboardAfterSave;

        // 计划任务接管时 Run 键刻意不存在；界面显示持久化意图，不能反向覆盖配置。
        bool runOnStartup = s.RunOnStartup;
        RadRunOnStartupOn.IsChecked = runOnStartup;
        RadRunOnStartupOff.IsChecked = !runOnStartup;
        _lastAutosaveRunOnStartup = s.RunOnStartup;

        RadAdminModeOn.IsChecked = s.RunAsAdmin;
        RadAdminModeOff.IsChecked = !s.RunAsAdmin;

        RefreshElevationUi();

        RadShowSaveNotificationOn.IsChecked = s.ShowSaveNotification;
        RadShowSaveNotificationOff.IsChecked = !s.ShowSaveNotification;

        RadShowStartupNotificationOn.IsChecked = s.ShowStartupNotification;
        RadShowStartupNotificationOff.IsChecked = !s.ShowStartupNotification;

        TxtAboutVersion.Text = "v" + Core.Update.UpdateService.CurrentVersionStr;
        RadCheckUpdateOn.IsChecked = s.CheckUpdateOnStartup;
        RadCheckUpdateOff.IsChecked = !s.CheckUpdateOnStartup;

        if (_statsLoaded)
        {
            RefreshUsageStatsUi();
        }

        RadPlaySaveSoundOn.IsChecked = s.PlaySaveSound;
        RadPlaySaveSoundOff.IsChecked = !s.PlaySaveSound;

        string autoMode = AppSettings.NormalizeAutoDetectMode(s.AutoDetectMode);
        RadAutoDetectElement.IsChecked = autoMode == "检测元素";
        RadAutoDetectWindow.IsChecked = autoMode == "仅窗口";
        RadAutoDetectNone.IsChecked = autoMode == "不检测";

        // 「检测元素」悬停检测参数回显（值未变时 ValueChanged 不触发，需显式同步大数值显示）
        PnlElementDwell.IsVisible = autoMode == "检测元素";
        NsdElementDwellMs.Value = Math.Clamp(s.ElementDwellMs, 0, 100);
        NsdElementDwellDeadband.Value = Math.Clamp(s.ElementDwellDeadbandPx, 0, 100);
        SyncDwellMsUi();
        SyncDwellDeadbandUi();

        string dblClickAction = AppSettings.NormalizeStickerDoubleClickAction(s.StickerDoubleClickAction);
        if (RadStickerDblClickClose != null) RadStickerDblClickClose.IsChecked = dblClickAction == "Close";
        if (RadStickerDblClickThumbnail != null) RadStickerDblClickThumbnail.IsChecked = dblClickAction == "Thumbnail";
        if (RadStickerDblClickCopy != null) RadStickerDblClickCopy.IsChecked = dblClickAction == "Copy";
        if (RadStickerDblClickNone != null) RadStickerDblClickNone.IsChecked = dblClickAction == "None";

        if (RadStickerDefaultShadowOn != null)
        {
            // 三档回填（2026-10-01，issue #50）：真源是 StickerShadowMode，布尔项只是兼容镜像
            string shadowMode = AppSettings.NormalizeStickerShadowMode(s.StickerShadowMode);
            RadStickerDefaultShadowOff.IsChecked = shadowMode == "Off";
            RadStickerShadowAuto.IsChecked = shadowMode == "Auto";
            RadStickerDefaultShadowOn.IsChecked = shadowMode == "On";
        }
        if (RadStickerCheckerboardOn != null)
        {
            RadStickerCheckerboardOn.IsChecked = s.StickerTransparentCheckerboard;
            RadStickerCheckerboardOff.IsChecked = !s.StickerTransparentCheckerboard;
        }
        if (RadStickerTextSelectableOn != null)
        {
            RadStickerTextSelectableOn.IsChecked = s.StickerTextSelectable;
            RadStickerTextSelectableOff.IsChecked = !s.StickerTextSelectable;
        }
        if (RadStickerSelectionToolbarOn != null)
        {
            RadStickerSelectionToolbarOn.IsChecked = s.StickerSelectionToolbar;
            RadStickerSelectionToolbarOff.IsChecked = !s.StickerSelectionToolbar;
        }
        if (RadStickerGrabSelectedTextOn != null)
        {
            RadStickerGrabSelectedTextOn.IsChecked = s.StickerGrabSelectedText;
            RadStickerGrabSelectedTextOff.IsChecked = !s.StickerGrabSelectedText;
        }
        if (RadStickerResizeReflowsTextOn != null)
        {
            RadStickerResizeReflowsTextOn.IsChecked = s.StickerResizeReflowsText;
            RadStickerResizeReflowsTextOff.IsChecked = !s.StickerResizeReflowsText;
        }
        if (RadStickerColorPickerOn != null)
        {
            RadStickerColorPickerOn.IsChecked = s.StickerColorPickerEnabled;
            RadStickerColorPickerOff.IsChecked = !s.StickerColorPickerEnabled;
        }
        bool stickerRestoreByCreated = string.Equals(s.StickerRestoreSortMode, "Created", StringComparison.OrdinalIgnoreCase);
        if (RadStickerRestoreByCreated != null)
        {
            RadStickerRestoreByCreated.IsChecked = stickerRestoreByCreated;
            RadStickerRestoreByClosed.IsChecked = !stickerRestoreByCreated;
        }
        if (RadShowLongScrollPreviewOn != null)
        {
            RadShowLongScrollPreviewOn.IsChecked = s.ShowLongScrollPreview;
            RadShowLongScrollPreviewOff.IsChecked = !s.ShowLongScrollPreview;
        }
        FillTriStateMode(RadGuidelineModeOff, RadGuidelineModeHoldAlt, RadGuidelineModeAlways, s.GuidelineMode);
        FillTriStateMode(RadCrosshairModeOff, RadCrosshairModeHoldAlt, RadCrosshairModeAlways, s.CrosshairMode);
        if (RadShowCaptureHintsOn != null)
        {
            RadShowCaptureHintsOn.IsChecked = s.ShowCaptureHints;
            RadShowCaptureHintsOff.IsChecked = !s.ShowCaptureHints;
        }
        if (RadReuseLastAnnotationToolOn != null)
        {
            RadReuseLastAnnotationToolOn.IsChecked = s.ReuseLastAnnotationTool;
            RadReuseLastAnnotationToolOff.IsChecked = !s.ReuseLastAnnotationTool;
        }
        if (RadCounterRenumberOn != null)
        {
            RadCounterRenumberOn.IsChecked = s.CounterRenumberOnDelete;
            RadCounterRenumberOff.IsChecked = !s.CounterRenumberOnDelete;
        }
        if (RadCounterRememberOn != null)
        {
            RadCounterRememberOn.IsChecked = s.CounterRememberStyle;
            RadCounterRememberOff.IsChecked = !s.CounterRememberStyle;
        }
        if (RadMattingModelGeneral != null && RadMattingModelPortrait != null)
        {
            string mattingModel = AppSettings.NormalizeMattingDefaultModel(s.MattingDefaultModel);
            RadMattingModelGeneral.IsChecked = mattingModel == AppSettings.MattingModelGeneral;
            RadMattingModelPortrait.IsChecked = mattingModel == AppSettings.MattingModelPortrait;
        }
        if (RadMattingResultClipboard != null && RadMattingResultSticker != null && RadMattingResultBoth != null)
        {
            string mattingResult = AppSettings.NormalizeMattingResult(s.MattingResultDestination);
            RadMattingResultClipboard.IsChecked = mattingResult == AppSettings.MattingResultClipboard;
            RadMattingResultSticker.IsChecked = mattingResult == AppSettings.MattingResultSticker;
            RadMattingResultBoth.IsChecked = mattingResult == AppSettings.MattingResultBoth;
        }

        // 截图历史
        if (RadHistoryOff != null && RadHistoryOn != null)
        {
            string histMode = AppSettings.NormalizeCaptureHistoryMode(s.CaptureHistoryMode);
            RadHistoryOff.IsChecked = histMode == "Off";
            RadHistoryOn.IsChecked = histMode == "On";
        }
        RadCaptureAutoOcrOn.IsChecked = s.CaptureAutoOcr;
        RadCaptureAutoOcrOff.IsChecked = !s.CaptureAutoOcr;
        RefreshHistoryUsageUi();

        // 翻译设置
        string trProvider = AppSettings.NormalizeTranslateProvider(s.TranslateProvider);
        RadTranslateNone.IsChecked = trProvider == "None";
        RadTranslateIndex.IsChecked = trProvider == "BilibiliIndex";
        RadTranslateBaidu.IsChecked = trProvider == "Baidu";
        RadTranslateYoudao.IsChecked = trProvider == "Youdao";
        TxtBaiduAppId.Text = s.BaiduAppId ?? "";
        TxtBaiduSecretKey.Text = s.BaiduSecretKey ?? "";
        TxtYoudaoAppId.Text = s.YoudaoAppId ?? "";
        TxtYoudaoSecretKey.Text = s.YoudaoSecretKey ?? "";

        string trMode = s.ToTranslationConfig().Mode == TranslationMode.ImageToImage ? "ImageToImage" : "ImageToText";
        RadTranslateImageToText.IsChecked = trMode == "ImageToText";
        RadTranslateImageToImage.IsChecked = trMode == "ImageToImage";
        RadTranslateStreamOn.IsChecked = s.TranslateStreamEnabled;
        RadTranslateStreamOff.IsChecked = !s.TranslateStreamEnabled;

        string miniPos = AppSettings.NormalizeTranslateMiniWindowPosition(s.TranslateMiniWindowPosition);
        RadTranslateMiniPosFollow.IsChecked = miniPos == "FollowSelection";
        RadTranslateMiniPosCenter.IsChecked = miniPos == "CenterScreen";
        RadTranslateMiniPosBottomCenter.IsChecked = miniPos == "BottomCenter";

        RadTranslateMiniAutoCloseOn.IsChecked = s.TranslateMiniWindowAutoClose;
        RadTranslateMiniAutoCloseOff.IsChecked = !s.TranslateMiniWindowAutoClose;

        RadTranslateMiniCopyOn.IsChecked = s.TranslateMiniWindowAutoCopy;
        RadTranslateMiniCopyOff.IsChecked = !s.TranslateMiniWindowAutoCopy;

        // OCR 结果输出方式与提示窗位置
        bool ocrSilent = string.Equals(s.OcrOutputMode, "Clipboard", StringComparison.OrdinalIgnoreCase);
        RadOcrOutputClipboard.IsChecked = ocrSilent;
        RadOcrOutputWindow.IsChecked = !ocrSilent;

        string toastPos = AppSettings.NormalizeOcrToastPosition(s.OcrToastPosition);
        RadOcrToastPosTopCenter.IsChecked = toastPos == "TopCenter";
        RadOcrToastPosBottomCenter.IsChecked = toastPos == "BottomCenter";
        RadOcrToastPosTopRight.IsChecked = toastPos == "TopRight";
        RadOcrToastPosBottomRight.IsChecked = toastPos == "BottomRight";
        RadOcrToastPosNone.IsChecked = toastPos == "None";

        int toastSec = AppSettings.NormalizeOcrToastSeconds(s.OcrToastSeconds);
        RadOcrToastSec3.IsChecked = toastSec == 3;
        RadOcrToastSec5.IsChecked = toastSec == 5;
        RadOcrToastSec10.IsChecked = toastSec == 10;

        PopulateTranslateLanguages(s.TranslateTargetLanguage);
        PopulateTranslateSecondaryLanguages(s.TranslateSecondaryTargetLanguage);
        PopulateTranslateSourceLanguages(s.TranslateSourceLanguage);

        // AI 大模型 / 识图
        RadAiUseDefaultPromptOn.IsChecked = s.AiUseDefaultPrompt;
        RadAiUseDefaultPromptOff.IsChecked = !s.AiUseDefaultPrompt;
        string promptPos = AppSettings.NormalizeAiPromptWindowPosition(s.AiPromptWindowPosition);
        RadAiPromptPosFollowMouse.IsChecked = promptPos == "FollowMouse";
        RadAiPromptPosCenterScreen.IsChecked = promptPos == "CenterScreen";
        s.EnsureDefaultAiPrompts();
        _aiPromptPresetsDraft = s.AiPromptPresets.Select(p => p.Clone()).ToList();
        if (_aiLoaded)
        {
            RenderAiPromptPresets();
        }
        RadAiSearchOn.IsChecked = s.AiSearchEnabled;
        RadAiSearchOff.IsChecked = !s.AiSearchEnabled;
        RadAiThinkingOn.IsChecked = s.AiThinkingEnabled;
        RadAiThinkingOff.IsChecked = !s.AiThinkingEnabled;

        _aiConfigsDraft = s.AiConfigs.Select(c => c.Clone()).ToList();
        if (_aiConfigsDraft.Count == 0)
        {
            _aiConfigsDraft.Add(AiApiConfig.CreateDefaultOpenAi());
        }
        if (_aiLoaded)
        {
            RenderAiProviders();
        }

        // 云同步设置
        TxtWebDavUrl.Text = s.WebDavUrl ?? "";
        TxtWebDavUsername.Text = s.WebDavUsername ?? "";
        TxtWebDavPassword.Text = s.WebDavPassword ?? "";
        TxtWebDavFileName.Text = string.IsNullOrWhiteSpace(s.WebDavFileName) ? "yuSnip-config-v1.json" : s.WebDavFileName;
        TxtCloudConfigPath.Text = AppSettings.SettingsPath;

        // Google Drive 连接状态
        _gdriveService = new GoogleDriveSyncService(_settings);
        UpdateGDriveStatusUi(null);

        // 云存储服务商选择
        string provider = _settings.CloudSyncProvider ?? "none";
        RadCloudNone.IsChecked = provider == "none";
        RadCloudGDrive.IsChecked = provider == "gdrive";
        RadCloudWebDav.IsChecked = provider == "webdav";
        UpdateCloudProviderVisibility();

        // 云端是否携带密钥与凭据
        RadCloudSecretsOn.IsChecked = _settings.CloudSyncSecrets;
        RadCloudSecretsOff.IsChecked = !_settings.CloudSyncSecrets;
        RadCloudAutoOn.IsChecked = _settings.CloudAutoSync;
        RadCloudAutoOff.IsChecked = !_settings.CloudAutoSync;
        UpdateCloudSecretsWarn();

        UpdateProviderCardHighlight();

        // 截图选框样式
        string borderStyle = AppSettings.NormalizeSelectionBorderStyle(s.SelectionBorderStyle);
        if (RadSelectionBorderDashed != null) RadSelectionBorderDashed.IsChecked = borderStyle == AppSettings.SelectionBorderStyleDashed;
        if (RadSelectionBorderSolid != null) RadSelectionBorderSolid.IsChecked = borderStyle == AppSettings.SelectionBorderStyleSolid;
        UpdateSelectionBorderColorUi(s.SelectionBorderColorArgb);
        UpdateSelectionBorderThicknessUi(s.SelectionBorderThickness);

        // 录屏栏（2026-09-24 瘦身）：只回显"参数条／预览窗没有入口"的那几项。
        // 画质／格式／延迟／系统声音／麦克风／按键显示／涟漪七项的控件已删，值改由预录参数条与预览窗
        // 经 RecLast*「上次使用」族承载，设置页不再是它们的第二份入口。
        RadRecCapAuto.IsChecked = s.RecCaptureSource != "GdiOnly";
        RadRecCapGdi.IsChecked = s.RecCaptureSource == "GdiOnly";
        var recEngine = ScreenCaptureTool.Core.Recording.RecordEngines.Normalize(s.RecEngine);
        RadRecEngineAuto.IsChecked = recEngine == ScreenCaptureTool.Core.Recording.RecordEngine.Auto;
        RadRecEngineWindows.IsChecked = recEngine == ScreenCaptureTool.Core.Recording.RecordEngine.Windows;
        RadRecEngineFfmpeg.IsChecked = recEngine == ScreenCaptureTool.Core.Recording.RecordEngine.Ffmpeg;

        RadRecCursorOn.IsChecked = s.RecVisCursor;
        RadRecCursorOff.IsChecked = !s.RecVisCursor;

        // 画质预设走独立文件 record_presets.ini（不在 settings.ini 里），这里只触发一次重铺
        RebuildRecPresetList();

        int micVol = Math.Clamp(s.RecMicVolume, 40, 100);
        if (NsdRecMicVol != null) NsdRecMicVol.Value = micVol;
        if (TxtRecMicVol != null) TxtRecMicVol.Text = micVol.ToString();

        TxtRecSaveDir.Text = s.RecSaveDirectory ?? "";
        // 框子空着＝用本机默认目录，此时<b>把那条默认路径真值写进水印</b>，让用户看得见"留空到底落到哪"。
        // ⚠ 只读显示、不写进 ini（写进去就等于把当前用户名烙进配置，云同步带到别的机器必错——见
        //   `AppSettings.ResolveRecordSaveDir` 的remarks）。水印只在框子为空时露出，选过目录就自动让位。
        TxtRecSaveDir.Watermark = "留空则用 " + AppSettings.ResolveRecordSaveDir("");

        RefreshDepsUi();
    }

    /// <summary>
    /// 云端 / 备份恢复成功后整表重载：重新读盘、重建全部内存草稿与列表、刷新各页 UI，并补做运行时副作用。
    /// 恢复方必须走这里而不是裸调 <see cref="ApplySettingsToUi"/>：逐项给 RadioButton / ComboBox 赋值会同步触发
    /// <see cref="AutoSaveSettings"/>，而它按"半新半旧"的 UI 状态整体回写磁盘，且工具栏布局草稿未被重建，
    /// 会把刚恢复的工具栏布局与 AI 配置静默回滚成恢复前的值。
    /// </summary>
    /// <param name="actionsChanged">本次重载是否覆盖了动作表（actions.ini），为真时补发 <see cref="ActionsChanged"/> 重注册全局热键。</param>
    internal void RefreshAfterAutomaticSync() => ReloadAllFromDisk(false, true);

    private void ReloadAllFromDisk(bool actionsChanged = false, bool preserveIdentity = false)
    {
        string oldTheme = _settings?.ThemeMode ?? "";

        _isLoading = true;
        try
        {
            // Reload 而非 Load：强制刷新 AppSettings 内存缓存，使全应用立即读到恢复后的配置
            _settings = preserveIdentity ? AppSettings.ReloadInPlace() : AppSettings.Reload();
            if (_settings.MigrateToolbarLayout())
            {
                _settings.Save();
            }
            ApplySettingsToUi(_settings);

            _toolbarDraft = CurrentToolbarDefinition.Parse(ToolbarScenes.Read(_settings, _toolbarScene));
            RebuildGroupCards(); // 组卡是组的唯一真源：草稿整表换掉后按新草稿与组库重建
            if (_toolbarLoaded)
            {
                RenderToolbarPage();
            }
            if (_actionsLoaded)
            {
                LoadActionsDraft();
                LoadBuiltinShortcutsDraft();
            }
        }
        finally
        {
            _isLoading = false;
        }

        // 主题必须显式比对：AutoSaveSettings 内的 oldTheme 比较在恢复场景下恒等，不会触发换肤
        if (oldTheme != _settings.ThemeMode)
        {
            ApplySettingsTheme();
        }

        // 动作表被覆盖过就要重注册全局热键：热键注册只由 ActionsChanged 驱动，
        // 不补发的话恢复出的快捷键要等到下次启动才生效。
        if (actionsChanged)
        {
            ActionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // -------------------- 实时自动保存 --------------------

    private void SetupAutoSaveEvents()
    {
        // 通用与存储
        TxtSaveDirectory.LostFocus += (_, _) => AutoSaveSettings();
        TxtFileNameTemplate.LostFocus += (_, _) => AutoSaveSettings();

        RadThemeLight.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadThemeDark.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadThemeSystem.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadRunOnStartupOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadRunOnStartupOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        // 管理员模式分段按钮：切换会触发提权/降权重启流程，走专用处理而不是普通实时保存
        RadAdminModeOff.IsCheckedChanged += OnAdminModeToggleChanged;
        RadAdminModeOn.IsCheckedChanged += OnAdminModeToggleChanged;

        RadShowSaveNotificationOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadShowSaveNotificationOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadShowStartupNotificationOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadShowStartupNotificationOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadPlaySaveSoundOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadPlaySaveSoundOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadCopyToClipboardOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCopyToClipboardOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadCheckUpdateOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCheckUpdateOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        // 截图历史（分段项必须每个单独订阅，漏一个就"点了不保存"——RadOcrToastSec* 踩过这个坑）
        RadHistoryOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadHistoryOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCaptureAutoOcrOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCaptureAutoOcrOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        // 统计时间范围：视图偏好，只刷 UI 不进 AutoSaveSettings
        RadStatsRange7.IsCheckedChanged += OnStatsRangeChanged;
        RadStatsRange30.IsCheckedChanged += OnStatsRangeChanged;
        RadStatsRange90.IsCheckedChanged += OnStatsRangeChanged;

        // 交互与行为
        RadAutoDetectElement.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAutoDetectWindow.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAutoDetectNone.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadShowLongScrollPreviewOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadShowLongScrollPreviewOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        if (RadGuidelineModeOff != null) RadGuidelineModeOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadGuidelineModeHoldAlt != null) RadGuidelineModeHoldAlt.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadGuidelineModeAlways != null) RadGuidelineModeAlways.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadCrosshairModeOff != null) RadCrosshairModeOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadCrosshairModeHoldAlt != null) RadCrosshairModeHoldAlt.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadCrosshairModeAlways != null) RadCrosshairModeAlways.IsCheckedChanged += (_, _) => AutoSaveSettings();

        if (RadShowCaptureHintsOff != null) RadShowCaptureHintsOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadShowCaptureHintsOn != null) RadShowCaptureHintsOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        if (RadReuseLastAnnotationToolOff != null) RadReuseLastAnnotationToolOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadReuseLastAnnotationToolOn != null) RadReuseLastAnnotationToolOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadCounterRenumberOff != null) RadCounterRenumberOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadCounterRenumberOn != null) RadCounterRenumberOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadCounterRememberOff != null) RadCounterRememberOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadCounterRememberOn != null) RadCounterRememberOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        if (RadMattingModelGeneral != null) RadMattingModelGeneral.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadMattingModelPortrait != null) RadMattingModelPortrait.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadMattingResultClipboard != null) RadMattingResultClipboard.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadMattingResultSticker != null) RadMattingResultSticker.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadMattingResultBoth != null) RadMattingResultBoth.IsCheckedChanged += (_, _) => AutoSaveSettings();

        if (RadStickerDblClickClose != null) RadStickerDblClickClose.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerDblClickThumbnail != null) RadStickerDblClickThumbnail.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerDblClickCopy != null) RadStickerDblClickCopy.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerDblClickNone != null) RadStickerDblClickNone.IsCheckedChanged += (_, _) => AutoSaveSettings();

        // 阴影三档：改完当场让已贴出的贴图按新档重算（`RefreshAllAlphaBacking` 内部就是逐窗 ApplyShadow()，
        // 与棋盘格那两行复用同一个入口）。⚠ 分段每一项都要单独订阅，漏一个就"点了不保存"
        // （本文件既有教训：RadOcrToastSec* 踩过这个坑）。
        if (RadStickerDefaultShadowOff != null) RadStickerDefaultShadowOff.IsCheckedChanged += (_, _) => { AutoSaveSettings(); StickerWindow.RefreshAllAlphaBacking(); };
        if (RadStickerShadowAuto != null) RadStickerShadowAuto.IsCheckedChanged += (_, _) => { AutoSaveSettings(); StickerWindow.RefreshAllAlphaBacking(); };
        if (RadStickerDefaultShadowOn != null) RadStickerDefaultShadowOn.IsCheckedChanged += (_, _) => { AutoSaveSettings(); StickerWindow.RefreshAllAlphaBacking(); };
        // 透明背景档：已贴出的贴图当场跟着切（只动显示层，不改任何贴图数据）
        if (RadStickerCheckerboardOn != null) RadStickerCheckerboardOn.IsCheckedChanged += (_, _) => { AutoSaveSettings(); StickerWindow.RefreshAllAlphaBacking(); };
        if (RadStickerCheckerboardOff != null) RadStickerCheckerboardOff.IsCheckedChanged += (_, _) => { AutoSaveSettings(); StickerWindow.RefreshAllAlphaBacking(); };
        if (RadStickerTextSelectableOff != null) RadStickerTextSelectableOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerTextSelectableOn != null) RadStickerTextSelectableOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerSelectionToolbarOff != null) RadStickerSelectionToolbarOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerSelectionToolbarOn != null) RadStickerSelectionToolbarOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerGrabSelectedTextOff != null) RadStickerGrabSelectedTextOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerGrabSelectedTextOn != null) RadStickerGrabSelectedTextOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerResizeReflowsTextOff != null) RadStickerResizeReflowsTextOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerResizeReflowsTextOn != null) RadStickerResizeReflowsTextOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerColorPickerOff != null) RadStickerColorPickerOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerColorPickerOn != null) RadStickerColorPickerOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerRestoreByClosed != null) RadStickerRestoreByClosed.IsCheckedChanged += (_, _) => AutoSaveSettings();
        if (RadStickerRestoreByCreated != null) RadStickerRestoreByCreated.IsCheckedChanged += (_, _) => AutoSaveSettings();

        // OCR 与翻译
        RadOcrOutputWindow.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadOcrOutputClipboard.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadOcrToastPosTopCenter.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadOcrToastPosBottomCenter.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadOcrToastPosTopRight.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadOcrToastPosBottomRight.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadOcrToastPosNone.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadTranslateNone.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateIndex.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateBaidu.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateYoudao.IsCheckedChanged += (_, _) => AutoSaveSettings();

        TxtBaiduAppId.LostFocus += (_, _) => AutoSaveSettings();
        TxtBaiduSecretKey.LostFocus += (_, _) => AutoSaveSettings();
        TxtYoudaoAppId.LostFocus += (_, _) => AutoSaveSettings();
        TxtYoudaoSecretKey.LostFocus += (_, _) => AutoSaveSettings();

        CmbTranslateSourceLang.SelectionChanged += (_, _) => AutoSaveSettings();
        CmbTranslateTargetLang.SelectionChanged += (_, _) => AutoSaveSettings();
        CmbTranslateSecondaryTargetLang.SelectionChanged += (_, _) => AutoSaveSettings();

        // 切换翻译模式连带刷新「仅图片到文本」选项；不整卡刷新，以免误清密钥内联验证结果
        RadTranslateImageToText.IsCheckedChanged += (_, _) => { AutoSaveSettings(); UpdateTranslateTextOnlyRows(); };
        RadTranslateImageToImage.IsCheckedChanged += (_, _) => { AutoSaveSettings(); UpdateTranslateTextOnlyRows(); };

        RadTranslateMiniPosFollow.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateMiniPosCenter.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateMiniPosBottomCenter.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadTranslateMiniAutoCloseOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateMiniAutoCloseOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadTranslateMiniCopyOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateMiniCopyOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateStreamOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadTranslateStreamOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        // AI
        RadAiUseDefaultPromptOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAiUseDefaultPromptOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAiPromptPosFollowMouse.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAiPromptPosCenterScreen.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAiSearchOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAiSearchOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAiThinkingOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadAiThinkingOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        // 云同步
        RadCloudNone.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCloudGDrive.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCloudWebDav.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCloudSecretsOn.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCloudSecretsOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadCloudAutoOn.IsCheckedChanged += OnCloudAutoToggled;
        RadCloudAutoOff.IsCheckedChanged += OnCloudAutoToggled;

        TxtWebDavUrl.LostFocus += (_, _) => AutoSaveSettings();
        TxtWebDavUsername.LostFocus += (_, _) => AutoSaveSettings();
        TxtWebDavPassword.LostFocus += (_, _) => AutoSaveSettings();
        TxtWebDavFileName.LostFocus += (_, _) => AutoSaveSettings();

        // 选框线型与粗细：都是互斥档位，切换即刷预览（虚线/实线改描边 DashStyle，档位改线宽）
        if (RadSelectionBorderDashed != null)
            RadSelectionBorderDashed.IsCheckedChanged += (_, _) => { UpdateSelectionBorderPreview(); AutoSaveSettings(); };
        if (RadSelectionBorderSolid != null)
            RadSelectionBorderSolid.IsCheckedChanged += (_, _) => { UpdateSelectionBorderPreview(); AutoSaveSettings(); };
        foreach (var thicknessRadio in SelectionBorderThicknessRadios)
        {
            if (thicknessRadio != null) thicknessRadio.IsCheckedChanged += OnSelectionBorderThicknessCheckedChanged;
        }

        // 录屏栏：每档都单独订阅，漏一个就"点了不保存"
        RadRecCapAuto.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadRecCapGdi.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadRecEngineAuto.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadRecEngineWindows.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadRecEngineFfmpeg.IsCheckedChanged += (_, _) => AutoSaveSettings();

        RadRecCursorOff.IsCheckedChanged += (_, _) => AutoSaveSettings();
        RadRecCursorOn.IsCheckedChanged += (_, _) => AutoSaveSettings();

        NsdRecMicVol.ValueChanged += (_, _) =>
        {
            SyncRecMicVolUi();
            AutoSaveSettings();
        };

        TxtRecSaveDir.LostFocus += (_, _) => AutoSaveSettings();
    }

    /// <summary>设置项实时保存（控件修改即落盘并即时应用）。</summary>
    private void AutoSaveSettings()
    {
        if (_isLoading) return;
        try
        {
            string saveDir = (TxtSaveDirectory?.Text ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(saveDir))
            {
                _settings.SaveDirectory = saveDir;
            }

            string fileNameTemplate = (TxtFileNameTemplate?.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(fileNameTemplate))
            {
                fileNameTemplate = "Screenshot_{yyyyMMdd_HHmmss}";
            }
            _settings.FileNameTemplate = fileNameTemplate;

            if (RadAutoDetectElement?.IsChecked == true) _settings.AutoDetectMode = "检测元素";
            else if (RadAutoDetectWindow?.IsChecked == true) _settings.AutoDetectMode = "仅窗口";
            else if (RadAutoDetectNone?.IsChecked == true) _settings.AutoDetectMode = "不检测";

            if (NsdElementDwellMs != null) _settings.ElementDwellMs = (int)Math.Clamp(NsdElementDwellMs.Value, 0, 100);
            if (NsdElementDwellDeadband != null) _settings.ElementDwellDeadbandPx = (int)Math.Clamp(NsdElementDwellDeadband.Value, 0, 100);

            string oldTheme = _settings.ThemeMode;
            if (RadThemeLight?.IsChecked == true) _settings.ThemeMode = "浅色";
            else if (RadThemeDark?.IsChecked == true) _settings.ThemeMode = "暗色";
            else if (RadThemeSystem?.IsChecked == true) _settings.ThemeMode = "跟随系统";

            if (RadCopyToClipboardOn != null) _settings.CopyToClipboardAfterSave = RadCopyToClipboardOn.IsChecked == true;
            if (RadRunOnStartupOn != null) _settings.RunOnStartup = RadRunOnStartupOn.IsChecked == true;
            if (RadShowSaveNotificationOn != null) _settings.ShowSaveNotification = RadShowSaveNotificationOn.IsChecked == true;
            if (RadShowStartupNotificationOn != null) _settings.ShowStartupNotification = RadShowStartupNotificationOn.IsChecked == true;
            if (RadPlaySaveSoundOn != null) _settings.PlaySaveSound = RadPlaySaveSoundOn.IsChecked == true;
            if (RadCheckUpdateOn != null) _settings.CheckUpdateOnStartup = RadCheckUpdateOn.IsChecked == true;

            // 截图历史（开启 / 关闭）
            if (RadHistoryOn?.IsChecked == true) _settings.CaptureHistoryMode = "On";
            else if (RadHistoryOff?.IsChecked == true) _settings.CaptureHistoryMode = "Off";
            _settings.CaptureAutoOcr = RadCaptureAutoOcrOn.IsChecked == true;

            if (RadStickerDblClickClose?.IsChecked == true) _settings.StickerDoubleClickAction = "Close";
            else if (RadStickerDblClickThumbnail?.IsChecked == true) _settings.StickerDoubleClickAction = "Thumbnail";
            else if (RadStickerDblClickCopy?.IsChecked == true) _settings.StickerDoubleClickAction = "Copy";
            else if (RadStickerDblClickNone?.IsChecked == true) _settings.StickerDoubleClickAction = "None";

            if (RadStickerDefaultShadowOn != null)
            {
                // 三档写回（2026-10-01，issue #50）：档位是真源，布尔项作为兼容镜像一起维护
                // （老版本与云同步只认那个键；Auto 在老版本里无从表达，按"画"落）
                string shadowMode = RadStickerDefaultShadowOff?.IsChecked == true ? "Off"
                    : RadStickerDefaultShadowOn.IsChecked == true ? "On" : "Auto";
                _settings.StickerShadowMode = shadowMode;
                _settings.StickerDefaultShadow = shadowMode != "Off";
            }
            if (RadStickerCheckerboardOn != null) _settings.StickerTransparentCheckerboard = RadStickerCheckerboardOn.IsChecked == true;
            if (RadStickerTextSelectableOn != null) _settings.StickerTextSelectable = RadStickerTextSelectableOn.IsChecked == true;
            if (RadStickerSelectionToolbarOn != null) _settings.StickerSelectionToolbar = RadStickerSelectionToolbarOn.IsChecked == true;
            if (RadStickerGrabSelectedTextOn != null) _settings.StickerGrabSelectedText = RadStickerGrabSelectedTextOn.IsChecked == true;
            if (RadStickerResizeReflowsTextOn != null) _settings.StickerResizeReflowsText = RadStickerResizeReflowsTextOn.IsChecked == true;
            if (RadStickerColorPickerOn != null) _settings.StickerColorPickerEnabled = RadStickerColorPickerOn.IsChecked == true;
            if (RadStickerRestoreByCreated != null) _settings.StickerRestoreSortMode = RadStickerRestoreByCreated.IsChecked == true ? "Created" : "ClosedAt";
            if (RadShowLongScrollPreviewOn != null) _settings.ShowLongScrollPreview = RadShowLongScrollPreviewOn.IsChecked == true;
            _settings.GuidelineMode = ReadTriStateMode(RadGuidelineModeOff, RadGuidelineModeHoldAlt, RadGuidelineModeAlways, _settings.GuidelineMode);
            _settings.CrosshairMode = ReadTriStateMode(RadCrosshairModeOff, RadCrosshairModeHoldAlt, RadCrosshairModeAlways, _settings.CrosshairMode);
            if (RadShowCaptureHintsOn != null) _settings.ShowCaptureHints = RadShowCaptureHintsOn.IsChecked == true;
            if (RadReuseLastAnnotationToolOn != null) _settings.ReuseLastAnnotationTool = RadReuseLastAnnotationToolOn.IsChecked == true;
            if (RadCounterRenumberOn != null) _settings.CounterRenumberOnDelete = RadCounterRenumberOn.IsChecked == true;
            if (RadCounterRememberOn != null) _settings.CounterRememberStyle = RadCounterRememberOn.IsChecked == true;

            if (RadSelectionBorderSolid?.IsChecked == true)
                _settings.SelectionBorderStyle = AppSettings.SelectionBorderStyleSolid;
            else if (RadSelectionBorderDashed?.IsChecked == true)
                _settings.SelectionBorderStyle = AppSettings.SelectionBorderStyleDashed;
            _settings.SelectionBorderColorArgb = _selectionBorderColorArgb;
            _settings.SelectionBorderThickness = ReadSelectionBorderThicknessFromUi();

            if (RadMattingModelPortrait?.IsChecked == true) _settings.MattingDefaultModel = AppSettings.MattingModelPortrait;
            else if (RadMattingModelGeneral?.IsChecked == true) _settings.MattingDefaultModel = AppSettings.MattingModelGeneral;
            if (RadMattingResultClipboard?.IsChecked == true) _settings.MattingResultDestination = AppSettings.MattingResultClipboard;
            else if (RadMattingResultSticker?.IsChecked == true) _settings.MattingResultDestination = AppSettings.MattingResultSticker;
            else if (RadMattingResultBoth?.IsChecked == true) _settings.MattingResultDestination = AppSettings.MattingResultBoth;

            // 翻译设置
            if (RadTranslateBaidu?.IsChecked == true) _settings.TranslateProvider = "Baidu";
            else if (RadTranslateIndex?.IsChecked == true) _settings.TranslateProvider = "BilibiliIndex";
            else if (RadTranslateYoudao?.IsChecked == true) _settings.TranslateProvider = "Youdao";
            else if (RadTranslateNone?.IsChecked == true) _settings.TranslateProvider = "None";

            if (TxtBaiduAppId != null) _settings.BaiduAppId = (TxtBaiduAppId.Text ?? "").Trim();
            if (TxtBaiduSecretKey != null) _settings.BaiduSecretKey = (TxtBaiduSecretKey.Text ?? "").Trim();
            if (TxtYoudaoAppId != null) _settings.YoudaoAppId = (TxtYoudaoAppId.Text ?? "").Trim();
            if (TxtYoudaoSecretKey != null) _settings.YoudaoSecretKey = (TxtYoudaoSecretKey.Text ?? "").Trim();

            if (RadTranslateImageToImage?.IsChecked == true) _settings.TranslateMode = "ImageToImage";
            else if (RadTranslateImageToText?.IsChecked == true) _settings.TranslateMode = "ImageToText";
            if (RadTranslateStreamOn != null) _settings.TranslateStreamEnabled = RadTranslateStreamOn.IsChecked == true;

            if (RadTranslateMiniPosCenter?.IsChecked == true)
                _settings.TranslateMiniWindowPosition = "CenterScreen";
            else if (RadTranslateMiniPosBottomCenter?.IsChecked == true)
                _settings.TranslateMiniWindowPosition = "BottomCenter";
            else
                _settings.TranslateMiniWindowPosition = "FollowSelection";

            if (RadTranslateMiniAutoCloseOn != null)
                _settings.TranslateMiniWindowAutoClose = RadTranslateMiniAutoCloseOn.IsChecked == true;

            if (RadTranslateMiniCopyOn != null)
                _settings.TranslateMiniWindowAutoCopy = RadTranslateMiniCopyOn.IsChecked == true;

            if (RadOcrOutputClipboard != null) _settings.OcrOutputMode = RadOcrOutputClipboard.IsChecked == true ? "Clipboard" : "Window";
            if (RadOcrToastPosTopCenter?.IsChecked == true) _settings.OcrToastPosition = "TopCenter";
            else if (RadOcrToastPosTopRight?.IsChecked == true) _settings.OcrToastPosition = "TopRight";
            else if (RadOcrToastPosBottomRight?.IsChecked == true) _settings.OcrToastPosition = "BottomRight";
            else if (RadOcrToastPosBottomCenter?.IsChecked == true) _settings.OcrToastPosition = "BottomCenter";
            else if (RadOcrToastPosNone?.IsChecked == true) _settings.OcrToastPosition = "None";

            if (RadOcrToastSec10?.IsChecked == true) _settings.OcrToastSeconds = 10;
            else if (RadOcrToastSec5?.IsChecked == true) _settings.OcrToastSeconds = 5;
            else if (RadOcrToastSec3?.IsChecked == true) _settings.OcrToastSeconds = 3;

            if (CmbTranslateTargetLang?.SelectedItem is ComboBoxItem langItem)
                _settings.TranslateTargetLanguage = langItem.Tag?.ToString() ?? "zh";
            if (CmbTranslateSecondaryTargetLang?.SelectedItem is ComboBoxItem secLangItem)
                _settings.TranslateSecondaryTargetLanguage = secLangItem.Tag?.ToString() ?? "en";
            if (CmbTranslateSourceLang?.SelectedItem is ComboBoxItem srcLangItem)
                _settings.TranslateSourceLanguage = srcLangItem.Tag?.ToString() ?? "auto";

            // AI 大模型 / 识图
            _settings.AiConfigs = _aiConfigsDraft.Select(c => c.Clone()).ToList();
            if (RadAiUseDefaultPromptOn != null) _settings.AiUseDefaultPrompt = RadAiUseDefaultPromptOn.IsChecked == true;
            if (RadAiPromptPosCenterScreen != null) _settings.AiPromptWindowPosition = RadAiPromptPosCenterScreen.IsChecked == true ? "CenterScreen" : "FollowMouse";
            if (_aiPromptPresetsDraft.Count > 0 && !_aiPromptPresetsDraft.Any(p => p.IsDefault))
            {
                _aiPromptPresetsDraft[0].IsDefault = true;
            }
            _settings.AiPromptPresets = _aiPromptPresetsDraft.Select(p => p.Clone()).ToList();
            _settings.AiCustomPrompt = _aiPromptPresetsDraft.FirstOrDefault(p => p.IsDefault)?.Content ?? AppSettings.DefaultAiCustomPrompt;
            if (RadAiSearchOn != null) _settings.AiSearchEnabled = RadAiSearchOn.IsChecked == true;
            if (RadAiThinkingOn != null) _settings.AiThinkingEnabled = RadAiThinkingOn.IsChecked == true;

            var activeConfig = _settings.GetEffectiveApiConfig();
            if (activeConfig != null)
            {
                _settings.AiActiveConfigId = activeConfig.Id;
                _settings.AiProtocol = activeConfig.Protocol;
                _settings.AiBaseUrl = activeConfig.BaseUrl;
                _settings.AiApiKey = activeConfig.ApiKey;
                _settings.AiModel = activeConfig.Model;
            }

            // 云同步设置
            if (TxtWebDavUrl != null) _settings.WebDavUrl = (TxtWebDavUrl.Text ?? "").Trim();
            if (TxtWebDavUsername != null) _settings.WebDavUsername = (TxtWebDavUsername.Text ?? "").Trim();
            if (TxtWebDavPassword != null) _settings.WebDavPassword = TxtWebDavPassword.Text ?? "";
            if (TxtWebDavFileName != null)
            {
                string fn = (TxtWebDavFileName.Text ?? "").Trim();
                _settings.WebDavFileName = string.IsNullOrWhiteSpace(fn) ? "yuSnip-config-v1.json" : fn;
            }

            if (RadCloudGDrive?.IsChecked == true) _settings.CloudSyncProvider = "gdrive";
            else if (RadCloudWebDav?.IsChecked == true) _settings.CloudSyncProvider = "webdav";
            else if (RadCloudNone?.IsChecked == true) _settings.CloudSyncProvider = "none";

            if (RadCloudSecretsOn?.IsChecked == true) _settings.CloudSyncSecrets = true;
            else if (RadCloudSecretsOff?.IsChecked == true) _settings.CloudSyncSecrets = false;
            if (RadCloudAutoOn?.IsChecked == true) _settings.CloudAutoSync = true;
            else if (RadCloudAutoOff?.IsChecked == true) _settings.CloudAutoSync = false;

            // 自定义工具栏：组卡是唯一真源，先刷进草稿，再分别写「一级栏」与「未上栏组库」两个键
            SyncCardsIntoDraft();
            List<string> offBarGroups = new List<string>();
            for (int ci = 0; ci < _groupCards.Count; ci++)
            {
                int toolCount = _groupCards[ci].Count(x => !IsToolbarDivider(x));
                if (!CardOnBar(ci) && toolCount >= ToolbarRegistry.MinGroupMembers)
                {
                    offBarGroups.Add(MakeToolbarGroupToken(_groupCards[ci]));
                }
            }
            ToolbarScenes.Write(_settings, _toolbarScene, string.Join(",", _toolbarDraft));
            ToolbarScenes.WriteGroups(_settings, _toolbarScene, string.Join(",", offBarGroups));

            // 录屏栏（2026-09-24 瘦身）：这一族与"上次使用"分层无关了——画质／格式／延迟／系统声音／麦克风／
            // 按键显示／涟漪七项的控件已删，它们在设置页不再有任何写入点，全部只由参数条与预览窗经
            // RecLast* 承载。剩下的都是"设置页独占入口"的单键项，故原先那段"改了默认顺手把上次使用刷成同值"
            // 的回填机器（防"设了不生效"）整体退役。
            _settings.RecCaptureSource = RadRecCapGdi?.IsChecked == true ? "GdiOnly" : "Auto";
            _settings.RecEngine = RadRecEngineWindows?.IsChecked == true ? "Windows" : RadRecEngineFfmpeg?.IsChecked == true ? "Ffmpeg" : "Auto";
            if (RadRecCursorOn != null) _settings.RecVisCursor = RadRecCursorOn.IsChecked == true;

            // RecAudioMix 键保留（参数条读取链照旧认 "Split"），但设置页「双开时混音」两档 2026-09-26 撤控件 ⇒
            // 界面侧恒自动混音，本方法自此对该键零写入点
            if (NsdRecMicVol != null) _settings.RecMicVolume = (int)Math.Clamp(Math.Round(NsdRecMicVol.Value), 40, 100);

            // RecFfmpegPath 键保留（老 ini 与 FfmpegLocator 探测链仍读它），但设置页已按用户口径撤掉「手动指定路径」控件
            if (TxtRecSaveDir != null) _settings.RecSaveDirectory = (TxtRecSaveDir.Text ?? "").Trim();

            // RecPreviewLoop / RecShowBadge 两项 2026-09-26 撤控件：恒为出厂默认"开"，键与消费点
            // （预览贴图窗循环播放、录制中 REC 时长角标）都留着，只是界面上不再给第二份入口。

            _settings.Save();
            // 只有开关变化才维护启动载体，普通字段保存不得绕过协调器重建 Run 键。
            if (_settings.RunOnStartup != _lastAutosaveRunOnStartup)
            {
                _lastAutosaveRunOnStartup = _settings.RunOnStartup;
                _ = ApplyStartupSettingsAsync();
            }

            if (oldTheme != _settings.ThemeMode)
            {
                ApplySettingsTheme();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.AutoSaveSettings 实时保存失败", ex);
        }
    }

    private async Task ApplyStartupSettingsAsync()
    {
        try
        {
            bool applied = await Task.Run(() => AutostartCoordinator.Apply(AppSettings.Current));
            if (!applied) ShowStatus("开机启动设置未能同步到 Windows，请检查运行日志后重试。");
        }
        catch (Exception ex) { AppLogger.Error("同步开机启动设置失败", ex); }
    }

    /// <summary>动作表实时保存并通知宿主重新注册热键。</summary>
    private void AutoSaveActions()
    {
        if (_isLoading) return;
        try
        {
            ScreenCaptureTool.Core.Actions.ActionsStore.Save(_actionsDraft);
            ActionsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.AutoSaveActions 动作表实时保存失败", ex);
        }
    }

    /// <summary>内置快捷键实时保存。</summary>
    private void AutoSaveBuiltinShortcuts()
    {
        if (_isLoading) return;
        try
        {
            Services.BuiltinShortcutsStore.Save(_builtinShortcutsDraft);
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.AutoSaveBuiltinShortcuts 内置快捷键实时保存失败", ex);
        }
    }

    private async void OnBrowseSaveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择截图保存目录",
                AllowMultiple = false,
            });

            IStorageFolder? folder = folders.FirstOrDefault();
            if (folder != null)
            {
                string? path = folder.TryGetLocalPath();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    TxtSaveDirectory.Text = path;
                    AutoSaveSettings();
                }
                else ShowError("无法获取所选目录的本地路径。");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.OnBrowseSaveDirectoryClick: 选择目录失败：" + ex.Message);
            ShowError("选择目录失败：" + ex.Message);
        }
    }

    // ============ 管理员模式分段开关：开启 → 提权重启常驻；关闭 → 清理计划任务并降权 ============

    /// <summary>
    /// 管理员模式分段按钮切换。开启：落盘后提权接力重启（UAC 一次确认），此后手动启动与开机自启都保持管理员；
    /// 关闭：落盘并清理提权计划任务，普通权限下即时生效，管理员权限下降权重启回普通模式。
    /// </summary>
    private void OnAdminModeToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (_isLoading || _isTogglingAdminMode) return;
        bool wantAdmin = RadAdminModeOn.IsChecked == true;
        if (wantAdmin == _settings.RunAsAdmin) return; // 回显/还原触发的状态同步，不是用户切换
        _ = ToggleAdminModeAsync(wantAdmin);
    }

    private async Task ToggleAdminModeAsync(bool wantAdmin)
    {
        _isTogglingAdminMode = true;
        try
        {
            AdminModeResult result = await AdminModeCoordinator.ToggleAsync(wantAdmin, ShowBusy);
            switch (result.Outcome)
            {
                case AdminModeOutcome.Restarted:
                    // 接力实例已创建：立刻退出，把单实例锁与全局热键交给它。
                    // 退出路径上还会落盘一次，先把新值同步进窗口手里的实例再走。
                    _settings.RunAsAdmin = wantAdmin;
                    HideStatus();
                    ShutdownFromSettings();
                    break;
                case AdminModeOutcome.Declined:
                    RevertAdminModeSetting();
                    ShowStatus(result.Message);
                    break;
                case AdminModeOutcome.Failed:
                    RevertAdminModeSetting();
                    ShowError("切换管理员模式失败：" + (result.Error ?? "未知原因"));
                    break;
                default:
                    // 协调器用的是自己 Load 的实例，这里必须把落盘值追回来
                    SyncAdminModeFromExternal();
                    ShowStatus(result.Message);
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.ToggleAdminModeAsync 失败", ex);
            ShowError("切换管理员模式失败：" + ex.Message);
            RefreshElevationUi();
        }
        finally
        {
            _isTogglingAdminMode = false;
        }
    }

    /// <summary>
    /// 外部入口（托盘菜单）改过 RunAsAdmin 后，让已打开的设置窗口追上落盘值。
    /// 只同步显示，不触发切换；防自激靠 OnAdminModeToggleChanged 里那句 wantAdmin 与 _settings 的比较。
    /// </summary>
    public void SyncAdminModeFromExternal()
    {
        bool on = AdminModeCoordinator.ReadRunAsAdmin();
        _settings.RunAsAdmin = on;
        RadAdminModeOn.IsChecked = on;
        RadAdminModeOff.IsChecked = !on;
        RefreshElevationUi();
    }

    /// <summary>UAC 被拒绝/提权失败时回滚管理员模式设置与分段按钮显示。</summary>
    private void RevertAdminModeSetting()
    {
        _settings.RunAsAdmin = false;
        _settings.Save();
        RadAdminModeOn.IsChecked = false;
        RadAdminModeOff.IsChecked = true;
        RefreshElevationUi();
    }

    /// <summary>切换管理员模式引发的自身重启：退出整个应用（接力实例会接管单实例锁与热键）。</summary>
    private void ShutdownFromSettings()
    {
        App.IsShuttingDown = true;
        try { Services.StickerArchiveStore.SaveActiveSession(); } catch { }
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>
    /// 回显管理员模式状态：分段按钮显示的是"期望状态"（RunAsAdmin 设置），
    /// 提示文案同时标注当前进程的真实权限，避免 UAC 被取消后出现"开了却没生效"的误导。
    /// </summary>
    private void RefreshElevationUi()
    {
        bool elevated = ElevationHelper.IsElevated();
        bool adminMode = _settings.RunAsAdmin;
        if (elevated && adminMode)
        {
            TxtElevationHint.Text = "当前以管理员身份运行；开启期间每次启动（含开机自启）都保持管理员权限，开机免 UAC 确认。";
        }
        else if (elevated)
        {
            TxtElevationHint.Text = "当前以管理员身份运行（手动）。开启管理员模式后，开机自启将自动提权。";
        }
        else if (adminMode)
        {
            TxtElevationHint.Text = "管理员模式已开启：手动启动会出现一次 UAC 确认，重启软件后进入管理员模式。";
        }
        else
        {
            TxtElevationHint.Text = "开启后软件始终以管理员权限运行，快捷键不再受高权限前台窗口（如任务管理器）限制；配合开机启动时自动改走计划任务，免每次 UAC 确认。";
        }
    }

    private void OnOpenSaveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        string dir = (TxtSaveDirectory.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            ShowError("保存目录为空。");
            return;
        }
        OpenDirectory(dir, createIfMissing: true);
    }

    private void OnOpenConfigDirectoryClick(object? sender, RoutedEventArgs e)
    {
        OpenDirectory(AppSettings.SettingsDirectory, createIfMissing: true);
    }

    // ============ 「检测元素」悬停检测参数（触发延时 / 移动幅度）============

    /// <summary>自动检测模式切到/切出「检测元素」：参数面板即时显隐。</summary>
    private void OnRadAutoDetectElementCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (PnlElementDwell != null)
        {
            PnlElementDwell.IsVisible = RadAutoDetectElement.IsChecked == true;
        }
    }

    // ============ 大数值仪表盘：大数值输入 ↔ 滑块双向同步 + 仅数字输入 ============

    /// <summary>触发延时：大数值随滑块值刷新（编辑中不打扰）。</summary>
    private void SyncDwellMsUi()
    {
        if (TxtDwellMs == null || NsdElementDwellMs == null) return;
        if (!TxtDwellMs.IsFocused)
            TxtDwellMs.Text = Math.Round(NsdElementDwellMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>移动幅度：大数值随滑块值刷新（编辑中不打扰）。</summary>
    private void SyncDwellDeadbandUi()
    {
        if (TxtDwellDeadband == null || NsdElementDwellDeadband == null) return;
        if (!TxtDwellDeadband.IsFocused)
            TxtDwellDeadband.Text = Math.Round(NsdElementDwellDeadband.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>只允许数字且不得超过上限（100），超出直接吞掉输入。</summary>
    private static bool FilterNumericInput(TextBox tb, string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || !text.All(char.IsDigit))
            return false;
        // 以"追加到末尾"预估合并结果（光标在中间时由提交环节夹取兜底）
        if (int.TryParse(tb.Text + text, out int v) && v > max)
            return false;
        return true;
    }

    private void OnTxtDwellMsTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is TextBox tb && !FilterNumericInput(tb, e.Text, 100))
            e.Handled = true;
    }

    private void OnTxtDwellDeadbandTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is TextBox tb && !FilterNumericInput(tb, e.Text, 100))
            e.Handled = true;
    }

    private void CommitDwellMsInput()
    {
        if (TxtDwellMs == null || NsdElementDwellMs == null) return;
        double v = double.TryParse(TxtDwellMs.Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out double val) ? val : NsdElementDwellMs.Value;
        NsdElementDwellMs.Value = Math.Clamp(Math.Round(v), 0, 100);
        TxtDwellMs.Text = Math.Round(NsdElementDwellMs.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutoSaveSettings();
    }

    private void CommitDwellDeadbandInput()
    {
        if (TxtDwellDeadband == null || NsdElementDwellDeadband == null) return;
        double v = double.TryParse(TxtDwellDeadband.Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out double val) ? val : NsdElementDwellDeadband.Value;
        NsdElementDwellDeadband.Value = Math.Clamp(Math.Round(v), 0, 100);
        TxtDwellDeadband.Text = Math.Round(NsdElementDwellDeadband.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutoSaveSettings();
    }

    private void OnTxtDwellMsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitDwellMsInput();
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnTxtDwellMsLostFocus(object? sender, RoutedEventArgs e) => CommitDwellMsInput();

    private void OnTxtDwellDeadbandKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitDwellDeadbandInput();
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnTxtDwellDeadbandLostFocus(object? sender, RoutedEventArgs e) => CommitDwellDeadbandInput();

    /// <summary>麦克风音量：同上一对（2026-09-26 换成格子滑块后与「触发延时」共用一套仪表盘配方）。</summary>
    private void SyncRecMicVolUi()
    {
        if (TxtRecMicVol == null || NsdRecMicVol == null) return;
        if (!TxtRecMicVol.IsFocused)
            TxtRecMicVol.Text = Math.Round(NsdRecMicVol.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnTxtRecMicVolTextInput(object? sender, TextInputEventArgs e)
    {
        if (sender is TextBox tb && !FilterNumericInput(tb, e.Text, 100))
            e.Handled = true;
    }

    private void CommitRecMicVolInput()
    {
        if (TxtRecMicVol == null || NsdRecMicVol == null) return;
        double v = double.TryParse(TxtRecMicVol.Text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out double val) ? val : NsdRecMicVol.Value;
        NsdRecMicVol.Value = Math.Clamp(Math.Round(v), 40, 100);
        TxtRecMicVol.Text = Math.Round(NsdRecMicVol.Value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        AutoSaveSettings();
    }

    private void OnTxtRecMicVolKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitRecMicVolInput();
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            e.Handled = true;
        }
    }

    private void OnTxtRecMicVolLostFocus(object? sender, RoutedEventArgs e) => CommitRecMicVolInput();

    private void OpenDirectory(string dir, bool createIfMissing)
    {
        try
        {
            if (createIfMissing && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }
            if (!System.IO.Directory.Exists(dir))
            {
                ShowError("目录不存在：" + dir);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.OpenDirectory 失败：" + ex.Message);
            ShowError("打开目录失败：" + ex.Message);
        }
    }

    private void OnJoinQQGroupClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://qm.qq.com/cgi-bin/qm/qr?_wv=1027&k=7q8lAcCA3xTr-PZ2XG8VA04BIyfKhXeV&authKey=rSwrwGZzcJKWm%2F3zfYeTATxVBt%2B170gK4DbDezYPwKMZGI0BH6VSYUp6PYZXTO%2BC&noverify=0&group_code=453478357");
    }

    private void OnOpenGitHubClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/linhuoxi/yuSnip");
    }

    private void OnOpenWebsiteClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://yusnip.top");
    }

    private void OnOpenFeedbackClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://github.com/yusnip/yuSnip/issues");
    }

    private async void OnOpenThirdPartyNoticesClick(object? sender, RoutedEventArgs e)
    {
        await new ThirdPartyNoticesWindow().ShowDialog(this);
    }

    private void OnAboutToggleChangelogClick(object? sender, RoutedEventArgs e)
    {
        bool expand = !PnlAboutChangelog.IsVisible;
        PnlAboutChangelog.IsVisible = expand;
        BtnAboutToggleChangelog.Content = expand ? "收起日志" : "更新日志";
        if (expand && string.IsNullOrEmpty(TxtAboutChangelog.Text))
        {
            TxtAboutChangelog.Text = LoadEmbeddedChangelog();
        }
    }

    private static string LoadEmbeddedChangelog()
    {
        try
        {
            var uri = new Uri("avares://yuSnip/Assets/更新记录.txt");
            if (Avalonia.Platform.AssetLoader.Exists(uri))
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(uri);
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
                string text = reader.ReadToEnd().Trim();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("[Settings] 读取内嵌更新记录失败: " + ex.Message);
        }
        return "暂无当前版本的更新日志说明。";
    }

    private void OnCheckUpdateClick(object? sender, RoutedEventArgs e)
    {
        var win = new UpdateWindow(silentAutoCheck: false);
        win.Show(this);
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.OpenUrl 失败：" + ex.Message);
            ShowError("打开链接失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 统计面板的观察窗口（天）。数值、趋势图与占比共用，属**视图偏好，刻意不落盘**
    /// ——与整张统计卡不参与 AutoSaveSettings 的口径一致。
    /// </summary>
    private int _statsRangeDays = 30;

    /// <summary>
    /// 回显本机使用统计（区间五项次数 + 趋势图 + 区间占比 + 历史累计概要）。
    /// 统计独立于 settings.ini，故不参与 AutoSaveSettings，也不受「恢复默认设置」与云端恢复影响。
    /// </summary>
    private void RefreshUsageStatsUi()
    {
        if (!_runtimeEnabled) return; // 离屏样本由探针注入，避免读取真实使用记录。
        try
        {
            RenderUsageStatsUi(UsageStatistics.GetRecentDays(_statsRangeDays), UsageStatistics.TotalCount);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.RefreshUsageStatsUi 失败：" + ex.Message);
        }
    }

    /// <summary>数值、曲线、占比共用分日快照；累计只作摘要，不混入所选区间。</summary>
    private void RenderUsageStatsUi(IReadOnlyList<UsageDayBucket> days, long lifetimeTotal)
    {
        long rShot = 0, rAi = 0, rOcr = 0, rTr = 0, rSticker = 0;
        for (int i = 0; i < days.Count; i++)
        {
            rShot += days[i].Screenshot;
            rAi += days[i].AiChat;
            rOcr += days[i].Ocr;
            rTr += days[i].Translate;
            rSticker += days[i].Sticker;
        }
        long rTotal = rShot + rAi + rOcr + rTr + rSticker;

        TxtStatScreenshot.Text = FormatCount(rShot);
        TxtStatAiChat.Text = FormatCount(rAi);
        TxtStatOcr.Text = FormatCount(rOcr);
        TxtStatTranslate.Text = FormatCount(rTr);
        TxtStatSticker.Text = FormatCount(rSticker);
        TxtStatScreenshotDelta.Text = FormatStatsRange(rShot);
        TxtStatAiChatDelta.Text = FormatStatsRange(rAi);
        TxtStatOcrDelta.Text = FormatStatsRange(rOcr);
        TxtStatTranslateDelta.Text = FormatStatsRange(rTr);
        TxtStatStickerDelta.Text = FormatStatsRange(rSticker);

        if (ChartUsage != null) ChartUsage.Days = days;

        TxtStatsSummary.Text = lifetimeTotal == 0
            ? "尚无使用记录，用起来后这里会有数据。"
            : "近 " + _statsRangeDays + " 天 " + FormatCount(rTotal) + " 次 · 历史累计 " + FormatCount(lifetimeTotal) + " 次 · 仅本机记录";
        BtnStatsClear.IsEnabled = lifetimeTotal > 0;

        UpdateStatsRatioBar(rShot, rAi, rOcr, rTr, rSticker, rTotal);
    }

    /// <summary>五联数值下方说明所属区间，不重复显示同一计数。</summary>
    private string FormatStatsRange(long value)
    {
        return value <= 0
            ? "近 " + _statsRangeDays + " 天无记录"
            : "近 " + _statsRangeDays + " 天";
    }

    /// <summary>
    /// 按所选区间占比重设占比条五段的列宽（<c>GridLength(ratio, Star)</c>）。
    /// 全零时五段归零，仅显示外层弱底。
    /// </summary>
    private void UpdateStatsRatioBar(long shot, long ai, long ocr, long tr, long sticker, long total)
    {
        if (GridStatsRatio == null || GridStatsRatio.ColumnDefinitions.Count < 5) return;

        if (total <= 0)
        {
            // 无数据：五段全部归零，整条只剩弱底槽（比均分四色更诚实）
            for (int i = 0; i < 5; i++)
                GridStatsRatio.ColumnDefinitions[i].Width = new GridLength(0, GridUnitType.Star);
            if (TxtStatsRatioHint != null) TxtStatsRatioHint.Text = "";
            return;
        }

        GridStatsRatio.ColumnDefinitions[0].Width = new GridLength(shot, GridUnitType.Star);
        GridStatsRatio.ColumnDefinitions[1].Width = new GridLength(ai, GridUnitType.Star);
        GridStatsRatio.ColumnDefinitions[2].Width = new GridLength(ocr, GridUnitType.Star);
        GridStatsRatio.ColumnDefinitions[3].Width = new GridLength(tr, GridUnitType.Star);
        GridStatsRatio.ColumnDefinitions[4].Width = new GridLength(sticker, GridUnitType.Star);

        if (TxtStatsRatioHint == null) return;

        // 只报占比最高的那一项：五项百分比全列出来会把 12px 的说明行挤成一团
        long top = shot;
        string name = "截图";
        if (ai > top) { top = ai; name = "AI 对话"; }
        if (ocr > top) { top = ocr; name = "OCR"; }
        if (tr > top) { top = tr; name = "翻译"; }
        if (sticker > top) { top = sticker; name = "贴图"; }
        double pct = total <= 0 ? 0 : top * 100.0 / total;
        TxtStatsRatioHint.Text = name + " 占 " + pct.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>统计时间范围切换：只刷 UI，不落盘（视图偏好）。</summary>
    private void OnStatsRangeChanged(object? sender, RoutedEventArgs e)
    {
        if (RadStatsRange7?.IsChecked == true) _statsRangeDays = 7;
        else if (RadStatsRange90?.IsChecked == true) _statsRangeDays = 90;
        else _statsRangeDays = 30;
        RefreshUsageStatsUi();
    }

    /// <summary>计数千分位展示（数字是本卡片的主视觉，读数要一眼可辨）。</summary>
    private static string FormatCount(long value)
    {
        return value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnStatsClearClick(object? sender, RoutedEventArgs e)
    {
        if (ConfirmOverlay.IsVisible) return;
        TxtConfirmTitle.Text = "清空全部使用统计？";
        TxtConfirmBody.Text = "将清空本机截图、AI 对话、OCR、翻译和贴图的全部累计次数及趋势记录，无法恢复。";
        BtnConfirmOk.Content = "确认清空";
        _confirmAction = DoStatsClear;
        ConfirmOverlay.IsVisible = true;
        BtnConfirmCancel.Focus();
    }

    private void DoStatsClear()
    {
        try
        {
            UsageStatistics.ClearAll();
            RefreshUsageStatsUi();
            ShowStatus("已清空本机使用统计。");
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.OnStatsClearClick 异常", ex);
            ShowError("清空统计失败：" + ex.Message);
        }
    }

    #region 截图历史

    /// <summary>
    /// 刷新「历史存储」行的用量说明与按钮可用态。
    /// 读的是 <see cref="CaptureHistoryStore"/> 的索引（首次会懒加载一次磁盘），失败只记 Warn。
    /// </summary>
    private void RefreshHistoryUsageUi()
    {
        try
        {
            if (TxtHistoryUsage == null) return;

            int count = CaptureHistoryStore.Count;
            long bytes = CaptureHistoryStore.TotalBytes;
            int maxCount = AppSettings.NormalizeCaptureHistoryMaxCount(_settings.CaptureHistoryMaxCount);
            int maxMb = AppSettings.NormalizeCaptureHistoryMaxMegabytes(_settings.CaptureHistoryMaxMegabytes);

            if (count <= 0)
            {
                TxtHistoryUsage.Text = "暂无历史记录 · 上限 " + maxCount + " 张 / " + maxMb + " MB";
            }
            else
            {
                string size = bytes < 1024L * 1024L
                    ? (bytes / 1024.0).ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + " KB"
                    : (bytes / 1024.0 / 1024.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " MB";
                TxtHistoryUsage.Text = "共 " + count + " 张 · 占用 " + size
                    + " · 上限 " + maxCount + " 张 / " + maxMb + " MB（任一超限从最旧删起）";
            }

            if (BtnHistoryClear != null) BtnHistoryClear.IsEnabled = count > 0;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.RefreshHistoryUsageUi 失败：" + ex.Message);
        }
    }

    private void OnHistoryOpenClick(object? sender, RoutedEventArgs e)
    {
        try { CaptureHistoryWindow.ShowHistory(); }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.OnHistoryOpenClick 异常", ex);
            ShowError("打开截图历史失败：" + ex.Message);
        }
    }

    private void OnHistoryClearClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            CaptureHistoryStore.ClearAll();
            RefreshHistoryUsageUi();
            ShowStatus("已清空截图历史。");
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.OnHistoryClearClick 异常", ex);
            ShowError("清空截图历史失败：" + ex.Message);
        }
    }

    #endregion

    private void OnRestoreDefaultsClick(object? sender, RoutedEventArgs e)
    {
        // 必须"先落盘、再整表重载"：只替换内存中的 _settings 并裸调 ApplySettingsToUi，
        // 会因 _isLoading 未置位让逐项赋值反向触发 AutoSaveSettings，把半新半旧的 UI 状态写回磁盘，
        // 工具栏布局草稿与 AppSettings.Current 缓存也不会跟着重置，界面看着已恢复、实际被静默回滚。
        var defaults = new AppSettings
        {
            // 保留启动意图与管理员档位，避免恢复其他设置时破坏计划任务接管关系。
            RunOnStartup = _settings.RunOnStartup,
            RunAsAdmin = _settings.RunAsAdmin,
        };
        defaults.Save();

        // 动作表一并恢复默认（常用核心预设）
        ScreenCaptureTool.Core.Actions.ActionsStore.Save(
            ScreenCaptureTool.Core.Actions.ActionsStore.CreateDefaults(defaults));

        ReloadAllFromDisk();
        ActionsChanged?.Invoke(this, EventArgs.Empty);

        ShowStatus("已恢复默认设置，立即生效。");
    }

    // -------------------- 翻译 --------------------

    private void OnTranslateProviderChanged(object? sender, RoutedEventArgs e)
    {
        UpdateProviderCardHighlight();
    }

    /// <summary>
    /// 根据当前选中的服务商，仅显示对应 API 密钥卡片，隐藏其余卡片。
    /// </summary>
    private void UpdateProviderCardHighlight()
    {
        bool baiduActive = RadTranslateBaidu.IsChecked == true;
        bool youdaoActive = RadTranslateYoudao.IsChecked == true;
        bool indexActive = RadTranslateIndex.IsChecked == true;
        if (TxtTranslateModeHint != null)
            TxtTranslateModeHint.Text = indexActive
                ? "图片到图片：本机识别横排文字、修复背景并回填译文；文字译文通过 bilibili-index 获取。"
                : "图片到文本：OCR 识别后翻译为纯文本；图片到图片：将译文覆盖回原图对应位置。";

        // 只显示选中服务商的卡片，隐藏另一个
        if (BaiduKeyCard != null)
            BaiduKeyCard.IsVisible = baiduActive;
        if (YoudaoKeyCard != null)
            YoudaoKeyCard.IsVisible = youdaoActive;
        if (TranslateOptionsCard != null)
            TranslateOptionsCard.IsVisible = baiduActive || youdaoActive || indexActive;
        if (RadTranslateImageToImage != null)
        {
            RadTranslateImageToImage.IsEnabled = true;
            ToolTip.SetTip(RadTranslateImageToImage, indexActive ? "复用本机 OCR，翻译文字后在本机生成译文图片" : null);
        }

        // 清空内联验证结果，避免切回另一服务商时残留上一次结论
        if (BaiduValidateStatus != null)
            BaiduValidateStatus.IsVisible = false;
        if (YoudaoValidateStatus != null)
            YoudaoValidateStatus.IsVisible = false;

        UpdateTranslateTextOnlyRows();
    }

    /// <summary>
    /// 流式翻译、极简浮窗位置、自动关闭与复制只在图片到文本模式下出现；
    /// 单独成方法是因为切换翻译模式时只需刷这里，不该连带清掉密钥内联验证结果。
    /// </summary>
    private void UpdateTranslateTextOnlyRows()
    {
        if (PnlTranslateTextOnly != null)
            PnlTranslateTextOnly.IsVisible = RadTranslateImageToText?.IsChecked == true;
    }

    private void PopulateTranslateLanguages(string? currentCode)
    {
        CmbTranslateTargetLang.Items.Clear();
        foreach (var (displayName, code) in TranslationLanguage.SupportedTargetLanguages)
        {
            var item = new ComboBoxItem { Content = displayName, Tag = code };
            CmbTranslateTargetLang.Items.Add(item);
            if (string.Equals(code, currentCode, StringComparison.OrdinalIgnoreCase))
                CmbTranslateTargetLang.SelectedItem = item;
        }
        if (CmbTranslateTargetLang.SelectedItem == null && CmbTranslateTargetLang.ItemCount > 0)
            CmbTranslateTargetLang.SelectedIndex = 0;
    }

    private void PopulateTranslateSecondaryLanguages(string? currentCode)
    {
        if (CmbTranslateSecondaryTargetLang == null) return;
        CmbTranslateSecondaryTargetLang.Items.Clear();
        foreach (var (displayName, code) in TranslationLanguage.SupportedTargetLanguages)
        {
            var item = new ComboBoxItem { Content = displayName, Tag = code };
            CmbTranslateSecondaryTargetLang.Items.Add(item);
            if (string.Equals(code, currentCode, StringComparison.OrdinalIgnoreCase))
                CmbTranslateSecondaryTargetLang.SelectedItem = item;
        }
        if (CmbTranslateSecondaryTargetLang.SelectedItem == null && CmbTranslateSecondaryTargetLang.ItemCount > 0)
        {
            var enItem = CmbTranslateSecondaryTargetLang.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), "en", StringComparison.OrdinalIgnoreCase));
            CmbTranslateSecondaryTargetLang.SelectedItem = enItem ?? CmbTranslateSecondaryTargetLang.Items[0];
        }
    }

    private void PopulateTranslateSourceLanguages(string? currentCode)
    {
        CmbTranslateSourceLang.Items.Clear();
        foreach (var (displayName, code) in TranslationLanguage.SupportedSourceLanguages)
        {
            var item = new ComboBoxItem { Content = displayName, Tag = code };
            CmbTranslateSourceLang.Items.Add(item);
            if (string.Equals(code, currentCode, StringComparison.OrdinalIgnoreCase))
                CmbTranslateSourceLang.SelectedItem = item;
        }
        if (CmbTranslateSourceLang.SelectedItem == null && CmbTranslateSourceLang.ItemCount > 0)
            CmbTranslateSourceLang.SelectedIndex = 0;
    }

    private async void OnTranslateValidateClick(object? sender, RoutedEventArgs e)
    {
        TranslationProvider provider;
        string appId, secretKey;
        Button btn;

        if (sender == BtnTranslateBaiduValidate)
        {
            provider = TranslationProvider.Baidu;
            appId = (TxtBaiduAppId.Text ?? "").Trim();
            secretKey = (TxtBaiduSecretKey.Text ?? "").Trim();
            btn = BtnTranslateBaiduValidate;
        }
        else if (sender == BtnTranslateYoudaoValidate)
        {
            provider = TranslationProvider.Youdao;
            appId = (TxtYoudaoAppId.Text ?? "").Trim();
            secretKey = (TxtYoudaoSecretKey.Text ?? "").Trim();
            btn = BtnTranslateYoudaoValidate;
        }
        else
        {
            // 兼容：按当前选中的服务商验证
            if (RadTranslateBaidu.IsChecked == true)
            {
                provider = TranslationProvider.Baidu;
                appId = (TxtBaiduAppId.Text ?? "").Trim();
                secretKey = (TxtBaiduSecretKey.Text ?? "").Trim();
                btn = BtnTranslateBaiduValidate;
            }
            else if (RadTranslateYoudao.IsChecked == true)
            {
                provider = TranslationProvider.Youdao;
                appId = (TxtYoudaoAppId.Text ?? "").Trim();
                secretKey = (TxtYoudaoSecretKey.Text ?? "").Trim();
                btn = BtnTranslateYoudaoValidate;
            }
            else
            {
                // 兜底：此处 btn 尚未确定，没有可归属的凭证卡片，只能走底部通栏而非就近内联。
                // 实际从 UI 不可达（本方法只由两张卡各自的验证按钮触发），保留为防御性分支，
                // 因此翻译凭证流程的反馈自始至终只有就近内联一个出口，不存在两条通道各说一遍。
                ShowError("请先选择翻译服务商。");
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(secretKey))
        {
            ShowTranslateValidationResult(btn, "请先填写 APP ID 和 API Key/密钥。", ok: false);
            return;
        }

        btn.IsEnabled = false;
        btn.Content = "验证中…";
        ShowTranslateValidationResult(btn, null, ok: false);

        try
        {
            var config = new TranslationConfig
            {
                Provider = provider,
                AppId = appId,
                SecretKey = secretKey,
                SourceLanguage = TranslationLanguage.Auto,
                TargetLanguage = TranslationLanguage.ChineseSimplified,
            };
            var service = new TranslationService(config);
            var (ok, error) = await service.ValidateAsync();

            if (ok)
                ShowTranslateValidationResult(btn, "验证成功！API 凭证可用。", ok: true);
            else
                ShowTranslateValidationResult(btn, "验证失败：" + error, ok: false);
        }
        catch (Exception ex)
        {
            ShowTranslateValidationResult(btn, "验证异常：" + ex.Message, ok: false);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = (btn == BtnTranslateBaiduValidate) ? "验证凭证" : "验证密钥";
        }
    }

    /// <summary>
    /// 在对应服务商卡片内内联展示凭证验证结果（成功=品牌紫勾选 / 失败=危险红感叹）。
    /// 标题栏状态条离鼠标操作点太远易被忽略，故验证类反馈就近显示；message 传 null 表示清空。
    /// </summary>
    private void ShowTranslateValidationResult(Button btn, string? message, bool ok)
    {
        bool isBaidu = btn == BtnTranslateBaiduValidate;
        var panel = isBaidu ? BaiduValidateStatus : YoudaoValidateStatus;
        var okIcon = isBaidu ? BaiduValidateStatusOkIcon : YoudaoValidateStatusOkIcon;
        var errIcon = isBaidu ? BaiduValidateStatusErrIcon : YoudaoValidateStatusErrIcon;
        var text = isBaidu ? BaiduValidateStatusText : YoudaoValidateStatusText;

        if (string.IsNullOrEmpty(message))
        {
            panel.IsVisible = false;
            return;
        }

        text.Text = message;
        okIcon.IsVisible = ok;
        errIcon.IsVisible = !ok;
        panel.Classes.Remove("ok");
        panel.Classes.Remove("err");
        panel.Classes.Add(ok ? "ok" : "err");
        panel.IsVisible = true;
    }

    private void OnBaiduLinkClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://api.fanyi.baidu.com/manage/apiKey");
    }

    private void OnYoudaoLinkClick(object? sender, RoutedEventArgs e)
    {
        OpenUrl("https://ai.youdao.com/");
    }

    // -------------------- 密码可见性切换 --------------------

    /// <summary>
    /// 切换密码输入框的显示/隐藏状态，同步切换眼睛图标。
    /// </summary>
    private void OnTogglePasswordVisibility(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;

        // 找到同一 Grid 中的 TextBox
        var parent = btn.Parent as Grid;
        if (parent == null) return;
        var textBox = parent.Children.OfType<TextBox>().FirstOrDefault();
        if (textBox == null) return;

        // 切换 PasswordChar：'\0' = 明文显示，'*' = 隐藏
        bool isHidden = textBox.PasswordChar != '\0';
        if (isHidden)
        {
            textBox.PasswordChar = '\0';
        }
        else
        {
            textBox.PasswordChar = '*';
        }

        // 切换眼睛图标可见性
        if (btn.Content is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Avalonia.Controls.Shapes.Path p)
                {
                    // eyeIcon 在隐藏时可见；eyeOffIcon 在显示时可见
                    bool isEyeOff = p.Classes.Contains("eyeOffIcon");
                    p.IsVisible = isEyeOff ? !isHidden : isHidden;
                }
            }
        }
    }

    // -------------------- 交互与行为页 · 四栏切换 --------------------

    private void OnInteractTabSelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (PnlInteractCapture == null || PnlInteractSticker == null
            || PnlInteractRecord == null || PnlInteractMatting == null) return;

        PnlInteractCapture.IsVisible = RadInteractTabCapture?.IsChecked == true;
        PnlInteractSticker.IsVisible = RadInteractTabSticker?.IsChecked == true;
        PnlInteractRecord.IsVisible = RadInteractTabRecord?.IsChecked == true;
        PnlInteractMatting.IsVisible = RadInteractTabMatting?.IsChecked == true;
    }

    // -------------------- 快捷键与动作页 --------------------

    private void OnActionTabSelectionChanged(object? sender, RoutedEventArgs e)
    {
        if (PnlViewCapture == null || PnlViewSticker == null || PnlViewRecord == null
            || PnlViewGlobalActions == null || PnlViewGuide == null) return;

        bool isCapture = RadActionTabCapture?.IsChecked == true;
        bool isSticker = RadActionTabSticker?.IsChecked == true;
        bool isRecord = RadActionTabRecord?.IsChecked == true;
        bool isGlobal = RadActionTabGlobal?.IsChecked == true;
        bool isGuide = RadActionTabGuide?.IsChecked == true;

        PnlViewCapture.IsVisible = isCapture;
        PnlViewSticker.IsVisible = isSticker;
        PnlViewRecord.IsVisible = isRecord;
        PnlViewGlobalActions.IsVisible = isGlobal;
        PnlViewGuide.IsVisible = isGuide;

        if (BtnAddAction != null) BtnAddAction.IsVisible = isGlobal;
        if (BtnResetAllDefaults != null) BtnResetAllDefaults.IsVisible = !isGuide;
    }

    private void OnResetAllShortcutDefaultsClick(object? sender, RoutedEventArgs e)
    {
        if (RadActionTabCapture?.IsChecked == true)
        {
            ResetBuiltinGroupDefaults(Services.BuiltinShortcutsStore.GroupCapture, "截图快捷键");
        }
        else if (RadActionTabSticker?.IsChecked == true)
        {
            ResetBuiltinGroupDefaults(Services.BuiltinShortcutsStore.GroupSticker, "贴图快捷键");
        }
        else if (RadActionTabRecord?.IsChecked == true)
        {
            ResetBuiltinGroupDefaults(Services.BuiltinShortcutsStore.GroupRecord, "录屏快捷键");
        }
        else if (RadActionTabGlobal?.IsChecked == true)
        {
            OnResetActionDefaultsClick(sender, e);
        }
    }

    private void ResetBuiltinGroupDefaults(string group, string groupDisplayName)
    {
        foreach (var sc in _builtinShortcutsDraft.Where(x => x.Group == group))
        {
            // 锁死项（录屏的兜底两枚）连"恢复默认"都不该动：它们没有可改的绑定
            if (sc.Locked) continue;
            Services.BuiltinShortcutsStore.ResetToDefault(sc);
        }
        _recordingBuiltin = null;
        if (TxtCaptureHotKeyTip != null) TxtCaptureHotKeyTip.Text = "";
        if (TxtStickerHotKeyTip != null) TxtStickerHotKeyTip.Text = "";
        if (TxtRecordHotKeyTip != null) TxtRecordHotKeyTip.Text = "";
        RenderBuiltinShortcutList();
        AutoSaveBuiltinShortcuts();
        ShowStatus(groupDisplayName + "已恢复出厂默认。");
    }

    /// <summary>从 ActionsStore 加载草稿。</summary>
    private void LoadActionsDraft()
    {
        _actionsDraft = new List<ScreenCaptureTool.Core.Actions.ActionDefinition>();
        foreach (var a in ScreenCaptureTool.Core.Actions.ActionsStore.Load())
        {
            _actionsDraft.Add(a.Clone());
        }
        RenderActionList();
    }

    /// <summary>渲染动作列表（预设/自定义分组；每行：名称+脚本摘要 / 键盘 / 鼠标 / 编辑、测试、删除）。</summary>
    private void RenderActionList()
    {
        if (ActionListPanel == null) return;
        ActionListPanel.Children.Clear();
        var conflicts = FindConflictHotKeys();

        var builtins = _actionsDraft.Where(a => a.IsBuiltin).ToList();
        var customs = _actionsDraft.Where(a => !a.IsBuiltin).ToList();

        if (builtins.Count > 0)
        {
            ActionListPanel.Children.Add(BuildActionGroupHeader("预设动作", new Thickness(0, 2, 0, 4)));
            foreach (var action in builtins)
            {
                ActionListPanel.Children.Add(BuildActionRow(action, conflicts));
            }
        }

        if (customs.Count > 0)
        {
            ActionListPanel.Children.Add(BuildActionGroupHeader("自定义动作", new Thickness(0, 12, 0, 4)));
            foreach (var action in customs)
            {
                ActionListPanel.Children.Add(BuildActionRow(action, conflicts));
            }
        }

        if (builtins.Count == 0 && customs.Count == 0)
        {
            ActionListPanel.Children.Add(new TextBlock
            {
                Text = "暂无动作，点击右上角「＋ 新增动作」添加。",
                Classes = { "fieldHint" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 16),
            });
        }
    }

    /// <summary>找出草稿中被两个及以上动作重复绑定的热键组合。</summary>
    private HashSet<long> FindConflictHotKeys()
    {
        var duplicated = new HashSet<long>();
        foreach (var a in _actionsDraft)
        {
            if (!a.Enabled || !a.HasHotKey) continue;
            long key = ((long)a.HotKeyModifiers << 32) | a.HotKeyVirtualKey;
            if (Services.BuiltinShortcutsStore.FindActionConflict(a.HotKeyModifiers, a.HotKeyVirtualKey,
                _actionsDraft, a, _builtinShortcutsDraft) != null) duplicated.Add(key);
        }
        return duplicated;
    }

    // 两个绑定列等宽，所有动作与组头使用同一套列宽。
    private const string ActionRowColumns = "1.4*,1.2*,1.2*,Auto";
    private const double ActionButtonWidth = 58;

    private Control BuildActionGroupHeader(string title, Thickness margin)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ActionRowColumns), Margin = margin };
        grid.Children.Add(new TextBlock { Text = title, Classes = { "groupLabel" } });
        for (int column = 1; column <= 3; column++)
        {
            var label = new TextBlock
            {
                Text = column == 1 ? "键盘快捷键" : column == 2 ? "全局鼠标" : "操作",
                TextAlignment = TextAlignment.Center,
                Classes = { "fieldHint" },
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            Grid.SetColumn(label, column);
            if (column == 3) label.Width = ActionButtonWidth * 3 + 12;
            else label.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(label);
        }
        return new ContinuousBorder { Padding = new Thickness(11, 0), Child = grid };
    }

    private Control BuildActionRow(ScreenCaptureTool.Core.Actions.ActionDefinition action, HashSet<long> conflicts)
    {
        // 左：名称 + 脚本摘要
        var nameBlock = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Text = action.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // 默认展示中文步骤，保留点击复制外部调用命令的入口。
        var scriptBlock = new TextBlock
        {
            Classes = { "fieldHint" },
            Margin = new Thickness(0),
            Text = ScreenCaptureTool.Core.Actions.ActionStepCatalog.Summary(action.Script),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        // 复制时还原真实脚本内容；内嵌双引号替换为单引号避免破坏命令行引号结构
        string copyCommand = "yuSnip.exe -r \"" + ScreenCaptureTool.Core.Actions.ActionStepCatalog.ExecutionScript(action).Trim().Replace("\"", "'") + "\"";
        ToolTip.SetTip(scriptBlock, ScreenCaptureTool.Core.Actions.ActionStepCatalog.Summary(action.Script) + "\n点击复制外部调用命令");
        scriptBlock.PointerPressed += async (_, _) =>
        {
            try
            {
                await (TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(copyCommand) ?? Task.CompletedTask);
                ShowStatus("已复制命令：" + copyCommand);
            }
            catch (Exception ex)
            {
                ShowError("复制失败: " + ex.Message);
            }
        };
        var textPanel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        textPanel.Children.Add(nameBlock);
        textPanel.Children.Add(scriptBlock);

        // 中：热键胶囊（点击打开弹窗编辑）；冲突时标红
        long hotkeyId = ((long)action.HotKeyModifiers << 32) | action.HotKeyVirtualKey;
        bool hasConflict = action.Enabled && action.HasHotKey && conflicts.Contains(hotkeyId);

        var chip = BuildKeycapButton(
            text: action.HasHotKey ? FormatHotKey(action.HotKeyModifiers, action.HotKeyVirtualKey) : "未绑定",
            tag: action,
            hasConflict: hasConflict,
            isRecording: false,
            isBound: action.HasHotKey);

        ToolTip.SetTip(chip, hasConflict ? "此组合已被"
            + Services.BuiltinShortcutsStore.FindActionConflict(action.HotKeyModifiers, action.HotKeyVirtualKey,
                _actionsDraft, action, _builtinShortcutsDraft) + "占用，点击重新录制" : "点击编辑动作快捷键");
        chip.Click += OnHotKeyChipClick;

        bool mouseSupported = ScreenCaptureTool.Core.Actions.ActionMouseBinding.SupportsClick(action.Script);
        bool mouseConflict = action.Enabled && action.HasMouseBinding && _actionsDraft.Any(a =>
            !ReferenceEquals(a, action) && a.Enabled && a.HasMouseBinding
            && ScreenCaptureTool.Core.Actions.ActionMouseBinding.Conflicts(a, action));
        var mouseChip = BuildKeycapButton(
            mouseSupported ? ScreenCaptureTool.Core.Actions.ActionMouseBinding.Format(action) : "暂不支持",
            action, mouseConflict, false, action.HasMouseBinding);
        ToolTip.SetTip(mouseChip, !mouseSupported ? "此脚本暂不支持全局鼠标，点击编辑键盘快捷键"
            : mouseConflict ? "鼠标组合与其他动作冲突，点击编辑" : "点击编辑全局鼠标绑定");
        mouseChip.Click += OnHotKeyChipClick;
        foreach (var binding in new[] { chip, mouseChip })
        {
            binding.Margin = new Thickness(0, 0, 10, 0);
            binding.MinWidth = 0;
            binding.Padding = new Thickness(8, 0);
            binding.Classes.Clear();
            binding.Classes.Add("keycap");
            binding.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            binding.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            if (binding.Content is TextBlock text)
            {
                text.FontWeight = FontWeight.Normal;
                text.TextTrimming = TextTrimming.CharacterEllipsis;
                text.TextWrapping = TextWrapping.NoWrap;
                text.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                text.TextAlignment = TextAlignment.Center;
            }
        }
        if (hasConflict) chip.Classes.Add("conflict");
        if (mouseConflict) mouseChip.Classes.Add("conflict");
        if (mouseChip.Content is TextBlock mouseText) mouseText.FontFamily = FontFamily.Default;
        ToolTip.SetTip(mouseChip, (mouseSupported ? ScreenCaptureTool.Core.Actions.ActionMouseBinding.Format(action) : "暂不支持")
            + "\n" + (mouseConflict ? "鼠标组合与其他动作冲突，点击编辑" : "点击编辑全局鼠标绑定"));

        // 右：操作按钮
        var editBtn = new Button { Content = "编辑", Width = ActionButtonWidth, MinWidth = ActionButtonWidth, Tag = action };
        editBtn.Click += OnEditActionClick;
        var testBtn = new Button { Content = "测试", Width = ActionButtonWidth, MinWidth = ActionButtonWidth, Margin = new Thickness(6, 0, 0, 0), Tag = action };
        testBtn.Click += OnTestActionRowClick;
        var deleteBtn = new Button { Content = "删除", Classes = { "danger" }, Width = ActionButtonWidth, MinWidth = ActionButtonWidth, Margin = new Thickness(6, 0, 0, 0), Tag = action };
        deleteBtn.Click += OnDeleteActionRowClick;

        var buttonPanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        buttonPanel.Children.Add(editBtn);
        buttonPanel.Children.Add(testBtn);
        buttonPanel.Children.Add(deleteBtn);
        buttonPanel.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(ActionRowColumns) };
        Grid.SetColumn(chip, 1);
        Grid.SetColumn(mouseChip, 2);
        Grid.SetColumn(buttonPanel, 3);
        grid.Children.Add(textPanel);
        grid.Children.Add(chip);
        grid.Children.Add(mouseChip);
        grid.Children.Add(buttonPanel);

        var border = new ContinuousBorder
        {
            Classes = { "hotkeyRow" },
            Tag = action.Id,
            Child = grid,
        };
        return border;
    }

    /// <summary>创建统一样式的按键按压样式胶囊/Keycap按钮</summary>
    private Button BuildKeycapButton(string text, object tag, bool hasConflict, bool isRecording, bool isBound)
    {
        var chipText = new TextBlock
        {
            FontSize = 12.5,
            FontFamily = new FontFamily("Consolas, 'Cascadia Code', monospace"),
            FontWeight = FontWeight.Bold,
            Text = text,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        };

        if (hasConflict && this.TryFindResource("SctDangerBrush", ActualThemeVariant, out object? dangerBrush) && dangerBrush is IBrush danger)
        {
            chipText.Foreground = danger;
        }
        else if (!isBound && !isRecording && this.TryFindResource("SctTextMutedBrush", ActualThemeVariant, out object? mutedBrush) && mutedBrush is IBrush muted)
        {
            chipText.Foreground = muted;
        }
        else if (this.TryFindResource("SctKeycapForegroundBrush", ActualThemeVariant, out object? keycapFg) && keycapFg is IBrush kfg)
        {
            chipText.Foreground = kfg;
        }

        var chip = new Button
        {
            Classes = { "keycapChip" },
            Content = chipText,
            Tag = tag,
            MinWidth = 88,
            Padding = new Thickness(10, 4),
            Margin = new Thickness(10, 0),
        };

        if (hasConflict)
        {
            chip.Classes.Add("conflict");
        }
        else if (isRecording)
        {
            chip.Classes.Add("recording");
        }

        return chip;
    }

    /// <summary>点击热键胶囊：打开动作编辑弹窗。</summary>
    private void OnHotKeyChipClick(object? sender, RoutedEventArgs e)
    {
        OnEditActionClick(sender, e);
    }

    /// <summary>恢复全部内置动作的出厂默认热键并实时生效。</summary>
    private void OnResetActionDefaultsClick(object? sender, RoutedEventArgs e)
    {
        // 传 null 跳过旧热键字段迁移，确保热键恢复为纯出厂默认值（如区域截图 Ctrl+Alt+A）
        _actionsDraft = ScreenCaptureTool.Core.Actions.ActionsStore.CreateDefaults(null!);
        RenderActionList();
        AutoSaveActions();
        ShowStatus("全局动作已恢复出厂默认。");
    }

    private async void OnAddActionClick(object? sender, RoutedEventArgs e)
    {
        var dlg = new ActionEditorWindow(null, _actionsDraft, _builtinShortcutsDraft);
        bool? result = await dlg.ShowDialog<bool>(this);
        if (result == true && dlg.ResultAction != null)
        {
            _actionsDraft.Add(dlg.ResultAction);
            RenderActionList();
            AutoSaveActions();
            ShowStatus("已添加动作「" + dlg.ResultAction.Name + "」。");
        }
    }

    private async void OnEditActionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not ScreenCaptureTool.Core.Actions.ActionDefinition action) return;
        var dlg = new ActionEditorWindow(action, _actionsDraft, _builtinShortcutsDraft);
        bool? result = await dlg.ShowDialog<bool>(this);
        if (result == true && dlg.ResultAction != null)
        {
            action.Id = dlg.ResultAction.Id;
            action.Name = dlg.ResultAction.Name;
            action.Script = dlg.ResultAction.Script;
            action.IsBuiltin = dlg.ResultAction.IsBuiltin;
            action.HotKeyModifiers = dlg.ResultAction.HotKeyModifiers;
            action.HotKeyVirtualKey = dlg.ResultAction.HotKeyVirtualKey;
            action.MouseModifiers = dlg.ResultAction.MouseModifiers;
            action.MouseButton = dlg.ResultAction.MouseButton;
            action.MouseTrigger = dlg.ResultAction.MouseTrigger;
            action.MousePrefixKey = dlg.ResultAction.MousePrefixKey;
            action.Enabled = dlg.ResultAction.Enabled;
            RenderActionList();
            AutoSaveActions();
            ShowStatus("已更新动作「" + action.Name + "」。");
        }
    }

    private void OnDeleteActionRowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ScreenCaptureTool.Core.Actions.ActionDefinition action)
            DeleteAction(action);
    }

    private void DeleteAction(ScreenCaptureTool.Core.Actions.ActionDefinition? action)
    {
        if (action == null) return;
        _actionsDraft.Remove(action);
        RenderActionList();
        AutoSaveActions();
        ShowStatus("动作「" + action.Name + "」已删除。");
    }

    private void OnTestActionRowClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is ScreenCaptureTool.Core.Actions.ActionDefinition action)
            TestRunScript(ScreenCaptureTool.Core.Actions.ActionStepCatalog.ExecutionScript(action));
    }

    private void TestRunScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            ShowError("脚本内容为空。");
            return;
        }
        try
        {
            if (Application.Current is App app)
            {
                app.RunScript(script);
                ShowStatus("测试运行已发送。");
            }
        }
        catch (Exception ex)
        {
            ShowError("脚本测试运行失败: " + ex.Message);
        }
    }

    // -------------------- 内置快捷键（截图 / 贴图窗口内按键） --------------------

    /// <summary>从 BuiltinShortcutsStore 加载草稿（深拷贝，取消时丢弃）。</summary>
    private void LoadBuiltinShortcutsDraft()
    {
        _builtinShortcutsDraft = new List<Services.BuiltinShortcutsStore.BuiltinShortcut>();
        foreach (var sc in Services.BuiltinShortcutsStore.Load())
        {
            _builtinShortcutsDraft.Add(sc.Clone());
        }
        _recordingBuiltin = null;
        RenderBuiltinShortcutList();
    }

    /// <summary>同组内重复绑定的冲突集合（key: 组|修饰键|主键）。</summary>
    private HashSet<string> FindBuiltinConflicts()
    {
        var duplicated = new HashSet<string>();
        foreach (var sc in _builtinShortcutsDraft)
        {
            if (sc.ModifierOnly) continue; // 按住与滚轮属于不同手势，可共用同一个修饰键。
            if (Services.BuiltinShortcutsStore.FindConflict(sc, _builtinShortcutsDraft, _actionsDraft) != null)
                duplicated.Add(Services.BuiltinShortcutsStore.ConflictKey(sc));
        }
        return duplicated;
    }

    /// <summary>渲染内置快捷键列表（分别放入截图/贴图/录屏容器中，按功能分组显示小标题）。</summary>
    private void RenderBuiltinShortcutList()
    {
        if (CaptureShortcutGroupPanel == null || StickerShortcutGroupPanel == null || RecordShortcutGroupPanel == null) return;
        CaptureShortcutGroupPanel.Children.Clear();
        StickerShortcutGroupPanel.Children.Clear();
        RecordShortcutGroupPanel.Children.Clear();
        var conflicts = FindBuiltinConflicts();

        // 截图快捷键按功能分组：标注工具 / 编辑与操作
        var captureShortcuts = _builtinShortcutsDraft.Where(x => x.Group == Services.BuiltinShortcutsStore.GroupCapture).ToList();
        // 标注工具用白名单显式枚举：新增的 capture.* 项默认落入"编辑与操作"，
        // 不再因为排除法而被自动归进"标注工具"（另存为/快速保存曾因此误栏）。
        var annotationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "capture.arrow", "capture.brush", "capture.shape", "capture.text", "capture.mosaic",
            "capture.spotlight", "capture.counter", "capture.eraser",
        };
        var captureAnnotation = captureShortcuts.Where(x => annotationIds.Contains(x.Id)).ToList();
        var captureEdit = captureShortcuts.Where(x => !captureAnnotation.Contains(x)).ToList();

        if (captureAnnotation.Count > 0 || captureEdit.Count > 0)
        {
            CaptureShortcutGroupPanel.Children.Add(BuildShortcutMatrix(
                ("标注工具", captureAnnotation), ("编辑与操作", captureEdit), conflicts));
        }

        // 贴图快捷键按功能分组：贴图状态 / 操作
        var stickerShortcuts = _builtinShortcutsDraft.Where(x => x.Group == Services.BuiltinShortcutsStore.GroupSticker).ToList();
        var stickerState = stickerShortcuts.Where(x =>
            x.Id == "sticker.lock" || x.Id == "sticker.penetrate" ||
            x.Id == "sticker.thumbnail" || x.Id == "sticker.shadow" || x.Id == "sticker.canvas" || x.ModifierOnly).ToList();
        var stickerAction = stickerShortcuts.Where(x => !stickerState.Contains(x)).ToList();

        if (stickerState.Count > 0 || stickerAction.Count > 0)
        {
            StickerShortcutGroupPanel.Children.Add(BuildShortcutMatrix(
                ("贴图状态", stickerState), ("操作", stickerAction), conflicts));
        }

        // 录屏会话键：左列是可改控制键，右列是走键盘钩子的两条兜底（锁死，只展示不给改）
        var recordShortcuts = _builtinShortcutsDraft.Where(x => x.Group == Services.BuiltinShortcutsStore.GroupRecord).ToList();
        var recordCtrl = recordShortcuts.Where(x => !x.Locked).ToList();
        var recordFallback = recordShortcuts.Where(x => x.Locked).ToList();

        if (recordCtrl.Count > 0 || recordFallback.Count > 0)
        {
            RecordShortcutGroupPanel.Children.Add(BuildShortcutMatrix(
                ("录制控制", recordCtrl), ("兜底键", recordFallback), conflicts));
        }
    }

    /// <summary>构建双列表格矩阵：左右等宽两组并排铺满卡片宽度，组头为浅底通栏标签，行间发丝线分隔。</summary>
    private Control BuildShortcutMatrix(
        (string Label, List<Services.BuiltinShortcutsStore.BuiltinShortcut> Items) left,
        (string Label, List<Services.BuiltinShortcutsStore.BuiltinShortcut> Items) right,
        HashSet<string> conflicts)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        var leftCol = BuildMatrixColumn(left.Label, left.Items, conflicts, withLeftDivider: false);
        var rightCol = BuildMatrixColumn(right.Label, right.Items, conflicts, withLeftDivider: true);
        Grid.SetColumn(leftCol, 0);
        Grid.SetColumn(rightCol, 1);
        grid.Children.Add(leftCol);
        grid.Children.Add(rightCol);
        return new ContinuousBorder { Classes = { "hotkeyMatrix" }, Child = grid };
    }

    /// <summary>矩阵单列：组头通栏标签 + 快捷键行（末行去底部分隔线）。</summary>
    private ContinuousBorder BuildMatrixColumn(string label, List<Services.BuiltinShortcutsStore.BuiltinShortcut> items,
        HashSet<string> conflicts, bool withLeftDivider)
    {
        var stack = new StackPanel();
        stack.Children.Add(new ContinuousBorder
        {
            Classes = { "hotkeyMatrixHead" },
            Child = new TextBlock { Text = label + " · " + items.Count + " 项", Classes = { "hotkeyMatrixHeadText" } },
        });
        for (int i = 0; i < items.Count; i++)
        {
            var row = (ContinuousBorder)BuildBuiltinShortcutRow(items[i], conflicts);
            if (i == items.Count - 1) row.Classes.Add("last");
            stack.Children.Add(row);
        }
        var col = new ContinuousBorder { Child = stack };
        if (withLeftDivider) col.Classes.Add("hotkeyMatrixCol");
        return col;
    }

    private Control BuildBuiltinShortcutRow(Services.BuiltinShortcutsStore.BuiltinShortcut sc, HashSet<string> conflicts)
    {
        var nameBlock = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeight.Normal,
            Text = sc.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };

        bool hasConflict = !sc.ModifierOnly && conflicts.Contains(Services.BuiltinShortcutsStore.ConflictKey(sc));
        bool isRecording = _recordingBuiltin == sc;

        var chip = BuildKeycapButton(
            text: isRecording ? "按下按键…" : Services.BuiltinShortcutsStore.Format(sc),
            tag: sc,
            hasConflict: hasConflict,
            isRecording: isRecording,
            isBound: sc.ModifierOnly || sc.Key != Key.None);

        if (sc.Locked)
        {
            // 锁死项：不给点（进了录制也存不下），置灰那档默认按钮样式自带"这儿不能动"的观感
            chip.IsEnabled = false;
            ToolTip.SetTip(chip, "固定键位，不可修改（走键盘钩子的兜底路径）");
        }
        else
        {
            ToolTip.SetTip(chip, sc.ModifierOnly
                ? sc.Id == "sticker.finezoom" ? "点击后单独按 Shift 或 Alt；滚轮固定，Ctrl+滚轮保留调整透明度"
                    : "点击后单独按 Shift、Alt 或 Ctrl；仍为按住唤出，松开隐藏"
                : hasConflict ? "此组合已被" + Services.BuiltinShortcutsStore.FindConflict(sc, _builtinShortcutsDraft, _actionsDraft)
                    + "占用，点击重新录制" : "点击重新录制快捷键");
            chip.Click += OnBuiltinShortcutChipClick;
            if (sc.Id == Services.BuiltinShortcutsStore.RecordToggleId)
            {
                ToolTip.SetTip(chip, "点击录制；留空时沿用区域截图快捷键，没有区域截图动作则不启用。右键可清空。");
                var clear = new MenuItem { Header = "清空（沿用区域截图）" };
                clear.Click += (_, _) =>
                {
                    sc.Key = Key.None; sc.Modifiers = KeyModifiers.None;
                    _recordingBuiltin = null;
                    AutoSaveBuiltinShortcuts();
                    ResumeGlobalHotkeysAfterRecording();
                    UpdateBuiltinRecordingTip(null);
                    RenderBuiltinShortcutList();
                };
                chip.ContextMenu = new ContextMenu { ItemsSource = new[] { clear } };
            }
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(nameBlock, 0);
        Grid.SetColumn(chip, 1);
        grid.Children.Add(nameBlock);
        grid.Children.Add(chip);

        return new ContinuousBorder
        {
            Classes = { "hotkeyMatrixRow" },
            Tag = sc.Id,
            Child = grid,
        };
    }

    /// <summary>点击内置快捷键胶囊：进入录制。</summary>
    private void OnBuiltinShortcutChipClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.Tag is not Services.BuiltinShortcutsStore.BuiltinShortcut sc) return;
        _recordingBuiltin = sc;
        UpdateBuiltinRecordingTip(sc);
        RenderBuiltinShortcutList();

        // 录制期间临时反注册全局热键，防止已注册热键拦截用户按键
        if (Application.Current is App app)
        {
            app.SuspendGlobalHotkeys();
        }

        Focus();
    }

    /// <summary>录制结束后恢复全局热键注册。</summary>
    private void ResumeGlobalHotkeysAfterRecording()
    {
        if (Application.Current is App app)
        {
            app.ResumeGlobalHotkeys();
        }
    }

    private void UpdateBuiltinRecordingTip(Services.BuiltinShortcutsStore.BuiltinShortcut? sc)
    {
        string tip = sc != null ? "正在录制「" + sc.Name + "」："
            + (sc.ModifierOnly ? sc.Id == "sticker.finezoom" ? "单独按 Shift 或 Alt，滚轮固定（Esc 取消）。"
                : "单独按 Shift、Alt 或 Ctrl（Esc 取消）。" : "请按下新快捷键（Esc 取消）。") : "";
        if (sc == null || sc.Group == Services.BuiltinShortcutsStore.GroupCapture)
        {
            if (TxtCaptureHotKeyTip != null) SetBuiltinShortcutTip(TxtCaptureHotKeyTip, tip, false);
        }
        if (sc == null || sc.Group == Services.BuiltinShortcutsStore.GroupSticker)
        {
            if (TxtStickerHotKeyTip != null) SetBuiltinShortcutTip(TxtStickerHotKeyTip, tip, false);
        }
        if (sc == null || sc.Group == Services.BuiltinShortcutsStore.GroupRecord)
        {
            if (TxtRecordHotKeyTip != null) SetBuiltinShortcutTip(TxtRecordHotKeyTip, tip, false);
        }
    }

    private void SetBuiltinShortcutTip(Services.BuiltinShortcutsStore.BuiltinShortcut sc, string text, bool conflict = false)
    {
        var target = sc.Group == Services.BuiltinShortcutsStore.GroupCapture ? TxtCaptureHotKeyTip
            : sc.Group == Services.BuiltinShortcutsStore.GroupSticker ? TxtStickerHotKeyTip : TxtRecordHotKeyTip;
        if (target != null) SetBuiltinShortcutTip(target, text, conflict);
    }

    private static void SetBuiltinShortcutTip(TextBlock target, string text, bool conflict)
    {
        target.Text = text;
        target.Bind(TextBlock.ForegroundProperty,
            new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension(conflict ? "SctDangerBrush" : "SctAccentBrush"));
    }

    /// <summary>全部内置快捷键恢复出厂默认并实时生效。</summary>
    private void OnResetBuiltinShortcutsClick(object? sender, RoutedEventArgs e)
    {
        foreach (var sc in _builtinShortcutsDraft)
        {
            Services.BuiltinShortcutsStore.ResetToDefault(sc);
        }
        _recordingBuiltin = null;
        UpdateBuiltinRecordingTip(null);
        RenderBuiltinShortcutList();
        ResumeGlobalHotkeysAfterRecording();
        AutoSaveBuiltinShortcuts();
        ShowStatus("内置快捷键已恢复出厂默认。");
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // 清空确认期间焦点留在取消／确认内，回车默认取消，Esc 同样不改统计。
        if (ConfirmOverlay.IsVisible && _confirmAction == DoStatsClear)
        {
            if (e.Key == Key.Escape)
            {
                OnConfirmCancelClick(null, new RoutedEventArgs()); e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                if (BtnConfirmCancel.IsFocused) BtnConfirmOk.Focus();
                else BtnConfirmCancel.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.K && e.KeyModifiers.HasFlag(KeyModifiers.Control)) e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _toolbarDrag.IsActive)
        {
            ResetToolbarDrag(); RenderToolbarPage(); e.Handled = true; return;
        }
        if (WeatherPickerOverlay.IsVisible) { HandleWeatherPickerKey(e); return; }
        if (_recordingBuiltin == null && e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
        {
            TxtSearchBox.Focus();
            TxtSearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // 内置快捷键录制（窗口级按键，允许任意主键 + 任意修饰键组合）
        if (_recordingBuiltin != null)
        {
            e.Handled = true;

            if (e.Key == Key.Escape)
            {
                _recordingBuiltin = null;
                HideStatus();
                UpdateBuiltinRecordingTip(null);
                RenderBuiltinShortcutList();
                ResumeGlobalHotkeysAfterRecording();
                return;
            }

            var sc = _recordingBuiltin;
            var candidate = sc.Clone();
            if (sc.ModifierOnly)
            {
                var modifier = Services.BuiltinShortcutsStore.ModifierOfKey(e.Key);
                if (!Services.BuiltinShortcutsStore.IsAllowedModifier(sc, modifier)
                    || (e.KeyModifiers & ~modifier) != KeyModifiers.None)
                { UpdateBuiltinRecordingTip(sc); return; }
                candidate.Modifiers = modifier;
                candidate.Key = Key.None;
            }
            else
            {
                // 普通快捷键仍等待主键，修饰键专用项不改变原录制规则。
                if (IsModifierKey(e.Key)) return;
                candidate.Modifiers = e.KeyModifiers;
                candidate.Key = e.Key;
            }
            if (sc.Id == Services.BuiltinShortcutsStore.RecordToggleId
                && Services.BuiltinShortcutsStore.VirtualKeyOf(candidate.Key) == 0)
            {
                ShowError("此按键暂不支持录屏快速启停，请换一个快捷键（Esc 取消）。");
                return;
            }
            string? conflict = Services.BuiltinShortcutsStore.FindConflict(candidate, _builtinShortcutsDraft, _actionsDraft);
            if (conflict != null)
            {
                string warning = Services.BuiltinShortcutsStore.Format(candidate) + " 已被" + conflict
                    + "占用，请重新录制（Esc 取消）。";
                SetBuiltinShortcutTip(sc, warning, true);
                // 表格较长时底部提示不在当前视口；复用既有浮动通知，不挤动快捷键列表。
                ShowError(warning);
                return;
            }
            sc.Modifiers = candidate.Modifiers; sc.Key = candidate.Key;
            if (sc.ModifierOnly) Services.BuiltinShortcutsStore.RefreshModifierDefaults(_builtinShortcutsDraft);
            _recordingBuiltin = null;
            ResumeGlobalHotkeysAfterRecording();
            AutoSaveBuiltinShortcuts();

            // 同组冲突即时提示
            string tip = "「" + sc.Name + "」已绑定为 " + Services.BuiltinShortcutsStore.Format(sc) + "。";
            
            if (sc.Group == Services.BuiltinShortcutsStore.GroupCapture && TxtCaptureHotKeyTip != null)
            {
                SetBuiltinShortcutTip(sc, tip);
            }
            else if (sc.Group == Services.BuiltinShortcutsStore.GroupSticker && TxtStickerHotKeyTip != null)
            {
                SetBuiltinShortcutTip(sc, tip);
            }
            else if (sc.Group == Services.BuiltinShortcutsStore.GroupRecord && TxtRecordHotKeyTip != null)
            {
                // 这三枚是"随录随卸"的临时全局键，正在录的那一段已经按旧键注册好了，改不动它
                SetBuiltinShortcutTip(sc, tip + "（下一次开始录制时起效）");
            }
            RenderBuiltinShortcutList();
            ShowStatus(sc.Group == Services.BuiltinShortcutsStore.GroupRecord ? tip + "（下一次开始录制时起效）" : tip);
            return;
        }

        // Ctrl+F：任意页面聚焦顶部搜索框（录制态优先，已在上方拦截）
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            if (TxtSearchBox != null)
            {
                TxtSearchBox.Focus();
                TxtSearchBox.SelectAll();
            }
        }
    }

    private static bool IsModifierKey(Key key)
    {
        return key == Key.LeftCtrl || key == Key.RightCtrl
            || key == Key.LeftAlt || key == Key.RightAlt
            || key == Key.LeftShift || key == Key.RightShift
            || key == Key.LWin || key == Key.RWin;
    }

    /// <summary>把 Avalonia Key 映射为 Win32 虚拟键码；不支持的返回 0。</summary>
    private static uint KeyToVirtualKey(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return (uint)(0x41 + (key - Key.A));
        if (key >= Key.D0 && key <= Key.D9) return (uint)(0x30 + (key - Key.D0));
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return (uint)(0x60 + (key - Key.NumPad0));
        if (key >= Key.F1 && key <= Key.F12) return (uint)(0x70 + (key - Key.F1));
        return key switch
        {
            Key.Space => 0x20,
            Key.Enter => 0x0D,
            Key.Tab => 0x09,
            Key.OemTilde => 0xC0,
            Key.OemMinus => 0xBD,
            Key.OemPlus => 0xBB,
            Key.OemOpenBrackets => 0xDB,
            Key.OemCloseBrackets => 0xDD,
            Key.OemSemicolon => 0xBA,
            Key.OemQuotes => 0xDE,
            Key.OemComma => 0xBC,
            Key.OemPeriod => 0xBE,
            Key.OemQuestion => 0xBF,
            Key.OemBackslash or Key.OemPipe => 0xDC,
            _ => 0,
        };
    }

    // -------------------- 导航 / 懒加载 --------------------

    private void EnsurePageLoaded(SettingsPage page)
    {
        if (!_runtimeEnabled) return; // 离屏导航不启动服务、不读取用户文件。
        // 只生成当前页内容，不再为垂直长页面补齐所有前置面板。
        switch (page)
        {
            case SettingsPage.Actions: EnsureActionsLoaded(); break;
            case SettingsPage.Ai: EnsureAiLoaded(); break;
            case SettingsPage.Deps: EnsureDepsLoaded(); break;
            case SettingsPage.Toolbar: EnsureToolbarLoaded(); break;
            case SettingsPage.Stats: EnsureStatsLoaded(); break;
        }
    }

    private void EnsureActionsLoaded()
    {
        if (_actionsLoaded) return;
        _actionsLoaded = true;
        LoadActionsDraft();
        LoadBuiltinShortcutsDraft();
    }

    private void EnsureAiLoaded()
    {
        if (_aiLoaded) return;
        _aiLoaded = true;
        RenderAiPromptPresets();
        RenderAiProviders();
    }

    private void EnsureToolbarLoaded()
    {
        if (_toolbarLoaded) return;
        _toolbarLoaded = true;
        // 列数随窗口宽度重算：本分支只走一次，订阅不会重复
        ToolbarGroupCards.SizeChanged += (_, _) => UpdateGroupColumns();
        RenderToolbarPage();
    }

    private void EnsureStatsLoaded()
    {
        if (_statsLoaded) return;
        _statsLoaded = true;
        RefreshUsageStatsUi();
    }

    private void ScrollToPanel(Control panel, SettingsPage page)
    {
        EnsurePageLoaded(page);
        SetNavActiveOnly(page);
    }

    private void OnNavOverviewClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelOverview, SettingsPage.Overview);
    private void OnOverviewNavigateClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target } && Enum.TryParse<SettingsPage>(target, out var page)) NavigateToPage(page);
    }

    private void OnNavBasicClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelBasic, SettingsPage.Basic);
    private void OnNavInteractClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelInteract, SettingsPage.Interact);
    private void OnNavAnnotClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelAnnot, SettingsPage.Annot);
    private void OnNavActionsClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelActions, SettingsPage.Actions);
    private void OnNavTranslateClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelTranslate, SettingsPage.Translate);
    private void OnNavAiClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelAi, SettingsPage.Ai);
    private void OnNavCloudSyncClick(object? sender, RoutedEventArgs e)
    {
        ScrollToPanel(PanelCloudSync, SettingsPage.CloudSync);
        _automaticCloud?.RequestCheck();
    }
    private void OnNavDepsClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelDeps, SettingsPage.Deps);
    private void OnNavToolbarClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelToolbar, SettingsPage.Toolbar);
    private void OnNavStatsClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelStats, SettingsPage.Stats);
    private void OnNavAboutClick(object? sender, RoutedEventArgs e) => ScrollToPanel(PanelAbout, SettingsPage.About);

    /// <summary>
    /// 外部跳转入口：滚动并切换至指定分页。
    /// </summary>
    public void NavigateToPage(SettingsPage page)
    {
        if (page == SettingsPage.CloudSync) _automaticCloud?.RequestCheck();
        Control? panel = GetPanelByPage(page);
        if (panel == null) return;
        EnsurePageLoaded(page);
        Dispatcher.UIThread.Post(() =>
        {
            ScrollToPanel(panel, page);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>外部跳转入口：滚动并切换至「使用统计」分页。</summary>
    public void NavigateToStats() => NavigateToPage(SettingsPage.Stats);

    /// <summary>外部跳转入口：滚动并切换至「工具栏与样式」分页。</summary>
    public void NavigateToToolbar() => NavigateToPage(SettingsPage.Toolbar);

    /// <summary>外部跳转入口：滚动并切换至「依赖运行」分页并高亮组件卡。</summary>
    public void NavigateToDeps()
    {
        NavigateToPage(SettingsPage.Deps);
        Dispatcher.UIThread.Post(() =>
        {
            if (CardDeps == null) return;
            CardDeps.BringIntoView();
            FlashHighlightTarget(CardDeps);
        }, DispatcherPriority.Loaded);
    }


    // ============ 依赖运行：三行状态机 ＋ 存储卡 ============

    /// <summary>各行进度轨最近一次的完成比例（轨道宽度变化时要按它重算填充）。</summary>
    private readonly Dictionary<string, double> _depRatio = new();
    private bool _depsSubscribed;
    /// <summary>确认浮层点头后要卸载的那一行（浮层摊开期间只有这一个待执行动作）。</summary>
    private string? _uninstallId;
    private Action? _confirmAction;

    private (ContinuousBorder? Pill, TextBlock? PillText, Button? Btn, Button? UninstallBtn, StackPanel? DlPanel,
             ContinuousBorder? Track, ContinuousBorder? Fill, TextBlock? DlText, TextBlock? ErrText) DepControls(string id) => id switch
    {
        DepIds.Ocr => (PillDepOcr, TxtDepOcrPill, BtnDepOcr, BtnDepUninstallOcr, PnlDepOcrDl, TrackDepOcr, FillDepOcr, TxtDepOcrDl, TxtDepOcrErr),
        DepIds.Matting => (PillDepMatting, TxtDepMattingPill, BtnDepMatting, BtnDepUninstallMatting, PnlDepMattingDl, TrackDepMatting, FillDepMatting, TxtDepMattingDl, TxtDepMattingErr),
        _ => (PillDepFfmpeg, TxtDepFfmpegPill, BtnDepFfmpeg, BtnDepUninstallFfmpeg, PnlDepFfmpegDl, TrackDepFfmpeg, FillDepFfmpeg, TxtDepFfmpegDl, TxtDepFfmpegErr),
    };

    private static readonly string[] DepIdsInRowOrder = { DepIds.Ocr, DepIds.Matting, DepIds.Ffmpeg };

    private void EnsureDepsLoaded()
    {
        if (!_depsSubscribed)
        {
            _depsSubscribed = true;
            DependencyManager.Changed += OnDependencyChanged;
            DependencyManager.ProgressTick += OnDependencyProgress;
            TrackDepOcr.SizeChanged += (_, _) => ApplyTrackFill(DepIds.Ocr);
            TrackDepMatting.SizeChanged += (_, _) => ApplyTrackFill(DepIds.Matting);
            TrackDepFfmpeg.SizeChanged += (_, _) => ApplyTrackFill(DepIds.Ffmpeg);
        }
        RefreshDepsUi();
        _ = FetchDepsManifestAsync();
    }

    /// <summary>只为把「未下载 X MB」那几个读数填准；拉不到清单就沿用磁盘缓存，界面上不催网络。</summary>
    private async Task FetchDepsManifestAsync()
    {
        if (!await DependencyManager.RefreshManifestAsync(force: false).ConfigureAwait(false)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_depsSubscribed) RefreshDepsUi();
        });
    }

    private void OnDependencyChanged(string id) => PostDepRefresh(id, withStore: true);
    private void OnDependencyProgress(string id) => PostDepRefresh(id, withStore: false);

    private void PostDepRefresh(string id, bool withStore)
    {
        if (!_depsSubscribed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_depsSubscribed) return;
            PaintDepRow(id);
            if (withStore) RefreshDepsStore();
        });
    }

    private void RefreshDepsUi()
    {
        foreach (string id in DepIdsInRowOrder) PaintDepRow(id);
        RefreshDepsStore();
    }

    /// <summary>按 <see cref="DependencyManager"/> 的快照重画一行：胶囊配色、按钮字与档位、进度排、失败原因行。</summary>
    private void PaintDepRow(string id)
    {
        var (pill, pillText, btn, uninstallBtn, dlPanel, track, fill, dlText, errText) = DepControls(id);
        if (pill == null || pillText == null || btn == null || uninstallBtn == null || dlPanel == null
            || track == null || fill == null || dlText == null || errText == null) return;

        var s = DependencyManager.Get(id);
        var pkg = DependencyManager.CurrentManifest()?.Find(id);

        pill.Classes.Remove("ok");
        pill.Classes.Remove("bad");
        pill.Classes.Remove("busy");
        btn.Classes.Remove("primary");
        btn.Classes.Remove("secondary");
        btn.Classes.Add("secondary");   // 只有未就绪那行的「下载」升 primary，视觉重量按稿 02 甲压给它
        uninstallBtn.IsVisible = s.State == DepState.Ready;   // 没装的东西谈不上卸载，别的状态下这枚不占位
        errText.IsVisible = false;

        switch (s.State)
        {
            case DepState.Ready:
                pill.Classes.Add("ok");
                pillText.Text = "已就绪 " + FormatMb(s.OnDiskBytes);
                btn.Content = "重新下载";
                break;

            case DepState.Queued:
                pill.Classes.Add("busy");
                pillText.Text = "排队中";
                btn.Content = "取消";
                dlPanel.IsVisible = true;
                SetRatio(id, 0);
                dlText.Text = "排在其他组件之后";
                break;

            case DepState.Downloading:
                pill.Classes.Add("busy");
                pillText.Text = s.Total > 0 ? "下载中 " + s.Percent + "%" : "下载中";
                btn.Content = "取消";
                dlPanel.IsVisible = true;
                SetRatio(id, s.Total > 0 ? (double)s.Done / s.Total : 1);
                dlText.Text = FormatMb(s.Done) + " / " + (s.Total > 0 ? FormatMb(s.Total) : "未知")
                    + (s.SpeedBps > 1 ? " · " + FormatMb((long)s.SpeedBps) + "/s" : "")
                    + (s.ShowEta ? " · 剩 " + EtaWords(s.EtaSeconds) : "");
                break;

            case DepState.Verifying:
                pill.Classes.Add("busy");
                pillText.Text = "校验中";
                btn.Content = "取消";
                dlPanel.IsVisible = true;
                SetRatio(id, 1);
                dlText.Text = "逐文件校验 SHA-256 并落位";
                break;

            case DepState.Failed:
                pill.Classes.Add("bad");
                pillText.Text = FailPillWords(s.Error);
                btn.Content = "重试";
                errText.Text = string.IsNullOrEmpty(s.Error) ? "上一次下载没有完成。" : s.Error;
                errText.IsVisible = true;
                break;

            default: // Absent
                pillText.Text = pkg != null ? "未下载 " + FormatMb(pkg.Size) : "未下载";
                btn.Content = "下载";
                btn.Classes.Remove("secondary");
                btn.Classes.Add("primary");
                break;
        }

        if (s.State is not (DepState.Queued or DepState.Downloading or DepState.Verifying))
            dlPanel.IsVisible = false;
    }

    private void SetRatio(string id, double ratio)
    {
        _depRatio[id] = ratio;
        ApplyTrackFill(id);
    }

    /// <summary>填充宽度按轨道实测宽度算（原 FFmpeg 行那个 const 220 到此换成实测值）。
    /// 刚把这一排显出来的那一拍量不到宽度，下一次进度节拍（≤250ms）或 SizeChanged 会补正。</summary>
    private void ApplyTrackFill(string id)
    {
        var (_, _, _, _, _, track, fill, _, _) = DepControls(id);
        if (track == null || fill == null) return;
        double w = track.Bounds.Width;
        fill.Width = w > 0 ? Math.Clamp(w * (_depRatio.TryGetValue(id, out var r) ? r : 0), 0, w) : 0;
    }

    private static string EtaWords(long seconds) =>
        seconds < 60 ? seconds + " 秒" : ((long)Math.Ceiling(seconds / 60.0)) + " 分";

    /// <summary>红胶囊按因换字：只留六字以内的半句，完整原因写在下一行。</summary>
    private static string FailPillWords(string err)
    {
        if (err.Contains("空间")) return "空间不足";
        if (err.Contains("校验")) return "校验不通过";
        if (err.Contains("网络") || err.Contains("连接") || err.Contains("HTTP") || err.Contains("中断")) return "网络异常";
        return "下载失败";
    }

    /// <summary>行内那一枚按钮：同一个位置按当前状态派发到下载／取消／重试／重新下载四个动作。</summary>
    private void OnDepActionClick(object? sender, RoutedEventArgs e)
    {
        string? id = DepIdsInRowOrder.FirstOrDefault(i => DepControls(i).Btn == sender);
        if (id == null) return;

        switch (DependencyManager.Get(id).State)
        {
            case DepState.Absent:
            case DepState.Failed:
                _ = DependencyManager.EnsureReadyAsync(id);
                break;
            case DepState.Queued:
            case DepState.Downloading:
            case DepState.Verifying:
                DependencyManager.Cancel(id);
                break;
            case DepState.Ready:
                _ = DependencyManager.RedownloadAsync(id);
                break;
        }
    }

    // -------------------- 存储卡：存放位置 / 离线导入 --------------------

    private void RefreshDepsStore()
    {
        if (TxtDepsStore == null) return;
        TxtDepsStore.Text = DependencyCatalog.RootDir + " · 已占用 " + FormatMb(DependencyManager.DepsBytes());
    }

    private void OnOpenDepsDirClick(object? sender, RoutedEventArgs e)
        => OpenDirectory(DependencyCatalog.RootDir, createIfMissing: true);

    /// <summary>行名：确认浮层与提示语里指代这一行，用字与左列标题一致。</summary>
    private static string DepDisplayName(string id) => id switch
    {
        DepIds.Ocr => "OCR 识别引擎",
        DepIds.Matting => "抠图模型",
        _ => "FFmpeg 录屏引擎",
    };

    /// <summary>「卸载」只针对这一行：删哪些路径由 Core 那份名单定，这里先过确认浮层把体积报清楚。</summary>
    private void OnDepUninstallClick(object? sender, RoutedEventArgs e)
    {
        string? id = DepIdsInRowOrder.FirstOrDefault(i => DepControls(i).UninstallBtn == sender);
        if (id == null) return;

        long bytes = DependencyManager.UninstallBytes(id);
        _uninstallId = id;
        TxtConfirmTitle.Text = "卸载 " + DepDisplayName(id) + "？";
        TxtConfirmBody.Text = $"将删掉本机这一项的全部文件（约 {FormatMb(bytes)}）与就绪登记，无法撤销。"
            + "这一行会回到「未下载」，要用时点「下载」即可装回来。";
        BtnConfirmOk.Content = "卸载";
        _confirmAction = DoUninstallDep;
        ConfirmOverlay.IsVisible = true;
    }

    private async void DoUninstallDep()
    {
        string? id = _uninstallId;
        _uninstallId = null;
        if (id == null) return;
        // 浮层摊开这段时间里这一行可能已经变成下载中，那种情况下不该动手
        if (DependencyManager.Get(id).State != DepState.Ready)
        {
            ShowStatus("这一项的状态已经变了，卸载没有执行。");
            return;
        }

        // 识别模型被共享 OCR 实例占着，不放开就删不动；这个实例下次用到时会自动重建
        if (id == DepIds.Ocr) Services.CaptureSession.ShutdownSharedOcr();

        var (freed, err) = await DependencyManager.UninstallAsync(id);
        RefreshDepsUi();
        if (err.Length > 0) ShowError($"只删掉 {FormatMb(freed)}，其余正被占用：{err}");
        else ShowStatus($"已卸载 {DepDisplayName(id)}，释放 {FormatMb(freed)}。");
    }

    private async void OnImportDepPackageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择依赖包压缩包",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("压缩包") { Patterns = new[] { "*.zip" } } }
            });
            string? path = files.FirstOrDefault()?.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) return;

            string err = await DependencyManager.ImportPackageAsync(path);
            if (err.Length == 0) ShowStatus("导入完成。");
            else ShowError(err);
            RefreshDepsUi();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.OnImportDepPackageClick 失败：" + ex.Message);
            ShowError("导入失败：" + ex.Message);
        }
    }


    private static string FormatMb(long bytes) => (bytes / 1048576.0).ToString("0.0") + " MB";

    private async void OnBrowseRecSaveDirClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择视频保存目录",
                AllowMultiple = false,
            });
            var folder = folders.FirstOrDefault();
            if (folder == null) return;
            string? path = folder.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                TxtRecSaveDir.Text = path;
                AutoSaveSettings();
            }
            else ShowError("无法获取所选目录的本地路径。");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.OnBrowseRecSaveDirClick 失败：" + ex.Message);
            ShowError("选择目录失败：" + ex.Message);
        }
    }

    private void OnOpenRecSaveDirClick(object? sender, RoutedEventArgs e)
    {
        // 生效目录走 AppSettings 那一份算式（原先这里自己抄了一遍"留空用 视频\yuSnip 录屏"）。
        // 读的是框里的草稿值而不是已落盘的设置项：刚选完还没保存就该打开新选的那个目录。
        OpenDirectory(AppSettings.ResolveRecordSaveDir((TxtRecSaveDir.Text ?? string.Empty).Trim()), createIfMissing: true);
    }

    protected override void OnClosed(EventArgs e)
    {
        ClearSearchHighlight();
        HideStatus(); // 顺带取消并释放状态条的淡出计时 CTS，防止关闭后延时回调打到已销毁控件
        base.OnClosed(e);
    }

    // -------------------- 截图选框样式 --------------------

    private int _selectionBorderColorArgb = AppSettings.DefaultSelectionBorderColorArgb;

    /// <summary>
    /// 选框边框颜色的预设色板。纯数据（用户可任选任意色），与明暗主题无关。
    /// **纯白放首位**：它正是出厂默认色，首开时第一枚亮起，用户一眼就知道当前用的是哪个档。
    /// </summary>
    private static readonly (string Hex, string Tip)[] SelectionBorderColorPresets =
    {
        ("#FFFFFF", "白色（默认）"),
        ("#00E5FF", "青色"),
        ("#4219F2", "品牌紫"),
        ("#EBEBEB", "浅灰"),
        ("#39FF14", "荧光绿"),
    };

    /// <summary>预设色块控件，顺序与 <see cref="SelectionBorderColorPresets"/> 一一对应（选中态刷新靠它）。</summary>
    private readonly List<Button> _selectionColorChips = new List<Button>();

    /// <summary>
    /// 动态构建预设色板。色块承载的是"用户数据色"而非主题色，故直接按预设值建画刷
    /// （C# 无画刷原则约束的是主题色长期持有）；选中态只挂 .active 类，视觉全部交给窗口样式。
    /// </summary>
    private void BuildSelectionColorChips()
    {
        if (PanelSelectionColorChips == null) return;
        PanelSelectionColorChips.Children.Clear();
        _selectionColorChips.Clear();

        foreach (var (hex, tip) in SelectionBorderColorPresets)
        {
            Color preset = Color.Parse(hex);
            var chip = new Button
            {
                Classes = { "colorTile" },
                Content = new ContinuousBorder
                {
                    Classes = { "colorTileFill" },
                    Background = new SolidColorBrush(preset),
                },
            };
            ToolTip.SetTip(chip, tip);
            chip.Click += (_, e) =>
            {
                e.Handled = true;
                UpdateSelectionBorderColorUi(unchecked((int)preset.ToUInt32()));
                AutoSaveSettings();
            };
            _selectionColorChips.Add(chip);
            PanelSelectionColorChips.Children.Add(chip);
        }
    }

    /// <summary>Alpha 兜底为不透明：色板比对与预览描边都按"实际看到的颜色"算。</summary>
    private static int OpaqueArgb(int argb)
    {
        byte a = (byte)((argb >> 24) & 0xFF);
        if (a == 0) a = 0xFF;
        return (a << 24) | (argb & 0x00FFFFFF);
    }

    /// <summary>设置值（ARGB 整数）→ 可直接绘制的 <see cref="Color"/>。</summary>
    private static Color ArgbToColor(int argb)
    {
        int v = OpaqueArgb(argb);
        return Color.FromArgb((byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF),
                              (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF));
    }

    /// <summary>HEX 输入框正在被代码回写文本时置位，避免 TextChanged 回声把用户输入打断。</summary>
    private bool _syncingHexInput;

    /// <summary>
    /// 把颜色灌进预览、色板选中态与 HEX 回显。
    /// <paramref name="syncHexText"/> 传 false 用于"用户正在 HEX 框里逐字输入"的场合：
    /// 此时只更新颜色状态、不回写文本，否则每敲一个字符都会被规范化成 <c>#RRGGBB</c> 并把光标顶到末尾。
    /// </summary>
    private void UpdateSelectionBorderColorUi(int argb, bool syncHexText = true)
    {
        _selectionBorderColorArgb = argb;

        // 当前值展示：自定义取色按钮里的色块 + HEX 输入框
        if (BorderSelectionColorPreview != null)
            BorderSelectionColorPreview.Background = new SolidColorBrush(ArgbToColor(argb));
        if (syncHexText) SyncSelectionBorderHexText();

        // 预设色板选中态：命中某个预设才亮外圈，自定义色则整排都不亮（当前值由 HEX 输入框与取色按钮中心色块表达）
        int opaque = OpaqueArgb(argb);
        for (int i = 0; i < _selectionColorChips.Count && i < SelectionBorderColorPresets.Length; i++)
        {
            bool active = opaque == unchecked((int)Color.Parse(SelectionBorderColorPresets[i].Hex).ToUInt32());
            if (active)
            {
                if (!_selectionColorChips[i].Classes.Contains("active")) _selectionColorChips[i].Classes.Add("active");
            }
            else
            {
                _selectionColorChips[i].Classes.Remove("active");
            }
        }

        UpdateSelectionBorderPreview();
    }

    /// <summary>
    /// 把当前值按规范格式回写进 HEX 输入框。带 <c>_syncingHexInput</c> 闸门，
    /// 避免回写触发的 TextChanged 被当成"用户在输入"而反过来又刷一次状态。
    /// </summary>
    private void SyncSelectionBorderHexText()
    {
        if (TxtSelectionBorderColorHex == null) return;
        _syncingHexInput = true;
        TxtSelectionBorderColorHex.Text = FormatHex(ArgbToColor(_selectionBorderColorArgb));
        _syncingHexInput = false;
    }

    /// <summary>
    /// 当前色的十六进制回显。不透明时用 6 位 <c>#RRGGBB</c>，带透明度时补成 8 位 <c>#AARRGGBB</c>。
    /// 8 位这一支不能省：取色器能选半透明色，而这个框**可编辑且会被回写**——
    /// 若一律只显示 6 位，用户看到的文本与他实际选中的颜色就对不上，随手改一次反而把透明度丢了。
    /// </summary>
    private static string FormatHex(Color c) =>
        c.A == 0xFF ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// 解析用户输入的十六进制色值：接受 <c>#RGB</c> / <c>#RRGGBB</c> / <c>#AARRGGBB</c>，
    /// 前导 <c>#</c> 可省略、大小写不限；3 位简写按"每位重复一次"展开（<c>#F0A</c> → <c>#FF00AA</c>）。
    /// </summary>
    private static bool TryParseHexInput(string? text, out int argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        s = s.Trim();
        if (s.Length == 3)
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        if (s.Length != 6 && s.Length != 8) return false;

        foreach (char ch in s)
        {
            if (!Uri.IsHexDigit(ch)) return false;
        }

        try
        {
            argb = unchecked((int)Color.Parse("#" + s).ToUInt32());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// HEX 框逐字输入：只要当前文本能解析成合法色值，就即时反映到预览与色板选中态，但**不落盘**——
    /// 每敲一个字符写一次配置既没必要也伤磁盘；落盘统一交给回车 / 失焦时的 <see cref="CommitSelectionBorderHexInput"/>。
    /// 输入途中（如刚敲到 <c>#FF</c>）解析失败就静默等下一个字符，不报错、不改文本。
    /// </summary>
    private void OnSelectionBorderHexChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingHexInput) return;
        if (!TryParseHexInput(TxtSelectionBorderColorHex?.Text, out int argb)) return;
        UpdateSelectionBorderColorUi(argb, syncHexText: false);
    }

    private void OnSelectionBorderHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitSelectionBorderHexInput();
        TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        e.Handled = true;
    }

    private void OnSelectionBorderHexLostFocus(object? sender, RoutedEventArgs e) => CommitSelectionBorderHexInput();

    /// <summary>
    /// 提交 HEX 输入。非法（含空串）一律**退回当前值**、不弹错误提示——这个框就贴在色板旁边，
    /// 对错肉眼可辨，弹窗反而是噪音。合法时回写规范化文本（补 <c>#</c> / 展开简写 / 统一大小写），
    /// 只在值真的变了时才落盘（回车会先提交再清焦点、进而触发一次 LostFocus 重复提交，靠这道比较挡掉）。
    /// </summary>
    private void CommitSelectionBorderHexInput()
    {
        if (!TryParseHexInput(TxtSelectionBorderColorHex?.Text, out int argb))
        {
            SyncSelectionBorderHexText();
            return;
        }

        // ⚠ 比对基准必须是 _settings（＝上次真正落盘的值），**不能**用 _selectionBorderColorArgb：
        // 后者已被逐字输入的处理改成新值了，拿它比会恒等、于是永远不落盘。
        // 用 OpaqueArgb 归一化是为了避免"配置里存的是 alpha=0、而 UI 按不透明显示"这种历史值
        // 被误判成有变化而多写一次盘。
        bool changed = OpaqueArgb(argb) != OpaqueArgb(_settings.SelectionBorderColorArgb);
        UpdateSelectionBorderColorUi(argb);
        if (changed) AutoSaveSettings();
    }

    private async void OnSelectionBorderColorClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Color initialColor = ArgbToColor(_selectionBorderColorArgb);

        Color? picked = await ColorPickerWindow.PickAsync(this, initialColor, BtnSelectionBorderColor);
        if (!picked.HasValue) return;

        Color c = picked.Value;
        int newArgb = (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
        UpdateSelectionBorderColorUi(newArgb);
        AutoSaveSettings();
    }

    /// <summary>粗细档位（0~5）对应的分段项，顺序即档位值。</summary>
    private RadioButton?[] SelectionBorderThicknessRadios => new[]
    {
        RadSelectionBorderThickness0, RadSelectionBorderThickness1, RadSelectionBorderThickness2,
        RadSelectionBorderThickness3, RadSelectionBorderThickness4, RadSelectionBorderThickness5,
    };

    /// <summary>从分段器读当前档位；全未选中时退回默认值。</summary>
    private int ReadSelectionBorderThicknessFromUi()
    {
        foreach (var radio in SelectionBorderThicknessRadios)
        {
            if (radio?.IsChecked != true) continue;
            if (int.TryParse(radio.Tag?.ToString(), out int v))
                return AppSettings.NormalizeSelectionBorderThickness(v);
        }
        return AppSettings.DefaultSelectionBorderThickness;
    }

    /// <summary>把档位写回分段器（加载设置时用）。</summary>
    private void UpdateSelectionBorderThicknessUi(int thickness)
    {
        thickness = AppSettings.NormalizeSelectionBorderThickness(thickness);
        foreach (var radio in SelectionBorderThicknessRadios)
        {
            if (radio == null) continue;
            bool shouldCheck = int.TryParse(radio.Tag?.ToString(), out int v) && v == thickness;
            if (radio.IsChecked != shouldCheck) radio.IsChecked = shouldCheck;
        }
        UpdateSelectionBorderPreview();
    }

    private void OnSelectionBorderThicknessCheckedChanged(object? sender, RoutedEventArgs e)
    {
        UpdateSelectionBorderPreview();
        AutoSaveSettings();
    }

    /// <summary>
    /// 刷新预览舞台里的选框描边：颜色 / 线型 / 粗细三参数合流到这一处。
    /// 虚线节距按绝对像素折算成"线宽倍数"（与 SelectionLayer 的 DashPx 同口径：12px 线 + 6px 空），
    /// 否则线宽一变虚线节距会跟着等比放大，与真实截图所见不符。
    /// </summary>
    private void UpdateSelectionBorderPreview()
    {
        if (PrevSelectionBorder == null) return;

        int thickness = ReadSelectionBorderThicknessFromUi();

        PrevSelectionBorder.Stroke = new SolidColorBrush(ArgbToColor(_selectionBorderColorArgb));
        PrevSelectionBorder.StrokeThickness = thickness;

        bool solid = RadSelectionBorderSolid?.IsChecked == true;
        PrevSelectionBorder.StrokeDashArray = (solid || thickness <= 0)
            ? null
            : new Avalonia.Collections.AvaloniaList<double> { 12.0 / thickness, 6.0 / thickness };
    }

    // -------------------- 工具栏与样式 --------------------

    /// <summary>
    /// 组卡草稿：一张卡＝一个组，成员顺序就是真机上二级成员栏的顺序，第一位即主栏那一格显示的图标。
    /// 卡是组的唯一真源，一级栏里那个 <c>[a+b+c]</c> token 只是它的引用，每次渲染前由卡刷出来。
    /// </summary>
    private readonly List<List<string>> _groupCards = new List<List<string>>();
    /// <summary>与 _groupCards 一一对应的成员行容器，拖放命中判定靠它（每次渲染重建）。</summary>
    private readonly List<ContinuousBorder> _groupRows = new List<ContinuousBorder>();
    /// <summary>与 _groupCards 一一对应的整张组卡外部 ContinuousBorder 容器，用于整卡大范围命中检测。</summary>
    private readonly List<ContinuousBorder> _groupCardBorders = new List<ContinuousBorder>();
    /// <summary>拖拽来源：整组／成员拖出时那张卡的下标（-1 ＝ 与组无关）。</summary>
    private int _toolbarDragCardIndex = -1;
    /// <summary>拖拽来源：是否是从组卡的左侧把手（而不是一级栏里的组格）起拖。</summary>
    private bool _toolbarDragIsCardGrip = false;
    /// <summary>拖拽来源：成员在卡内的下标（-1 ＝ 拖的不是组成员）。</summary>
    private int _toolbarDragMemberIndex = -1;
    /// <summary>拖拽途中亮起"可放入"反馈的那一行。</summary>
    private ContinuousBorder? _toolbarDropRowHint;
    /// <summary>拖拽途中亮起高亮描边的那张组卡外部边框。</summary>
    private ContinuousBorder? _toolbarDropCardBorderHint;
    /// <summary>拖拽途中组内动态让位紫色虚线占位框。</summary>
    private Avalonia.Controls.Shapes.Rectangle? _groupDropPlaceholder;
    /// <summary>当前显示组内占位框的组卡下标（-1 ＝ 无）。</summary>
    private int _groupDropPlaceholderCardIndex = -1;
    /// <summary>新建组卡片网格末尾的磁贴引用，用于在拖拽悬停空白处时联动高亮。</summary>
    private Grid? _newGroupTile;
    private Avalonia.Controls.Shapes.Rectangle? _newGroupDash;
    private Button? _newGroupBtn;
    private TextBlock? _newGroupTileText;
    private bool _groupAreaBlankHovered = false;

    private void RenderToolbarPage()
    {
        SyncCardsIntoDraft();
        NormalizeToolbarDraft();
        RenderToolbarLayout();
        ToolbarSceneTitle.Text = CurrentToolbarDefinition.Name + "工具栏";
        ToolbarGroupsArea.IsVisible = true;
        RenderToolbarGroupCards();
        RenderToolbarPool();
        RenderToolbarUsage();
    }

    /// <summary>从设置项重建组卡：一级栏里内联的组在前（保持栏内顺序），组库里未上栏的补在后面。</summary>
    private void RebuildGroupCards()
    {
        _groupCards.Clear();
        foreach (string token in _toolbarDraft)
        {
            if (ToolbarRegistry.IsGroupToken(token)) _groupCards.Add(ToolbarGroupMembers(token));
        }
        foreach (string token in CurrentToolbarDefinition.ParseLibrary(ToolbarScenes.ReadGroups(_settings, _toolbarScene), _toolbarDraft))
        {
            List<string> members = ToolbarGroupMembers(token);
            if (CardOwningToken(members) < 0) _groupCards.Add(members);
        }
    }

    /// <summary>
    /// 把卡里的成员刷回一级栏的组 token。认领卡片用"与该 token 共享成员最多"而不是"组头相同"——
    /// 组内改序、把某枚拖到最左换组头之后，按组头去找就找不到自己那张卡，会把旧成员清单又写回去。
    /// 卡掉到不足 2 枚时那一格散架回独立格、卡本身销毁：宁可摊开，也不要让工具静默消失。
    /// </summary>
    private void SyncCardsIntoDraft()
    {
        for (int i = _toolbarDraft.Count - 1; i >= 0; i--)
        {
            if (!ToolbarRegistry.IsGroupToken(_toolbarDraft[i])) continue;
            int ci = CardOwningToken(ToolbarGroupMembers(_toolbarDraft[i]));
            if (ci < 0) continue; // 卡区里没这张卡了（外部改过配置）：交给 Parse 就地校正
            List<string> card = _groupCards[ci];
            _toolbarDraft.RemoveAt(i);
            int toolCount = card.Count(x => !IsToolbarDivider(x));
            if (toolCount >= ToolbarRegistry.MinGroupMembers) _toolbarDraft.Insert(i, MakeToolbarGroupToken(card));
            else
            {
                _toolbarDraft.InsertRange(i, card.Where(x => !IsToolbarDivider(x)));
                _groupCards.RemoveAt(ci);
            }
        }
    }

    /// <summary>这份成员清单属于哪张卡（按共享成员数认领；同一枚工具跨卡唯一，故不会认错）；无交集返回 -1。</summary>
    private int CardOwningToken(IEnumerable<string> ids)
    {
        List<string> target = ids.Where(id => !IsToolbarDivider(id)).ToList();
        int best = -1, bestHit = 0;
        for (int ci = 0; ci < _groupCards.Count; ci++)
        {
            int hit = _groupCards[ci].Count(id => !IsToolbarDivider(id) && target.Contains(id, StringComparer.OrdinalIgnoreCase));
            if (hit > bestHit) { bestHit = hit; best = ci; }
        }
        return best;
    }

    /// <summary>已上栏的卡数（一级栏里最多只有 MaxGroups 格组）。</summary>
    private int OnBarCardCount() => Enumerable.Range(0, _groupCards.Count).Count(CardOnBar);

    private int CardIndexOfTool(string id) =>
        _groupCards.FindIndex(c => c.Contains(id, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 一级栏里那张组格的下标；未上栏返回 -1。
    /// 🔴 这是"这张卡是否已上栏"的<b>唯一真值来源</b>——判据只能是"栏上确实有一格认领到这张卡"。
    /// 曾经的写法是 <c>CardOwningToken(_groupCards[i]) == i</c>，即拿卡自己的成员反查归属：
    /// 工具跨卡唯一，反查结果永远是它自己 ⇒ 恒真，卡里一凑够 2 枚就对外宣称"已上栏"，而栏上什么都没有。
    /// </summary>
    private int BarIndexOfCard(int cardIndex)
    {
        if (cardIndex < 0 || cardIndex >= _groupCards.Count) return -1;
        for (int i = 0; i < _toolbarDraft.Count; i++)
        {
            if (ToolbarRegistry.IsGroupToken(_toolbarDraft[i])
                && CardOwningToken(ToolbarGroupMembers(_toolbarDraft[i])) == cardIndex) return i;
        }
        return -1;
    }

    /// <summary>这张卡是否已在一级栏里占了一格。</summary>
    private bool CardOnBar(int cardIndex) => BarIndexOfCard(cardIndex) >= 0;

    /// <summary>这枚工具现在被谁占用（一级栏独立格或某张卡）——工具池的"已添加"判定与跨区搬移都靠它。</summary>
    private bool IsToolUsed(string id) => CurrentToolbarDefinition.HasId(_toolbarDraft, id) || CardIndexOfTool(id) >= 0;

    /// <summary>把一枚工具从它现在的住处摘掉（一级栏独立格或所在卡），原卡是否散架交给下一次 Sync。</summary>
    private void DetachToolFromHome(string id)
    {
        int at = _toolbarDraft.FindIndex(t => !ToolbarRegistry.IsGroupToken(t) && !IsToolbarDivider(t)
            && string.Equals(FindToolbarTool(t)?.Id, id, StringComparison.OrdinalIgnoreCase));
        if (at >= 0) _toolbarDraft.RemoveAt(at);
        else
        {
            int ci = CardIndexOfTool(id);
            if (ci >= 0) _groupCards[ci].Remove(id);
        }
    }

    private string ToolName(string id) => ToolbarRegistry.IsGroupToken(id) ? ToolName(ToolbarGroupMembers(id).FirstOrDefault() ?? "") + "工具组" : FindToolbarTool(id)?.Name ?? id;

    // ==================== 渲染 ====================

    private void RenderToolbarLayout()
    {
        ToolbarLayoutPreview.Children.Clear();
        for (int i = 0; i < _toolbarDraft.Count; i++)
        {
            ToolbarLayoutPreview.Children.Add(BuildBarCell(_toolbarDraft[i], i));
        }
    }

    /// <summary>
    /// 一级栏仿真格：与真实工具栏同构——组格画成主栏上那两枚独立圆角钮（左＝第一位工具图标钮、右＝窄箭头钮，中间 2px 空隙），
    /// 成员不在这条栏上，它们画在自己的组卡里。
    /// </summary>
    private Control BuildBarCell(string token, int index)
    {
        bool isGroup = ToolbarRegistry.IsGroupToken(token);
        Control cell;
        if (IsToolbarDivider(token) && _toolbarScene != ToolbarScene.Capture) cell = CreateSceneDivider();
        else if (!isGroup && IsToolbarDivider(token))
        {
            ContinuousBorder hit = new ContinuousBorder();
            hit.Classes.Add("toolbarHit");
            ContinuousBorder line = new ContinuousBorder { Width = 1, Height = 20, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            line.Classes.Add("tbDividerV");
            hit.Child = line;
            cell = hit;
        }
        else if (_toolbarScene != ToolbarScene.Capture)
        {
            var head = TakeScenePreview(isGroup ? ToolbarGroupMembers(token)[0] : token);
            cell = isGroup ? BuildPreviewGroupCell(head) : head;
        }
        else
        {
            Button button = new Button { Cursor = new Cursor(StandardCursorType.SizeAll) };
            button.Classes.Add("toolIcon");
            List<string> members = isGroup ? ToolbarGroupMembers(token) : new List<string> { token };
            ToolbarButtonInfo? info = members.Count > 0 ? FindToolbarTool(members[0]) : null;
            if (info != null)
            {
                button.Content = CreateCaptureToolbarIcon(info);
                ToolTip.SetTip(button, isGroup
                    ? $"{info.Name} 等 {members.Count} 项（整组）：在栏里左右拖＝改位置；拖出栏＝撤下这一格（保留在卡里）；拖到工具池＝解散整组"
                    : info.Name);
            }
            cell = isGroup && info != null ? BuildPreviewGroupCell(button) : button;
        }

        int cardIndex = isGroup ? CardOwningToken(ToolbarGroupMembers(token)) : -1;
        var host = BuildDraggableCell(cell,
            () =>
            {
                _toolbarDragFromIndex = index;
                _toolbarDragCardIndex = cardIndex;
                _toolbarDragMemberIndex = -1;
                _toolbarDragPoolToken = null;
                _toolbarDragIsCardGrip = false;
            });
        host.ContextMenu = CreateToolbarCellMenu(token, index);
        if (_toolbarScene != ToolbarScene.Capture && host is Grid grip) grip.Background = Brushes.Transparent;
        host.Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(host, IsToolbarDivider(token) ? "分隔线" : ToolName(token));
        host.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F10 && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            { host.ContextMenu.Open(host); e.Handled = true; }
        });
        if (_toolbarScene != ToolbarScene.Capture) ToolTip.SetTip(host, $"{ToolName(token)}：拖动排序或入组，右键调整或隐藏");
        return host;
    }

    /// <summary>分裂格的仿真样子：左半工具图标 + 右半一枚窄箭头，两半之间不画分割线（与真机一致）。</summary>
    private Control BuildPreviewGroupCell(Control head)
    {
        StackPanel cell = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        cell.Classes.Add("toolbarPreviewGroup");
        head.Classes.Add("tbGroupHead");
        cell.Children.Add(head);
        Avalonia.Controls.Shapes.Path chevron = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0 0 L 6.4 0 L 3.2 4 Z"),
            Width = 6.4,
            Height = 4,
        };
        chevron.Classes.Add("tbChevron");
        var arrowButton = new Button { Content = chevron, Cursor = new Cursor(StandardCursorType.SizeAll) };
        arrowButton.Classes.Add("toolIcon");
        arrowButton.Classes.Add("tbGroupChevron");
        cell.Children.Add(arrowButton);
        return cell;
    }

    // 组卡把手保持原有紧凑图标，仅一级工具栏预览采用真实分裂格。
    private Control BuildGroupCellContent(ToolbarButtonInfo? headInfo)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (headInfo != null && CreateToolbarIcon(headInfo, 14) is { } icon) cell.Children.Add(icon);
        else cell.Children.Add(new TextBlock { Text = headInfo?.Name ?? "组", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        var chevron = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse("M 0 0 L 6.4 0 L 3.2 4 Z"), Width = 6.4, Height = 4, Margin = new Thickness(2, 0, 0, 0) };
        chevron.Classes.Add("tbChevron");
        cell.Children.Add(chevron);
        return cell;
    }

    /// <summary>组卡最小列宽：低于此值就不再多开一列（列数 = 可用宽度按这一档折算，等宽列铺满整行）。</summary>
    private const double GroupCardMinWidth = 200;
    /// <summary>列间距，与卡片 <c>Margin</c> 的右/下留白同源。</summary>
    private const double GroupCardGap = 10;

    /// <summary>
    /// 组卡区（方案甲 · 并排抽屉格）：一张卡＝一个组，横向并排。
    /// 上栏与未上栏的区分：**未上栏整卡降一档压暗**（<c>off</c>），上栏是正常描边的实底卡。
    /// ⚠ 未上栏不再叠那层紫色虚线框（2026-09-20 按用户要求去掉——它贴着卡片实线框的内沿，看着就是"多一条框"）。
    /// 卡身单行双矩形（2026-09-14（三））：左边整组把手一枚圆角钮、右边成员行另一枚圆角矩形，同一排彼此独立。
    /// 上栏／撤栏／并组／移除全靠拖，卡上不放任何按钮（悬停 ✕ 已废除——成员拖出卡＝退回工具池，
    /// 少于 2 枚组自动散架；栏上那格拖出栏＝撤下）。
    /// </summary>
    private void RenderToolbarGroupCards()
    {
        ToolbarGroupCards.Children.Clear();
        _groupRows.Clear();
        _groupCardBorders.Clear();
        bool barFull = OnBarCardCount() >= ToolbarRegistry.MaxGroups;
        int onBar = 0;

        for (int ci = 0; ci < _groupCards.Count; ci++)
        {
            int cardIndex = ci; // 闭包捕获：卡的下标要固定在本次循环
            List<string> members = _groupCards[cardIndex];
            bool bar = CardOnBar(cardIndex);
            if (bar) onBar++;

            ContinuousBorder card = new ContinuousBorder { Margin = new Thickness(0, 0, GroupCardGap, GroupCardGap) };
            card.Classes.Add("tbGroupCard");
            if (!bar) card.Classes.Add("off");

            // 单行双矩形：Auto 列放把手钮、Star 列放成员行，同一排、两枚独立圆角矩形（中间 6px 空隙）
            Grid body = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                Margin = new Thickness(0, 2, 0, 0),
            };
            // 把手始终保持可拖（超额上栏由 DropIntoBar 拦截），确保用户始终可拖去并组或拖到工具池解散删除
            Button grip = new Button
            {
                Content = BuildGroupCellContent(FindToolbarTool(members.FirstOrDefault(m => !IsToolbarDivider(m)) ?? "")),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
            grip.Classes.Add("tbGrip");
            ToolTip.SetTip(grip, members.Count == 0
                ? "拖到下方工具池或卡外＝删除此空组；从工具池拖入工具即可添加成员"
                : (bar
                    ? "在工具栏里左右拖＝改位置；拖出工具栏＝撤下此格（组保留在卡里）；拖到工具池＝解散整组并删除卡片"
                    : "按住拖进上方工具栏＝上栏；拖到别的组卡上＝并过去；拖到工具池＝解散整组并删除卡片"));
            var gripHost = BuildDraggableCell(grip,
                () =>
                {
                    _toolbarDragFromIndex = BarIndexOfCard(cardIndex);
                    _toolbarDragCardIndex = cardIndex;
                    _toolbarDragMemberIndex = -1;
                    _toolbarDragPoolToken = null;
                    _toolbarDragIsCardGrip = true;
                }, true);
            Grid.SetColumn(gripHost, 0);
            body.Children.Add(gripHost);

            // 成员行用 WrapPanel：组内枚数不限（2026-09-20），单行 StackPanel 撑到第五枚就会溢出卡片边框，
            // 换行后卡片随行数长高，因此把手与行一律贴顶（原先的 Center 会让把手悬在两行中间）。
            // 行高仍由 tbGroupRow 的 MinHeight=32 统一（单行时与把手等高）。
            WrapPanel rowPanel = new WrapPanel { Orientation = Orientation.Horizontal };
            if (members.Count == 0)
            {
                // WrapPanel 只按子项自身高度排布，原先靠父级拉伸实现的垂直居中失效，改用 Height+LineHeight 撑满净高
                rowPanel.Children.Add(new TextBlock
                {
                    Text = $"拖工具进来 · 满 {ToolbarRegistry.MinGroupMembers} 枚才算一组",
                    Classes = { "fieldHint" },
                    Margin = new Thickness(2, 0),
                    Height = 26,
                    LineHeight = 26,
                    VerticalAlignment = VerticalAlignment.Top,
                });
            }
            else
            {
                for (int mi = 0; mi < members.Count; mi++) rowPanel.Children.Add(BuildMemberCell(cardIndex, mi, members[mi]));
            }
            ContinuousBorder row = new ContinuousBorder { Child = rowPanel, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            row.Classes.Add("tbGroupRow");
            Grid.SetColumn(row, 1);
            _groupRows.Add(row);
            body.Children.Add(row);

            card.Child = body;
            _groupCardBorders.Add(card);
            ToolbarGroupCards.Children.Add(card);
        }

        bool hasCards = _groupCards.Count > 0;
        ToolbarGroupCards.IsVisible = hasCards;
        ToolbarGroupEmpty.IsVisible = !hasCards;

        if (hasCards)
        {
            // 新建组：网格的最后一格，与组卡同宽同高；虚线格内居中「新建组」文案（2026-09-14（三）按用户口径去掉加号徽标），
            // 悬停整格转品牌紫（样式见 tbGroupNew）
            Grid newTile = new Grid { Margin = new Thickness(0, 0, GroupCardGap, GroupCardGap) };
            var dash = new Rectangle { Classes = { "tbGroupDash" }, Margin = new Thickness(0.5) };
            newTile.Children.Add(dash);
            var btnText = new TextBlock { Text = "新建组", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center };
            Button newBtn = new Button { Content = btnText };
            newBtn.Classes.Add("tbGroupNew");
            ToolTip.SetTip(newBtn, "新建一张空组卡，从工具池往里面拖工具");
            newBtn.Click += OnToolbarNewGroupClick;
            newTile.Children.Add(newBtn);
            ToolbarGroupCards.Children.Add(newTile);

            _newGroupTile = newTile;
            _newGroupDash = dash;
            _newGroupBtn = newBtn;
            _newGroupTileText = btnText;

            UpdateGroupColumns();
        }
        else
        {
            _newGroupTile = null;
            _newGroupDash = null;
            _newGroupBtn = null;
            _newGroupTileText = null;
        }

        ToolbarGroupUsage.Text = $"共 {_groupCards.Count} 张 · {onBar} 张已上栏（上限 {ToolbarRegistry.MaxGroups}）";
    }

    /// <summary>
    /// 组区列数按可用宽度现算：等宽列铺满整行。
    /// 🔴 不许退回"固定宽度 + 换行"——那样右侧永远参差，窗口每个尺寸都长得不对（设计规范 §7.1 多列铺满红线）。
    /// 只改 <c>Columns</c>，UniformGrid 自己重排，不必整页重渲染。
    /// </summary>
    private void UpdateGroupColumns()
    {
        double w = ToolbarGroupCards.Bounds.Width;
        if (w <= 0) return;   // 页还没布局（懒加载/未滚到）：等 SizeChanged 再来一次
        int cols = Math.Max(1, (int)((w + GroupCardGap) / (GroupCardMinWidth + GroupCardGap)));
        if (ToolbarGroupCards.Columns != cols) ToolbarGroupCards.Columns = cols;
    }

    /// <summary>组卡里一枚成员格：拖＝改序／并到别的卡／挪去一级栏变独立格／拖出一切栏＝退回工具池。</summary>
    private Control BuildMemberCell(int cardIndex, int memberIndex, string id)
    {
        if (IsToolbarDivider(id))
        {
            ContinuousBorder hit = new ContinuousBorder();
            hit.Classes.Add("toolbarHit");
            hit.Height = 26;
            ContinuousBorder line = new ContinuousBorder
            {
                Width = 1,
                Height = 16,
                CornerRadius = new CornerRadius(0.5),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            line.Classes.Add("tbDividerV");
            hit.Child = line;
            ToolTip.SetTip(hit, "分隔线：在组内拖动调整排序；拖到一级工具栏移出为独立分隔线；拖到工具池删除");
            return BuildDraggableCell(hit,
                () =>
                {
                    _toolbarDragFromIndex = -1;
                    _toolbarDragCardIndex = cardIndex;
                    _toolbarDragMemberIndex = memberIndex;
                    _toolbarDragPoolToken = null;
                    _toolbarDragIsCardGrip = false;
                });
        }

        ToolbarButtonInfo? info = FindToolbarTool(id);
        Button button = new Button
        {
            Content = info != null ? CreateToolbarIcon(info, 14) ?? new TextBlock { Text = info.Name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center } : null,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        button.Classes.Add("simIcon");
        ToolTip.SetTip(button, $"{info?.Name ?? id}：在本行里拖＝组内改序（拖到最左＝换组头）；拖到别的卡＝并过去；拖到截图工具栏＝移出本组变独立格；拖出一切栏＝退回工具池");
        return BuildDraggableCell(button,
            () =>
            {
                _toolbarDragFromIndex = -1;
                _toolbarDragCardIndex = cardIndex;
                _toolbarDragMemberIndex = memberIndex;
                _toolbarDragPoolToken = null;
                _toolbarDragIsCardGrip = false;
            });
    }

    private void RenderToolbarUsage()
    {
        if (_toolbarScene != ToolbarScene.Capture)
        {
            ToolbarLayoutPreview.Measure(Size.Infinity);
            ToolbarLayoutUsage.Text = $"一级 {_toolbarDraft.Count(t => t != "|")} 格 · {OnBarCardCount()} 组 · {ToolbarLayoutPreview.DesiredSize.Width:0} DIP · 右键调整顺序／显示";
            return;
        }
        int slots = ToolbarRegistry.SlotCount(_toolbarDraft);
        int dividers = _toolbarDraft.Count(t => IsToolbarDivider(t));
        int defaultSlots = ToolbarRegistry.SlotCount(ToolbarRegistry.Parse(null));
        // 36 ＝ 真实工具栏一格（按钮 28 + 左右 margin 4）、9 ＝ 一条分隔线（1 + margin 8）、16 ＝ 内板左右留白。
        // 50 ＝ 一个组格（分裂格 42 = 工具钮 28 + 间隙 2 + 箭头钮 12，加左右 margin 8）：组格比普通格宽 14 DIP，这是它换来的省法。
        int groupSlots = _toolbarDraft.Count(ToolbarRegistry.IsGroupToken);
        int plainSlots = slots - groupSlots;
        // 加上外壳左右各 1 DIP；预览现已与真实工具栏同尺寸。
        ToolbarLayoutUsage.Text = $"一级 {slots} 格 · {groupSlots} 组 · 约 {plainSlots * 36 + groupSlots * 50 + dividers * 9 + 18} DIP（默认 {defaultSlots} 格）";
    }

    /// <summary>
    /// 工具池：图标 + 名称的紧凑 chip，<b>不放任何按钮</b>——已占用的整枚降透明且不可拖，灰态自己说话。
    /// 按住拖＝投送到组卡或一级栏；单击（没拖动）＝加到一级栏末尾，给不想拖的人留一条路。
    /// </summary>
    private void RenderToolbarPool()
    {
        ToolbarButtonPool.Children.Clear();
        foreach (ToolbarButtonInfo info in CurrentToolbarDefinition.Buttons)
        {
            bool added = IsToolUsed(info.Id);
            ContinuousBorder card = new ContinuousBorder { Margin = new Thickness(0, 0, 8, 8) };
            card.Classes.Add("toolbarItem");
            if (added) card.Classes.Add("inactive");

            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (CreateToolbarIcon(info, 15) is { } poolIcon) row.Children.Add(poolIcon);
            row.Children.Add(new TextBlock { Text = info.Name, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            card.Child = row;

            ToolTip.SetTip(card, added ? $"{info.Name}：已在工具栏或某个组里"
                : (IsToolbarNoGroupId(info.Id) ? $"{info.Name}（出口键，不参与分组）· 单击或拖到工具栏" : $"{info.Name} · 单击加到工具栏，或拖进某个组"));

            if (!added)
            {
                card.PointerPressed += (s, e) => OnToolbarPoolPointerPressed(s, e, info.Id);
                card.PointerMoved += OnToolbarItemPointerMoved;
                card.PointerReleased += OnToolbarPoolPointerReleased;
            }
            ToolbarButtonPool.Children.Add(card);
        }

        // 分隔线 chip（2026-09-14（三）取代「＋ 分隔线」按钮）：与其他工具的区别——无限供应，
        // 永远高亮可拖（不存在"已用完"的降档态）；拖进组卡会被 PutToolIntoCard 拒绝（分隔线不进组）
        ContinuousBorder dividerCard = new ContinuousBorder { Margin = new Thickness(0, 0, 8, 8) };
        dividerCard.Classes.Add("toolbarItem");
        StackPanel dividerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        ContinuousBorder dividerGlyph = new ContinuousBorder { Width = 1.5, Height = 15, CornerRadius = new CornerRadius(1) };
        dividerGlyph.Bind(Border.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctIconBrush"));
        dividerRow.Children.Add(dividerGlyph);
        dividerRow.Children.Add(new TextBlock { Text = "分隔线", FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        dividerCard.Child = dividerRow;
        ToolTip.SetTip(dividerCard, "分隔线 · 无限使用：单击插到最后一个工具之前，或直接拖到工具栏/组卡里想隔开的位置");
        dividerCard.PointerPressed += (s, e) => OnToolbarPoolPointerPressed(s, e, ToolbarRegistry.DividerToken);
        dividerCard.PointerMoved += OnToolbarItemPointerMoved;
        dividerCard.PointerReleased += OnToolbarPoolPointerReleased;
        ToolbarButtonPool.Children.Add(dividerCard);
    }

    /// <summary>一级预览按真实截图栏的图标配方绘制，工具池与组卡继续保留原紧凑尺寸。</summary>
    private static Control CreateCaptureToolbarIcon(ToolbarButtonInfo info)
    {
        if (info.Id is "brush" or "arrow" or "text" or "rectangle" or "counter" or "watermark" or "eraser" or "undo" or "mosaic" or "spotlight")
            return AnnotationToolButton.CreateIcon(info.Id)!;
        double size = 16 * info.IconScale;
        var icon = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(info.IconPath), Width = size, Height = size, Stretch = Stretch.Uniform };
        if (!info.IsFill || info.Id == "record") icon.Classes.Add("icon");
        if (info.Id == "record") icon.Classes.Add("iconFill");
        if (info.IsFill) icon.Bind(Avalonia.Controls.Shapes.Path.FillProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("SctIconBrush"));
        if (info.StrokeColor != null) icon.Stroke = SolidColorBrush.Parse(info.StrokeColor);
        if (info.Id == "aivision") icon.RenderTransform = new TranslateTransform(0, 1.8);
        return icon;
    }

    private Control? CreateToolbarIcon(ToolbarButtonInfo info, double size)
    {
        if (_toolbarScene != ToolbarScene.Capture) return CreateScenePoolIcon(info.Id, size);
        double scaled = size * (info.IconScale > 0 ? info.IconScale : 1.0);
        // 马赛克实际同时填充与描边；只按注册表 IsFill 画会与截图工具栏不同。
        if (info.Id == "mosaic")
        {
            var shared = AnnotationToolButton.CreateIcon(info.Id)!;
            shared.Width = shared.Height = scaled;
            return shared;
        }
        Avalonia.Controls.Shapes.Path icon = new Avalonia.Controls.Shapes.Path
        {
            Width = scaled,
            Height = scaled,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse(info.IconPath),
            Classes = { info.IsFill ? "tbFill" : "tbStroke" },
        };
        if (!info.IsFill && !string.IsNullOrEmpty(info.StrokeColor))
        {
            icon.Stroke = SolidColorBrush.Parse(info.StrokeColor);
        }
        // Uniform 拉伸后非正方形图形会锚定在左上角（视觉偏上/偏左），按边界框补偿使其几何居中
        Rect b = icon.Data.Bounds;
        if (b.Width > 0 && b.Height > 0)
        {
            double s = scaled / Math.Max(b.Width, b.Height);
            double dx = (scaled - b.Width * s) / 2;
            double dy = (scaled - b.Height * s) / 2;
            if (dx > 0.05 || dy > 0.05)
                icon.RenderTransform = new TranslateTransform(dx, dy);
        }
        return icon;
    }

    /// <summary>
    /// 把一格包成可拖单元。canDrag=false 时只包装不挂拖拽事件——把手虽然被禁用，但 Tunnel 处理器挂在
    /// 外层 Grid 上照样能起拖，必须从这一层就断掉。悬停 ✕ 已废除（2026-09-14（三））：移除只靠拖拽。
    /// </summary>
    private Control BuildDraggableCell(Control cell, Action beginSource, bool canDrag = true)
    {
        Grid host = new Grid();
        host.Children.Add(cell);

        if (!canDrag) return host;

        // Button 类处理会拦截指针事件，Tunnel 路由优先收到拖拽事件
        host.AddHandler(InputElement.PointerPressedEvent,
            (s, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                beginSource();
                _toolbarDragSourceItem = cell;
                _toolbarDragging = false;
                _toolbarDrag.Begin(cell, e, this);
                e.Handled = true;
            },
            RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerMovedEvent,
            (s, e) => OnToolbarItemPointerMoved(s, e),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        host.AddHandler(InputElement.PointerReleasedEvent,
            (s, e) => OnToolbarCellPointerReleased(s, e),
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        return host;
    }

    // ==================== 组的操作 ====================

    /// <summary>
    /// 规范化组卡成员列表：保证首位不为分隔线、折叠连续分隔线；
    /// 在组卡编辑态下允许暂存末尾单条分隔线，方便用户继续拖入后置工具（真正上栏时由 CleanGroup 自动清洗尾部分隔线）。
    /// </summary>
    private static void NormalizeCardMembers(List<string> card)
    {
        while (card.Count > 0 && IsToolbarDivider(card[0]))
        {
            card.RemoveAt(0);
        }
        for (int i = card.Count - 1; i > 0; i--)
        {
            if (IsToolbarDivider(card[i]) && IsToolbarDivider(card[i - 1]))
            {
                card.RemoveAt(i);
            }
        }
    }

    /// <summary>把一枚工具或分隔线放进某张卡（改序／并入／从池或独立格加入）；越界一律拒绝并说明原因，不静默改数据。</summary>
    private void PutToolIntoCard(string id, int cardIndex, int insertAt)
    {
        if (cardIndex < 0 || cardIndex >= _groupCards.Count) return;
        bool isDivider = IsToolbarDivider(id);
        if (!isDivider && (string.IsNullOrEmpty(id) || FindToolbarTool(id) == null)) return;
        List<string> card = _groupCards[cardIndex];

        if (isDivider)
        {
            int toolCount = card.Count(x => !IsToolbarDivider(x));
            if (toolCount == 0)
            {
                ShowError("空组无法直接放入分隔线，请先拖入工具作为组头。");
                return;
            }

            if (_toolbarDragMemberIndex >= 0 && _toolbarDragCardIndex == cardIndex)
            {
                if (_toolbarDragMemberIndex < card.Count)
                {
                    card.RemoveAt(_toolbarDragMemberIndex);
                    if (insertAt > _toolbarDragMemberIndex) insertAt--;
                }
            }
            else if (_toolbarDragMemberIndex >= 0 && _toolbarDragCardIndex >= 0 && _toolbarDragCardIndex < _groupCards.Count)
            {
                if (_toolbarDragMemberIndex < _groupCards[_toolbarDragCardIndex].Count)
                {
                    _groupCards[_toolbarDragCardIndex].RemoveAt(_toolbarDragMemberIndex);
                }
            }
            else if (_toolbarDragFromIndex >= 0 && _toolbarDragFromIndex < _toolbarDraft.Count)
            {
                if (IsToolbarDivider(_toolbarDraft[_toolbarDragFromIndex]))
                {
                    _toolbarDraft.RemoveAt(_toolbarDragFromIndex);
                }
            }

            // 分隔线不能放在第 0 位（不能作为组头），至少放在第 1 位
            int target = Math.Max(1, Math.Min(insertAt, card.Count));
            card.Insert(target, ToolbarRegistry.DividerToken);

            NormalizeCardMembers(card);
            return;
        }

        if (IsToolbarNoGroupId(id))
        {
            ShowError($"「{ToolName(id)}」是出口键，不参与分组");
            return;
        }
        int own = CardIndexOfTool(id);
        if (own == cardIndex && insertAt > card.IndexOf(id)) insertAt--;
        else if (own != cardIndex) DetachToolFromHome(id);

        if (own == cardIndex) card.Remove(id);
        card.Insert(Math.Max(0, Math.Min(insertAt, card.Count)), id);

        NormalizeCardMembers(card);
    }

    /// <summary>整组并入另一张卡（拖卡左侧把手到别的卡成员行上）。</summary>
    private void MergeCardInto(int fromCard, int toCard)
    {
        if (fromCard == toCard) { ShowError("不能把组合并进自己"); return; }
        List<string> src = _groupCards[fromCard], dst = _groupCards[toCard];
        dst.AddRange(src);
        var cleaned = CleanToolbarGroup(dst);
        dst.Clear();
        dst.AddRange(cleaned);
        int at = BarIndexOfCard(fromCard);
        if (at >= 0) _toolbarDraft.RemoveAt(at);
        _groupCards.RemoveAt(fromCard);
        ShowStatus($"已把「{ToolName(src.FirstOrDefault(x => !IsToolbarDivider(x)) ?? "")}」并进「{ToolName(dst.FirstOrDefault(x => !IsToolbarDivider(x)) ?? "")}」（{dst.Count(x => !IsToolbarDivider(x))} 枚）。");
    }

    // ==================== 拖放 ====================

    private static bool IsToolbarDivider(string token) =>
        string.Equals(token, ToolbarRegistry.DividerToken, StringComparison.Ordinal);

    /// <summary>指针当前压在哪一格上（分隔线也算，靠调用方分辨）；压在空隙上返回 -1。</summary>
    private int HitToolbarItemIndex(Point pointInPreview)
    {
        for (int i = 0; i < ToolbarLayoutPreview.Children.Count && i < _toolbarDraft.Count; i++)
        {
            Visual child = ToolbarLayoutPreview.Children[i];
            Point? origin = child.TranslatePoint(new Point(0, 0), ToolbarLayoutPreview);
            if (origin == null) continue;
            Rect box = new Rect(origin.Value.X - 2, origin.Value.Y - 6, child.Bounds.Width + 4, child.Bounds.Height + 12);
            if (box.Contains(pointInPreview)) return i;
        }
        return -1;
    }

    /// <summary>
    /// 指针落在哪张组卡上（优先命中成员行精准算槽位，次选整张组卡边界含左侧把手及边框）；
    /// 命中的行内坐标写进 posInRow，没落在任何组卡上返回 -1。
    /// </summary>
    private int HitGroupCard(Point win, out Point posInRow)
    {
        posInRow = default;
        // 1. 优先命中成员行（四周留容差）
        for (int ci = 0; ci < _groupRows.Count; ci++)
        {
            ContinuousBorder row = _groupRows[ci];
            Point? origin = row.TranslatePoint(new Point(0, 0), this);
            if (origin == null) continue;
            Rect box = new Rect(origin.Value.X - 10, origin.Value.Y - 12, row.Bounds.Width + 20, row.Bounds.Height + 24);
            if (box.Contains(win))
            {
                posInRow = new Point(win.X - origin.Value.X, win.Y - origin.Value.Y);
                return ci;
            }
        }
        // 2. 整卡命中（落在把手、卡片边框或卡内留白处）
        for (int ci = 0; ci < _groupCardBorders.Count; ci++)
        {
            ContinuousBorder card = _groupCardBorders[ci];
            if (!ListReorderBehavior.TryGetBounds(card, this, out Rect box)) continue;
            if (box.Contains(win))
            {
                if (ci < _groupRows.Count)
                {
                    ContinuousBorder row = _groupRows[ci];
                    Point? rowOrigin = row.TranslatePoint(new Point(0, 0), this);
                    if (rowOrigin != null)
                    {
                        posInRow = new Point(win.X - rowOrigin.Value.X, win.Y - rowOrigin.Value.Y);
                    }
                }
                return ci;
            }
        }
        return -1;
    }

    /// <summary>指针当前是否落在「组」区域（ToolbarGroupsArea）内。</summary>
    private bool PointerInsideGroupArea(Point win)
    {
        if (ToolbarGroupsArea == null) return false;
        return ListReorderBehavior.TryGetBounds(ToolbarGroupsArea, this, out Rect box) && box.Contains(win);
    }

    /// <summary>组卡行内的插入下标：落在第几枚的左半边就插在第几枚前面。</summary>
    private int ComputeRowInsertIndex(int cardIndex, Point posInRow) =>
        ComputeRowSlotIndex(cardIndex, posInRow, null);

    private bool PointerInsidePreview(Point pointInPreview) =>
        pointInPreview.X >= -20 && pointInPreview.X <= ToolbarLayoutPreview.Bounds.Width + 20
        && pointInPreview.Y >= -24 && pointInPreview.Y <= ToolbarLayoutPreview.Bounds.Height + 24;

    /// <summary>指针当前是否落在下方「工具池」卡片区域。</summary>
    private bool PointerInsidePool(Point win)
    {
        return ListReorderBehavior.TryGetBounds(ToolbarPoolCard, this, out Rect box) && box.Contains(win);
    }

    /// <summary>
    /// 拖拽整组或单枚工具/分隔线时，在工具池上方展现高品质紫色退还/解散遮罩，
    /// 悬停进入工具池时动态强化高亮与文案。从工具池自身拖出时不显示。
    /// </summary>
    private void UpdateToolbarPoolDropMask(Point win)
    {
        if (ToolbarPoolDropMask == null) return;
        bool shouldShow = _toolbarDragging && _toolbarDragPoolToken == null;
        if (!shouldShow)
        {
            if (ToolbarPoolDropMask.IsVisible) ToolbarPoolDropMask.IsVisible = false;
            return;
        }

        if (!ToolbarPoolDropMask.IsVisible)
        {
            ToolbarPoolDropMask.IsVisible = true;
            if (ToolbarPoolDropMaskIcon.Data == null)
            {
                ToolbarPoolDropMaskIcon.Data = Geometry.Parse(OcrIconGeometry.Delete);
            }
            // 确保垃圾桶图标在圆形背景中几何绝对居中（Uniform 拉伸偏左补偿：dx = (18 - 14)/2 = 2px）
            Rect b = ToolbarPoolDropMaskIcon.Data?.Bounds ?? default;
            if (b.Width > 0 && b.Height > 0)
            {
                double s = ToolbarPoolDropMaskIcon.Width / Math.Max(b.Width, b.Height);
                double dx = (ToolbarPoolDropMaskIcon.Width - b.Width * s) / 2;
                double dy = (ToolbarPoolDropMaskIcon.Height - b.Height * s) / 2;
                if (dx > 0.05 || dy > 0.05)
                    ToolbarPoolDropMaskIcon.RenderTransform = new TranslateTransform(dx, dy);
            }
        }

        bool toPool = PointerInsidePool(win);
        bool isDraggingGroup = _toolbarDragMemberIndex < 0 && _toolbarDragCardIndex >= 0;

        if (isDraggingGroup)
        {
            bool isEmpty = _toolbarDragCardIndex < _groupCards.Count && _groupCards[_toolbarDragCardIndex].Count == 0;
            string headName = _toolbarDragCardIndex < _groupCards.Count
                ? ToolName(_groupCards[_toolbarDragCardIndex].FirstOrDefault() ?? "")
                : "";
            int memberCount = _toolbarDragCardIndex < _groupCards.Count ? _groupCards[_toolbarDragCardIndex].Count : 0;

            if (toPool)
            {
                ToolbarPoolDropMask.BorderThickness = new Thickness(2);
                ToolbarPoolDropMaskTint.Opacity = 0.22;
                ToolbarPoolDropMaskTitle.Text = isEmpty ? "松手立即删除空组卡" : $"松手立即解散「{headName}」组";
                ToolbarPoolDropMaskDesc.Text = isEmpty ? "释放即可移除此组卡片" : $"组内共 {memberCount} 枚工具将全数归还工具池";
            }
            else
            {
                ToolbarPoolDropMask.BorderThickness = new Thickness(1.5);
                ToolbarPoolDropMaskTint.Opacity = 0.12;
                ToolbarPoolDropMaskTitle.Text = isEmpty ? "拖拽到此处删除空组" : "拖拽到此处解散该组";
                ToolbarPoolDropMaskDesc.Text = isEmpty ? "释放即可移除此组卡片" : "组内所有工具将全数归还工具池";
            }
        }
        else
        {
            string token = DraggedToken();
            bool isDivider = IsToolbarDivider(token);
            string name = isDivider ? "分隔线" : ToolName(token);

            if (toPool)
            {
                ToolbarPoolDropMask.BorderThickness = new Thickness(2);
                ToolbarPoolDropMaskTint.Opacity = 0.22;
                ToolbarPoolDropMaskTitle.Text = isDivider ? "松手立即删除分隔线" : $"松手立即退还「{name}」";
                ToolbarPoolDropMaskDesc.Text = isDivider ? "释放后该分隔线将从工具栏/组中移除" : "该工具将全数归还工具池并重新点亮";
            }
            else
            {
                ToolbarPoolDropMask.BorderThickness = new Thickness(1.5);
                ToolbarPoolDropMaskTint.Opacity = 0.12;
                ToolbarPoolDropMaskTitle.Text = isDivider ? "拖拽到此处删除分隔线" : "拖拽到此处退还工具";
                ToolbarPoolDropMaskDesc.Text = isDivider ? "释放即可移除此分隔线" : $"释放即可将「{name}」归还至工具池";
            }
        }
    }

    private void OnToolbarNewGroupClick(object? sender, RoutedEventArgs e)
    {
        _groupCards.Add(new List<string>());
        RenderToolbarPage();
        ShowStatus($"已新建一张空组卡：从工具池往它里面拖，至少 {ToolbarRegistry.MinGroupMembers} 枚才算一组。");
    }

    /// <summary>
    /// 「恢复默认布局」会连带清空所有组卡（含还没上栏的备用组）且无法撤销 ⇒ 先过一道确认。
    /// 没有组可丢时不必打扰用户，直接执行。
    /// </summary>
    private void OnToolbarResetClick(object? sender, RoutedEventArgs e)
    {
        if (_groupCards.Count == 0)
        {
            DoToolbarReset();
            return;
        }
        int spare = Enumerable.Range(0, _groupCards.Count).Count(i => !CardOnBar(i));
        TxtConfirmTitle.Text = "恢复默认工具栏布局？";
        TxtConfirmBody.Text = $"{CurrentToolbarDefinition.Name}一级栏会回到默认的 {ToolbarRegistry.SlotCount(CurrentToolbarDefinition.Parse(null))} 格，"
            + $"并删掉 {GroupCardCountWords(_groupCards.Count, spare)}，无法撤销。";
        BtnConfirmOk.Content = "恢复默认";
        _confirmAction = DoToolbarReset;
        ConfirmOverlay.IsVisible = true;
    }

    private static string GroupCardCountWords(int total, int spare) =>
        spare == 0 ? $"{total} 个组" : spare == total ? $"{total} 个组（都还没上栏）"
                                                      : $"{total} 个组（其中 {spare} 个还没上栏）";

    private void OnConfirmCancelClick(object? sender, RoutedEventArgs e)
    {
        bool statsClear = _confirmAction == DoStatsClear;
        ConfirmOverlay.IsVisible = false;
        _confirmAction = null;
        if (statsClear) BtnStatsClear.Focus();
    }

    private void OnConfirmOkClick(object? sender, RoutedEventArgs e)
    {
        ConfirmOverlay.IsVisible = false;
        var act = _confirmAction;
        _confirmAction = null;
        act?.Invoke();
    }

    /// <summary>执行恢复默认布局：草稿回默认、组卡清空（工具本体一枚不删，全部回到工具池）。</summary>
    private void DoToolbarReset()
    {
        _toolbarDraft = CurrentToolbarDefinition.Parse(null);
        _groupCards.Clear();
        RenderToolbarPage();
        AutoSaveSettings();
        ShowStatus(_toolbarScene == ToolbarScene.Capture ? "已恢复默认工具栏布局，所有组已清空（工具本体一个没删）。" : $"已恢复{CurrentToolbarDefinition.Name}工具栏默认布局。");
    }

    private void OnToolbarItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_toolbarDragSourceItem == null) return;
        if (!_toolbarDragging)
        {
            if (!_toolbarDrag.Update(e)) return;
            _toolbarDragging = true;
            _toolbarDragSourceItem.Classes.Add("dragging");
        }
        Point current = e.GetPosition(this);
        UpdateToolbarDragDropVisuals(current);
        UpdateToolbarPoolDropMask(current);

        Point p = e.GetPosition(ToolbarLayoutPreview);
        if (_toolbarDragging && PointerInsidePreview(p))
        {
            UpdateToolbarDropPlaceholder(p);
        }
        else
        {
            RemoveToolbarDropPlaceholder();
        }

        e.Handled = true;
    }

    /// <summary>
    /// 统一更新拖拽过程中的所有微光与高亮视觉反馈：
    /// ① 组卡整卡微光描边（dropOver）；
    /// ② 组卡成员行高亮描边（over）；
    /// ③ 组内动态让位紫色虚线占位框（reorderPlaceholder）；
    /// ④ 拖拽单项悬停至组区域空白处时的外壳微光（dropHint）与新建组格激活反馈。
    /// </summary>
    private void UpdateToolbarDragDropVisuals(Point win)
    {
        ContinuousBorder? rowHint = null;
        ContinuousBorder? cardHint = null;
        int hitCardIndex = -1;
        Point posInRow = default;
        bool isInsideGroupArea = false;

        if (_toolbarDragging)
        {
            hitCardIndex = HitGroupCard(win, out posInRow);
            if (hitCardIndex >= 0 && hitCardIndex < _groupRows.Count)
            {
                rowHint = _groupRows[hitCardIndex];
                if (hitCardIndex < _groupCardBorders.Count)
                {
                    cardHint = _groupCardBorders[hitCardIndex];
                }
            }
            else
            {
                isInsideGroupArea = PointerInsideGroupArea(win);
            }
        }

        // 1. 组卡边框微光特效（整卡 dropOver）
        if (!ReferenceEquals(cardHint, _toolbarDropCardBorderHint))
        {
            _toolbarDropCardBorderHint?.Classes.Remove("dropOver");
            if (cardHint != null) cardHint.Classes.Add("dropOver");
            _toolbarDropCardBorderHint = cardHint;
        }

        // 2. 组卡成员行边框高亮（tbGroupRow.over）
        if (!ReferenceEquals(rowHint, _toolbarDropRowHint))
        {
            _toolbarDropRowHint?.Classes.Remove("over");
            if (rowHint != null) rowHint.Classes.Add("over");
            _toolbarDropRowHint = rowHint;
        }

        // 3. 组内动态虚线占位框
        bool isDraggingGroup = _toolbarDragMemberIndex < 0 && _toolbarDragCardIndex >= 0 && DraggedIsGroupToken();
        if (hitCardIndex >= 0 && !isDraggingGroup)
        {
            UpdateGroupDropPlaceholder(hitCardIndex, posInRow);
        }
        else
        {
            RemoveGroupDropPlaceholder();
        }

        // 4. 组区域空白处拖拽建组特效（ToolbarGroupsArea.dropHint + 新建组格 active）
        bool isSingleItemForNewGroup = _toolbarDragging && !isDraggingGroup && hitCardIndex < 0 && isInsideGroupArea;
        if (isSingleItemForNewGroup)
        {
            string token = DraggedToken();
            if (IsToolbarNoGroupId(token)) isSingleItemForNewGroup = false;
        }

        UpdateGroupAreaBlankVisuals(isSingleItemForNewGroup);
    }

    /// <summary>排除组内占位符自身，计算指针落点应插入组卡成员行的第几个槽位（避免抖动）。</summary>
    private int ComputeGroupRowDropIndexExcludingPlaceholder(int cardIndex, Point posInRow) =>
        ComputeRowSlotIndex(cardIndex, posInRow, _groupDropPlaceholder);

    /// <summary>
    /// 成员行里指针应插到第几个槽位。行是可换行的 WrapPanel，故必须先按 Y 定位到指针所在的那一视觉行，
    /// 再在该行内比中心 X——只看 X 会把第二行的落点一路算成"本行末尾"，让占位框跳回第一行。
    /// <paramref name="exclude"/> 传入让位占位框本身，使槽位计数不随占位框自己移动而抖动。
    /// </summary>
    private int ComputeRowSlotIndex(int cardIndex, Point posInRow, Visual? exclude)
    {
        if (cardIndex < 0 || cardIndex >= _groupRows.Count) return 0;
        ContinuousBorder rowBorder = _groupRows[cardIndex];
        if (rowBorder.Child is not WrapPanel panel) return _groupCards[cardIndex].Count;
        // posInRow 是行 ContinuousBorder 的坐标（含描边与内边距），要换算到 WrapPanel 坐标系才能与子项对齐
        Point? p = rowBorder.TranslatePoint(posInRow, panel);
        if (p == null) return _groupCards[cardIndex].Count;
        double x = p.Value.X;
        double y = p.Value.Y;

        // 收集候选槽位项：几何信息取自身 Bounds（WrapPanel 的直接子项，Bounds 即本面板坐标）
        List<(double Top, double Bottom, double CenterX)> items = new List<(double, double, double)>();
        foreach (Visual child in panel.Children)
        {
            if (exclude != null && ReferenceEquals(child, exclude)) continue;
            if (child is TextBlock) continue; // 空组提示文本不作为槽位项
            if (child is Control c && c.Bounds.Width > 0 && c.Bounds.Height > 0)
                items.Add((c.Bounds.Y, c.Bounds.Bottom, c.Bounds.X + c.Bounds.Width / 2.0));
        }
        if (items.Count == 0) return 0;

        // 分桶成视觉行：WrapPanel 同一行内所有子项顶边相同，顶边一变即换行
        List<(int Start, int Count, double Top, double Bottom)> lines =
            new List<(int, int, double, double)>();
        for (int i = 0; i < items.Count; i++)
        {
            (double top, double bottom, double _) = items[i];
            if (lines.Count > 0 && Math.Abs(top - lines[lines.Count - 1].Top) < 0.5)
            {
                var last = lines[lines.Count - 1];
                lines[lines.Count - 1] = (last.Start, last.Count + 1, last.Top, Math.Max(last.Bottom, bottom));
            }
            else lines.Add((i, 1, top, bottom));
        }

        // 指针 Y 命中哪一行：落在首行之上算首行，末行之下列尾行（行带彼此相接，无缝隙）
        int lineIdx = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (y >= lines[i].Top && y < lines[i].Bottom) { lineIdx = i; break; }
            if (y >= lines[i].Bottom) lineIdx = i;
        }

        (int startSlot, int count, double _, double _) = lines[lineIdx];
        for (int i = 0; i < count; i++)
        {
            if (x < items[startSlot + i].CenterX) return startSlot + i;
        }
        return startSlot + count;
    }

    /// <summary>悬停在某张组卡成员行上时动态让出紫色虚线圆角矩形占位空位。</summary>
    private void UpdateGroupDropPlaceholder(int cardIndex, Point posInRow)
    {
        if (cardIndex < 0 || cardIndex >= _groupRows.Count)
        {
            RemoveGroupDropPlaceholder();
            return;
        }

        string token = DraggedToken();
        if (string.IsNullOrEmpty(token))
        {
            RemoveGroupDropPlaceholder();
            return;
        }

        bool isDivider = IsToolbarDivider(token);
        if (cardIndex < _groupCards.Count)
        {
            int toolCount = _groupCards[cardIndex].Count(x => !IsToolbarDivider(x));
            // 如果是分隔线，而组内目前没有工具（空组），分隔线无法放入，不呈现让位占位
            if (isDivider && toolCount == 0)
            {
                RemoveGroupDropPlaceholder();
                return;
            }
        }

        if (_groupRows[cardIndex].Child is not WrapPanel panel)
        {
            RemoveGroupDropPlaceholder();
            return;
        }

        if (_groupDropPlaceholder == null)
        {
            _groupDropPlaceholder = new Avalonia.Controls.Shapes.Rectangle();
            _groupDropPlaceholder.Classes.Add("reorderPlaceholder");
        }

        _groupDropPlaceholder.Width = isDivider ? 6 : 26;
        _groupDropPlaceholder.Height = isDivider ? 18 : 26;
        _groupDropPlaceholder.RadiusX = isDivider ? 2 : 6;
        _groupDropPlaceholder.RadiusY = isDivider ? 2 : 6;

        if (_groupDropPlaceholderCardIndex != cardIndex && _groupDropPlaceholderCardIndex >= 0)
        {
            RemoveGroupDropPlaceholder();
        }

        // 如果此卡原本是空卡且含提示 TextBlock，先隐藏提示 TextBlock
        foreach (Visual child in panel.Children)
        {
            if (child is TextBlock tb) tb.IsVisible = false;
        }

        int targetSlot = ComputeGroupRowDropIndexExcludingPlaceholder(cardIndex, posInRow);
        if (isDivider)
        {
            int currentToolCount = _groupCards[cardIndex].Count(x => !IsToolbarDivider(x));
            if (currentToolCount > 0)
            {
                targetSlot = Math.Max(1, targetSlot);
            }
        }

        ListReorderBehavior.MovePlaceholder(panel, _groupDropPlaceholder, targetSlot);
        _groupDropPlaceholderCardIndex = cardIndex;
    }

    /// <summary>移除组内占位空位并复位空卡提示。</summary>
    private void RemoveGroupDropPlaceholder()
    {
        if (_groupDropPlaceholder != null)
        {
            ListReorderBehavior.RemovePlaceholder(_groupDropPlaceholder);
            _groupDropPlaceholder = null;
        }

        if (_groupDropPlaceholderCardIndex >= 0 && _groupDropPlaceholderCardIndex < _groupRows.Count)
        {
            // 如果此卡没有实际成员，恢复提示 TextBlock
            if (_groupDropPlaceholderCardIndex < _groupCards.Count && _groupCards[_groupDropPlaceholderCardIndex].Count == 0)
            {
                if (_groupRows[_groupDropPlaceholderCardIndex].Child is Panel panel)
                {
                    foreach (Visual child in panel.Children)
                    {
                        if (child is TextBlock tb) tb.IsVisible = true;
                    }
                }
            }
        }

        _groupDropPlaceholderCardIndex = -1;
    }

    /// <summary>更新拖拽单项悬停至组区域空白处时的视觉反馈与新建组格激活特效。</summary>
    private void UpdateGroupAreaBlankVisuals(bool active)
    {
        if (_groupAreaBlankHovered == active) return;
        _groupAreaBlankHovered = active;

        if (active)
        {
            ToolbarGroupsArea?.Classes.Add("dropHint");
            if (_newGroupTile != null)
            {
                _newGroupDash?.Classes.Add("active");
                _newGroupBtn?.Classes.Add("active");
                if (_newGroupTileText != null) _newGroupTileText.Text = "松手创建新组";
            }
            if (ToolbarGroupEmpty != null && ToolbarGroupEmpty.IsVisible)
            {
                ToolbarGroupEmpty.Classes.Add("active");
                if (TxtToolbarGroupEmptyTitle != null) TxtToolbarGroupEmptyTitle.Text = "松手在此创建新组";
            }
        }
        else
        {
            ToolbarGroupsArea?.Classes.Remove("dropHint");
            if (_newGroupTile != null)
            {
                _newGroupDash?.Classes.Remove("active");
                _newGroupBtn?.Classes.Remove("active");
                if (_newGroupTileText != null) _newGroupTileText.Text = "新建组";
            }
            if (ToolbarGroupEmpty != null)
            {
                ToolbarGroupEmpty.Classes.Remove("active");
                if (TxtToolbarGroupEmptyTitle != null) TxtToolbarGroupEmptyTitle.Text = "＋ 新建组";
            }
        }
    }

    /// <summary>排除占位符自身，计算指针落点应插入工具栏的第几个槽位（避免边界振荡）。</summary>
    private int ComputeToolbarDropIndexExcludingPlaceholder(Point pointInPanel) =>
        ListReorderBehavior.GetInsertionIndex(ToolbarLayoutPreview, pointInPanel, Orientation.Horizontal, _toolbarDropPlaceholder);

    /// <summary>悬停在工具栏上时动态让出紫色虚线圆角矩形占位空位（与按钮尺寸、圆角严格一致）。</summary>
    private void UpdateToolbarDropPlaceholder(Point pointInPreview)
    {
        string token = DraggedToken();
        if (string.IsNullOrEmpty(token)) return;

        bool isDivider = IsToolbarDivider(token);
        bool isGroup = DraggedIsGroupToken();

        if (_toolbarDropPlaceholder == null)
        {
            _toolbarDropPlaceholder = new Avalonia.Controls.Shapes.Rectangle();
            _toolbarDropPlaceholder.Classes.Add("reorderPlaceholder");
        }

        _toolbarDropPlaceholder.Width = isDivider ? 6 : (isGroup ? 36 : 26);
        _toolbarDropPlaceholder.Height = isDivider ? 20 : 26;
        _toolbarDropPlaceholder.RadiusX = isDivider ? 2 : 6;
        _toolbarDropPlaceholder.RadiusY = isDivider ? 2 : 6;

        int targetSlot = ComputeToolbarDropIndexExcludingPlaceholder(pointInPreview);

        ListReorderBehavior.MovePlaceholder(ToolbarLayoutPreview, _toolbarDropPlaceholder, targetSlot);
    }

    /// <summary>移除工具栏占位空位并复位布局。</summary>
    private void RemoveToolbarDropPlaceholder()
    {
        if (_toolbarDropPlaceholder != null)
        {
            ListReorderBehavior.RemovePlaceholder(_toolbarDropPlaceholder);
            _toolbarDropPlaceholder = null;
        }
    }

    private void OnToolbarCellPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (!_toolbarDragging) return;
            Point win = e.GetPosition(this);
            int cardHit = HitGroupCard(win, out Point posInRow);
            if (cardHit >= 0)
            {
                DropIntoCard(cardHit, posInRow);
            }
            else if (PointerInsideGroupArea(win))
            {
                DropIntoGroupBlank();
            }
            else
            {
                Point p = e.GetPosition(ToolbarLayoutPreview);
                if (PointerInsidePreview(p)) DropIntoBar(p);
                else DropOutOfBar(win);
            }
            NormalizeToolbarDraft();
        }
        finally
        {
            ResetToolbarDrag();
            RenderToolbarPage();
            AutoSaveSettings();
        }
        e.Handled = true;
    }

    /// <summary>
    /// 现在被拖着的东西，取真值的四种来源：
    /// ① 工具池里的一枚；② 某张卡里的成员（栏上没有它的 token，真值在卡里按下标取）；
    /// ③ 一级栏上的一格；④ 还没上栏那张卡的把手（栏上根本没有它 ⇒ 现造一个组 token）。
    /// 曾经只认 ①，导致 ②④ 返回空串、下游 `id.Length == 0` 直接 return——成员改序、并组、
    /// 把成员拖出栏变独立格这三条拖拽路径全部静默失效。
    /// </summary>
    private string DraggedToken()
    {
        if (_toolbarDragPoolToken != null) return _toolbarDragPoolToken;
        if (_toolbarDragMemberIndex >= 0) return _toolbarDragTokenOfMember();
        if (_toolbarDragFromIndex >= 0 && _toolbarDragFromIndex < _toolbarDraft.Count) return _toolbarDraft[_toolbarDragFromIndex];
        if (_toolbarDragCardIndex >= 0 && _toolbarDragCardIndex < _groupCards.Count)
            return MakeToolbarGroupToken(_groupCards[_toolbarDragCardIndex]);
        return "";
    }

    private void DropIntoCard(int cardIndex, Point posInRow)
    {
        bool wholeGroup = _toolbarDragMemberIndex < 0 && _toolbarDragCardIndex >= 0 && DraggedIsGroupToken();
        if (wholeGroup)
        {
            MergeCardInto(_toolbarDragCardIndex, cardIndex);
            return;
        }
        if (DraggedIsGroupToken())
        {
            ShowError("不能把整组放进组里");
            return;
        }
        string token = DraggedToken();
        bool isDivider = IsToolbarDivider(token);
        string id = isDivider ? ToolbarRegistry.DividerToken : (FindToolbarTool(token)?.Id ?? "");
        if (id.Length == 0) return;

        int insertAt;
        if (_groupDropPlaceholder != null && _groupDropPlaceholderCardIndex == cardIndex
            && _groupRows[cardIndex].Child is Panel panel && _groupDropPlaceholder.Parent == panel)
        {
            insertAt = panel.Children.IndexOf(_groupDropPlaceholder);
        }
        else
        {
            insertAt = ComputeRowInsertIndex(cardIndex, posInRow);
        }
        RemoveGroupDropPlaceholder();

        List<string> card = _groupCards[cardIndex];
        int countBefore = card.Count;
        PutToolIntoCard(id, cardIndex, insertAt);
        if (_toolbarDragMemberIndex >= 0 && _toolbarDragCardIndex == cardIndex)
        {
            ShowStatus(isDivider ? "已在组里调整了分隔线位置。" : $"已在「{ToolName(_groupCards[cardIndex].FirstOrDefault(x => !IsToolbarDivider(x)) ?? "")}」组里改了顺序。");
        }
        else if (isDivider)
        {
            if (card.Count > countBefore)
                ShowStatus($"已将分隔线插入「{ToolName(card.FirstOrDefault(x => !IsToolbarDivider(x)) ?? "")}」组。");
        }
        else if (card.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            ShowStatus($"已把「{ToolName(id)}」放进「{ToolName(card.FirstOrDefault(x => !IsToolbarDivider(x)) ?? "")}」组（{card.Count(x => !IsToolbarDivider(x))} 枚）。");
        }
    }

    /// <summary>
    /// 拖拽单项到「组」区域空白处时：自动创建新组卡并放入该工具；
    /// 若拖的是分隔线，提示不能作为组头；若拖的是整组或未拖动，保持原样。
    /// </summary>
    private void DropIntoGroupBlank()
    {
        if (DraggedIsGroupToken())
        {
            // 整组把手拖到组空白处：原样保留防误触
            return;
        }

        string token = DraggedToken();
        if (string.IsNullOrEmpty(token)) return;

        if (IsToolbarDivider(token))
        {
            ShowError("分隔线不能作为组头，请先拖入工具创建组。");
            return;
        }

        string id = FindToolbarTool(token)?.Id ?? "";
        if (id.Length == 0) return;

        if (IsToolbarNoGroupId(id))
        {
            ShowError($"「{ToolName(id)}」是出口键，不参与分组");
            return;
        }

        // 1. 如果来自工具栏一级预览：从一级工具栏移除
        if (_toolbarDragFromIndex >= 0 && _toolbarDragFromIndex < _toolbarDraft.Count)
        {
            _toolbarDraft.RemoveAt(_toolbarDragFromIndex);
        }
        // 2. 如果来自其他组卡：从原组卡移除
        else if (_toolbarDragMemberIndex >= 0 && _toolbarDragCardIndex >= 0 && _toolbarDragCardIndex < _groupCards.Count)
        {
            if (_toolbarDragMemberIndex < _groupCards[_toolbarDragCardIndex].Count)
            {
                _groupCards[_toolbarDragCardIndex].RemoveAt(_toolbarDragMemberIndex);
            }
        }
        else
        {
            // 3. 来自工具池等其他来源
            DetachToolFromHome(id);
        }

        // 4. 自动创建新组卡并放入该工具
        var newGroup = new List<string> { id };
        _groupCards.Add(newGroup);
        ShowStatus($"已在组区域空白处自动创建新组，并放入「{ToolName(id)}」工具。");
    }

    private bool DraggedIsGroupToken() => ToolbarRegistry.IsGroupToken(DraggedToken());

    private void DropIntoBar(Point pointInPreview)
    {
        int drop;
        if (_toolbarDropPlaceholder != null && _toolbarDropPlaceholder.Parent == ToolbarLayoutPreview)
        {
            drop = ToolbarLayoutPreview.Children.IndexOf(_toolbarDropPlaceholder);
        }
        else
        {
            drop = Math.Max(0, Math.Min(ComputeToolbarDropIndex(pointInPreview), _toolbarDraft.Count));
        }
        RemoveToolbarDropPlaceholder();
        string token = DraggedToken();

        if (_toolbarDragMemberIndex >= 0)
        {
            if (IsToolbarDivider(token))
            {
                if (_toolbarDragCardIndex >= 0 && _toolbarDragCardIndex < _groupCards.Count
                    && _toolbarDragMemberIndex < _groupCards[_toolbarDragCardIndex].Count)
                {
                    _groupCards[_toolbarDragCardIndex].RemoveAt(_toolbarDragMemberIndex);
                }
                _toolbarDraft.Insert(Math.Min(drop, _toolbarDraft.Count), ToolbarRegistry.DividerToken);
                ShowStatus("分隔线已移出本组，在一级工具栏占位。");
                return;
            }
            string id = FindToolbarTool(token)?.Id ?? "";
            if (id.Length == 0) return;
            if (IsToolbarNoGroupId(id)) { ShowError($"「{ToolName(id)}」是出口键，不参与分组"); return; }
            DetachToolFromHome(id);
            _toolbarDraft.Insert(Math.Min(drop, _toolbarDraft.Count), id);
            ShowStatus($"「{ToolName(id)}」已移出本组，在一级单独占一格。");
            return;
        }

        if (_toolbarDragPoolToken != null)
        {
            _toolbarDraft.Insert(Math.Min(drop, _toolbarDraft.Count), _toolbarDragPoolToken);
            return;
        }

        // 未上栏的组被拖进栏里＝上栏（落在指针那一格的前面）。🔴 判据不能挂在 _toolbarDragFromIndex 上：
        // 这张卡在栏上还没有格，那个下标恒为 -1，挂进去就等于把唯一的上栏入口写死。
        if (_toolbarDragMemberIndex < 0 && _toolbarDragCardIndex >= 0 && !CardOnBar(_toolbarDragCardIndex))
        {
            if (OnBarCardCount() >= ToolbarRegistry.MaxGroups) { ShowError($"一条工具栏最多 {ToolbarRegistry.MaxGroups} 组"); return; }
            List<string> card = _groupCards[_toolbarDragCardIndex];
            if (card.Count < ToolbarRegistry.MinGroupMembers) { ShowError($"组至少 {ToolbarRegistry.MinGroupMembers} 枚才能上栏"); return; }
            _toolbarDraft.Insert(Math.Min(drop, _toolbarDraft.Count), MakeToolbarGroupToken(card));
            ShowStatus($"「{ToolName(card[0])}」已上栏，在一级里占一格。");
            return;
        }

        if (_toolbarDragFromIndex >= 0)
        {
            string moved = _toolbarDraft[_toolbarDragFromIndex];
            _toolbarDraft.RemoveAt(_toolbarDragFromIndex);
            if (drop > _toolbarDragFromIndex) drop--;
            _toolbarDraft.Insert(Math.Max(0, Math.Min(drop, _toolbarDraft.Count)), moved);
        }
    }

    /// <summary>
    /// 拖到工具池或栏外：
    /// ① 独立格撤掉退回工具池；
    /// ② 单个成员退回工具池；
    /// ③ 新建空组卡仅在拖到工具池时删除；空白处松手不删除，避免误触；
    /// ④ 有内容的组仅在拖到工具池时解散整组（撤栏 + 删卡 + 成员全部退回工具池）；
    /// ⑤ 从一级栏拖出的组松手在空白处＝仅从一级栏撤下该格，组卡保留在下方备用；从把手拖出到空白处＝保持原样防误触。
    /// </summary>
    private void DropOutOfBar(Point win)
    {
        if (_toolbarDragPoolToken != null) return;
        bool toPool = PointerInsidePool(win);

        if (_toolbarDragMemberIndex >= 0 && _toolbarDragCardIndex >= 0)
        {
            string memberToken = _toolbarDragTokenOfMember();
            bool isDivider = IsToolbarDivider(memberToken);
            string name = isDivider ? "分隔线" : ToolName(memberToken);
            if (toPool)
            {
                if (isDivider)
                {
                    if (_toolbarDragCardIndex < _groupCards.Count && _toolbarDragMemberIndex < _groupCards[_toolbarDragCardIndex].Count)
                    {
                        _groupCards[_toolbarDragCardIndex].RemoveAt(_toolbarDragMemberIndex);
                        ShowStatus("分隔线已从组内移除。");
                    }
                }
                else
                {
                    string id = FindToolbarTool(memberToken)?.Id ?? "";
                    if (!CurrentToolbarDefinition.CanHide(id, _toolbarDraft)) { ShowError("此项为必要入口，不能隐藏。"); return; }
                    DetachToolFromHome(id);
                    ShowStatus($"「{name}」已退回工具池。");
                }
            }
            else
            {
                ShowStatus(isDivider
                    ? "未拖入工具池，分隔线已在组内保留。"
                    : $"未拖入工具池，组内「{name}」已保留；拖入下方工具池可退还。");
            }
            return;
        }
        if (_toolbarDragCardIndex >= 0 && _toolbarDragCardIndex < _groupCards.Count)
        {
            List<string> card = _groupCards[_toolbarDragCardIndex];
            int at = BarIndexOfCard(_toolbarDragCardIndex);
            string name = ToolName(card.FirstOrDefault() ?? "");
            if (at >= 0 && !CurrentToolbarDefinition.CanHide(MakeToolbarGroupToken(card), _toolbarDraft))
            { ShowError("组内包含最后一个必要操作，请先将它移到一级栏。"); return; }

            // ① 新建的空组：只有明确拖入工具池才删除；空白处松手取消删除防误触
            if (card.Count == 0)
            {
                if (toPool)
                {
                    _groupCards.RemoveAt(_toolbarDragCardIndex);
                    ShowStatus("已删除新建的空组卡。");
                }
                else
                {
                    ShowStatus("未拖入工具池，空组卡已保留（拖入下方工具池即可删除）。");
                }
                return;
            }

            // ② 只有明确拖到了工具池：彻底解散整组并删除卡片，成员全数退回工具池
            if (toPool)
            {
                if (at >= 0) _toolbarDraft.RemoveAt(at);
                int count = card.Count;
                _groupCards.RemoveAt(_toolbarDragCardIndex);
                ShowStatus($"已解散「{name}」组，共 {count} 枚工具已退回工具池。");
                return;
            }

            // ③ 空白处松手（未进入工具池）：
            // 若是从一级工具栏拖拽组格至空白处：仅从工具栏撤下该格，组卡完整保留在下方备用
            if (!_toolbarDragIsCardGrip && at >= 0)
            {
                _toolbarDraft.RemoveAt(at);
                ShowStatus($"「{name}」那一格已从工具栏撤下，组保留在下方备用。");
            }
            else
            {
                // 若是从组卡把手拖出到空白处：防误触直接忽略，原样保留
                ShowStatus($"未拖入工具池，已取消操作；拖到下方工具池可解散并删除此组。");
            }
            return;
        }
        if (_toolbarDragFromIndex >= 0 && _toolbarDragFromIndex < _toolbarDraft.Count)
        {
            string moved = _toolbarDraft[_toolbarDragFromIndex];
            bool isDivider = IsToolbarDivider(moved);
            string name = isDivider ? "分隔线" : ToolName(moved);
            if (toPool)
            {
                if (!CurrentToolbarDefinition.CanHide(moved, _toolbarDraft)) { ShowError("此项为必要入口，不能隐藏。"); return; }
                _toolbarDraft.RemoveAt(_toolbarDragFromIndex);
                ShowStatus(isDivider ? "分隔线已从工具栏移除。" : $"「{name}」已退回工具池。");
            }
            else
            {
                ShowStatus(isDivider
                    ? "未拖入工具池，分隔线已保留；拖入下方工具池即可删除。"
                    : $"未拖入工具池，已保留在工具栏；拖入下方工具池可退还「{name}」。");
            }
        }
    }

    /// <summary>成员拖拽时被拖的那枚：token 记的是它所在卡的那一格，真值在卡里按下标取。</summary>
    private string _toolbarDragTokenOfMember()
    {
        if (_toolbarDragCardIndex >= 0 && _toolbarDragCardIndex < _groupCards.Count
            && _toolbarDragMemberIndex >= 0 && _toolbarDragMemberIndex < _groupCards[_toolbarDragCardIndex].Count)
        {
            return _groupCards[_toolbarDragCardIndex][_toolbarDragMemberIndex];
        }
        return "";
    }

    private int ComputeToolbarDropIndex(Point pointInPanel)
    {
        for (int i = 0; i < ToolbarLayoutPreview.Children.Count; i++)
        {
            Visual child = ToolbarLayoutPreview.Children[i];
            Point? center = child.TranslatePoint(new Point(child.Bounds.Width / 2.0, 0), ToolbarLayoutPreview);
            if (center.HasValue && pointInPanel.X < center.Value.X) return i;
        }
        return _toolbarDraft.Count;
    }

    private void ResetToolbarDrag()
    {
        RemoveToolbarDropPlaceholder();
        RemoveGroupDropPlaceholder();
        UpdateGroupAreaBlankVisuals(false);
        if (_toolbarDragSourceItem != null) _toolbarDragSourceItem.Classes.Remove("dragging");
        _toolbarDropRowHint?.Classes.Remove("over");
        _toolbarDropRowHint = null;
        _toolbarDropCardBorderHint?.Classes.Remove("dropOver");
        _toolbarDropCardBorderHint = null;
        if (ToolbarPoolDropMask != null) ToolbarPoolDropMask.IsVisible = false;
        _toolbarDragSourceItem = null;
        _toolbarDragFromIndex = -1;
        _toolbarDragCardIndex = -1;
        _toolbarDragIsCardGrip = false;
        _toolbarDragMemberIndex = -1;
        _toolbarDragPoolToken = null;
        _toolbarDragging = false;
        _toolbarDrag.Reset();
    }

    /// <summary>
    /// 改完草稿后统一归一：组数上限、组内至少 2 枚（枚数无上限）、出口键出组、跨格去重，全部由 ToolbarRegistry.Parse 校正
    /// （散架与溢出的成员一律退回独立格，绝不静默丢工具）。
    /// </summary>
    private void NormalizeToolbarDraft()
    {
        _toolbarDraft = CurrentToolbarDefinition.Parse(string.Join(",", _toolbarDraft));
        if (_toolbarScene != ToolbarScene.Capture)
        {
            var onBar = CurrentToolbarDefinition.ToolIds(_toolbarDraft).ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _groupCards.Count; i++)
                if (!CardOnBar(i))
                {
                    _groupCards[i].RemoveAll(id => id != "|" && onBar.Contains(id));
                    var cleaned = CleanToolbarGroup(_groupCards[i]); _groupCards[i].Clear(); _groupCards[i].AddRange(cleaned);
                }
        }
    }

    private void OnToolbarPoolPointerPressed(object? sender, PointerPressedEventArgs e, string id)
    {
        if (e.Source is Button || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return; // ＋ 按钮走 Click
        _toolbarDragFromIndex = -1;
        _toolbarDragCardIndex = -1;
        _toolbarDragMemberIndex = -1;
        _toolbarDragPoolToken = id;
        _toolbarDragSourceItem = sender as Control;
        _toolbarDragging = false;
        if (_toolbarDragSourceItem != null) _toolbarDrag.Begin(_toolbarDragSourceItem, e, this);
        e.Handled = true;
    }

    private void OnToolbarPoolPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
            if (_toolbarDragPoolToken == null) return;
            bool draggingDivider = IsToolbarDivider(_toolbarDragPoolToken);
            // 没拖动＝单击：加到一级栏（＋ 按钮已删，不拖也得能加）
            if (!_toolbarDragging)
            {
                if (draggingDivider)
                {
                    // 分隔线无限供应；直接追加到末尾会被 Parse 的"剔除行尾分隔线"规则吃掉（原「＋ 分隔线」按钮
                    // "没反应"的根因），所以插到最后一个工具之前，再拖到想隔开的位置
                    if (_toolbarDraft.Count < 2) { ShowError("至少要有两个工具，分隔线才有地方夹。"); return; }
                    _toolbarDraft.Insert(_toolbarDraft.Count - 1, ToolbarRegistry.DividerToken);
                    NormalizeToolbarDraft();
                    ShowStatus("分隔线已插到最后一个工具之前，拖到你想让它隔开的位置。");
                    return;
                }
                DetachToolFromHome(_toolbarDragPoolToken);
                _toolbarDraft.Add(_toolbarDragPoolToken);
                NormalizeToolbarDraft();
                ShowStatus($"「{ToolName(_toolbarDragPoolToken)}」已加到工具栏末尾。");
                return;
            }
            Point win = e.GetPosition(this);
            int cardHit = HitGroupCard(win, out Point posInRow);
            if (cardHit >= 0)
            {
                int insertAt;
                if (_groupDropPlaceholder != null && _groupDropPlaceholderCardIndex == cardHit
                    && _groupRows[cardHit].Child is Panel panel && _groupDropPlaceholder.Parent == panel)
                {
                    insertAt = panel.Children.IndexOf(_groupDropPlaceholder);
                }
                else
                {
                    insertAt = ComputeRowInsertIndex(cardHit, posInRow);
                }
                RemoveGroupDropPlaceholder();

                List<string> card = _groupCards[cardHit];
                int countBefore = card.Count;
                PutToolIntoCard(_toolbarDragPoolToken, cardHit, insertAt);
                // 只有真的落进这张卡才播报成功——出口键会被 PutToolIntoCard 拒绝并自行报错
                if (draggingDivider)
                {
                    if (card.Count > countBefore)
                        ShowStatus($"已将分隔线插入「{ToolName(card.FirstOrDefault(x => !IsToolbarDivider(x)) ?? "")}」组。");
                }
                else if (card.Contains(_toolbarDragPoolToken, StringComparer.OrdinalIgnoreCase))
                {
                    ShowStatus($"已把「{ToolName(_toolbarDragPoolToken)}」放进「{ToolName(card.FirstOrDefault(x => !IsToolbarDivider(x)) ?? "")}」组（{card.Count(x => !IsToolbarDivider(x))} 枚）。");
                }
            }
            else if (PointerInsideGroupArea(win))
            {
                DropIntoGroupBlank();
            }
            else
            {
                Point p = e.GetPosition(ToolbarLayoutPreview);
                if (PointerInsidePreview(p))
                {
                    DropIntoBar(p);
                    ShowStatus(draggingDivider
                        ? "分隔线已插到一级栏该位置。"
                        : $"「{ToolName(_toolbarDragPoolToken)}」已在一级单独占一格。");
                }
            }
            NormalizeToolbarDraft();
        }
        finally
        {
            ResetToolbarDrag();
            RenderToolbarPage();
            AutoSaveSettings();
        }
        e.Handled = true;
    }

    // -------------------- AI 模型 识图 --------------------

    private List<AiApiConfig> _aiConfigsDraft = new();

    private async void OnAddAiProviderClick(object? sender, RoutedEventArgs e)
    {
        var newConfig = new AiApiConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"服务商 {_aiConfigsDraft.Count + 1}",
            Protocol = "OpenAI",
            BaseUrl = "https://api.openai.com/v1",
            ApiKey = "",
            DefaultModel = "",
            IsEnabled = true,
            CustomModels = new List<string>(),
            CachedModels = new List<string>()
        };

        var editWin = new AiProviderEditWindow(newConfig);
        await editWin.ShowDialog(this);

        if (editWin.IsConfirmed)
        {
            _aiConfigsDraft.Add(editWin.ResultConfig);
            RenderAiProviders();
            AutoSaveSettings();
            ShowStatus($"已添加服务商「{editWin.ResultConfig.Name}」");
        }
    }

    private void RenderAiProviders()
    {
        if (AiProvidersPanel == null) return;
        AiProvidersPanel.Children.Clear();

        if (_aiConfigsDraft.Count == 0)
        {
            var emptyBorder = new ContinuousBorder
            {
                Classes = { "card" },
                Padding = new Thickness(20, 16),
                Child = new TextBlock
                {
                    Text = "暂无配置的 AI 服务商，点击右上角「＋ 添加服务商」以添加。",
                    Classes = { "muted" },
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                }
            };
            AiProvidersPanel.Children.Add(emptyBorder);
            return;
        }

        for (int i = 0; i < _aiConfigsDraft.Count; i++)
        {
            var config = _aiConfigsDraft[i];
            int index = i;
            var item = BuildAiProviderListItem(config, index);
            AiProvidersPanel.Children.Add(item);
        }
    }

    private Control BuildAiProviderListItem(AiApiConfig config, int index)
    {
        var card = new ContinuousBorder
        {
            Classes = { "card" },
            Padding = new Thickness(14, 12),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        // 1. 左侧：状态指示、服务商名称、协议 Badge、脱敏摘要
        var infoStack = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var titleRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("8,*,Auto"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var statusDot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Fill = config.IsEnabled
                ? new SolidColorBrush(Color.Parse("#22C55E"))
                : new SolidColorBrush(Color.Parse("#8A8A8A"))
        };

        var titleText = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Text = !string.IsNullOrWhiteSpace(config.Name) ? config.Name : $"服务商 #{index + 1}",
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var badgeBorder = new ContinuousBorder
        {
            Classes = { "providerBadge" },
            Child = new TextBlock
            {
                Classes = { "providerBadgeText" },
                Text = !string.IsNullOrWhiteSpace(config.Protocol) ? config.Protocol : "OpenAI"
            }
        };

        Grid.SetColumn(titleText, 1);
        Grid.SetColumn(badgeBorder, 2);
        ToolTip.SetTip(titleText, titleText.Text);
        titleRow.Children.Add(statusDot);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(badgeBorder);

        var detailRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        detailRow.Children.Add(BuildProviderDetailItem(ProviderIconGeometry.Url, FormatBaseUrl(config.BaseUrl)));
        detailRow.Children.Add(BuildProviderDetailItem(ProviderIconGeometry.ApiKey, MaskApiKey(config.ApiKey)));

        string modelSummary;
        int enabledCount = config.CustomModels?.Count ?? 0;
        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            modelSummary = enabledCount > 1
                ? $"{config.Model} (等 {enabledCount} 个模型)"
                : config.Model;
        }
        else if (enabledCount > 0)
        {
            modelSummary = $"{config.CustomModels![0]} (等 {enabledCount} 个模型)";
        }
        else
        {
            modelSummary = "未指定模型";
        }
        detailRow.Children.Add(BuildProviderDetailItem(ProviderIconGeometry.Model, modelSummary));

        foreach (var detail in detailRow.Children) detail.Margin = new Thickness(0, 0, 16, 2);
        infoStack.Children.Add(titleRow);
        infoStack.Children.Add(detailRow);

        // 2. 右侧操作区：启停分段按钮、编辑按钮、删除按钮
        var actionStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var toggleBorder = new ContinuousBorder
        {
            Classes = { "segmentedGroup" }
        };

        var toggleStack = new StackPanel { Orientation = Orientation.Horizontal };
        var radOff = new RadioButton
        {
            Classes = { "segmented" },
            Content = "停用",
            GroupName = $"AiItemEnable_{index}_{config.Id}",
            IsChecked = !config.IsEnabled
        };
        var radOn = new RadioButton
        {
            Classes = { "segmented" },
            Content = "启用",
            GroupName = $"AiItemEnable_{index}_{config.Id}",
            IsChecked = config.IsEnabled
        };

        radOn.IsCheckedChanged += (_, _) =>
        {
            config.IsEnabled = radOn.IsChecked == true;
            statusDot.Fill = config.IsEnabled
                ? new SolidColorBrush(Color.Parse("#22C55E"))
                : new SolidColorBrush(Color.Parse("#8A8A8A"));
            AutoSaveSettings();
        };

        toggleStack.Children.Add(radOff);
        toggleStack.Children.Add(radOn);
        toggleBorder.Child = toggleStack;

        var btnEdit = new Button
        {
            Classes = { "secondary" },
            Content = "编辑",
            Padding = new Thickness(14, 0),
            Height = 32,
            MinHeight = 32,
            MinWidth = 56,
            FontSize = 13.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(btnEdit, "弹出窗口编辑该服务商详细参数与测试连接");

        async Task OpenEditDialogAsync()
        {
            var editWin = new AiProviderEditWindow(config);
            await editWin.ShowDialog(this);
            if (editWin.IsConfirmed)
            {
                _aiConfigsDraft[index] = editWin.ResultConfig;
                RenderAiProviders();
                AutoSaveSettings();
                ShowStatus($"已更新服务商「{editWin.ResultConfig.Name}」");
            }
        }

        btnEdit.Click += async (_, _) => await OpenEditDialogAsync();

        actionStack.Children.Add(toggleBorder);
        actionStack.Children.Add(btnEdit);

        if (_aiConfigsDraft.Count > 1)
        {
            var btnDelete = new Button
            {
                Classes = { "titleIcon" },
                Content = "✕",
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            ToolTip.SetTip(btnDelete, "删除此服务商配置");
            btnDelete.Click += (_, _) =>
            {
                _aiConfigsDraft.RemoveAt(index);
                RenderAiProviders();
                AutoSaveSettings();
                ShowStatus($"已删除服务商「{config.Name}」");
            };
            actionStack.Children.Add(btnDelete);
        }

        Grid.SetColumn(infoStack, 0);
        Grid.SetColumn(actionStack, 1);

        grid.Children.Add(infoStack);
        grid.Children.Add(actionStack);
        card.Child = grid;

        card.DoubleTapped += async (_, _) => await OpenEditDialogAsync();

        return card;
    }

    // -------------------- 预设提示词（多预设 + 技能命令 + @ 唤起） --------------------

    private List<AiPromptPreset> _aiPromptPresetsDraft = new();

    private async void OnAddAiPromptPresetClick(object? sender, RoutedEventArgs e)
    {
        var preset = new AiPromptPreset
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"提示词 {_aiPromptPresetsDraft.Count + 1}",
            Content = "",
            IsDefault = _aiPromptPresetsDraft.Count == 0
        };

        var editWin = new AiPromptPresetEditWindow(preset);
        await editWin.ShowDialog(this);

        if (editWin.IsConfirmed)
        {
            _aiPromptPresetsDraft.Add(editWin.ResultPreset);
            RenderAiPromptPresets();
            AutoSaveSettings();
            ShowStatus($"已添加提示词「{editWin.ResultPreset.Name}」");
        }
    }

    private void RenderAiPromptPresets()
    {
        if (AiPromptPresetsPanel == null) return;
        AiPromptPresetsPanel.Children.Clear();

        if (_aiPromptPresetsDraft.Count == 0)
        {
            AiPromptPresetsPanel.Children.Add(new TextBlock
            {
                Text = "暂无预设提示词，点击右上角「＋ 添加提示词」以添加。",
                Classes = { "muted" },
                FontSize = 12.5,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = new Thickness(0, 4)
            });
            return;
        }

        foreach (var preset in _aiPromptPresetsDraft)
        {
            AiPromptPresetsPanel.Children.Add(BuildAiPromptPresetListItem(preset));
        }
    }

    private Control BuildAiPromptPresetListItem(AiPromptPreset preset)
    {
        var card = new ContinuousBorder
        {
            Classes = { "card" },
            Padding = new Thickness(12, 8),
            Cursor = new Cursor(StandardCursorType.Hand)
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };

        // 1. 左侧：技能命令名 + 默认徽标 + 内容摘要
        var infoStack = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        titleRow.Children.Add(new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeight.SemiBold,
            Text = !string.IsNullOrWhiteSpace(preset.Name) ? preset.Name : "未命名",
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });

        titleRow.Children.Add(new ContinuousBorder
        {
            Classes = { "providerBadge" },
            Child = new TextBlock
            {
                Classes = { "providerBadgeText" },
                Text = "@" + preset.Name
            }
        });

        if (preset.IsDefault)
        {
            titleRow.Children.Add(new ContinuousBorder
            {
                Classes = { "providerBadge" },
                Background = ThemeBrush("SctAccentBrush", "#797EFE", "#797EFE"),
                Child = new TextBlock
                {
                    Classes = { "providerBadgeText" },
                    Foreground = Brushes.White,
                    Text = "默认 · 直接识图"
                }
            });
        }

        infoStack.Children.Add(titleRow);

        infoStack.Children.Add(new TextBlock
        {
            FontSize = 13.5,
            Classes = { "muted" },
            Text = string.IsNullOrWhiteSpace(preset.Content) ? "（内容为空）" : preset.Content.ReplaceLineEndings(" "),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 520
        });

        // 2. 右侧操作区：设为默认 / 编辑 / 删除
        var actionStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        if (!preset.IsDefault)
        {
            var btnSetDefault = new Button
            {
                Classes = { "secondary" },
                Content = "设为默认",
                Padding = new Thickness(12, 0),
                Height = 32,
                MinHeight = 32,
                FontSize = 13.5,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            ToolTip.SetTip(btnSetDefault, "将此提示词设为「直接识图」的默认提示词");
            btnSetDefault.Click += (_, _) =>
            {
                foreach (var p in _aiPromptPresetsDraft) p.IsDefault = false;
                preset.IsDefault = true;
                RenderAiPromptPresets();
                AutoSaveSettings();
                ShowStatus($"已将「{preset.Name}」设为默认提示词");
            };
            actionStack.Children.Add(btnSetDefault);
        }

        var btnEdit = new Button
        {
            Classes = { "secondary" },
            Content = "编辑",
            Padding = new Thickness(14, 0),
            Height = 32,
            MinHeight = 32,
            MinWidth = 56,
            FontSize = 13.5,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(btnEdit, "弹出窗口编辑技能命令名与提示词内容");
        btnEdit.Click += async (_, _) =>
        {
            var editWin = new AiPromptPresetEditWindow(preset);
            await editWin.ShowDialog(this);
            if (editWin.IsConfirmed)
            {
                int idx = _aiPromptPresetsDraft.IndexOf(preset);
                if (idx >= 0) _aiPromptPresetsDraft[idx] = editWin.ResultPreset;
                RenderAiPromptPresets();
                AutoSaveSettings();
                ShowStatus($"已更新提示词「{editWin.ResultPreset.Name}」");
            }
        };
        actionStack.Children.Add(btnEdit);

        var btnDelete = new Button
        {
            Classes = { "titleIcon" },
            Content = "✕",
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            FontSize = 12,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(btnDelete, "删除此预设提示词");
        btnDelete.Click += (_, _) =>
        {
            bool wasDefault = preset.IsDefault;
            _aiPromptPresetsDraft.Remove(preset);
            // 删除的是默认项时，默认转移给剩余第一条
            if (wasDefault && _aiPromptPresetsDraft.Count > 0)
            {
                _aiPromptPresetsDraft[0].IsDefault = true;
            }
            RenderAiPromptPresets();
            AutoSaveSettings();
            ShowStatus($"已删除提示词「{preset.Name}」");
        };
        actionStack.Children.Add(btnDelete);

        Grid.SetColumn(infoStack, 0);
        Grid.SetColumn(actionStack, 1);
        grid.Children.Add(infoStack);
        grid.Children.Add(actionStack);
        card.Child = grid;

        return card;
    }

    /// <summary>服务商摘要行单色 SVG 图标（Material 风格轮廓，颜色由 providerDetailIcon 样式跟随主题）。</summary>
    private static class ProviderIconGeometry
    {
        /// <summary>地球：接口地址</summary>
        public const string Url = "M11.99 2C6.47 2 2 6.48 2 12s4.47 10 9.99 10C17.52 22 22 17.52 22 12S17.52 2 11.99 2zm6.93 6h-2.95c-.32-1.25-.78-2.45-1.38-3.56 1.84.63 3.37 1.91 4.33 3.56zM12 4.04c.83 1.2 1.48 2.53 1.91 3.96h-3.82c.43-1.43 1.08-2.76 1.91-3.96zM4.26 14C4.1 13.36 4 12.69 4 12s.1-1.36.26-2h3.38c-.08.66-.14 1.32-.14 2 0 .68.06 1.34.14 2H4.26zm.82 2h2.95c.32 1.25.78 2.45 1.38 3.56-1.84-.63-3.37-1.9-4.33-3.56zm2.95-8H5.08c.96-1.66 2.49-2.93 4.33-3.56C8.81 5.55 8.35 6.75 8.03 8zM12 19.96c-.83-1.2-1.48-2.53-1.91-3.96h3.82c-.43 1.43-1.08 2.76-1.91 3.96zM14.34 14H9.66c-.09-.66-.16-1.32-.16-2 0-.68.07-1.35.16-2h4.68c.09.65.16 1.32.16 2 0 .68-.07 1.34-.16 2zm.25 5.56c.6-1.11 1.06-2.31 1.38-3.56h2.95c-.96 1.65-2.49 2.93-4.33 3.56zM16.36 14c.08-.66.14-1.32.14-2 0-.68-.06-1.34-.14-2h3.38c.16.64.26 1.31.26 2s-.1 1.36-.26 2h-3.38z";

        /// <summary>钥匙：API 密钥</summary>
        public const string ApiKey = "M12.65 10C11.83 7.67 9.61 6 7 6c-3.31 0-6 2.69-6 6s2.69 6 6 6c2.61 0 4.83-1.67 5.65-4H17v4h4v-4h2v-4H12.65zM7 14c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2z";

        /// <summary>芯片机器人：默认模型</summary>
        public const string Model = "M20 9V7c0-1.1-.9-2-2-2h-3c0-1.66-1.34-3-3-3S9 3.34 9 5H6c-1.1 0-2 .9-2 2v2c-1.66 0-3 1.34-3 3s1.34 3 3 3v4c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2v-4c1.66 0 3-1.34 3-3s-1.34-3-3-3zM7 19H6v-2h1v2zm0-4H6v-2h1v2zm0-4H6v-2h1v2zm7 7h-2v-2h2v2zm0-4h-2v-2h2v2zm0-4h-2v-2h2v2zm0-4h-2V6h2v2zm5 12h-1v-2h1v2zm0-4h-1v-2h1v2zm0-4h-1v-2h1v2z";
    }

    /// <summary>构建摘要行“单色 SVG 图标 + 文本”组合项（主题色由样式类控制，代码零硬编码颜色）。</summary>
    private static StackPanel BuildProviderDetailItem(string iconGeometry, string text)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(new Avalonia.Controls.Shapes.Path
        {
            Classes = { "providerDetailIcon" },
            Data = StreamGeometry.Parse(iconGeometry)
        });
        panel.Children.Add(new TextBlock
        {
            Classes = { "providerDetailText" },
            Text = text,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        ToolTip.SetTip(panel, text);
        return panel;
    }

    private static string MaskApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "未配置密钥";
        key = key.Trim();
        if (key.Length <= 8) return "••••••";
        string prefix = key.Substring(0, Math.Min(4, key.Length));
        string suffix = key.Substring(Math.Max(0, key.Length - 4));
        return $"{prefix}••••••{suffix}";
    }

    private static string FormatBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "默认接口地址";
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                string path = (uri.PathAndQuery != "/" && !string.IsNullOrWhiteSpace(uri.PathAndQuery)) ? uri.PathAndQuery : "";
                return $"{uri.Host}{path}";
            }
        }
        catch { }
        return url.Length > 28 ? url.Substring(0, 25) + "..." : url;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        AutoSaveSettings();
        _recordingBuiltin = null;
        _gdriveService?.CancelLogin();
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _overviewClosed = true;
        _weatherCts?.Cancel();
        CloseWeatherPicker(false);
        SkyOverview.IsAnimated = false;
        _overviewClock?.Stop();
        _overviewUpdateCts?.Cancel();
        if (_overviewSubscribed)
        {
            DependencyManager.Changed -= OnOverviewDependencyChanged;
            DependencyManager.ProgressTick -= OnOverviewDependencyChanged;
            _overviewSubscribed = false;
        }
        if (_automaticCloud != null) _automaticCloud.Changed -= UpdateAutomaticCloudUi;
        AutoSaveSettings();
        // 依赖管理器是进程级的，下载不随本窗关闭而停；这里只断订阅，别让事件拽着已关的窗口
        DependencyManager.Changed -= OnDependencyChanged;
        DependencyManager.ProgressTick -= OnDependencyProgress;
        _depsSubscribed = false;
        _gdriveService?.CancelLogin();
        _gdriveService?.Dispose();
        _gdriveService = null;
    }

    // -------------------- 顶部即时搜索与回车导航 --------------------

    private sealed class SettingSearchItem
    {
        public required string Id { get; init; }
        public required SettingsPage Page { get; init; }
        public required string Category { get; init; }
        public required string Title { get; init; }
        public required string Description { get; init; }
        public string Keywords { get; init; } = string.Empty;
        public required Func<Control?> GetTarget { get; init; }
        public Action? PrepareAction { get; init; }

        // ---- 运行时预计算的匹配缓存（BuildSearchIndex 末尾一次性填充）----
        /// <summary>标题 + 说明 + 分类 + 关键词的拼接文本，子串匹配主域。</summary>
        public string SearchText { get; set; } = string.Empty;
        /// <summary>关键词按空白切分的词元，英文/拼音词做整词前缀匹配（避免单字母子串误命中）。</summary>
        public string[] KeywordTokens { get; set; } = Array.Empty<string>();
        /// <summary>各 ASCII 词元的声母首字母链（如 zhuti→zt），拼音简拼匹配域。</summary>
        public string KeywordInitials { get; set; } = string.Empty;
    }

    private readonly List<SettingSearchItem> _searchIndex = new();
    private readonly List<SettingSearchItem> _searchResults = new();
    private int _searchIndexCursor = -1;
    private CancellationTokenSource? _highlightCts;
    private Control? _lastHighlightedControl;
    /// <summary>命令浮层的结果行控件缓存（与 _searchResults 按下标一一对应，用于切换选中态）。</summary>
    private readonly List<ContinuousBorder> _searchResultRows = new();
    /// <summary>最近一次查询的分词词元，用于结果行内关键词高亮。</summary>
    private string[] _searchTermTokens = Array.Empty<string>();
    /// <summary>命令浮层表头文本（结果数 / 无结果 / 当前位置），供选中态刷新。</summary>
    private TextBlock? _resultHeaderText;

    // 焦点态描边由 XAML 的 Border.searchBox:focus-within 伪类样式承载（带 BrushTransition），
    // 代码侧不再手工取色设置 BorderBrush——那会在主题切换瞬间滞留旧主题色。

    private void OnSearchBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        string text = TxtSearchBox?.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            ClearSearchState();
            return;
        }

        // 只更新命令浮层结果，不自动跳页；跳转交由 Enter / 点击
        ExecuteSearch(text);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (TxtSearchBox != null) TxtSearchBox.Text = string.Empty;
            ClearSearchState();
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
            return;
        }

        if (_searchResults.Count == 0) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                {
                    int idx = _searchIndexCursor >= 0 ? _searchIndexCursor : 0;
                    CloseSearchPopup();
                    NavigateToSearchItem(_searchResults[idx]);
                }
                break;
            case Key.Up:
                e.Handled = true;
                SelectResult(_searchIndexCursor <= 0 ? _searchResults.Count - 1 : _searchIndexCursor - 1);
                break;
            case Key.Down:
                e.Handled = true;
                SelectResult((_searchIndexCursor + 1) % _searchResults.Count);
                break;
        }
    }

    /// <summary>切换到指定下标的搜索结果：更新选中态与表头，并把选中行滚入浮层可视区。</summary>
    private void SelectResult(int index)
    {
        if (index < 0 || index >= _searchResults.Count) return;
        _searchIndexCursor = index;
        for (int i = 0; i < _searchResultRows.Count; i++)
            _searchResultRows[i].Classes.Set("sel", i == index);
        // 键盘 ↑↓ 越过可视区时跟随滚动（悬浮触发时该行本就可见，滚入为无操作）
        if (index < _searchResultRows.Count)
            _searchResultRows[index].BringIntoView();
        UpdateResultHeader();
    }

    private void CloseSearchPopup()
    {
        if (SearchResultPopup != null) SearchResultPopup.IsOpen = false;
    }

    /// <summary>浮层关闭（含 light-dismiss 点击外部关闭）：一并撤去主体内容区遮罩。</summary>
    private void OnSearchPopupClosed(object? sender, EventArgs e)
    {
        if (SearchScrim != null) SearchScrim.IsVisible = false;
    }

    private void ClearSearchState()
    {
        _searchResults.Clear();
        _searchIndexCursor = -1;
        _searchTermTokens = Array.Empty<string>();
        _searchResultRows.Clear();
        SearchResultList?.Children.Clear();
        SearchResultHeaderList?.Children.Clear();
        CloseSearchPopup();
        ClearSearchHighlight();
    }

    /// <summary>判断点击源是否位于命令浮层（SearchResultPopup）逻辑子树内。</summary>
    private bool IsSourceInSearchPopup(object? source)
    {
        if (SearchResultPopup is not { IsOpen: true } || source is not Visual visual) return false;
        foreach (var ancestor in visual.GetLogicalAncestors())
        {
            if (ReferenceEquals(ancestor, SearchResultPopup)) return true;
        }
        return false;
    }

    /// <summary>点回搜索框时若已有内容则重开浮层（此前浮层关闭后必须再敲一个字才重新展开）。</summary>
    private void OnSearchBoxGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtSearchBox?.Text)
            && _searchResults.Count > 0
            && SearchResultPopup is { IsOpen: false })
        {
            RebuildSearchPopup();
        }
    }

    /// <summary>
    /// 执行搜索：按空白分词，全部词元同时命中（AND）才算匹配，支持「贴图 阴影」这类多词检索。
    /// 中文词元走子串匹配；纯英文字母词元额外支持关键词整词前缀与拼音声母简拼（如 zt 命中 zhuti）。
    /// 命中后按相关度降序排列：标题命中 > 关键词命中 > 说明命中，前缀命中 > 子串命中。
    /// </summary>
    private void ExecuteSearch(string keyword)
    {
        _searchResults.Clear();
        _searchIndexCursor = -1;

        string[] terms = keyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            ClearSearchState();
            return;
        }

        // 边搜边打分：单条目总分 = 各词元最佳命中分之和（多词全中标题者自然最高）
        var scored = new List<(SettingSearchItem Item, int Score, int Order)>();
        int order = 0;
        foreach (var item in _searchIndex)
        {
            int score = 0;
            bool allMatch = true;
            foreach (string term in terms)
            {
                int s = ScoreTermMatch(item, term);
                if (s <= 0)
                {
                    allMatch = false;
                    break;
                }
                score += s;
            }
            if (allMatch) scored.Add((item, score, order++));
        }

        // 相关度排序：分值降序，同分保持索引原序（List.Sort 不稳定，Order 兜底）
        scored.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Order.CompareTo(b.Order));
        foreach (var entry in scored) _searchResults.Add(entry.Item);

        // 始终选中第一个结果，重建并展开命令浮层
        _searchTermTokens = terms;
        _searchIndexCursor = _searchResults.Count > 0 ? 0 : -1;
        RebuildSearchPopup();
    }

    /// <summary>
    /// 单词元命中分值（0 = 不命中，判定域与打分域一致）：
    /// 标题前缀 100 &gt; 标题子串 70 &gt; 关键词整词前缀 50 &gt; 拼音声母简拼 40 &gt; 说明/分类子串 20。
    /// </summary>
    private static int ScoreTermMatch(SettingSearchItem item, string term)
    {
        if (item.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 100;
        if (item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) return 70;

        if (IsAsciiLetters(term))
        {
            string kw = term.ToLowerInvariant();
            foreach (string token in item.KeywordTokens)
            {
                if (token.StartsWith(kw, StringComparison.Ordinal)) return 50;
            }
            if (item.KeywordInitials.Contains(kw, StringComparison.Ordinal)) return 40;
        }

        if (item.SearchText.Contains(term, StringComparison.OrdinalIgnoreCase)) return 20;
        return 0;
    }

    private static bool IsAsciiLetters(string text)
    {
        if (text.Length == 0) return false;
        foreach (char c in text)
        {
            if (c is (< 'a' or > 'z') and (< 'A' or > 'Z')) return false;
        }
        return true;
    }

    /// <summary>
    /// 从 ASCII 词元提取拼音声母首字母链（zhuti→zt、kaiji→kj、quse→qs）：
    /// zh/ch/sh 双字母声母优先，其余单声母贪心命中，非声母字母（韵母）跳过。
    /// 英文单词同样会得到一条辅音链，仅作为附加匹配域，不会引入误排除。
    /// </summary>
    private static string BuildPinyinInitials(string token)
    {
        string lower = token.ToLowerInvariant();
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < lower.Length)
        {
            if (i + 1 < lower.Length)
            {
                string two = lower.Substring(i, 2);
                if (two is "zh" or "ch" or "sh")
                {
                    sb.Append(two[0]);
                    i += 2;
                    continue;
                }
            }
            char c = lower[i];
            if (c is 'b' or 'p' or 'm' or 'f' or 'd' or 't' or 'n' or 'l'
                or 'g' or 'k' or 'h' or 'j' or 'q' or 'x' or 'r'
                or 'z' or 'c' or 's' or 'y' or 'w')
            {
                sb.Append(c);
            }
            i++;
        }
        return sb.ToString();
    }

    /// <summary>重建并展开命令浮层：固定表头（结果数/无结果/当前位置）+ 可滚动结果行（标题/说明/分类徽标）。</summary>
    private void RebuildSearchPopup()
    {
        if (SearchResultList == null || SearchResultHeaderList == null) return;
        SearchResultList.Children.Clear();
        SearchResultHeaderList.Children.Clear();
        _searchResultRows.Clear();

        if (_searchResults.Count == 0)
        {
            SearchResultHeaderList.Children.Add(BuildPopupHeader("未找到相关设置 · 换个关键词试试（支持拼音简拼，如 zt→主题）"));
        }
        else
        {
            SearchResultHeaderList.Children.Add(BuildPopupHeader($"{_searchResults.Count} 项结果  ·  ↑↓ 选择  ↵ 跳转  Esc 关闭"));
            for (int i = 0; i < _searchResults.Count; i++)
            {
                int index = i;
                var row = CreateResultRow(_searchResults[i]);
                row.PointerReleased += (_, _) =>
                {
                    CloseSearchPopup();
                    NavigateToSearchItem(_searchResults[index]);
                };
                // 悬浮与键盘选中联动：鼠标停在哪行，选中态与回车目标就切到哪行
                row.PointerEntered += (_, _) => SelectResult(index);
                _searchResultRows.Add(row);
                SearchResultList.Children.Add(row);
            }
        }

        ApplyResultSelection();
        UpdateResultHeader();
        if (SearchResultPopup != null) SearchResultPopup.IsOpen = true;
        // 浮层展开：压暗主体内容区（scrim），让浮层与内容区分层
        if (SearchScrim != null) SearchScrim.IsVisible = true;
    }

    private ContinuousBorder BuildPopupHeader(string text)
    {
        var header = new ContinuousBorder();
        header.Classes.Add("searchRHeader");
        var title = new TextBlock();
        title.Classes.Add("searchRHeaderTitle");
        title.Text = text;
        _resultHeaderText = title;
        header.Child = title;
        return header;
    }

    private void UpdateResultHeader()
    {
        if (_resultHeaderText == null) return;
        _resultHeaderText.Text = _searchResults.Count > 0
            ? $"{_searchIndexCursor + 1} / {_searchResults.Count}  ·  ↑↓ 选择  ↵ 跳转  Esc 关闭"
            : "未找到相关设置 · 换个关键词试试（支持拼音简拼，如 zt→主题）";
    }

    private void ApplyResultSelection()
    {
        for (int i = 0; i < _searchResultRows.Count; i++)
            _searchResultRows[i].Classes.Set("sel", i == _searchIndexCursor);
    }

    private ContinuousBorder CreateResultRow(SettingSearchItem item) => CreateSearchResultRow(item.Title, item.Description, item.Category, _searchTermTokens);

    /// <summary>设置搜索与地区搜索共用排版、关键词高亮及连续结果底片。</summary>
    private ContinuousBorder CreateSearchResultRow(string title, string? description, string category, string[] terms)
    {
        var row = new ContinuousBorder();
        row.Classes.Add("searchResultRow");

        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

        var texts = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2, Margin = new Thickness(0, 0, 12, 0) };
        texts.Children.Add(BuildHighlightedText(title, true, terms));
        if (!string.IsNullOrEmpty(description))
            texts.Children.Add(BuildHighlightedText(description, false, terms));
        Grid.SetColumn(texts, 0);
        layout.Children.Add(texts);

        var cat = new ContinuousBorder { Classes = { "searchRCategory" }, Margin = new Thickness(0, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var catText = new TextBlock { Classes = { "searchRCategoryText" }, Text = category };
        cat.Child = catText;
        Grid.SetColumn(cat, 1);
        layout.Children.Add(cat);

        row.Child = layout;
        return row;
    }

    /// <summary>构建带关键词高亮的文本：各查询词元命中段染品牌紫并加粗。</summary>
    private TextBlock BuildHighlightedText(string text, bool isTitle, string[] terms)
    {
        var tb = new TextBlock();
        tb.Classes.Add(isTitle ? "searchRTitle" : "searchRDesc");
        tb.TextTrimming = TextTrimming.CharacterEllipsis;

        if (string.IsNullOrEmpty(text) || terms.Length == 0)
        {
            tb.Text = text;
            return tb;
        }

        var ranges = new List<(int Start, int End)>();
        foreach (var term in terms)
        {
            if (string.IsNullOrEmpty(term)) continue;
            int idx = 0;
            while (idx <= text.Length - term.Length && (idx = text.IndexOf(term, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                ranges.Add((idx, idx + term.Length));
                idx += term.Length;
            }
        }

        if (ranges.Count == 0)
        {
            tb.Text = text;
            return tb;
        }

        // 合并重叠区间
        ranges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<(int Start, int End)>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else
                merged.Add(r);
        }

        IBrush? accent = null;
        if (this.TryFindResource("SctAccentBrush", ActualThemeVariant, out var v) && v is IBrush ab)
            accent = ab;

        var inlines = new InlineCollection();
        int pos = 0;
        foreach (var (start, end) in merged)
        {
            if (start > pos)
                inlines.Add(new Run(text.Substring(pos, start - pos)));
            inlines.Add(new Run(text.Substring(start, end - start))
            {
                Foreground = accent,
                FontWeight = FontWeight.Bold,
            });
            pos = end;
        }
        if (pos < text.Length)
            inlines.Add(new Run(text.Substring(pos)));

        tb.Inlines = inlines;
        return tb;
    }

    private async void NavigateToSearchItem(SettingSearchItem item)
    {
        EnsurePageLoaded(item.Page);
        item.PrepareAction?.Invoke();
        SetNavActiveOnly(item.Page);
        int version = _pageChangeVersion;

        // 允许 UI 任务（如子页签展开、面板懒加载渲染）完成测量
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(30);
        if (version != _pageChangeVersion) return;

        Control? target = item.GetTarget();
        if (target == null)
        {
            Control? panel = GetPanelByPage(item.Page);
            if (panel != null) ScrollToPanel(panel, item.Page);
            return;
        }

        Control highlightTarget = GetHighlightContainer(target);

        if (MainScrollViewer?.Content is Visual contentVisual)
        {
            Point? p = highlightTarget.TranslatePoint(new Point(0, 0), contentVisual);
            if (p.HasValue)
            {
                _isNavigating = true;
                double targetY = Math.Max(0, p.Value.Y - 60);
                MainScrollViewer.Offset = new Vector(0, targetY);
                await Task.Delay(50);
                if (version != _pageChangeVersion) return;
                _isNavigating = false;
            }
        }

        FlashHighlightTarget(highlightTarget);
    }

    private static Control GetHighlightContainer(Control element)
    {
        Visual? current = element;
        while (current != null && current is Control c)
        {
            if (c.Classes.Contains("fieldRow") || c.Classes.Contains("hotkeyMatrixRow") || c.Classes.Contains("hotkeyRow") || c.Classes.Contains("guideBox"))
                return c;
            if (c.Parent is not Visual p || p is Window)
                break;
            current = p;
        }
        return element;
    }

    private async void FlashHighlightTarget(Control target)
    {
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        _highlightCts = new CancellationTokenSource();
        var ct = _highlightCts.Token;

        if (_lastHighlightedControl != null && _lastHighlightedControl != target)
        {
            _lastHighlightedControl.Classes.Remove("searchMatchFlash");
        }

        _lastHighlightedControl = target;
        target.Classes.Add("searchMatchFlash");

        try
        {
            await Task.Delay(1400, ct);
            if (!ct.IsCancellationRequested)
            {
                target.Classes.Remove("searchMatchFlash");
                if (_lastHighlightedControl == target)
                {
                    _lastHighlightedControl = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ClearSearchHighlight()
    {
        _highlightCts?.Cancel();
        _highlightCts?.Dispose();
        _highlightCts = null;

        if (_lastHighlightedControl != null)
        {
            _lastHighlightedControl.Classes.Remove("searchMatchFlash");
            _lastHighlightedControl = null;
        }
    }

    private void SwitchTab(RadioButton? tab)
    {
        if (tab != null && tab.IsChecked != true)
        {
            tab.IsChecked = true;
        }
    }

    private Control? FindBuiltinRowById(string id)
    {
        if (CaptureShortcutGroupPanel != null)
        {
            var found = FindControlByTag(CaptureShortcutGroupPanel, id);
            if (found != null) return found;
        }
        if (StickerShortcutGroupPanel != null)
        {
            var found = FindControlByTag(StickerShortcutGroupPanel, id);
            if (found != null) return found;
        }
        return null;
    }

    private Control? FindActionRowById(string id)
    {
        if (ActionListPanel != null)
        {
            return FindControlByTag(ActionListPanel, id);
        }
        return null;
    }

    private static Control? FindControlByTag(Control parent, string targetTag)
    {
        if (Equals(parent.Tag, targetTag)) return parent;
        if (parent is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                var found = FindControlByTag(child, targetTag);
                if (found != null) return found;
            }
        }
        else if (parent is ContentControl cc && cc.Content is Control childControl)
        {
            var found = FindControlByTag(childControl, targetTag);
            if (found != null) return found;
        }
        else if (parent is Decorator b && b.Child is Control bChild)
        {
            var found = FindControlByTag(bChild, targetTag);
            if (found != null) return found;
        }
        return null;
    }

    private Control? GetPanelByPage(SettingsPage page) => page switch
    {
        SettingsPage.Overview => PanelOverview,
        SettingsPage.Basic => PanelBasic,
        SettingsPage.Interact => PanelInteract,
        SettingsPage.Annot => PanelAnnot,
        SettingsPage.Actions => PanelActions,
        SettingsPage.Translate => PanelTranslate,
        SettingsPage.Ai => PanelAi,
        SettingsPage.CloudSync => PanelCloudSync,
        SettingsPage.Deps => PanelDeps,
        SettingsPage.Toolbar => PanelToolbar,
        SettingsPage.Stats => PanelStats,
        SettingsPage.About => PanelAbout,
        _ => null
    };

    private void BuildSearchIndex()
    {
        _searchIndex.Clear();

        _searchIndex.Add(new SettingSearchItem
        {
            Id = "overview.home", Page = SettingsPage.Overview, Category = "概览", Title = "概览工作台",
            Description = "今日使用、近期趋势、常用快捷键、功能配置与最近操作通知。",
            Keywords = "home dashboard 概览 总览 首页 工作台 今日 天空 版本 更新 运行依赖", GetTarget = () => PanelOverview
        });

        // 1. 通用与存储
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.theme",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "外观主题",
            Description = "即时切换应用亮暗色界面风格（浅色 / 暗色 / 跟随系统）。",
            Keywords = "theme zhuti 浅色 暗色 跟随系统 深色 换肤 皮肤 明亮 黑暗",
            GetTarget = () => RadThemeLight
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.startup",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "开机启动",
            Description = "开机自动启动 yuSnip；管理员模式开启时自动改走计划任务提权启动。",
            Keywords = "kaiji qidong startup 自启 自动启动 计划任务 开机自启",
            GetTarget = () => RadRunOnStartupOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.admin",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "管理员模式",
            Description = "开启后软件始终以管理员权限运行，快捷键不再受高权限前台窗口限制。",
            Keywords = "admin guanliyuan tiquan 提权 权限 UAC 任务管理器 管理员",
            GetTarget = () => RadAdminModeOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.save_notification",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "保存成功后显示通知",
            Description = "截图保存成功后在屏幕角落弹出系统或应用通知气泡。",
            Keywords = "baocun tongzhi notice toast 保存通知 弹窗提示",
            GetTarget = () => RadShowSaveNotificationOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.startup_notification",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "启动后显示就绪通知",
            Description = "应用启动就绪后显示系统提示通知。",
            Keywords = "qidong tongzhi ready 就绪提示 启动通知",
            GetTarget = () => RadShowStartupNotificationOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.clipboard",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "截图保存后自动复制到剪贴板",
            Description = "截图保存成功后，自动将截图复制到系统剪贴板。",
            Keywords = "jiantieban clipboard copy 剪切板 复制 剪贴 自动复制 保存后复制",
            GetTarget = () => RadCopyToClipboardOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.sound",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "保存时播放提示音",
            Description = "截图完成或保存成功时播放轻量快门/提示音效。",
            Keywords = "sound tisheng yinxiao 音效 提示音 声音 快门声 保存声音",
            GetTarget = () => RadPlaySaveSoundOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.update",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "启动时自动检查更新",
            Description = "启动应用时自动在后台静默检查是否有最新版本发布。",
            Keywords = "gengxin update shengji 升级 更新 版本 检查更新",
            GetTarget = () => RadCheckUpdateOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.history",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "截图历史记录与自动 OCR",
            Description = "开启后自动记录最近截图内容，支持在贴图/历史面板中回看与文本检索。",
            Keywords = "lishi history 历史 截图历史 自动OCR 文字提取",
            GetTarget = () => RadHistoryOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.savedir",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "截图保存目录",
            Description = "默认截图文件保存位置与文件夹路径。",
            Keywords = "mulu path dir lujing 路径 保存位置 文件夹 存储路径",
            GetTarget = () => TxtSaveDirectory
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.rec_savedir",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "录屏保存目录",
            Description = "录制完成快速保存的落盘位置，留空则用 视频\\yuSnip 录屏。",
            Keywords = "baocun mulu dir jian 保存目录 落盘 路径 视频目录 录屏保存",
            GetTarget = () => TxtRecSaveDir
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "basic.filename",
            Page = SettingsPage.Basic,
            Category = "通用与存储",
            Title = "文件命名规则",
            Description = "保存文件名的生成模板（支持年月日时分秒等时间占位变量）。",
            Keywords = "mingming template name geshi 模板 命名 格式 时间戳 文件名",
            GetTarget = () => TxtFileNameTemplate
        });

        // 2. 交互与行为（四栏）＋ 标注页
        // ⚠ 栏内控件藏在隐藏的 StackPanel 里：每条都要 PrepareAction 先把所属栏切出来，
        //    否则跳转与高亮打在一个 IsVisible=false 的控件上，现象是"搜到了但页面没动"。
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.autodetect",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "自动检测模式",
            Description = "控制截图时鼠标自动吸附窗口或内部元素的行为（检测元素 / 仅窗口 / 不检测）。",
            Keywords = "jiance subwindow xifu 吸附 窗口 元素 智能检测 探测 选区",
            PrepareAction = () => SwitchTab(RadInteractTabCapture),
            GetTarget = () => RadAutoDetectElement
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.dwell_ms",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "触发延时",
            Description = "鼠标停下后多久检测元素。数值越小响应越灵敏，数值越大越防抖。",
            Keywords = "yanshi dwell ms 延迟 防抖 停顿 悬停 响应时间 触发延时",
            PrepareAction = () => SwitchTab(RadInteractTabCapture),
            GetTarget = () => PnlElementDwell
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.dwell_deadband",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "悬停检测移动幅度死区",
            Description = "忽略小幅移动的范围。鼠标微调小于该距离时不重新检测。",
            Keywords = "siqu deadband px 微动 抖动 幅度 鼠标防抖 范围",
            PrepareAction = () => SwitchTab(RadInteractTabCapture),
            GetTarget = () => PnlElementDwell
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.guideline",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "截图辅助线",
            Description = "截图时显示紫色参考线对齐窗口/元素，起框后改对齐选区自身。",
            Keywords = "cankaoxian guideline zhuxian 参考线 辅助线 坐标 贯穿对齐",
            PrepareAction = () => SwitchTab(RadInteractTabCapture),
            GetTarget = () => RadGuidelineModeOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.crosshair",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "十字准星",
            Description = "跟随鼠标贯穿整屏的十字瞄准线，起框后自动消失。",
            Keywords = "zhunxing crosshair shizi 准星 十字线 瞄准 光标 贯穿 对齐",
            PrepareAction = () => SwitchTab(RadInteractTabCapture),
            GetTarget = () => RadCrosshairModeOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.hints",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "左下角提示",
            Description = "开启后，在截图遮罩左下角显示当前操作与快捷键提示小抄。",
            Keywords = "zuoxiajiao xiaochao hint tishi 左下角提示 提示条 快捷键提示 技巧 操作指南 小抄",
            PrepareAction = () => SwitchTab(RadInteractTabCapture),
            GetTarget = () => RadShowCaptureHintsOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.longscroll_preview",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "长截图拼接过程与预览",
            Description = "长截图滚动拼接过程中实时展示拼接动画与微型缩略图浮窗。",
            Keywords = "changjietu scroll changtu preview 滚动 长图 拼接 预览浮窗",
            PrepareAction = () => SwitchTab(RadInteractTabCapture),
            GetTarget = () => RadShowLongScrollPreviewOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_dblclick",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "贴图双击动作",
            Description = "鼠标双击贴图窗口时的默认响应动作（关闭 / 缩略图 / 复制 / 无）。",
            Keywords = "shuangji doubleclick dblclick 关闭 缩略图 复制 贴图双击",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerDblClickClose
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_shadow",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "贴图阴影",
            Description = "贴图窗口外围阴影：关闭 / 自动（跟随截图时的阴影勾选反向）/ 开启。",
            Keywords = "yinying shadow 投影 贴图边缘 贴图阴影 外框阴影 自动 auto 边界 轮廓 看不清",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerDefaultShadowOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_checkerboard",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "透明图片背景",
            Description = "透明图片贴图时垫透明格子或纯透明透出桌面，窗口阴影照常保留。",
            Keywords = "touming gezi checkerboard transparent 棋盘格 透明格子 纯透明 背景 贴图背景",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerCheckerboardOn
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_text_select",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "贴图文字可选中",
            Description = "贴图窗口内含 OCR 文本时，允许鼠标直接框选复制文字。",
            Keywords = "xuanze select text 选中文本 划词 贴图文字 文本选择",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerTextSelectableOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_selection_toolbar",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "划词工具栏",
            Description = "选中贴图文字后浮出工具条：复制、标注（荧光/虚线/波浪线）、翻译、AI 解释与联网搜索。",
            Keywords = "huaci toolbar 划词 工具栏 浮条 荧光 波浪线 虚线 标注 翻译 ai 贴图文字",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerSelectionToolbarOn
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_grab_text",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "选中文本贴图",
            Description = "开启后，在别的软件里选中文字即可直接按贴图热键钉到屏幕上，无需先复制；原来复制的内容会自动还原。",
            Keywords = "xuanzhongwenben grab sticker 选中文字 钉贴图 文字贴图 直接钉屏",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerGrabSelectedTextOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_reflow",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "贴图缩放自动重排文本",
            Description = "调整贴图窗口尺寸时，文本段落内容自动重排适应宽度。",
            Keywords = "chongpai reflow resize 缩放 文字换行 文本重排",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerResizeReflowsTextOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_color_picker",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "贴图取色器",
            Description = "图片贴图上按住 Shift 悬停出放大镜与色值，可复制色值、切换 HEX／RGB。",
            Keywords = "quse color picker 取色 吸管 放大镜 色值 hex rgb 贴图取色",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerColorPickerOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.sticker_restore_sort",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "贴图关闭后恢复顺序",
            Description = "按关闭顺序或按创建顺序恢复已关闭的贴图窗口。",
            Keywords = "huifu restore sort 贴图恢复 顺序 重新打开",
            PrepareAction = () => SwitchTab(RadInteractTabSticker),
            GetTarget = () => RadStickerRestoreByClosed
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.matting",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "智能抠图默认模型",
            Description = "一键智能抠图消除背景时默认使用的推理模型（通用 RMBG / 人像专用）。",
            Keywords = "koutu matting bria rmbg 人像 通用 抠图 背景消除 抠图模型",
            PrepareAction = () => SwitchTab(RadInteractTabMatting),
            GetTarget = () => RadMattingModelGeneral
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "interact.matting_result",
            Page = SettingsPage.Interact,
            Category = "交互与行为",
            Title = "抠图结果",
            Description = "智能抠图完成后结果的去向：写入剪贴板、生成贴图，或剪贴板并贴图。",
            Keywords = "koutujieguo quxiang shuchu koutu result jiantieban tie 去向 输出 结果 抠图去处 剪贴板 贴图",
            PrepareAction = () => SwitchTab(RadInteractTabMatting),
            GetTarget = () => RadMattingResultClipboard
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "annot.reuse_tool",
            Page = SettingsPage.Annot,
            Category = "标注",
            Title = "记忆上次标注工具",
            Description = "再次启动截图时自动延续选用上一次关闭前所选的标注工具。",
            Keywords = "jiyi reuse last 标注工具 保持工具 记忆上次 沿用上次标注工具",
            GetTarget = () => RadReuseLastAnnotationToolOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "annot.counter_renumber",
            Page = SettingsPage.Annot,
            Category = "标注",
            Title = "删除序号时自动重编号",
            Description = "删掉某个序号后，比它大的编号依次前移保持连续；关闭则保留原编号。",
            Keywords = "shanhao xuhao chongxin bianhao counter renumber 序号 重编号 编号 删除序号",
            GetTarget = () => RadCounterRenumberOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "annot.counter_style_memory",
            Page = SettingsPage.Annot,
            Category = "标注",
            Title = "记住上次序号样式",
            Description = "序号的类型、外形、底色、胶囊圆角与引线箭头是否沿用上次，关闭则每次截图回到默认。",
            Keywords = "xuhao yangshi jiyi counter style glyph 序号 样式 类型 形状 底色 记忆",
            GetTarget = () => RadCounterRememberOff
        });

        // 3. 快捷键与动作
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.capture.colorpicker",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "取色器 (截图快捷键)",
            Description = "截图时按 C 键直接读取光标处或放大镜中心像素十六进制/RGB色号并复制到剪贴板。",
            Keywords = "quse colorpicker xiguan 吸管 C 复制颜色 拾色器 十六进制 RGB 取色 色号",
            PrepareAction = () => SwitchTab(RadActionTabCapture),
            GetTarget = () => FindBuiltinRowById("capture.colorpicker") ?? CaptureShortcutGroupPanel
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.capture.counter",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "序号标号工具 (截图快捷键)",
            Description = "截图标注序号工具，按点击顺序在选区内连续标记递增数字标号。",
            Keywords = "xuhao counter 123 序号 标号 步骤标注 编号",
            PrepareAction = () => SwitchTab(RadActionTabCapture),
            GetTarget = () => FindBuiltinRowById("capture.counter") ?? CaptureShortcutGroupPanel
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.capture.tools",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "截图标注工具快捷键",
            Description = "矩形、椭圆、画笔、箭头、文本、马赛克、橡皮擦等标注工具快捷键切换与改键。",
            Keywords = "biaozhu rectangle arrow brush text mosaic huabi jianxing 矩形 箭头 画笔 文本 马赛克 橡皮擦 荧光笔 聚光灯",
            PrepareAction = () => SwitchTab(RadActionTabCapture),
            GetTarget = () => CaptureShortcutGroupPanel
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.capture.edit",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "截图操作与编辑快捷键",
            Description = "截图完成、撤销、重做、保存、复制、钉住贴图、长截图等操作按键设置。",
            Keywords = "chexiao fuzhi baocun dingzhu undo redo save copy pin 撤销 重做 保存 复制 贴图 钉住 另存为",
            PrepareAction = () => SwitchTab(RadActionTabCapture),
            GetTarget = () => CaptureShortcutGroupPanel
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.sticker",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "贴图快捷键",
            Description = "贴图窗口的锁定、穿透、缩略图、取色唤出键、精细缩放修饰键与关闭等设置。",
            Keywords = "tietu sticker 贴图 缩放 精细 微调 取色 唤出 修饰键 Alt Shift 销毁 置顶 透明度 穿透 锁定 贴图快捷键",
            PrepareAction = () => SwitchTab(RadActionTabSticker),
            GetTarget = () => StickerShortcutGroupPanel
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.record",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "录屏快捷键",
            Description = "录制会话期间的暂停/继续、完成录制、重新录制按键；随录制开始注册、结束即卸载。",
            Keywords = "luping record zanshi tingzhi pause stop F10 F11 录屏 录制 暂停 继续 结束 重新录制 兜底",
            PrepareAction = () => SwitchTab(RadActionTabRecord),
            GetTarget = () => RecordShortcutGroupPanel
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.global",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "全局动作与鼠标",
            Description = "为动作独立配置键盘快捷键、辅助键与鼠标点击或拖动，截图类动作可直接拖动框选。",
            Keywords = "quanju hotkey rejian shubiao mouse Win Shift Ctrl Alt X1 X2 全局鼠标 鼠标手势 拖动 辅助键 引导键 区域截图 全局快捷键 快捷键 动作 热键 全屏截图",
            PrepareAction = () => SwitchTab(RadActionTabGlobal),
            GetTarget = () => ActionListPanel
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "actions.guide",
            Page = SettingsPage.Actions,
            Category = "快捷键与动作",
            Title = "外部命令行与脚本调用指引",
            Description = "通过命令行参数或 JavaScript 脚本从外部程序直接调度 yuSnip 的执行指南。",
            Keywords = "minglinghang cli script js javascript 命令行 脚本 外部调用 终端",
            PrepareAction = () => SwitchTab(RadActionTabGuide),
            GetTarget = () => PnlViewGuide
        });

        // 4. OCR 与翻译
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.ocr_mode",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "OCR 结果输出模式",
            Description = "文字识别后是弹窗查看编辑排版，还是直接静默复制到系统剪贴板。",
            Keywords = "ocr shibie wenzishibie 文字识别 剪贴板 弹窗 输出模式",
            GetTarget = () => RadOcrOutputWindow
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.ocr_toast_sec",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "OCR 气泡停留时间",
            Description = "识别文本复制后，屏幕浮窗气泡提示的展示驻留秒数（3秒 / 5秒 / 10秒）。",
            Keywords = "qipao toast mi 秒 停留 提示时间 气泡停留 浮窗时间",
            GetTarget = () => RadOcrToastSec3
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.ocr_toast_pos",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "提示窗位置",
            Description = "静默复制成功时在屏幕上弹出的提示卡片位置。",
            Keywords = "qipao weizhi 屏幕顶部 右上角 居中 方位 弹出位置 提示窗 卡片位置",
            GetTarget = () => RadOcrToastPosTopCenter
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.provider",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "翻译服务商",
            Description = "选择 bilibili-index 免费翻译，或配置百度翻译／有道智云，也可不启用。",
            Keywords = "fanyi translate bilibili index 免费翻译 baidu youdao 百度翻译 有道翻译 服务商 引擎 翻译接口",
            GetTarget = () => RadTranslateNone
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.baidu",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "百度翻译凭证",
            Description = "配置百度开放平台翻译 API 的 AppId 与 SecretKey 密钥。",
            Keywords = "baidu appid secretkey miyao 百度密钥 凭证 百度翻译",
            GetTarget = () => TxtBaiduAppId
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.youdao",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "有道翻译凭证",
            Description = "配置有道智云翻译 API 的应用 ID 与应用密钥凭证。",
            Keywords = "youdao appid secretkey miyao 有道密钥 凭证 有道翻译",
            GetTarget = () => TxtYoudaoAppId
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.lang",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "翻译语言设置",
            Description = "配置翻译源语言、默认目标语言与备选目标语言。",
            Keywords = "yuyan language zhongwen yingwen 源语言 目标语言 自动识别 语种",
            GetTarget = () => CmbTranslateTargetLang
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.mode",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "翻译模式",
            Description = "选择仅提取文本翻译还是在原图上涂抹替换译文（图片到文本 / 图片到图片）。",
            Keywords = "tupianfanyi image translate 翻译模式 图片翻译模式 图片到文本 图片到图片 原位替换 涂抹翻译",
            GetTarget = () => RadTranslateImageToText
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.stream", Page = SettingsPage.Translate, Category = "OCR与翻译",
            Title = "流式翻译", Description = "图片到文本逐段显示原文与译文对照，默认关闭，可按需开启。",
            Keywords = "liushi stream 流式翻译 实时 对照 原文 译文",
            GetTarget = () => RadTranslateStreamOn
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "translate.mini_window",
            Page = SettingsPage.Translate,
            Category = "OCR与翻译",
            Title = "极简翻译小窗位置与行为",
            Description = "快捷翻译浮层小窗的停靠位置（跟随选区 / 屏幕居中）、自动关闭与自动复制选项。",
            Keywords = "xiaochuang mini 极简浮窗 跟随选区 居中 自动关闭 自动复制 翻译小窗",
            GetTarget = () => RadTranslateMiniPosFollow
        });

        // 5. AI 模型
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "ai.presets",
            Page = SettingsPage.Ai,
            Category = "AI 模型",
            Title = "AI 提示词预设",
            Description = "管理截图提问和图像分析常用的 Prompt 模板与技能命令。",
            Keywords = "tishici prompt yushe 角色 提示词 预设 模板 技能 识图提示词",
            PrepareAction = () => EnsureAiLoaded(),
            GetTarget = () => AiPromptPresetsPanel ?? PanelAi
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "ai.providers",
            Page = SettingsPage.Ai,
            Category = "AI 模型",
            Title = "AI 服务商配置",
            Description = "配置 OpenAI、Claude、DeepSeek、本地 Ollama 等视觉多模态大模型服务商与 API Key。",
            Keywords = "openai claude deepseek ollama api key 模型 大模型 服务商 接口地址",
            PrepareAction = () => EnsureAiLoaded(),
            GetTarget = () => AiProvidersPanel ?? PanelAi
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "ai.default_prompt",
            Page = SettingsPage.Ai,
            Category = "AI 模型",
            Title = "识图触发方式",
            Description = "选择点击 AI 识图后的交互流程：一键使用默认提示词直接分析，或每次弹出提问窗口输入定制需求。",
            Keywords = "moren prompt 直接提问 免输入 默认提示词 识图触发 一键分析",
            GetTarget = () => RadAiUseDefaultPromptOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "ai.window_pos",
            Page = SettingsPage.Ai,
            Category = "AI 模型",
            Title = "AI 提问窗口位置",
            Description = "唤起 AI 提问窗口时的默认弹出位置（跟随鼠标 / 屏幕居中）。",
            Keywords = "weizhi followmouse center 鼠标跟随 屏幕居中 提问窗口位置",
            GetTarget = () => RadAiPromptPosCenterScreen
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "ai.web_search",
            Page = SettingsPage.Ai,
            Category = "AI 模型",
            Title = "AI 识图联网搜索",
            Description = "大模型识图分析时是否自动联网检索最新相关网页信息。",
            Keywords = "websearch lianwang 联网 搜索 网页检索 联网搜索",
            GetTarget = () => RadAiSearchOff
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "ai.thinking",
            Page = SettingsPage.Ai,
            Category = "AI 模型",
            Title = "AI 深度思考",
            Description = "推理模型是否先思考再作答；关闭后直接快答，日常问答更快。",
            Keywords = "thinking reasoning shensi 深度思考 思考 推理 快答 慢 速度",
            GetTarget = () => RadAiThinkingOff
        });

        // 6. 云存储配置
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "cloud.provider",
            Page = SettingsPage.CloudSync,
            Category = "云存储配置",
            Title = "云同步服务商",
            Description = "配置 Google Drive 或 WebDAV 同步设置与截图记录备份。",
            Keywords = "yuntongbu sync webdav gdrive google 网盘 备份 同步 云存储",
            GetTarget = () => RadCloudNone
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "cloud.webdav",
            Page = SettingsPage.CloudSync,
            Category = "云存储配置",
            Title = "WebDAV 账户配置",
            Description = "配置私有 WebDAV 服务器地址、用户名、密码与同步文件名。",
            Keywords = "webdav url yonghuming mima 服务器 用户名 密码 坚果云 私有云",
            GetTarget = () => TxtWebDavUrl
        });

        // 7. 工具栏与样式
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "toolbar.selection_border",
            Page = SettingsPage.Toolbar,
            Category = "工具栏与样式",
            Title = "截图选框样式（线型、颜色与粗细）",
            Description = "设置截图选框的虚线/实线线型、自定义边框颜色与线条粗细（0~5像素，0表示不绘制）。",
            Keywords = "xuanzhekuang biankuang xianxing xuxian shixian yanse cuxi 选框 边框 线型 虚线 实线 颜色 粗细 宽度 像素 品牌紫",
            PrepareAction = () => EnsureToolbarLoaded(),
            GetTarget = () => (Control?)RadSelectionBorderDashed ?? PanelToolbar
        });

        _searchIndex.Add(new SettingSearchItem
        {
            Id = "toolbar.custom",
            Page = SettingsPage.Toolbar,
            Category = "工具栏与样式",
            Title = "自定义工具栏按钮与布局",
            Description = "自由拖拽排序和增删截图控制栏中的功能图标与垂直分隔线。",
            Keywords = "gongjulan toolbar tubiao 图标 拖拽 布局 排序 分隔线 按钮排序",
            PrepareAction = () => EnsureToolbarLoaded(),
            GetTarget = () => ToolbarLayoutPreview ?? PanelToolbar
        });

        _searchIndex.Add(new SettingSearchItem
        {
            Id = "toolbar.groups",
            Page = SettingsPage.Toolbar,
            Category = "工具栏与样式",
            Title = "工具栏分组（组卡管理）",
            Description = "把低频工具收成一格折叠显示：新建组、拖拽上栏与撤栏，以及组内成员的拖排与换组头（移除只靠拖拽）。",
            Keywords = "zu dagzu zuhe group 组 打组 分组 新建组 删除组 上栏 撤栏 并组 成员 折叠 二级工具栏 分隔线",
            PrepareAction = () => EnsureToolbarLoaded(),
            GetTarget = () => (Control?)(ToolbarGroupCards.IsVisible ? ToolbarGroupCards : ToolbarGroupEmpty) ?? PanelToolbar
        });

        // 录屏栏（2026-09-24 瘦身：只留设置页仍持有入口的项，画质／格式／延迟／声音两路／按键显示随控件一并删除；
        // 2026-09-26 撤「预览循环播放」「显示状态角标」两行时其两条搜索条目同删，同日再撤「双开时混音」⇒ 现 3 条）
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "record.cursor",
            Page = SettingsPage.Interact,
            Category = "录屏",
            Title = "光标入画",
            Description = "系统硬件光标是否进画面。关闭后成品里没有鼠标，点击涟漪也一并看不见。",
            Keywords = "guangbiao keshihua cursor ruzhua lianyi 光标 鼠标 入画 可视化 涟漪",
            PrepareAction = () => SwitchTab(RadInteractTabRecord),
            GetTarget = () => RadRecCursorOn
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "record.capture_source",
            Page = SettingsPage.Interact,
            Category = "录屏",
            Title = "画面采集方式",
            Description = "用于 FFmpeg：推荐 DXGI，兼容 GDI。Windows 原生使用 GDI。",
            Keywords = "huamian caiji dda dxgi gdi zhuaping 画面采集方式 采集方式 桌面复制 硬件加速 黑屏 冻结 兼容",
            PrepareAction = () => SwitchTab(RadInteractTabRecord),
            GetTarget = () => RadRecCapAuto
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "record.engine", Page = SettingsPage.Interact, Category = "录屏", Title = "录制引擎",
            Description = "自动优先 Windows；FFmpeg 需要运行组件。",
            Keywords = "luping luzhi yinqing windows ffmpeg 原生 系统 录制引擎 免安装",
            PrepareAction = () => SwitchTab(RadInteractTabRecord), GetTarget = () => RadRecEngineAuto
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "record.mic_volume",
            Page = SettingsPage.Interact,
            Category = "录屏",
            Title = "麦克风音量",
            Description = "混音时麦克风占比（40-100%），默认 40%。",
            Keywords = "maikefeng yinliang volume ji 麦克风音量 占比 音量 大小",
            PrepareAction = () => SwitchTab(RadInteractTabRecord),
            GetTarget = () => NsdRecMicVol
        });
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "deps.run",
            Page = SettingsPage.Deps,
            Category = "依赖运行",
            Title = "运行组件与 FFmpeg",
            Description = "OCR 识别引擎、抠图模型与 FFmpeg 录屏引擎的下载状态与存放位置。",
            Keywords = "ffmpeg huanjing download 环境 检测 下载 路径 转码 缺失 ocr moxing 模型 组件 依赖",
            GetTarget = () => PanelDeps
        });

        // 8. 使用统计
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "stats.report",
            Page = SettingsPage.Stats,
            Category = "使用统计",
            Title = "使用统计与分析报表",
            Description = "查看截图、AI 识图、OCR 与翻译的本机累计使用频次与近期趋势图表。",
            Keywords = "tongji stats cishu 次数 图表 趋势 近期 报表 使用数据",
            PrepareAction = () => EnsureStatsLoaded(),
            GetTarget = () => PanelStats
        });

        // 9. 关于
        _searchIndex.Add(new SettingSearchItem
        {
            Id = "about.info",
            Page = SettingsPage.About,
            Category = "关于",
            Title = "关于 yuSnip 与版本信息",
            Description = "查看软件当前版本号、检查新版本发布与开源协议信息。",
            Keywords = "guanyu about banben version 开源协议 更新 日志 版本号",
            GetTarget = () => PanelAbout
        });

        // 预计算匹配缓存：子串域 / 关键词词元 / 拼音声母简拼链（每项只算一次）
        foreach (var item in _searchIndex)
        {
            item.SearchText = string.Join('\n', item.Title, item.Description, item.Category, item.Keywords);
            item.KeywordTokens = item.Keywords.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string initials = string.Empty;
            foreach (string token in item.KeywordTokens)
            {
                if (IsAsciiLetters(token)) initials += BuildPinyinInitials(token);
            }
            item.KeywordInitials = initials;
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            try { BeginMoveDrag(e); } catch { }
        }
    }

    private void ApplySettingsTheme() => (Application.Current as App)?.ApplyThemeVariant();

    private void OnThemeChanged(object? sender, RoutedEventArgs e)
    {
        if (_isLoading || sender is not RadioButton { IsChecked: true } || RadThemeLight == null || RadThemeDark == null || RadThemeSystem == null) return;
        if (RadThemeLight.IsChecked == true) _settings.ThemeMode = "浅色";
        else if (RadThemeDark.IsChecked == true) _settings.ThemeMode = "暗色";
        else _settings.ThemeMode = "跟随系统";
        
        _settings.Save();
        ApplySettingsTheme();
    }

    private void SetNavActiveOnly(SettingsPage page)
    {
        if (_recordingBuiltin != null && page != SettingsPage.Actions)
        {
            _recordingBuiltin = null;
            UpdateBuiltinRecordingTip(null);
            RenderBuiltinShortcutList();
        }

        if (page == SettingsPage.Overview) RefreshOverviewUi();
        bool changed = _activePage != page;
        // 未完成布局的中间页仍持有上一页 Offset，快速连点时不能记到它名下。
        if (changed && !_isNavigating) _pageOffsets[_activePage] = MainScrollViewer.Offset.Y;
        _activePage = page;
        UpdateOverviewMotion();
        _isNavigating = true;
        int version = ++_pageChangeVersion;
        foreach (SettingsPage candidate in Enum.GetValues<SettingsPage>())
        {
            if (GetPanelByPage(candidate) is { } candidatePanel)
                candidatePanel.IsVisible = candidate == page;
        }
        TxtPageBreadcrumb.Text = page switch
        {
            SettingsPage.Overview => "概览",
            SettingsPage.Basic => "通用与存储",
            SettingsPage.Interact => "交互与行为",
            SettingsPage.Annot => "标注",
            SettingsPage.Actions => "快捷键与动作",
            SettingsPage.Translate => "OCR 与翻译",
            SettingsPage.Ai => "AI 模型",
            SettingsPage.CloudSync => "云存储配置",
            SettingsPage.Deps => "依赖运行",
            SettingsPage.Toolbar => "工具栏与样式",
            SettingsPage.Stats => "使用统计",
            _ => "关于",
        };
        if (GetPanelByPage(page) is { } activePanel)
            ShowPageWithMotion(activePanel, version, _pageOffsets.GetValueOrDefault(page), changed && SettingsMotionEnabled());

        SetNavActive(BtnNavOverview, page == SettingsPage.Overview);
        SetNavActive(BtnNavBasic, page == SettingsPage.Basic);
        SetNavActive(BtnNavInteract, page == SettingsPage.Interact);
        SetNavActive(BtnNavAnnot, page == SettingsPage.Annot);
        SetNavActive(BtnNavActions, page == SettingsPage.Actions);
        SetNavActive(BtnNavTranslate, page == SettingsPage.Translate);
        SetNavActive(BtnNavAi, page == SettingsPage.Ai);
        SetNavActive(BtnNavCloudSync, page == SettingsPage.CloudSync);
        SetNavActive(BtnNavDeps, page == SettingsPage.Deps);
        SetNavActive(BtnNavToolbar, page == SettingsPage.Toolbar);
        SetNavActive(BtnNavStats, page == SettingsPage.Stats);
        SetNavActive(BtnNavAbout, page == SettingsPage.About);
    }

    private void OnMainScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // 每页独立记住阅读位置，滚动不再跳选其他分页。
        if (!_isNavigating) _pageOffsets[_activePage] = MainScrollViewer.Offset.Y;
    }

    /// <summary>只读查询 Windows 辅助功能动画开关，禁用时直接呈现最终状态。</summary>
    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    private static partial int ReadSystemAnimation(uint action, uint parameter, out int enabled, uint flags);

    private static bool SystemMotionEnabled()
    {
        try { return ReadSystemAnimation(0x1042, 0, out int enabled, 0) != 0 && enabled != 0; }
        catch { return false; }
    }

    private bool SettingsMotionEnabled() => IsVisible && !_isLoading && SystemMotionEnabled();

    private void ShowPageWithMotion(Control panel, int version, double offset, bool animate)
    {
        panel.Transitions = null;
        var translate = new TranslateTransform(0, animate ? 6 : 0);
        panel.RenderTransform = translate;
        panel.Opacity = animate ? 0 : 1;
        TimeSpan duration = this.TryFindResource("SctMotionPageDuration", ActualThemeVariant, out var value)
            && value is TimeSpan time ? time : TimeSpan.FromMilliseconds(220);
        if (animate)
        {
            panel.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = OpacityProperty, Duration = duration,
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            };
            translate.Transitions = new Avalonia.Animation.Transitions
            {
                new Avalonia.Animation.DoubleTransition
                {
                    Property = TranslateTransform.YProperty, Duration = duration,
                    Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                },
            };
        }
        // 新页测量后再恢复位置；世代校验阻止快速连点时旧回调改写当前页。
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _pageChangeVersion) return;
            MainScrollViewer.Offset = new Vector(0, offset);
            panel.Opacity = 1;
            translate.Y = 0;
            _isNavigating = false;
        }, DispatcherPriority.Loaded);
    }

    /// <summary>概览复用统计和运行状态，不搬移原设置控件。</summary>
    private void RefreshOverviewUi()
    {
        EnsureOverviewWeather();
        RefreshOverviewClock(DateTime.Now);
        TxtOverviewDate.Text = DateTime.Today.ToString("M 月 d 日 · 仅本机记录");
        TxtOverviewVersion.Text = "v" + Core.Update.UpdateService.CurrentVersionStr;
        if (!_runtimeEnabled || _overviewClosed) return;
        var today = UsageStatistics.GetRecentDays(1)[0];
        TxtOverviewScreenshot.Text = FormatCount(today.Screenshot);
        TxtOverviewOcr.Text = FormatCount(today.Ocr);
        TxtOverviewTranslate.Text = FormatCount(today.Translate);
        TxtOverviewAi.Text = FormatCount(today.AiChat);
        TxtOverviewSticker.Text = FormatCount(today.Sticker);
        var recent = UsageStatistics.GetRecentDays(7);
        // 概览只显示截至今日的七个自然日，不改变原统计页的数据口径。
        ChartOverview.Days = Enumerable.Range(0, 7).Select(i =>
        {
            DateTime date = DateTime.Today.AddDays(i - 6);
            return recent.FirstOrDefault(d => d.Date == date) ?? new UsageDayBucket { Date = date };
        }).ToArray();
        RefreshOverviewDependencies();
        if (!IsVisible) return;
        if (!_overviewSubscribed)
        {
            _overviewSubscribed = true;
            DependencyManager.Changed += OnOverviewDependencyChanged;
            DependencyManager.ProgressTick += OnOverviewDependencyChanged;
            _overviewClock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _overviewClock.Tick += (_, _) =>
            {
                if (_activePage == SettingsPage.Overview)
                {
                    RenderOverviewWeather(DateTimeOffset.Now);
                    RefreshOverviewClock(DateTime.Now);
                    _ = RefreshOverviewWeatherAsync();
                }
            };
            _overviewClock.Start();
        }
        // 尊重已有检查更新开关，每个设置窗口只自动检查一次，不循环请求。
        if (_settings.CheckUpdateOnStartup && !_overviewUpdateAttempted) _ = CheckOverviewUpdateAsync();
    }

    private void UpdateOverviewMotion()
    {
        SkyOverview.IsAnimated = _runtimeEnabled && !_overviewClosed && IsVisible && WindowState != WindowState.Minimized
            && _activePage == SettingsPage.Overview && !Classes.Contains("reduceMotion");
        UpdateWeatherLifecycle();
    }

    private void RefreshOverviewClock(DateTime now)
    {
        string period = OverviewSky.PeriodAt(now.Hour);
        SkyOverview.Period = period;
        ApplyWeatherClock(new DateTimeOffset(now));
        period = SkyOverview.Period;
        foreach (string name in new[] { "day", "dusk", "night" }) OverviewWelcome.Classes.Set(name, name == period.ToLowerInvariant());
        TxtOverviewGreeting.Text = now.Hour switch
        {
            >= 6 and < 11 => "早上好！", >= 11 and < 17 => "下午好！", >= 17 and < 19 => "傍晚好！",
            >= 19 => "晚上好！", _ => "夜深了！"
        };
        // 副文案跟随本机问候时段，不被天气场景覆盖。
        TxtOverviewSkyNote.Text = now.Hour switch
        {
            >= 6 and < 11 => "早起的鸟儿有虫吃，早起的我还在找咖啡。",
            >= 11 and < 17 => "这会儿最容易走神，别问我怎么知道。",
            >= 17 and < 19 => "今天的进度先存档，晚饭才是下一关。",
            >= 19 => "白天没冒出来的灵感，晚上开始排队了。",
            _ => "怎么还没睡，是灵感不肯下班吗？"
        };
    }

    private void OnOverviewDependencyChanged(string id)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_overviewSubscribed && !_overviewClosed) RefreshOverviewDependencies();
        });
    }

    private void RefreshOverviewDependencies()
    {
        bool known = DependencyManager.CurrentManifest() != null;
        RenderOverviewDependencies(DepIdsInRowOrder.Select(DependencyManager.Get).ToArray(), known);
    }

    /// <summary>快照渲染入口供真实服务与隔离验收共用，不在 UI 内另做磁盘检测。</summary>
    private void RenderOverviewDependencies(IReadOnlyList<DepSnapshot> snapshots, bool known)
    {
        int ready = snapshots.Count(s => s.State == DepState.Ready);
        bool busy = snapshots.Any(s => s.State is DepState.Queued or DepState.Downloading or DepState.Verifying);
        TxtOverviewDepsCount.Text = $"{ready} / {snapshots.Count}";
        TxtOverviewDepsState.Text = !known ? "等待检测" : busy ? "组件准备中" : ready == snapshots.Count ? "全部已就绪" : $"{snapshots.Count - ready} 项待准备";
        // 同一组依赖的进度只更新文字，保留按钮实例和键盘焦点。
        if (!OverviewDependencies.Children.OfType<ContinuousBorder>().Select(b => b.Name)
            .SequenceEqual(snapshots.Select(s => "overviewDep_" + s.Id))) OverviewDependencies.Children.Clear();
        int rowIndex = 0;
        foreach (var snapshot in snapshots)
        {
            var (name, description) = snapshot.Id switch
            {
                DepIds.Ocr => ("OCR 模型", "本地文字识别"),
                DepIds.Matting => ("抠图模型", "智能移除背景"),
                _ => ("FFmpeg", "录屏与视频处理")
            };
            string state = snapshot.State switch
            {
                DepState.Ready => "已安装", DepState.Queued => "排队中", DepState.Downloading => snapshot.Total > 0 ? $"下载中 {snapshot.Percent}%" : "下载中",
                DepState.Verifying => "校验中", DepState.Failed => "安装失败", _ => known ? "未安装" : "待检测"
            };
            StackPanel content;
            TextBlock status, detail;
            Button button;
            if (rowIndex < OverviewDependencies.Children.Count)
            {
                content = (StackPanel)((ContinuousBorder)OverviewDependencies.Children[rowIndex]).Child!;
                status = (TextBlock)((Grid)content.Children[0]).Children[1];
                detail = (TextBlock)content.Children[1];
                button = (Button)content.Children[2];
            }
            else
            {
                content = new StackPanel { Spacing = 8 };
                var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                title.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeight.SemiBold });
                status = new TextBlock { Classes = { "overviewDetail" }, Margin = new Thickness(8, 0, 0, 0) };
                Grid.SetColumn(status, 1); title.Children.Add(status); content.Children.Add(title);
                detail = new TextBlock { Classes = { "overviewDetail" } };
                content.Children.Add(detail);
                button = new Button { Tag = snapshot.Id, Classes = { "secondary", "actionSurface" }, HorizontalAlignment = HorizontalAlignment.Left };
                button.Click += OnOverviewDepActionClick;
                content.Children.Add(button);
                OverviewDependencies.Children.Add(new ContinuousBorder { Name = "overviewDep_" + snapshot.Id, Classes = { "overviewDependency" }, Child = content });
            }
            rowIndex++;
            status.Text = state;
            status.Classes.Set("ready", snapshot.State == DepState.Ready);
            status.Classes.Set("error", snapshot.State == DepState.Failed);
            status.Classes.Set("busy", snapshot.State is DepState.Queued or DepState.Downloading or DepState.Verifying);
            detail.Text = snapshot.State == DepState.Ready && !string.IsNullOrWhiteSpace(snapshot.Version)
                ? snapshot.Version + " · " + description : description;
            string action = snapshot.State switch
            {
                DepState.Ready => "查看", DepState.Absent => known ? "安装" : "检测组件",
                DepState.Failed => "重试", _ => "查看进度"
            };
            button.Content = action;
            ToolTip.SetTip(content, snapshot.State == DepState.Failed ? snapshot.Error : null);
        }
    }

    private void OnOverviewDepActionClick(object? sender, RoutedEventArgs e)
    {
        if (!_runtimeEnabled || sender is not Button { Tag: string id }) return;
        var snapshot = DependencyManager.Get(id);
        if (DependencyManager.CurrentManifest() != null && snapshot.State is DepState.Absent or DepState.Failed)
            _ = DependencyManager.EnsureReadyAsync(id);
        else NavigateToDeps();
    }

    private async Task CheckOverviewUpdateAsync()
    {
        if (!_runtimeEnabled || _overviewClosed || _overviewUpdateBusy) return;
        _overviewUpdateAttempted = true;
        _overviewUpdateBusy = true;
        BtnOverviewUpdate.IsEnabled = false;
        TxtOverviewUpdate.Text = "正在检查…";
        _overviewUpdateCts = new CancellationTokenSource();
        try
        {
            var info = await Core.Update.UpdateService.CheckAsync(_overviewUpdateCts.Token);
            if (!_overviewClosed) RenderOverviewUpdate(info, null);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_overviewClosed) RenderOverviewUpdate(null, ex.Message);
        }
        finally
        {
            _overviewUpdateBusy = false;
            _overviewUpdateCts?.Dispose();
            _overviewUpdateCts = null;
            if (!_overviewClosed) BtnOverviewUpdate.IsEnabled = true;
        }
    }

    private void RenderOverviewUpdate(Core.Update.UpdateInfo? info, string? error)
    {
        _overviewUpdateInfo = info;
        bool newer = info != null && Core.Update.UpdateService.HasNewer(info);
        TxtOverviewUpdate.Text = error != null ? "检查失败" : newer ? "可更新 v" + info!.Version : info != null ? "已是最新版本" : "尚未检查";
        TxtOverviewUpdate.Classes.Set("error", error != null);
        TxtOverviewUpdate.Classes.Set("busy", newer);
        ToolTip.SetTip(TxtOverviewUpdate, error ?? (newer ? "点击查看新版说明与更新操作" : null));
        BtnOverviewUpdate.Content = error != null ? "重新检查" : newer ? "查看更新" : "检查更新";
    }

    private void OnOverviewUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (!_runtimeEnabled) return;
        if (_overviewUpdateInfo != null && Core.Update.UpdateService.HasNewer(_overviewUpdateInfo))
            new UpdateWindow(silentAutoCheck: false, known: _overviewUpdateInfo).Show(this);
        else _ = CheckOverviewUpdateAsync();
    }

    /// <summary>通知档位：决定语义配色、图标与停留时长。</summary>
    private enum StatusTier
    {
        /// <summary>成功与中性反馈：主题表面，停留 3.2 秒后淡出。</summary>
        Info,
        /// <summary>失败与被拒：危险色提示，保留至手动关闭或后续反馈覆盖。</summary>
        Error,
        /// <summary>进行中的等待：主题表面，常驻到下一次提示（不能自动消失）。</summary>
        Busy,
    }

    private void ShowStatus(string text) => ShowStatusCore(text, StatusTier.Info);

    private void ShowError(string text) => ShowStatusCore(text, StatusTier.Error);

    private void ShowBusy(string text) => ShowStatusCore(text, StatusTier.Busy);

    private void ShowStatusCore(string text, StatusTier tier)
    {
        // 任何一次调用都作废旧的淡出计时，并推进世代号让已过期的延时失效
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = null;
        int gen = ++_statusGen;

        if (string.IsNullOrEmpty(text))
        {
            HideStatus();
            return;
        }

        TxtStatusBar.Text = text;
        // Busy 档目前只接"已打开浏览器等待授权"这一处交接，图标用跳转外链而非对勾
        IconStatusBar.Data = PathGeometry.Parse(tier switch
        {
            StatusTier.Error => OcrIconGeometry.ErrorCircle,
            StatusTier.Busy => OcrIconGeometry.OpenInNew,
            _ => OcrIconGeometry.CheckCircle,
        });
        SetErrorTier(tier == StatusTier.Error);
        // ✕ 只给失败档：中性档几秒就退场，常驻档要等下一次提示覆盖，都不该被手工关闭
        BtnStatusClose.IsVisible = tier == StatusTier.Error;

        PnlStatusBar.Opacity = 1.0;
        PnlStatusBar.IsHitTestVisible = true;
        TxtStatusBar.IsHitTestVisible = true;

        // 错误留给用户阅读，进行中等待后续结果；仅普通反馈自动淡出。
        if (tier != StatusTier.Info) return;

        var cts = new CancellationTokenSource();
        _statusCts = cts;
        _ = AutoHideStatusAsync(cts.Token, gen, 3200);
    }

    private void HideStatus()
    {
        _statusGen++;
        _statusCts?.Cancel();
        _statusCts?.Dispose();
        _statusCts = null;

        // 保留 Text 内容让整条淡出时不至于先空掉（淡的是整条 Opacity，不是背景色，
        // 所以不会有 BrushTransition 那种插值出脏色的问题）
        PnlStatusBar.Opacity = 0.0;
        BtnStatusClose.IsVisible = false;
        // Avalonia 里 Opacity=0 的元素仍参与命中测试：不关掉就会留下点得到的"幽灵 ✕"，
        // 以及鼠标经过时弹出上一条提示的悬空 ToolTip
        PnlStatusBar.IsHitTestVisible = false;
        TxtStatusBar.IsHitTestVisible = false;
    }

    private void SetErrorTier(bool error)
    {
        if (error)
        {
            if (!PnlStatusBar.Classes.Contains("error")) PnlStatusBar.Classes.Add("error");
        }
        else
        {
            PnlStatusBar.Classes.Remove("error");
        }
    }

    private async Task AutoHideStatusAsync(CancellationToken token, int gen, int holdMs)
    {
        try
        {
            await Task.Delay(holdMs, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        // 延时期间用户可能已触发新提示，世代号不符就不能把新消息一起收掉
        if (gen != _statusGen) return;
        HideStatus();
    }

    private void OnStatusCloseClick(object? sender, RoutedEventArgs e)
    {
        HideStatus();
    }

    private static void SetNavActive(Button button, bool active)
    {
        if (active)
        {
            if (!button.Classes.Contains("active")) button.Classes.Add("active");
        }
        else
        {
            button.Classes.Remove("active");
        }
    }

    private static void SelectComboBoxItem(ComboBox comboBox, string value)
    {
        for (int i = 0; i < comboBox.ItemCount; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item)
            {
                string text = item.Content?.ToString() ?? string.Empty;
                if (string.Equals(text, value, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }
        }

        comboBox.SelectedIndex = comboBox.ItemCount > 0 ? 0 : -1;
    }

    private static string GetSelectedComboText(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item)
        {
            return item.Content?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static void SelectComboBoxItemByTag(ComboBox comboBox, string tagValue)
    {
        for (int i = 0; i < comboBox.ItemCount; i++)
        {
            if (comboBox.Items[i] is ComboBoxItem item)
            {
                string tag = item.Tag?.ToString() ?? string.Empty;
                if (string.Equals(tag, tagValue, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }
        }
        comboBox.SelectedIndex = comboBox.ItemCount > 0 ? 0 : -1;
    }

    private static string GetSelectedComboTag(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item)
        {
            return item.Tag?.ToString() ?? "Mobile";
        }
        return "Mobile";
    }

    private static string FormatHotKey(uint modifiers, uint virtualKey)
    {
        var parts = new System.Collections.Generic.List<string>();
        ScreenCaptureTool.Core.Actions.ActionKeyboardBinding.AddModifierNames(parts, modifiers);

        parts.Add(VirtualKeyToText(virtualKey));
        return string.Join("+", parts);
    }

    private static string VirtualKeyToText(uint vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (char)('0' + (vk - 0x60));
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
        return vk switch
        {
            0x20 => "Space",
            0x0D => "Enter",
            0x09 => "Tab",
            0x14 => "CapsLock",
            0xC0 => "`",
            0xBD => "-",
            0xBB => "=",
            0xDB => "[",
            0xDD => "]",
            0xBA => ";",
            0xDE => "'",
            0xBC => ",",
            0xBE => ".",
            0xBF => "/",
            0xDC => "\\",
            _ => "VK_" + vk.ToString("X"),
        };
    }


    // -------------------- 云存储配置 --------------------

    // ---- 服务商选择 ----

    private void OnCloudProviderChanged(object? sender, RoutedEventArgs e)
    {
        UpdateCloudProviderVisibility();
    }

    private void UpdateCloudProviderVisibility()
    {
        bool gdrive = RadCloudGDrive.IsChecked == true;
        bool webdav = RadCloudWebDav.IsChecked == true;
        if (GDriveCard != null) GDriveCard.IsVisible = gdrive;
        if (WebDavCard != null) WebDavCard.IsVisible = webdav;
        UpdateCloudActionUi();
        if (PnlCloudAuto != null && GDriveSyncHost != null && WebDavSyncHost != null)
        {
            // 同一组控件随服务商移动，保持设置绑定和状态入口唯一。
            var host = webdav ? WebDavSyncHost : GDriveSyncHost;
            if (!ReferenceEquals(host.Content, PnlCloudAuto))
            {
                GDriveSyncHost.Content = null; WebDavSyncHost.Content = null;
                host.Content = PnlCloudAuto;
            }
        }
        UpdateCloudSecretsWarn();
    }

    /// <summary>扳动「自动同步」开关：落盘后立刻重算操作行的在场按钮，不等后台账本回话（那边有 10 秒防抖）。</summary>
    private void OnCloudAutoToggled(object? sender, RoutedEventArgs e)
    {
        AutoSaveSettings();
        UpdateCloudActionUi();
    }

    /// <summary>
    /// 云操作显隐由服务商与自动同步开关统一计算。历史入口与主操作集中在工具栏右侧。
    /// 开档右侧为立即同步；关档右侧为恢复手动备份与上传手动备份。
    /// 同步开关固定在工具栏左侧，显式历史选择不依赖后台开关。
    /// </summary>
    private void UpdateCloudActionUi()
    {
        if (BtnCloudVersions == null || BtnGDriveUpload == null) return;
        bool gdrive = RadCloudGDrive.IsChecked == true;
        bool webdav = RadCloudWebDav.IsChecked == true;
        bool auto = _automaticCloud != null && _settings.CloudAutoSync;
        BtnGDriveUpload.IsVisible = BtnGDriveDownload.IsVisible = gdrive && !auto;
        BtnWebDavUpload.IsVisible = BtnWebDavDownload.IsVisible = webdav && !auto;
        BtnCloudVersions.IsVisible = gdrive || webdav;
        BtnCloudSyncNow.IsVisible = auto;
        BtnCloudSyncNow.IsEnabled = auto && _automaticCloud?.Report.State != CloudSyncState.Checking;
    }

    /// <summary>「密钥与凭据」分段切换：仅刷新就近提示，落盘由 AutoSaveSettings 订阅完成。</summary>
    private void OnCloudSecretsChanged(object? sender, RoutedEventArgs e)
    {
        if (_isLoading) return;
        UpdateCloudSecretsWarn();
    }

    /// <summary>
    /// 关闭密钥同步后，旧修订和手动备份里的旧密钥不会自动消失，
    /// 这里就地提醒而非静默改写；仅在已启用云服务商且选择「不同步」时显示。
    /// 读单选框实际状态而非 <c>_settings</c>：XAML 声明的事件先于代码订阅触发，此刻设置对象尚未更新。
    /// </summary>
    private void UpdateCloudSecretsWarn()
    {
        if (TxtCloudSecretsWarn == null) return;
        bool providerActive = RadCloudGDrive?.IsChecked == true || RadCloudWebDav?.IsChecked == true;
        bool keepSecrets = RadCloudSecretsOn?.IsChecked != false;
        TxtCloudSecretsWarn.IsVisible = providerActive && !keepSecrets;
    }

    /// <summary>上传成功文案后缀：关闭密钥同步时如实说明本次上传的范围。</summary>
    private string CloudSecretsUploadNote()
        => _settings.CloudSyncSecrets ? "" : "本次未上传密钥与凭据。";

    /// <summary>恢复成功文案后缀：v1 旧信封只有设置段，动作表与内置快捷键未被覆盖，必须如实告知。</summary>
    private static string CloudRestoreNote(CloudSyncResult result)
        => result.LegacyEnvelope ? "旧版备份不含动作与快捷键，二者保持本机原状。" : "";

    // ---- Google Drive ----

    private async void OnGDriveLoginClick(object? sender, RoutedEventArgs e)
    {
        _gdriveService = new GoogleDriveSyncService(_settings);
        BtnGDriveLogin.IsEnabled = false;
        string? error = await _gdriveService.StartLoginAsync();
        if (error != null)
        {
            BtnGDriveLogin.IsEnabled = true;
            ShowError(error);
            return;
        }

        // 常驻档：授权回传最长要等 120 秒，中性档 3.2 秒就淡出会把等待中的提示抽走，
        // 终态由下面的 ShowStatus / ShowError 覆盖
        ShowBusy("已打开浏览器，请完成 Google 授权…");
        BtnGDriveLogin.IsEnabled = false;
        BtnGDriveLogin.Content = "等待授权…";

        // 轮询登录回调结果（真实成败由回调换Token决定），最多 120 秒
        for (int i = 0; i < 120; i++)
        {
            await Task.Delay(1000);
            var (finished, success, loginError) = _gdriveService.GetLoginOutcome();
            if (!finished) continue;

            BtnGDriveLogin.IsEnabled = true;
            BtnGDriveLogin.Content = "登录 Google Drive";
            if (success)
            {
                UpdateGDriveStatusUi(_gdriveService.GetStatus());
                ShowStatus("Google Drive 连接成功！");
            }
            else
            {
                UpdateGDriveStatusUi(null);
                ShowError("Google 登录失败：" + loginError);
            }
            return;
        }

        ShowError("Google 登录超时，请重试。");
        BtnGDriveLogin.IsEnabled = true;
        BtnGDriveLogin.Content = "登录 Google Drive";
        _gdriveService.CancelLogin();
    }

    private async void OnGDriveLogoutClick(object? sender, RoutedEventArgs e)
    {
        if (_gdriveService == null) return;
        BtnGDriveLogout.IsEnabled = false;
        try
        {
            await _gdriveService.LogoutAsync();
            UpdateGDriveStatusUi(null);
            ShowStatus("已断开 Google Drive 连接。");
        }
        finally
        {
            BtnGDriveLogout.IsEnabled = true;
        }
    }

    private void UpdateAutomaticCloudUi()
    {
        string unavailable = (Application.Current as App)?.AutoCloudSyncError ?? "";
        var report = _automaticCloud?.Report ?? new CloudSyncReport(CloudSyncState.Disabled, unavailable.Length > 0 ? unavailable : "自动同步已关闭。");
        _shownCloudReport = report;
        // 状态行的时间只在"没有账号行可放"的那一家出现：Google Drive 卡上「上次同步」已经落在头像行里，
        // 同一张卡不该把同一个时间写两遍；WebDAV 卡没有头像行，时间仍跟在状态词后面。
        TxtCloudAutoStatus.Text = report.Message;
        if (report.State == CloudSyncState.Synced && report.LastSuccess is { } success && GDriveCard?.IsVisible != true)
            TxtCloudAutoStatus.Text = "已同步 · " + success.LocalDateTime.ToString("MM-dd HH:mm");
        UpdateGDriveAccountLine();
        bool busy = report.State == CloudSyncState.Checking;
        UpdateCloudActionUi();
        // 可见性已由 UpdateCloudActionUi 算过（开档才在场），这里只补"在场上是否按得动"
        BtnCloudSyncNow.IsEnabled = BtnCloudSyncNow.IsVisible && !busy;
        BtnCloudSyncNow.Content = report.State == CloudSyncState.Conflict ? "处理冲突"
            : report.State is CloudSyncState.Retry or CloudSyncState.AuthenticationRequired ? "重试"
            : busy ? "同步中…" : "立即同步";
    }
    private async void OnCloudSyncNowClick(object? sender, RoutedEventArgs e)
    {
        AutoSaveSettings();
        if (_shownCloudReport?.State == CloudSyncState.Conflict)
        {
            await OpenCloudVersionsAsync(); return;
        }
        if (_automaticCloud != null) await _automaticCloud.SynchronizeAsync();
    }
    private async void OnCloudVersionsClick(object? sender, RoutedEventArgs e) => await OpenCloudVersionsAsync();
    private async Task OpenCloudVersionsAsync()
    {
        AutoSaveSettings();
        var window = new CloudVersionsWindow(_automaticCloud, AutoSaveSettings);
        await window.ShowDialog(this);
    }

    private async void OnGDriveUploadClick(object? sender, RoutedEventArgs e)
    {
        if (_cloudSyncBusy) return;
        if (_gdriveService == null)
        {
            ShowError("请先登录 Google Drive。");
            return;
        }

        _cloudSyncBusy = true;
        SetGDriveButtonsBusy(true, "上传中…");
        ShowStatus("");
        try
        {
            using var syncOperation = await CloudSnapshot.EnterOperationAsync();
            // ExportToJson 打包的是磁盘文件，而未失焦的输入框尚未落盘，先补一次保存
            AutoSaveSettings();
            string json = CloudConfigHelper.ExportToJson();
            var result = await _gdriveService.UploadAsync(json);
            if (result.Ok)
            {
                UpdateGDriveStatusUi(null);
                ShowStatus("配置已上传至 Google Drive。" + CloudSecretsUploadNote());
            }
            else
            {
                if (result.AuthFailed) UpdateGDriveStatusUi(null);
                ShowError("上传失败：" + result.Error);
            }
        }
        finally
        {
            _cloudSyncBusy = false;
            SetGDriveButtonsBusy(false);
        }
    }

    private async void OnGDriveDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (_cloudSyncBusy) return;
        if (_gdriveService == null)
        {
            ShowError("请先登录 Google Drive。");
            return;
        }

        _cloudSyncBusy = true;
        SetGDriveButtonsBusy(true, "恢复中…");
        ShowStatus("");
        try
        {
            using var syncOperation = await CloudSnapshot.EnterOperationAsync();
            var result = await _gdriveService.DownloadAsync();
            if (!result.Ok)
            {
                if (result.AuthFailed) UpdateGDriveStatusUi(null);
                ShowError(result.NotFound ? "云端暂无配置文件，请先上传。" : "恢复失败：" + result.Error);
                return;
            }

            var importResult = CloudConfigHelper.ImportFromJson(result.Data);
            if (importResult.Ok)
            {
                // 整表重载：刷新配置缓存、重建各页草稿与列表、补做换肤
                ReloadAllFromDisk(importResult.SidecarsRestored);
                ShowStatus("配置已从 Google Drive 恢复。" + CloudRestoreNote(importResult));
            }
            else
            {
                ShowError("恢复失败：" + importResult.Error);
            }
        }
        finally
        {
            _cloudSyncBusy = false;
            SetGDriveButtonsBusy(false);
        }
    }

    private async void UpdateGDriveStatusUi(GoogleDriveStatus? status)
    {
        status ??= _gdriveService?.GetStatus() ?? new GoogleDriveStatus();
        bool connected = status.Connected;
        _gdriveConnected = connected;

        // 账号、同步状态与底部操作保持同一套几何；连接状态只切换文字与账号按钮。
        TxtGDriveEmail.Text = connected ? status.UserEmail : "未连接 Google 账号";
        TxtGDriveConnected.Text = connected ? "已连接" : "未连接";
        if (connected) GDriveStatusDot.Classes.Add("on"); else GDriveStatusDot.Classes.Remove("on");
        UpdateGDriveAccountLine();
        BtnGDriveLogin.IsVisible = !connected;
        BtnGDriveLogout.IsVisible = connected;

        if (!connected)
        {
            // 头像常驻占位（藏掉会让账号行整体左移），退出登录后必须把上一次的真人照片换回占位底
            if (this.TryFindResource("SctRowBgBrush", ActualThemeVariant, out var placeholder)
                && placeholder is Avalonia.Media.IBrush brush) GDriveAvatar.Fill = brush;
            return;
        }

        if (!string.IsNullOrWhiteSpace(status.UserAvatar))
        {
            try
            {
                AppLogger.Info("开始加载 Google 头像，URL=" + status.UserAvatar);
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var resp = await http.GetAsync(status.UserAvatar);
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync();
                var bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                GDriveAvatar.Fill = new Avalonia.Media.ImageBrush(bitmap) { Stretch = Avalonia.Media.Stretch.UniformToFill };
                AppLogger.Info("Google 头像加载成功");
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Google 头像加载失败：" + ex.GetType().Name + "：" + ex.Message);
            }
        }
        else
        {
            AppLogger.Warn("Google 头像 URL 为空，跳过加载");
        }
    }

    /// <summary>
    /// 头像行第二行的读数：已连接时是「上次同步」，未连接时是授权指引。
    /// 时间取<b>当前目标账本</b>的最近成功（<code>GetLastSuccess()</code>），不拿手动 <code>GoogleDriveLastSync</code>
    /// 冒充本地应用成功（《自动云同步设计》口径）；状态行 <c>TxtCloudAutoStatus</c> 随之只写状态词，
    /// 同一个时间不在一处卡片里出现两遍。
    /// </summary>
    private void UpdateGDriveAccountLine()
    {
        if (TxtGDriveStatus == null) return;
        TxtGDriveStatus.Text = !_gdriveConnected ? "点右侧按钮，在浏览器里完成 Google 授权。"
            : _automaticCloud?.GetLastSuccess() is { } success ? "上次同步 · " + success.LocalDateTime.ToString("MM-dd HH:mm")
            : "暂无成功同步记录";
    }

    private void SetGDriveButtonsBusy(bool busy, string? busyText = null)
    {
        BtnGDriveUpload.IsEnabled = !busy;
        BtnGDriveDownload.IsEnabled = !busy;
        BtnGDriveLogout.IsEnabled = !busy;
        BtnGDriveUpload.Content = busy && busyText != null ? busyText : "上传手动备份";
    }

    // ---- WebDAV ----

    private WebDavSyncService? CreateWebDavService()
    {
        string url = (TxtWebDavUrl.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowError("请先填写 WebDAV 地址。");
            return null;
        }
        string username = (TxtWebDavUsername.Text ?? "").Trim();
        string password = TxtWebDavPassword.Text ?? "";
        return new WebDavSyncService(url, username, password);
    }

    private async void OnWebDavTestClick(object? sender, RoutedEventArgs e)
    {
        var service = CreateWebDavService();
        if (service == null) return;

        BtnWebDavTest.IsEnabled = false;
        BtnWebDavTest.Content = "测试中…";
        ShowStatus("");
        try
        {
            var result = await service.TestConnectionAsync();
            if (result.Ok) ShowStatus("WebDAV 连接测试成功！");
            else ShowError("连接测试失败：" + result.Error);
        }
        finally
        {
            BtnWebDavTest.IsEnabled = true;
            BtnWebDavTest.Content = "测试连接";
        }
    }

    private async void OnWebDavUploadClick(object? sender, RoutedEventArgs e)
    {
        if (_cloudSyncBusy) return;
        var service = CreateWebDavService();
        if (service == null) return;

        _cloudSyncBusy = true;
        SetWebDavButtonsBusy(true, "上传中…");
        ShowStatus("");
        try
        {
            using var syncOperation = await CloudSnapshot.EnterOperationAsync();
            // ExportToJson 打包的是磁盘文件，而未失焦的输入框尚未落盘，先补一次保存
            AutoSaveSettings();
            string json = CloudConfigHelper.ExportToJson();
            string fileName = (TxtWebDavFileName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "yuSnip-config-v1.json";

            var result = await service.UploadAsync(json, fileName);
            if (result.Ok) ShowStatus("配置已上传至 WebDAV。" + CloudSecretsUploadNote());
            else ShowError("上传失败：" + result.Error);
        }
        finally
        {
            _cloudSyncBusy = false;
            SetWebDavButtonsBusy(false);
        }
    }

    private async void OnWebDavDownloadClick(object? sender, RoutedEventArgs e)
    {
        if (_cloudSyncBusy) return;
        var service = CreateWebDavService();
        if (service == null) return;

        _cloudSyncBusy = true;
        SetWebDavButtonsBusy(true, "恢复中…");
        ShowStatus("");
        try
        {
            using var syncOperation = await CloudSnapshot.EnterOperationAsync();
            string fileName = (TxtWebDavFileName.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "yuSnip-config-v1.json";

            var result = await service.DownloadAsync(fileName);
            if (!result.Ok)
            {
                ShowError(result.NotFound ? "云端暂无配置文件，请先上传。" : "恢复失败：" + result.Error);
                return;
            }

            var importResult = CloudConfigHelper.ImportFromJson(result.Data);
            if (importResult.Ok)
            {
                ReloadAllFromDisk(importResult.SidecarsRestored);
                ShowStatus("配置已从 WebDAV 恢复。" + CloudRestoreNote(importResult));
            }
            else
            {
                ShowError("恢复失败：" + importResult.Error);
            }
        }
        finally
        {
            _cloudSyncBusy = false;
            SetWebDavButtonsBusy(false);
        }
    }

    private void SetWebDavButtonsBusy(bool busy, string? busyText = null)
    {
        BtnWebDavTest.IsEnabled = !busy;
        BtnWebDavUpload.IsEnabled = !busy;
        BtnWebDavDownload.IsEnabled = !busy;
        BtnWebDavUpload.Content = busy && busyText != null ? busyText : "上传手动备份";
    }

    // ---- 本地配置文件 ----

    private void OnCloudOpenDirClick(object? sender, RoutedEventArgs e)
    {
        OpenDirectory(AppSettings.SettingsDirectory, createIfMissing: true);
    }

    private async void OnCloudExportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出配置备份",
                DefaultExtension = "json",
                SuggestedFileName = "yuSnip-config-backup",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON 配置文件") { Patterns = new[] { "*.json" } }
                }
            });

            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) { ShowError("无法获取文件路径。"); return; }

            // ExportToFile 打包的是磁盘文件，而未失焦的输入框尚未落盘，先补一次保存
            AutoSaveSettings();
            using var syncOperation = await CloudSnapshot.EnterOperationAsync();
            var result = CloudConfigHelper.ExportToFile(path);
            if (result.Ok) ShowStatus("配置已导出到：" + path);
            else ShowError("导出失败：" + result.Error);
        }
        catch (Exception ex)
        {
            ShowError("导出失败：" + ex.Message);
        }
    }

    private async void OnCloudImportClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入配置备份",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON 配置文件") { Patterns = new[] { "*.json" } }
                }
            });

            var file = files.FirstOrDefault();
            if (file == null) return;
            string? path = file.TryGetLocalPath();
            if (string.IsNullOrEmpty(path)) { ShowError("无法获取文件路径。"); return; }

            using var syncOperation = await CloudSnapshot.EnterOperationAsync();
            var result = CloudConfigHelper.ImportFromFile(path);
            if (result.Ok)
            {
                ReloadAllFromDisk(result.SidecarsRestored);
                ShowStatus("配置已从备份文件恢复。" + CloudRestoreNote(result));
            }
            else
            {
                ShowError("导入失败：" + result.Error);
            }
        }
        catch (Exception ex)
        {
            ShowError("导入失败：" + ex.Message);
        }
    }
}
