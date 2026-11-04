using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Windows;

/// <summary>
/// 贴图 Shift 取色器的呈现窗（2026-09-29，同日唤出键由 Alt 改为 Shift）。只管"画"：采样、定位判据、复制都由 <see cref="StickerWindow"/> 负责。
/// 无激活、不进任务栏、Win32 层鼠标穿透（<see cref="WindowHelper.MakeToolWindowNoActivate"/>），
/// 因此它压在指针旁边也不会抢走贴图的指针事件与键盘焦点。
/// </summary>
public partial class StickerColorPickerWindow : Window
{
    private static WeakReference<StickerColorPickerWindow>? _activePicker;

    /// <summary>当前可见取色窗的层级边界；隐藏或关闭后立即失效，仅在 UI 线程查询。</summary>
    internal static IntPtr ActivePickerHwnd =>
        _activePicker != null && _activePicker.TryGetTarget(out var window)
        && window.IsVisible && window.IsEnabled && window.Topmost
            ? window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero : IntPtr.Zero;

    /// <summary>放大区边长（源图像素，奇数，中心格＝取色点）。与截图放大镜同为 17。</summary>
    public const int SamplePx = 17;

    private readonly WriteableBitmap _zoom;
    private bool _crossUseBlack;
    private bool _styled;

    public StickerColorPickerWindow()
    {
        InitializeComponent();
        _zoom = new WriteableBitmap(new PixelSize(SamplePx, SamplePx), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
        ZoomImage.Source = _zoom;
        HintText.Text = BuildHintText();
        Opened += (_, _) => EnsureToolStyle();
    }

    /// <summary>
    /// 底部那行提示按<b>当前绑定</b>现生成：两枚动作键 2026-09-29 进了改键目录，再写死键名就会与用户改后的键不符。
    /// 绑定在窗口创建时一次性取出（与 <see cref="StickerWindow"/> 同一口径）⇒ 设置页改完键，下一次唤出才反映到新键。
    /// 目录里读不到这两条时退回中性文案，不留空行。
    /// </summary>
    private static string BuildHintText()
    {
        var map = Services.BuiltinShortcutsStore.LoadGroupMap(Services.BuiltinShortcutsStore.GroupSticker);
        if (map.TryGetValue("sticker.pickcopy", out var copy) && map.TryGetValue("sticker.pickformat", out var fmt))
            return $"{Services.BuiltinShortcutsStore.Format(copy)} 复制，{Services.BuiltinShortcutsStore.Format(fmt)} 切换格式";
        return "按住唤出键取色";
    }

    private void EnsureToolStyle()
    {
        if (_styled) return;
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        WindowHelper.MakeToolWindowNoActivate(hwnd, transparent: true);
        _styled = true;
    }

    /// <summary>每次唤出登记当前浮层，再在已有置顶带内升层；后续贴图升层须停在本窗下方。</summary>
    internal void ShowAboveStickers()
    {
        _activePicker = new WeakReference<StickerColorPickerWindow>(this);
        Show();
        IntPtr hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE
                | 0x0200); // SWP_NOOWNERZORDER，不连带抬高共享 owner 下的贴图
    }

    /// <summary>刷新放大区像素（<paramref name="premulBgra"/> 为 <see cref="SamplePx"/>² 个预乘 BGRA 像素）与文字。</summary>
    public void UpdateContent(byte[] premulBgra, Color color, bool opaque, string colorText, string coordText)
    {
        using (var fb = _zoom.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(premulBgra, 0, fb.Address,
                Math.Min(premulBgra.Length, fb.RowBytes * SamplePx));
        }
        ZoomImage.InvalidateVisual();

        CoordText.Text = coordText;
        ColorText.Text = colorText;
        ColorSwatch.Fill = new SolidColorBrush(opaque ? color : Color.FromArgb(0, 0, 0, 0));

        // 十字线按取色点亮度切深/浅，任何底色上都看得清（与截图放大镜同一阈值）
        int lum = (color.R * 2126 + color.G * 7152 + color.B * 722) / 10000;
        bool useBlack = opaque && lum >= 200;
        if (useBlack != _crossUseBlack || CrossLineH.Stroke == null)
        {
            string key = useBlack ? "SctMagnifierCrossDarkBrush" : "SctMagnifierCrossLightBrush";
            if (this.TryFindResource(key, ActualThemeVariant, out object? o) && o is IBrush b)
            {
                CrossLineH.Stroke = b;
                CrossLineV.Stroke = b;
            }
            _crossUseBlack = useBlack;
        }
    }

    /// <summary>只改色值文字（切换 HEX/RGB 时用）。</summary>
    public void SetColorText(string text) => ColorText.Text = text;

    /// <summary>贴着指针右下方摆放；越出指针所在屏幕的右/下边时翻到左/上侧。</summary>
    public void PlaceNearCursor(int cursorX, int cursorY)
    {
        double s = DesktopScaling > 0 ? DesktopScaling : 1.0;
        int w = (int)Math.Ceiling((Bounds.Width > 0 ? Bounds.Width : 152) * s);
        int h = (int)Math.Ceiling((Bounds.Height > 0 ? Bounds.Height : 200) * s);
        int gap = (int)Math.Round(16 * s);

        int x = cursorX + gap, y = cursorY + gap;
        var mon = MonitorHelper.GetMonitorInfoAtPoint(cursorX, cursorY);
        if (mon.HasValue)
        {
            var r = mon.Value.rect;
            if (x + w > r.Right) x = cursorX - gap - w;
            if (y + h > r.Bottom) y = cursorY - gap - h;
            if (x < r.Left) x = r.Left;
            if (y < r.Top) y = r.Top;
        }
        var p = new PixelPoint(x, y);
        if (Position != p) Position = p;
    }
}
