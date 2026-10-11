using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Services;
using ScreenCaptureTool.Windows;

namespace ScreenCaptureTool;

public partial class App : Application
{
    private ScreenCaptureTool.Core.HotKeyManager? _hotKey;
    private ScreenCaptureTool.Core.HotKeyManager? _stickerHotKey;
    private SettingsWindow? _settingsWindow;
    private bool _captureRunning;
    private string _registeredHotKeyText = "Ctrl+Alt+A";
    private string _registeredStickerHotKeyText = "F3";
    /// <summary>当前正在运行的截图会话（RunCaptureAsync 期间非空）。供 F3 注入用。</summary>
    private CaptureSession? _activeSession;
    /// <summary>贴图阴影 toast 是否显示（缓存自设置，避免每次按键读盘）。</summary>
    private bool _showStickerShadowToast = true;

    public override void Initialize()
    {
        AppLogger.Info("App.Initialize: 开始加载 Avalonia XAML");
        AvaloniaXamlLoader.Load(this);
        AppLogger.Info("App.Initialize: Avalonia XAML 加载完成");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppLogger.Info("App.OnFrameworkInitializationCompleted: Avalonia 框架初始化完成");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnDesktopExit;
            AppLogger.Info("App.OnFrameworkInitializationCompleted: ShutdownMode=OnExplicitShutdown");
        }
        else
        {
            AppLogger.Warn("App.OnFrameworkInitializationCompleted: ApplicationLifetime 不是 ClassicDesktop");
        }

        RegisterGlobalHotKey();
        RegisterStickerHotKey();
        CacheStickerToastSetting();

        TryShowStartupNotification();

        // 启动时在后台异步预热 OCR 引擎，消除用户首次点击 OCR 的长等待时间
        Services.CaptureSession.WarmUpOcr();

        base.OnFrameworkInitializationCompleted();

        Dispatcher.UIThread.Post(() =>
        {
            ForceMemoryCleanup();
            AppLogger.Info("App.OnFrameworkInitializationCompleted: 启动后强制内存清理完成");
        }, DispatcherPriority.Background);
    }

    private void TryShowStartupNotification()
    {
        try
        {
            var settings = AppSettings.Load();
            if (!settings.ShowStartupNotification) return;

            UpdateTrayToolTip("yuSnip");
            AppLogger.Info("App.TryShowStartupNotification: 已更新托盘提示为 yuSnip");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.TryShowStartupNotification: 失败 " + ex.Message);
        }
    }

    private void UpdateTrayToolTip(string text)
    {
        try
        {
            var icons = TrayIcon.GetIcons(this);
            if (icons == null) return;
            foreach (var icon in icons)
            {
                icon.ToolTipText = text;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.UpdateTrayToolTip: 更新托盘提示失败 " + ex.Message);
        }
    }

    private void RegisterGlobalHotKey()
    {
        try
        {
            var settings = AppSettings.Load();
            _hotKey = new ScreenCaptureTool.Core.HotKeyManager(OnHotKeyPressed, 0x5343);

            uint preferredModifiers = settings.HotKeyModifiers;
            uint preferredKey = settings.HotKeyVirtualKey;
            string preferredText = FormatHotKey(preferredModifiers, preferredKey);
            AppLogger.Info("App.RegisterGlobalHotKey: 尝试注册首选热键=" + preferredText);

            if (_hotKey.Register(preferredModifiers, preferredKey))
            {
                _registeredHotKeyText = preferredText;
                AppLogger.Info("已成功注册截图热键：" + preferredText);
            }
            else
            {
                _registeredHotKeyText = "未注册";
                AppLogger.Warn($"截图热键 {preferredText} 注册失败（已被系统或其他软件占用），错误码: " + _hotKey.LastRegisterError);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.RegisterGlobalHotKey 注册热键异常", ex);
        }
    }

    private static string FormatHotKey(uint modifiers, uint virtualKey)
    {
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & 0x0002) != 0) parts.Add("Ctrl");
        if ((modifiers & 0x0001) != 0) parts.Add("Alt");
        if ((modifiers & 0x0004) != 0) parts.Add("Shift");
        if ((modifiers & 0x0008) != 0) parts.Add("Win");

        string keyText;
        if (virtualKey >= 0x70 && virtualKey <= 0x7B)
        {
            keyText = "F" + (virtualKey - 0x70 + 1); // VK_F1..VK_F12
        }
        else if (virtualKey >= 0x41 && virtualKey <= 0x5A)
        {
            keyText = ((char)virtualKey).ToString();
        }
        else
        {
            keyText = "VK_" + virtualKey.ToString("X");
        }
        parts.Add(keyText);
        return string.Join("+", parts);
    }

    private void OnHotKeyPressed()
    {
        AppLogger.Info("App.OnHotKeyPressed: 收到全局热键 " + _registeredHotKeyText);
        Dispatcher.UIThread.Post(async () => await RunCaptureAsync());
    }

    // -------------------- 快速贴图热键（F3） --------------------

    private const int STICKER_HOTKEY_ID = 0x5344;

    private void CacheStickerToastSetting()
    {
        try { _showStickerShadowToast = AppSettings.Load().ShowStickerShadowToast; }
        catch { _showStickerShadowToast = true; }
    }

    private void RegisterStickerHotKey()
    {
        try
        {
            var settings = AppSettings.Load();
            _stickerHotKey = new ScreenCaptureTool.Core.HotKeyManager(OnStickerHotKeyPressed, STICKER_HOTKEY_ID);

            uint preferredModifiers = settings.StickerHotKeyModifiers;
            uint preferredKey = settings.StickerHotKeyVirtualKey;
            string preferredText = FormatHotKey(preferredModifiers, preferredKey);
            AppLogger.Info("App.RegisterStickerHotKey: 尝试注册首选贴图热键=" + preferredText);

            if (_stickerHotKey.Register(preferredModifiers, preferredKey))
            {
                _registeredStickerHotKeyText = preferredText;
                AppLogger.Info("已成功注册快速贴图热键：" + preferredText);
            }
            else
            {
                _registeredStickerHotKeyText = "未注册";
                AppLogger.Warn($"快速贴图热键 {preferredText} 注册失败（已被系统或其他软件占用），错误码: " + _stickerHotKey.LastRegisterError);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.RegisterStickerHotKey 异常", ex);
        }
    }

    private void OnStickerHotKeyPressed()
    {
        AppLogger.Info("App.OnStickerHotKeyPressed: 收到快速贴图热键 " + _registeredStickerHotKeyText);
        Dispatcher.UIThread.Post(HandleQuickSticker);
    }

    /// <summary>
    /// 快速贴图分支逻辑（UI 线程）：
    /// a) 截图会话进行中且有选区窗口 → 把选区转贴图（复用现有贴图链路）。
    /// b) 否则取剪贴板内容生成贴图（图片优先，其次文本）。
    /// c) 都没有 → 提示"无可贴图内容"。
    /// </summary>
    private void HandleQuickSticker()
    {
        try
        {
            // 分支 a：截图进行中
            if (_activeSession != null && _activeSession.HasActiveSelectionWindow)
            {
                AppLogger.Info("App.HandleQuickSticker: 截图选区 → 贴图");
                _activeSession.RequestStickerFromCurrentSelection();
                return;
            }

            // 分支 b：剪贴板图片
            System.Drawing.Bitmap? img = ClipboardService.TryGetImage();
            if (img != null)
            {
                AppLogger.Info("App.HandleQuickSticker: 剪贴板图片 → 贴图，尺寸=" + img.Width + "x" + img.Height);
                ShowStickerFromClipboardImage(img);
                return;
            }

            // 分支 b：剪贴板文本
            string? text = ClipboardService.TryGetText();
            if (!string.IsNullOrEmpty(text))
            {
                AppLogger.Info("App.HandleQuickSticker: 剪贴板文本 → 贴图，长度=" + text.Length);
                ShowStickerFromClipboardText(text);
                return;
            }

            // 分支 c：无内容
            Windows.StickerToastWindow.Show("无可贴图内容");
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.HandleQuickSticker 异常", ex);
        }
    }

    /// <summary>
    /// 剪贴板图片生成贴图。
    /// 若该图是"最近一次截图"（尺寸匹配且未过期），还原到原位置、原尺寸（不缩放）；
    /// 否则保持宽高比缩放（≤屏宽60%/屏高70%），居中于鼠标所在屏。
    /// </summary>
    private void ShowStickerFromClipboardImage(System.Drawing.Bitmap bitmap)
    {
        var sticker = new Windows.StickerWindow(bitmap);

        // 先尝试匹配"最近截图"——还原原位置。
        System.Drawing.Point? remembered = LastCaptureMemory.TryMatch(bitmap.Width, bitmap.Height);
        if (remembered.HasValue)
        {
            AppLogger.Info("App.ShowStickerFromClipboardImage: 匹配到最近截图，还原原位置 " + remembered.Value);
            sticker.SetStickerScreenPosition(new PixelPoint(remembered.Value.X, remembered.Value.Y));
        }
        else
        {
            // 无记忆 → 居中 + 按需缩放。
            var monitor = ScreenCaptureTool.Platform.MonitorHelper.GetMonitorAtCursor();
            int maxW = (int)(monitor.Width * 0.6);
            int maxH = (int)(monitor.Height * 0.7);

            double scale = Math.Min((double)maxW / bitmap.Width, (double)maxH / bitmap.Height);
            if (scale < 1.0)
            {
                int targetW = Math.Max(1, (int)(bitmap.Width * scale));
                int targetH = Math.Max(1, (int)(bitmap.Height * scale));
                PositionStickerCentered(sticker, monitor, targetW, targetH);
            }
            else
            {
                PositionStickerCentered(sticker, monitor, bitmap.Width, bitmap.Height);
            }
        }

        sticker.Show();
        StickerFocusManager.SetFocus(sticker);
    }

    /// <summary>剪贴板文本生成贴图：渲染为位图后按图片贴图规则居中。</summary>
    private void ShowStickerFromClipboardText(string text)
    {
        var monitor = ScreenCaptureTool.Platform.MonitorHelper.GetMonitorAtCursor();
        int maxContentW = (int)(monitor.Width * 0.6);

        string? htmlText = ClipboardService.TryGetHtml();
        TextStickerResult result;
        if (!string.IsNullOrEmpty(htmlText))
        {
            result = Core.TextStickerRenderer.RenderHtml(htmlText, text, maxContentW);
        }
        else
        {
            result = Core.TextStickerRenderer.Render(text, maxContentW);
        }

        using (result)
        {
            var sticker = new Windows.StickerWindow(result.Bitmap, text, result.LayoutInfo);
            PositionStickerCentered(sticker, monitor, result.Bitmap.Width, result.Bitmap.Height);
            sticker.Show();
            StickerFocusManager.SetFocus(sticker);
        }
    }

    /// <summary>把贴图 Image 定位到指定显示器中央（窗口本身会带阴影边距，由 SetStickerScreenPosition 处理）。</summary>
    private static void PositionStickerCentered(Windows.StickerWindow sticker, System.Drawing.Rectangle monitor, int bmpW, int bmpH)
    {
        // Image 目标位置 = 显示器中央（按位图尺寸居中，物理像素）。
        int x = monitor.X + (monitor.Width - bmpW) / 2;
        int y = monitor.Y + (monitor.Height - bmpH) / 2;
        sticker.SetStickerScreenPosition(new Avalonia.PixelPoint(Math.Max(0, x), Math.Max(0, y)));
    }

    /// <summary>
    /// Y 键切换阴影时调用：按设置决定是否显示 toast。
    /// 由 StickerWindow.OnWindowKeyDown 调用，toast 定位到该贴图区域中央。
    /// </summary>
    internal static void RaiseStickerShadowToast(bool shadowOn, Windows.StickerWindow? sticker)
    {
        if (Current?._showStickerShadowToast != true) return;
        Windows.StickerToastWindow.Show(shadowOn ? "贴图阴影：开" : "贴图阴影：关", sticker);
    }

    /// <summary>当前 App 实例（供静态入口访问实例字段）。</summary>
    private static new App? Current => Application.Current as App;

    private void OnSettingsHotKeyChanged(object? sender, HotKeyChangedEventArgs e)
    {
        try
        {
            if (_hotKey == null)
            {
                AppLogger.Warn("App.OnSettingsHotKeyChanged: 热键管理器为空，无法重新注册");
                e.Succeeded = false;
                return;
            }

            string newText = FormatHotKey(e.Modifiers, e.VirtualKey);
            if (_hotKey.Register(e.Modifiers, e.VirtualKey))
            {
                _registeredHotKeyText = newText;
                AppLogger.Info("App.OnSettingsHotKeyChanged: 已重新注册热键 " + newText);
                var settings = AppSettings.Load();
                if (settings.ShowStartupNotification)
                {
                    UpdateTrayToolTip("yuSnip");
                }
                e.Succeeded = true;
            }
            else
            {
                AppLogger.Warn("App.OnSettingsHotKeyChanged: 重新注册热键失败 " + newText + " error=" + _hotKey.LastRegisterError + "，尝试恢复旧热键");
                e.Succeeded = false;
                RegisterGlobalHotKey(); // 恢复旧热键（从配置文件读取）
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.OnSettingsHotKeyChanged 异常", ex);
            e.Succeeded = false;
        }
    }

    private void OnSettingsStickerHotKeyChanged(object? sender, HotKeyChangedEventArgs e)
    {
        try
        {
            if (_stickerHotKey == null)
            {
                AppLogger.Warn("App.OnSettingsStickerHotKeyChanged: 贴图热键管理器为空，无法重新注册");
                e.Succeeded = false;
                return;
            }

            string newText = FormatHotKey(e.Modifiers, e.VirtualKey);
            if (_stickerHotKey.Register(e.Modifiers, e.VirtualKey))
            {
                _registeredStickerHotKeyText = newText;
                AppLogger.Info("App.OnSettingsStickerHotKeyChanged: 已重新注册快速贴图热键 " + newText);
                e.Succeeded = true;
            }
            else
            {
                AppLogger.Warn("App.OnSettingsStickerHotKeyChanged: 重新注册快速贴图热键失败 " + newText + " error=" + _stickerHotKey.LastRegisterError + "，尝试恢复旧热键");
                e.Succeeded = false;
                RegisterStickerHotKey(); // 恢复旧热键（从配置文件读取）
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.OnSettingsStickerHotKeyChanged 异常", ex);
            e.Succeeded = false;
        }
    }

    private async void OnTrayClicked(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnTrayClicked: 托盘点击触发截图");
        await RunCaptureAsync();
    }

    private async void OnMenuCaptureClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuCaptureClick: 菜单触发截图");
        await RunCaptureAsync();
    }

    private void OnMenuSettingsClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuSettingsClick: 打开设置");
        ShowSettingsWindow();
    }

    private void ShowSettingsWindow()
    {
        if (_settingsWindow != null)
        {
            AppLogger.Info("App.ShowSettingsWindow: 复用已有设置窗口");
            _settingsWindow.Show();
            _settingsWindow.Activate();
            return;
        }

        AppLogger.Info("App.ShowSettingsWindow: 创建设置窗口");
        _settingsWindow = new SettingsWindow();
        _settingsWindow.HotKeyChanged += OnSettingsHotKeyChanged;
        _settingsWindow.StickerHotKeyChanged += OnSettingsStickerHotKeyChanged;
        _settingsWindow.Closed += (_, _) =>
        {
            AppLogger.Info("App.ShowSettingsWindow: 设置窗口已关闭");
            if (_settingsWindow != null)
            {
                _settingsWindow.HotKeyChanged -= OnSettingsHotKeyChanged;
                _settingsWindow.StickerHotKeyChanged -= OnSettingsStickerHotKeyChanged;
            }
            // 设置窗口关闭后刷新 toast 开关缓存。
            CacheStickerToastSetting();
            _settingsWindow = null;
            ForceMemoryCleanup();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnMenuExitClick(object? sender, EventArgs e)
    {
        AppLogger.Info("App.OnMenuExitClick: 用户请求退出");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            AppLogger.Warn("App.OnMenuExitClick: 无 ClassicDesktop lifetime，无法正常 Shutdown");
        }
    }

    private async Task RunCaptureAsync()
    {
        if (_captureRunning)
        {
            AppLogger.Warn("App.RunCaptureAsync: 截图会话已在运行，忽略重复触发");
            return;
        }
        _captureRunning = true;

        try
        {
            AppLogger.Info("App.RunCaptureAsync: 截图会话开始");
            await Task.Delay(120);

            var settings = AppSettings.Load();
            AppLogger.Info("App.RunCaptureAsync: 设置加载完成，AutoDetectMode=" + settings.AutoDetectMode + ", SaveDirectory=" + settings.SaveDirectory);

            // 延迟截图：在抓屏前等待用户配置的秒数，便于切换到目标界面。
            int delaySeconds = Math.Clamp(settings.DelaySeconds, 0, 10);
            if (delaySeconds > 0)
            {
                AppLogger.Info("App.RunCaptureAsync: 延迟截图 " + delaySeconds + " 秒");
                await Task.Delay(delaySeconds * 1000);
            }

            var session = new CaptureSession(settings);
            _activeSession = session; // 暴露给 F3 注入用
            CaptureSessionResult? result = await session.RunAsync();

            if (result == null)
            {
                AppLogger.Info("截图已取消。");
            }
            else if (result.CreatedSticker)
            {
                AppLogger.Info("截图已创建贴图。");
            }
            else if (result.PickedColor)
            {
                AppLogger.Info("已复制颜色：" + result.PickedColorHex);
            }
            else if (result.IsLongScroll)
            {
                AppLogger.Info("长截图：" + (result.Message ?? "已结束。"));
            }
            else if (result.ClipboardCopied)
            {
                AppLogger.Info("截图已复制到剪贴板。");
            }
            else
            {
                AppLogger.Info("截图已保存：" + result.SavedPath);
                if (settings.PlaySaveSound)
                {
                    PlaySaveSound();
                }
                if (settings.ShowSaveNotification)
                {
                    ShowSavedNotification(result.SavedPath);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("App.RunCaptureAsync 异常", ex);
        }
        finally
        {
            _activeSession = null;
            _captureRunning = false;
            AppLogger.Info("App.RunCaptureAsync: 截图会话结束");
            ForceMemoryCleanup();
        }
    }

    private void ShowSavedNotification(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            var notification = new SavedNotificationWindow(path);
            notification.Show();
            AppLogger.Info("App.ShowSavedNotification: 已显示保存通知，path=" + path);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.ShowSavedNotification: 显示保存通知失败：" + ex.Message);
        }
    }

    private void PlaySaveSound()
    {
        try
        {
            // MessageBeep 播放系统默认提示音，无需引用 System.Windows.Extensions。
            MessageBeep(0x00000040); // MB_ICONASTERISK
        }
        catch (Exception ex)
        {
            AppLogger.Warn("App.PlaySaveSound: 播放提示音失败：" + ex.Message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);


    private static void ForceMemoryCleanup()
    {
        try
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch { }

        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            ScreenCaptureTool.Platform.NativeMethods.EmptyWorkingSet(proc.Handle);
        }
        catch { }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        AppLogger.Info("App.OnDesktopExit: 应用退出，开始释放热键");
        try { _hotKey?.Dispose(); AppLogger.Info("App.OnDesktopExit: 截图热键已释放"); } catch (Exception ex) { AppLogger.Error("App.OnDesktopExit: 释放截图热键异常", ex); }
        try { _stickerHotKey?.Dispose(); AppLogger.Info("App.OnDesktopExit: 贴图热键已释放"); } catch (Exception ex) { AppLogger.Error("App.OnDesktopExit: 释放贴图热键异常", ex); }
        _hotKey = null;
        _stickerHotKey = null;
        AppLogger.Shutdown("App.OnDesktopExit");
    }
}
