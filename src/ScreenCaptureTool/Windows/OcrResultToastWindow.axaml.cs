using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// OCR 静默复制结果提示窗口（6 秒自动关闭，支持鼠标悬停暂停与多方位弹出）。
/// 展示识别文本预览；点击整卡打开 OCR 编辑窗口（结果数据与位图所有权随之转移），
/// 超时未点击则位图随窗口关闭释放。同时只保留一条提示。
/// </summary>
public partial class OcrResultToastWindow : Window
{
    /// <summary>设置缺失或异常时的兜底等待秒数（与 <see cref="AppSettings.OcrToastSeconds"/> 默认档位一致）。</summary>
    private const double DefaultAutoCloseSeconds = 5.0;

    /// <summary>卡片四周为投影预留的透明缓冲（DIP），须与 axaml 中 ShadowHost 的 Margin 一致。</summary>
    private const double ShadowPadDip = 26;

    /// <summary>首次布局前（SizeToContent 尚未算出高度）按三行预览估算的卡片高度，沿用改前取值以免位置漂移。</summary>
    private const double CardHeightFallbackDip = 140;

    private static OcrResultToastWindow? _current;

    /// <summary>本次提示的实际等待秒数（来自 <see cref="AppSettings.OcrToastSeconds"/>），进度条比例以此为分母。</summary>
    private double _autoCloseSeconds = DefaultAutoCloseSeconds;

    private DispatcherTimer _autoCloseTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private TimeSpan _autoCloseRemaining = TimeSpan.FromSeconds(DefaultAutoCloseSeconds);
    private DateTime _autoCloseDeadline;
    private ScaleTransform? _progressScale;

    private OcrResultData? _result;
    private System.Drawing.Bitmap? _bitmap;
    private double _elapsedSeconds;
    private string _positionMode = "BottomCenter";

    public OcrResultToastWindow()
    {
        InitializeComponent();
        _progressScale = this.FindControl<Border>("ProgressBar")?.RenderTransform as ScaleTransform;
        _autoCloseTimer.Tick += OnAutoCloseTimerTick;

        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>
    /// 显示（或替换现有）OCR 静默复制提示。需在 UI 线程调用。
    /// </summary>
    public static void Show(OcrResultData result, System.Drawing.Bitmap? bitmap, double elapsedSeconds)
    {
        try
        {
            // 旧的提示先收掉（未点击 → 位图在 Closed 中释放）
            _current?.Close();

            var settings = AppSettings.Load();
            string positionMode = AppSettings.NormalizeOcrToastPosition(settings.OcrToastPosition);
            if (positionMode == "None")
            {
                // 静默复制已完成；关闭提示时仍须释放转交给本入口的位图。
                bitmap?.Dispose();
                return;
            }
            int toastSeconds = AppSettings.NormalizeOcrToastSeconds(settings.OcrToastSeconds);

            var toast = new OcrResultToastWindow
            {
                _result = result,
                _bitmap = bitmap,
                _elapsedSeconds = elapsedSeconds,
                _positionMode = positionMode,
                _autoCloseSeconds = toastSeconds,
            };

            string preview = (result.Text ?? string.Empty).Trim();
            const int maxPreview = 400;
            toast.TxtPreview.Text = preview.Length > maxPreview
                ? preview[..maxPreview] + "…"
                : preview;

            toast.PositionAtScreen(positionMode);
            _current = toast;
            toast.Show();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OcrResultToastWindow.Show 失败：" + ex.Message);
            try { bitmap?.Dispose(); } catch { }
        }
    }

    /// <summary>点击提示卡：打开 OCR 编辑窗口（转移结果与位图所有权），关闭提示。</summary>
    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _autoCloseTimer.Stop();
        var result = _result;
        var bitmap = _bitmap;
        _result = null;
        _bitmap = null; // 所有权转移，Closed 不再释放
        Close();
        if (result != null)
        {
            OcrWindow.ShowResult(result, bitmap, _elapsedSeconds);
        }
        else
        {
            try { bitmap?.Dispose(); } catch { }
        }
    }

    /// <summary>鼠标悬停进入卡片：暂停倒计时。</summary>
    private void OnRootPointerEntered(object? sender, PointerEventArgs e)
    {
        PauseAutoClose();
    }

    /// <summary>鼠标移出卡片：恢复倒计时。</summary>
    private void OnRootPointerExited(object? sender, PointerEventArgs e)
    {
        ResumeAutoClose();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        PositionAtScreen(_positionMode);
        if (_progressScale != null) _progressScale.ScaleX = 1.0;
        StartAutoClose(TimeSpan.FromSeconds(_autoCloseSeconds));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _autoCloseTimer.Stop();
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
        // 未点击（超时/被替换）：释放暂存的位图
        try { _bitmap?.Dispose(); } catch { }
        _bitmap = null;
        _result = null;
    }

    private void StartAutoClose(TimeSpan delay)
    {
        _autoCloseRemaining = delay;
        _autoCloseDeadline = DateTime.Now.Add(delay);
        _autoCloseTimer.Start();
    }

    private void PauseAutoClose()
    {
        if (!_autoCloseTimer.IsEnabled) return;
        TimeSpan remaining = _autoCloseDeadline - DateTime.Now;
        _autoCloseRemaining = remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        _autoCloseTimer.Stop();
    }

    private void ResumeAutoClose()
    {
        if (_autoCloseTimer.IsEnabled) return;
        if (_autoCloseRemaining <= TimeSpan.Zero)
        {
            _autoCloseRemaining = TimeSpan.FromMilliseconds(500);
        }
        _autoCloseDeadline = DateTime.Now.Add(_autoCloseRemaining);
        _autoCloseTimer.Start();
    }

    private void OnAutoCloseTimerTick(object? sender, EventArgs e)
    {
        if (_progressScale != null && _autoCloseRemaining.TotalMilliseconds > 0)
        {
            var remaining = _autoCloseDeadline - DateTime.Now;
            _progressScale.ScaleX = Math.Max(0, remaining.TotalMilliseconds / (_autoCloseSeconds * 1000));
        }

        if (DateTime.Now < _autoCloseDeadline) return;
        _autoCloseTimer.Stop();
        Close();
    }

    /// <summary>
    /// 根据设定的位置模式（TopCenter、BottomCenter、TopRight、BottomRight）
    /// 将窗口定位在目标显示器工作区。
    /// </summary>
    private void PositionAtScreen(string positionMode)
    {
        try
        {
            PixelPoint cursorPt = new PixelPoint(0, 0);
            if (NativeMethods.GetCursorPos(out var pt))
            {
                cursorPt = new PixelPoint(pt.X, pt.Y);
            }

            var screen = Screens.ScreenFromPoint(cursorPt) ?? Screens.Primary;
            PixelRect area;
            if (screen != null)
            {
                area = screen.WorkingArea;
                if (area.Width <= 0 || area.Height <= 0)
                {
                    area = screen.Bounds;
                }
            }
            else
            {
                area = new PixelRect(0, 0, 1920, 1080);
            }

            double scale = (screen != null && screen.Scaling > 0) ? screen.Scaling : (DesktopScaling > 0 ? DesktopScaling : 1.0);

            // 只有"看得见的卡片"参与贴边排版；窗口比卡片四周各多出一段投影缓冲，定位时要扣回来
            double cardWidth = Math.Max(1, (Bounds.Width > 0 ? Bounds.Width : Width) - ShadowPadDip * 2);
            double cardHeight = Math.Max(1, (Bounds.Height > 0 ? Bounds.Height : CardHeightFallbackDip + ShadowPadDip * 2) - ShadowPadDip * 2);

            int pixelWidth = (int)Math.Round(cardWidth * scale);
            int pixelHeight = (int)Math.Round(cardHeight * scale);
            int pixelPad = (int)Math.Round(ShadowPadDip * scale);

            const int marginPhysical = 40;

            int cardX;
            int cardY;

            switch (AppSettings.NormalizeOcrToastPosition(positionMode))
            {
                case "TopCenter":
                    cardX = area.X + Math.Max(0, (area.Width - pixelWidth) / 2);
                    cardY = area.Y + marginPhysical;
                    break;

                case "TopRight":
                    cardX = area.X + Math.Max(0, area.Width - pixelWidth - marginPhysical);
                    cardY = area.Y + marginPhysical;
                    break;

                case "BottomRight":
                    cardX = area.X + Math.Max(0, area.Width - pixelWidth - marginPhysical);
                    cardY = area.Y + Math.Max(0, area.Height - pixelHeight - marginPhysical);
                    break;

                case "BottomCenter":
                default:
                    cardX = area.X + Math.Max(0, (area.Width - pixelWidth) / 2);
                    cardY = area.Y + Math.Max(0, area.Height - pixelHeight - marginPhysical);
                    break;
            }

            Position = new PixelPoint(cardX - pixelPad, cardY - pixelPad);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("OcrResultToastWindow.PositionAtScreen 定位异常：" + ex.Message);
        }
    }
}
