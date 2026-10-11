using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 设置窗口：通用 / 截图 / 热键 / 关于 四个分页，读写 <see cref="AppSettings"/>。
/// 支持热键录制并在保存后通知宿主重新注册全局热键。
/// </summary>
public partial class SettingsWindow : Window
{
    private enum SettingsPage
    {
        General,
        Capture,
        HotKey,
        About,
    }

    // Win32 修饰键位
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;

    private AppSettings _settings = new AppSettings();

    private bool _isRecordingHotKey;
    private uint _recordedModifiers;
    private uint _recordedVirtualKey;

    private bool _isRecordingStickerHotKey;
    private uint _recordedStickerModifiers;
    private uint _recordedStickerVirtualKey;

    /// <summary>保存成功且热键发生变化时触发，宿主据此重新注册全局热键。</summary>
    public event EventHandler<HotKeyChangedEventArgs>? HotKeyChanged;

    /// <summary>保存成功且快速贴图热键发生变化时触发。</summary>
    public event EventHandler<HotKeyChangedEventArgs>? StickerHotKeyChanged;

    public SettingsWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;

        SliderDelaySeconds.PropertyChanged += (_, ev) =>
        {
            if (ev.Property == Slider.ValueProperty) UpdateSliderLabels();
        };

        // 录制热键时在窗口级别捕获按键
        AddHandler(KeyDownEvent, OnGlobalKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        FitWindowToScreen();
        LoadSettingsToUi();
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

            // 留出窗口外边距（Border Margin=12）和一点安全余量
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

    private void LoadSettingsToUi()
    {
        _settings = AppSettings.Load();
        ApplySettingsToUi(_settings);

        TxtAppName.Text = AppBuildInfo.SoftwareName;
        TxtSettingsPath.Text = AppSettings.SettingsPath;
        TxtStartupValueName.Text = StartupRegistry.ValueName;
        TxtStatus.Text = string.Empty;
        ShowPage(SettingsPage.General);
    }

    private void ApplySettingsToUi(AppSettings s)
    {
        TxtSaveDirectory.Text = s.SaveDirectory ?? string.Empty;
        TxtFileNameTemplate.Text = s.FileNameTemplate ?? "Screenshot_{yyyyMMdd_HHmmss}";

        SliderDelaySeconds.Value = Math.Clamp(s.DelaySeconds, 0, 10);

        ChkCopyToClipboard.IsChecked = s.CopyToClipboardAfterSave;
        ChkRunOnStartup.IsChecked = StartupRegistry.IsEnabled() || s.RunOnStartup;
        ChkShowSaveNotification.IsChecked = s.ShowSaveNotification;
        ChkShowStartupNotification.IsChecked = s.ShowStartupNotification;
        ChkPlaySaveSound.IsChecked = s.PlaySaveSound;

        SelectComboBoxItem(CmbAutoDetectMode, AppSettings.NormalizeAutoDetectMode(s.AutoDetectMode));

        _recordedModifiers = s.HotKeyModifiers;
        _recordedVirtualKey = s.HotKeyVirtualKey;
        _recordedStickerModifiers = s.StickerHotKeyModifiers;
        _recordedStickerVirtualKey = s.StickerHotKeyVirtualKey;
        if (ChkShowStickerShadowToast != null) ChkShowStickerShadowToast.IsChecked = s.ShowStickerShadowToast;
        UpdateHotKeyDisplay();
        UpdateStickerHotKeyDisplay();
        UpdateSliderLabels();
    }

    private void UpdateSliderLabels()
    {
        if (TxtDelayValue != null) TxtDelayValue.Text = ((int)Math.Round(SliderDelaySeconds.Value)) + " 秒";
    }

    // -------------------- 保存 --------------------

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (_isRecordingHotKey) StopRecordingHotKey(false);
            if (_isRecordingStickerHotKey) StopRecordingStickerHotKey(false);

            string saveDir = (TxtSaveDirectory.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(saveDir))
            {
                ShowStatus("保存目录不能为空。");
                return;
            }

            string fileNameTemplate = (TxtFileNameTemplate.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(fileNameTemplate))
            {
                fileNameTemplate = "Screenshot_{yyyyMMdd_HHmmss}";
            }

            // 截图热键允许单键（如 F1），只要有主键即可。
            if (_recordedVirtualKey == 0)
            {
                ShowStatus("截图热键无效：需要一个主键。");
                return;
            }

            // 快速贴图热键允许无修饰键（如 F3），但必须有主键。
            if (_recordedStickerVirtualKey == 0)
            {
                ShowStatus("快速贴图热键无效：需要一个主键。");
                return;
            }

            uint oldModifiers = _settings.HotKeyModifiers;
            uint oldVirtualKey = _settings.HotKeyVirtualKey;
            uint oldStickerModifiers = _settings.StickerHotKeyModifiers;
            uint oldStickerVirtualKey = _settings.StickerHotKeyVirtualKey;

            bool hotKeyChanged = oldModifiers != _recordedModifiers || oldVirtualKey != _recordedVirtualKey;
            bool stickerHotKeyChanged = oldStickerModifiers != _recordedStickerModifiers || oldStickerVirtualKey != _recordedStickerVirtualKey;

            // 1. 如果截图热键被修改，先测试注册
            if (hotKeyChanged)
            {
                var args = new HotKeyChangedEventArgs(_recordedModifiers, _recordedVirtualKey);
                HotKeyChanged?.Invoke(this, args);
                if (!args.Succeeded)
                {
                    ShowStatus("保存失败：截图快捷键已被占用或注册失败，请重新录制！");
                    return;
                }
            }

            // 2. 如果贴图热键被修改，再测试注册
            if (stickerHotKeyChanged)
            {
                var args = new HotKeyChangedEventArgs(_recordedStickerModifiers, _recordedStickerVirtualKey);
                StickerHotKeyChanged?.Invoke(this, args);
                if (!args.Succeeded)
                {
                    ShowStatus("保存失败：贴图快捷键已被占用或注册失败，请重新录制！");
                    // 恢复前面成功修改的截图快捷键，以保持状态一致
                    if (hotKeyChanged)
                    {
                        HotKeyChanged?.Invoke(this, new HotKeyChangedEventArgs(oldModifiers, oldVirtualKey));
                    }
                    return;
                }
            }

            // 3. 热键注册通过，保存设置到文件
            _settings.SaveDirectory = saveDir;
            _settings.FileNameTemplate = fileNameTemplate;
            _settings.DelaySeconds = (int)Math.Round(SliderDelaySeconds.Value);
            _settings.AutoDetectMode = AppSettings.NormalizeAutoDetectMode(GetSelectedComboText(CmbAutoDetectMode));
            _settings.CopyToClipboardAfterSave = ChkCopyToClipboard.IsChecked == true;
            _settings.RunOnStartup = ChkRunOnStartup.IsChecked == true;
            _settings.ShowSaveNotification = ChkShowSaveNotification.IsChecked == true;
            _settings.ShowStartupNotification = ChkShowStartupNotification.IsChecked == true;
            _settings.PlaySaveSound = ChkPlaySaveSound.IsChecked == true;
            _settings.HotKeyModifiers = _recordedModifiers;
            _settings.HotKeyVirtualKey = _recordedVirtualKey;
            _settings.StickerHotKeyModifiers = _recordedStickerModifiers;
            _settings.StickerHotKeyVirtualKey = _recordedStickerVirtualKey;
            _settings.ShowStickerShadowToast = ChkShowStickerShadowToast.IsChecked == true;

            bool startupApplied = StartupRegistry.Apply(_settings.RunOnStartup);
            _settings.Save();
            if (!startupApplied)
            {
                ShowStatus("设置已保存，但开机启动注册表更新失败。");
                return;
            }
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error("SettingsWindow.OnSaveClick 保存设置失败", ex);
            ShowStatus("保存失败：" + ex.Message);
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
                if (!string.IsNullOrWhiteSpace(path)) TxtSaveDirectory.Text = path;
                else ShowStatus("无法获取所选目录的本地路径。");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.OnBrowseSaveDirectoryClick: 选择目录失败：" + ex.Message);
            ShowStatus("选择目录失败：" + ex.Message);
        }
    }

    private void OnOpenSaveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        string dir = (TxtSaveDirectory.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dir))
        {
            ShowStatus("保存目录为空。");
            return;
        }
        OpenDirectory(dir, createIfMissing: true);
    }

    private void OnOpenConfigDirectoryClick(object? sender, RoutedEventArgs e)
    {
        OpenDirectory(AppSettings.SettingsDirectory, createIfMissing: true);
    }

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
                ShowStatus("目录不存在：" + dir);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SettingsWindow.OpenDirectory 失败：" + ex.Message);
            ShowStatus("打开目录失败：" + ex.Message);
        }
    }

    private void OnRestoreDefaultsClick(object? sender, RoutedEventArgs e)
    {
        if (_isRecordingHotKey) StopRecordingHotKey(false);
        if (_isRecordingStickerHotKey) StopRecordingStickerHotKey(false);
        _settings = new AppSettings();
        ApplySettingsToUi(_settings);
        ShowStatus("已恢复默认设置，点击“保存”后生效。");
    }

    // -------------------- 热键录制 --------------------

    private void OnRecordHotKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_isRecordingHotKey)
        {
            StopRecordingHotKey(false);
            return;
        }
        // 切到截图热键录制前，先停掉贴图热键录制（同一时间只录一个）。
        if (_isRecordingStickerHotKey) StopRecordingStickerHotKey(false);
        StartRecordingHotKey();
    }

    private void StartRecordingHotKey()
    {
        _isRecordingHotKey = true;
        BtnRecordHotKey.Content = "按下组合键…";
        if (!BtnRecordHotKey.Classes.Contains("active")) BtnRecordHotKey.Classes.Add("active");
        HotKeyDisplayBorder.BorderBrush = this.FindResource("SctAccentBrush") as Avalonia.Media.IBrush;
        TxtHotKey.Text = "等待输入…";
        TxtHotKeyTip.Text = "按 Esc 取消录制。";
        Focus();
    }

    private void StopRecordingHotKey(bool keepNew)
    {
        _isRecordingHotKey = false;
        BtnRecordHotKey.Content = "录制";
        BtnRecordHotKey.Classes.Remove("active");
        HotKeyDisplayBorder.BorderBrush = this.FindResource("SctBorderBrush") as Avalonia.Media.IBrush;
        if (!keepNew)
        {
            UpdateHotKeyDisplay();
            TxtHotKeyTip.Text = string.Empty;
        }
    }

    // -------------------- 快速贴图热键录制 --------------------

    private void OnRecordStickerHotKeyClick(object? sender, RoutedEventArgs e)
    {
        if (_isRecordingStickerHotKey)
        {
            StopRecordingStickerHotKey(false);
            return;
        }
        if (_isRecordingHotKey) StopRecordingHotKey(false);
        StartRecordingStickerHotKey();
    }

    private void StartRecordingStickerHotKey()
    {
        _isRecordingStickerHotKey = true;
        BtnRecordStickerHotKey.Content = "按下按键…";
        if (!BtnRecordStickerHotKey.Classes.Contains("active")) BtnRecordStickerHotKey.Classes.Add("active");
        StickerHotKeyDisplayBorder.BorderBrush = this.FindResource("SctAccentBrush") as Avalonia.Media.IBrush;
        TxtStickerHotKey.Text = "等待输入…";
        TxtStickerHotKeyTip.Text = "按 Esc 取消录制。";
        Focus();
    }

    private void StopRecordingStickerHotKey(bool keepNew)
    {
        _isRecordingStickerHotKey = false;
        BtnRecordStickerHotKey.Content = "录制";
        BtnRecordStickerHotKey.Classes.Remove("active");
        StickerHotKeyDisplayBorder.BorderBrush = this.FindResource("SctBorderBrush") as Avalonia.Media.IBrush;
        if (!keepNew)
        {
            UpdateStickerHotKeyDisplay();
            TxtStickerHotKeyTip.Text = string.Empty;
        }
    }

    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        // 同时只会有一个录制态为真（切换时已互斥停止另一个）。
        if (_isRecordingHotKey)
        {
            HandleKeyDownForCapture(e);
            return;
        }
        if (_isRecordingStickerHotKey)
        {
            HandleKeyDownForSticker(e);
            return;
        }
    }

    private void HandleKeyDownForCapture(KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopRecordingHotKey(false);
            return;
        }

        // 仅按下修饰键时不结束，等待主键
        if (IsModifierKey(e.Key)) return;

        uint modifiers = 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= MOD_CONTROL;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= MOD_ALT;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= MOD_SHIFT;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= MOD_WIN;

        uint vk = KeyToVirtualKey(e.Key);
        if (vk == 0)
        {
            TxtHotKeyTip.Text = "不支持的按键，请换一个主键。";
            return;
        }

        // 截图热键允许无修饰键（如 F1、F2 等单键）。
        _recordedModifiers = modifiers;
        _recordedVirtualKey = vk;
        UpdateHotKeyDisplay();
        StopRecordingHotKey(true);
        TxtHotKeyTip.Text = "录制完成，点击“保存”后生效。";
    }

    private void HandleKeyDownForSticker(KeyEventArgs e)
    {
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            StopRecordingStickerHotKey(false);
            return;
        }

        if (IsModifierKey(e.Key)) return;

        uint modifiers = 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= MOD_CONTROL;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= MOD_ALT;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= MOD_SHIFT;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= MOD_WIN;

        uint vk = KeyToVirtualKey(e.Key);
        if (vk == 0)
        {
            TxtStickerHotKeyTip.Text = "不支持的按键，请换一个主键。";
            return;
        }

        // 贴图热键允许无修饰键（如 F3）。
        _recordedStickerModifiers = modifiers;
        _recordedStickerVirtualKey = vk;
        UpdateStickerHotKeyDisplay();
        StopRecordingStickerHotKey(true);
        TxtStickerHotKeyTip.Text = "录制完成，点击“保存”后生效。";
    }

    private void UpdateHotKeyDisplay()
    {
        TxtHotKey.Text = FormatHotKey(_recordedModifiers, _recordedVirtualKey);
    }

    private void UpdateStickerHotKeyDisplay()
    {
        TxtStickerHotKey.Text = FormatHotKey(_recordedStickerModifiers, _recordedStickerVirtualKey);
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

    // -------------------- 导航 / 窗口 --------------------

    private void OnNavGeneralClick(object? sender, RoutedEventArgs e) => ShowPage(SettingsPage.General);
    private void OnNavCaptureClick(object? sender, RoutedEventArgs e) => ShowPage(SettingsPage.Capture);
    private void OnNavHotKeyClick(object? sender, RoutedEventArgs e) => ShowPage(SettingsPage.HotKey);
    private void OnNavAboutClick(object? sender, RoutedEventArgs e) => ShowPage(SettingsPage.About);

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        if (_isRecordingHotKey) StopRecordingHotKey(false);
        if (_isRecordingStickerHotKey) StopRecordingStickerHotKey(false);
        Close();
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

    private void ShowPage(SettingsPage page)
    {
        if (_isRecordingHotKey && page != SettingsPage.HotKey) StopRecordingHotKey(false);
        if (_isRecordingStickerHotKey && page != SettingsPage.HotKey) StopRecordingStickerHotKey(false);

        ScrollGeneral.IsVisible = page == SettingsPage.General;
        ScrollCapture.IsVisible = page == SettingsPage.Capture;
        ScrollHotKey.IsVisible = page == SettingsPage.HotKey;
        ScrollAbout.IsVisible = page == SettingsPage.About;

        SetNavActive(BtnNavGeneral, page == SettingsPage.General);
        SetNavActive(BtnNavCapture, page == SettingsPage.Capture);
        SetNavActive(BtnNavHotKey, page == SettingsPage.HotKey);
        SetNavActive(BtnNavAbout, page == SettingsPage.About);
    }

    private void ShowStatus(string text)
    {
        TxtStatus.Text = text;
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
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");

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
}

/// <summary>热键变更事件参数（Win32 修饰键 + 虚拟键码）。</summary>
public sealed class HotKeyChangedEventArgs : EventArgs
{
    public HotKeyChangedEventArgs(uint modifiers, uint virtualKey)
    {
        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public uint Modifiers { get; }
    public uint VirtualKey { get; }
    public bool Succeeded { get; set; } = true;
}
