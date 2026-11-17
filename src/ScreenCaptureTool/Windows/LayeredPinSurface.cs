using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Core.Capture;
using ScreenCaptureTool.Platform;
using SkiaSharp;
using DrawingPointF = System.Drawing.PointF;
using DrawingRectangleF = System.Drawing.RectangleF;

namespace ScreenCaptureTool.Windows;

// 画布图片仍保留各自原始像素，只提交屏幕矩形；不在缩放时反复合成、重采样整张画布。
internal readonly record struct PinPicture(Avalonia.Media.Imaging.Bitmap Image, double L, double T, double W, double H);

/// <summary>
/// 贴图呈现层的一帧输入（全部为<b>屏幕物理像素</b>，由 <c>StickerWindow.TryBuildPinFrame</c> 按 Avalonia
/// 同一套几何换算得出：窗口原点 + RootGrid 变换 + 各层 Margin/宽高）。本结构只描述"画什么、画在哪"，
/// 不含任何 Avalonia 对象，保证呈现层与交互层之间只有这一条数据通道。
/// </summary>
internal struct PinFrame
{
    /// <summary>图片显示矩形（可越出视口，越出部分由 <see cref="ClipL"/> 一组裁掉）。</summary>
    public double ImgL, ImgT, ImgW, ImgH;
    /// <summary>内容裁剪矩形＝交互窗 RootBorder 的可见范围（平移常驻态下比图片小）。</summary>
    public double ClipL, ClipT, ClipR, ClipB;
    /// <summary>光晕裁剪矩形＝交互窗 HWND 矩形（光晕画在 RootBorder 之外、只受窗口边裁）。静止态给一个极大值＝不裁。</summary>
    public double HaloClipL, HaloClipT, HaloClipR, HaloClipB;

    public bool ShadowOn;
    /// <summary>阴影层矩形（光晕基准＝它内缩 <see cref="InsetPx"/>，再外扩 <see cref="StrokePx"/>）。</summary>
    public double ShL, ShT, ShW, ShH;
    public uint ShadowArgb;
    public double InsetPx, RadiusPx, StrokePx, ExtentPx, SigmaPx;

    public bool CheckerOn;
    public double CkL, CkT, CkW, CkH;
    public uint CheckerBgArgb, CheckerTileArgb;
    public int CheckerCellPx;

    public bool BackdropOn;
    public double BdL, BdT, BdW, BdH;
    public uint BackdropArgb;

    /// <summary>带烘焙阴影贴图的内容主体（不含烘焙阴影）高亮开关与几何（屏幕物理像素）。</summary>
    public bool CardHighlightOn;
    public double CardL, CardT, CardW, CardH, CardR, CardStroke;
    public uint CardHighlightArgb;

    /// <summary>整窗不透明度（贴图「不透明度」，255＝不透明）。走 <c>UpdateLayeredWindow</c> 的
    /// SourceConstantAlpha，一次乘在整张 DIB（画面＋光晕＋底色）上，与 Avalonia 退路的 Opacity 同一语义。</summary>
    public byte ConstantAlpha;

    /// <summary>显示比源图小（缩小方向）→ 走 mipmap 采样，否则双线性。</summary>
    public bool Downscale;
}

/// <summary>
/// 贴图的<b>原生分层呈现窗</b>（2026-09-28 立，<see cref="StickerStyling.LayeredPinSurface"/>）。
///
/// <para><b>为什么要它</b>：Avalonia（WinUIComposition/DirectComposition）改窗口几何走 UI 线程的
/// <c>SetWindowPos</c>、交像素走渲染线程下一帧的 <c>BeginDraw</c>，两条线天然差一帧——缩放必须挪原点，
/// 那一帧就是"旧画面出现在新原点"（Avalonia #9103 not planned）。<c>UpdateLayeredWindow</c> 是 Win32 里
/// 唯一一个<b>位置、尺寸、像素一次调用提交</b>的接口（Qt 系贴图工具在 Windows 上就是走它），故图片贴图的
/// 像素改由本窗呈现：每帧 CPU 画进 DIB、一次 ULW 落地，结构上不存在"几何与像素错帧"。</para>
///
/// <para><b>分工</b>：本窗只画图片、阴影、棋盘格、底色，<c>WS_EX_TRANSPARENT</c> 不收任何输入；
/// Avalonia 窗口原样保留、叠在本窗<b>正上方</b>当透明交互层（输入、菜单、标注、选词、浮条、徽章）。
/// 两者 z 序由 <see cref="SyncZOrder"/> 钉成"本窗紧贴在交互窗之下"。</para>
///
/// <para><b>阴影</b>：与 <see cref="Controls.StickerShadowLayer"/> 同一条高斯斜坡公式、同一批超椭圆等距环，
/// 但只画一次左上角象限（外加一条直边剖面），四角镜像、四边拉伸——阴影每帧成本与贴图面积无关。
/// 分割线取在圆角与直边的切点上：超椭圆外法线在切点处与轴平行，四角偏移环严格落在各自象限内，
/// 直边段的剖面沿边向恒定，拆分是逐像素精确的。</para>
///
/// <para>AOT：全部为 P/Invoke + SkiaSharp CPU 光栅（Avalonia 自带的同一份 native），无反射、无 COM。</para>
/// </summary>
internal sealed class LayeredPinSurface : IDisposable
{
    private const string ClassName = "yuSnip_LayeredPinSurface";
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const int HTTRANSPARENT = -1;
    private const int MA_NOACTIVATE = 3;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    private static readonly object ClassLock = new();
    private static bool _classRegistered;
    private static readonly NativeMethods.WndProcDelegate WndProcKeepAlive = SurfaceWndProc;

    private readonly IntPtr _owner;
    private IntPtr _hwnd;

    // —— DIB（ULW 的像素来源，顶向 32bpp 预乘 BGRA，恰好就是 Skia 的 Bgra8888/Premul）——
    private IntPtr _hdcMem, _hBitmap, _hOldBitmap, _bits;
    private int _capW, _capH;

    // —— 源图（换图时整份拷一次；Avalonia 侧该层 Opacity=0 不再上传纹理，内存总量近似不变）——
    private object? _imageRef;
    private SKImage? _image;
    private int _imageId;
    private PinPicture[] _pictures = Array.Empty<PinPicture>();
    private readonly Dictionary<Avalonia.Media.Imaging.Bitmap, SKImage> _pictureImages = new();

    // —— 阴影象限片 ——
    /// <summary>一张阴影象限片（圆角或直角）及其切片参数。</summary>
    private sealed class HaloTile : IDisposable
    {
        public required SKImage Image;
        public required HaloKey Key;
        public int P, Rc, T;
        public void Dispose() => Image.Dispose();
    }

    // 圆角片 + 直角片：平移常驻态下被视口切掉的那一侧，卡片在屏幕上是直边截断，该侧两角按直角画
    private HaloTile? _haloRound, _haloSquare;

    // 内容卡片高亮与图片无关，只随设备像素半径/描边/状态色变化。保存一张有界角片，避免每帧整圈模糊。
    private sealed class HighlightTile : IDisposable
    {
        public required SKImage Image;
        public float Radius, Stroke;
        public uint Argb;
        public void Dispose() => Image.Dispose();
    }
    private HighlightTile? _highlightTile;

    // —— 棋盘格平铺片 ——
    private SKImage? _checkerTile;
    private (uint Bg, uint Tile, int Cell) _checkerKey;

    private Layout _last;
    private bool _hasLast;
    private bool _shown;
    private bool _ownerVisible = true;
    private bool _disposed;

    // —— 采证（由宿主汇总进"缩放段采证 / 拖拽段采证"那一行）——
    private int _statFrames, _statTileBuilds, _statHighlightBuilds, _statMoves;
    private double _statDrawMs, _statDrawMax, _statCommitMs, _statCommitMax;
    private long _statMaxArea;
    private int _statMaxW, _statMaxH;

    private LayeredPinSurface(IntPtr owner, IntPtr hwnd)
    {
        _owner = owner;
        _hwnd = hwnd;
    }

    public IntPtr Handle => _hwnd;

    /// <summary>为指定交互窗建一个呈现窗（初始隐藏，首帧提交时才显示）。失败返回 null，调用方退回 Avalonia 呈现。</summary>
    public static LayeredPinSurface? TryCreate(IntPtr owner)
    {
        if (owner == IntPtr.Zero) return null;
        try
        {
            EnsureClass();
            uint ex = (uint)(NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT |
                             NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW |
                             NativeMethods.WS_EX_TOPMOST);
            // ⚠ hWndParent 必须为 0：WS_POPUP 的 parent 参数就是"所有者"，被拥有窗恒在所有者之上，
            //   而本窗必须在交互窗**之下**。z 序改由 SyncZOrder 显式维护。
            IntPtr hwnd = NativeMethods.CreateWindowExW(ex, ClassName, null, NativeMethods.WS_POPUP,
                0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                AppLogger.Warn("LayeredPinSurface: CreateWindowExW 失败 err=" + Marshal.GetLastWin32Error());
                return null;
            }
            return new LayeredPinSurface(owner, hwnd);
        }
        catch (Exception ex)
        {
            AppLogger.Error("LayeredPinSurface: 创建失败", ex);
            return null;
        }
    }

    private static void EnsureClass()
    {
        if (_classRegistered) return;
        lock (ClassLock)
        {
            if (_classRegistered) return;
            var wc = new NativeMethods.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcKeepAlive),
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            NativeMethods.RegisterClassExW(ref wc);
            _classRegistered = true;
        }
    }

    private static IntPtr SurfaceWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_WINDOWPOSCHANGING)
            StickerWindow.KeepStickerBelowFloatingTools(lParam);
        // 双保险：WS_EX_TRANSPARENT 已让系统跳过本窗，这里再答"透明"与"不激活"
        if (msg == WM_NCHITTEST) return (IntPtr)HTTRANSPARENT;
        if (msg == WM_MOUSEACTIVATE) return (IntPtr)MA_NOACTIVATE;
        return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // =====================================================================
    // 源图
    // =====================================================================

    /// <summary>换源图（按引用判等，同一张位图零开销）。像素格式不认识时返回 false，调用方应退回 Avalonia 呈现。</summary>
    public bool SetImage(Avalonia.Media.Imaging.Bitmap bmp)
    {
        if (ReferenceEquals(bmp, _imageRef) && _image != null) return true;
        var next = ReadImage(bmp);
        if (next == null) return false;
        ClearPictures();
        _image?.Dispose();
        _image = next;
        _imageRef = bmp;
        _imageId++;
        _hasLast = false;
        return true;
    }

    public bool SetPictures(PinPicture[] pictures)
    {
        // 屏幕原点整数平移与从局部坐标重算的加法顺序不同，仅吸收双精度尾差。
        // 图源、顺序或真正的几何变化仍使帧失效。
        bool same = _pictures.Length == pictures.Length;
        for (int i = 0; same && i < pictures.Length; i++)
        {
            var previous = _pictures[i];
            var next = pictures[i];
            same = ReferenceEquals(previous.Image, next.Image)
                && Math.Abs(previous.L - next.L) < 0.000001
                && Math.Abs(previous.T - next.T) < 0.000001
                && Math.Abs(previous.W - next.W) < 0.000001
                && Math.Abs(previous.H - next.H) < 0.000001;
        }
        if (same) return true;
        foreach (var p in pictures)
        {
            if (_pictureImages.ContainsKey(p.Image)) continue;
            var image = ReadImage(p.Image);
            if (image == null) return false;
            _pictureImages.Add(p.Image, image);
        }
        foreach (var key in _pictureImages.Keys.Where(key => !pictures.Any(p => ReferenceEquals(p.Image, key))).ToArray())
        {
            _pictureImages[key].Dispose();
            _pictureImages.Remove(key);
        }
        _image?.Dispose();
        _image = null;
        _imageRef = null;
        _pictures = pictures;
        _imageId++;
        _hasLast = false;
        return true;
    }

    private void ClearPictures()
    {
        foreach (var image in _pictureImages.Values) image.Dispose();
        _pictureImages.Clear();
        _pictures = Array.Empty<PinPicture>();
    }

    // 普通贴图与画布共用同一份像素转换与格式退路。
    private static SKImage? ReadImage(Avalonia.Media.Imaging.Bitmap bmp)
    {
        SKImage? next = null;
        try
        {
            var ps = bmp.PixelSize;
            if (ps.Width <= 0 || ps.Height <= 0) return null;
            int stride = ps.Width * 4;
            int size = stride * ps.Height;
            var data = SKData.Create(size);
            if (data == null) return null;
            try
            {
                // 首选"带转码"的那条：任何源格式一律落成 Bgra8888/预乘（与 DIB、ULW 的要求逐位一致）
                using (var fb = new RawFramebuffer(data.Data, ps, stride))
                    bmp.CopyPixels(fb, Avalonia.Platform.AlphaFormat.Premul);
                next = SKImage.FromPixels(new SKImageInfo(ps.Width, ps.Height, SKColorType.Bgra8888, SKAlphaType.Premul), data, stride);
            }
            catch (Exception)
            {
                // 退路：原样块拷（只认两种 32bpp 布局）
                SKColorType ct;
                var fmt = bmp.Format;
                if (fmt == null || fmt.Value == Avalonia.Platform.PixelFormat.Bgra8888) ct = SKColorType.Bgra8888;
                else if (fmt.Value == Avalonia.Platform.PixelFormat.Rgba8888) ct = SKColorType.Rgba8888;
                else { data.Dispose(); return null; }
                SKAlphaType at = bmp.AlphaFormat switch
                {
                    Avalonia.Platform.AlphaFormat.Unpremul => SKAlphaType.Unpremul,
                    Avalonia.Platform.AlphaFormat.Opaque => SKAlphaType.Opaque,
                    _ => SKAlphaType.Premul,
                };
                bmp.CopyPixels(new Avalonia.PixelRect(0, 0, ps.Width, ps.Height), data.Data, size, stride);
                next = SKImage.FromPixels(new SKImageInfo(ps.Width, ps.Height, ct, at), data, stride);
            }
            data.Dispose();   // SKImage 已持有引用
            if (next == null) return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LayeredPinSurface: 源图转换失败，退回 Avalonia 呈现：" + ex.Message);
            next?.Dispose();
            return null;
        }

        return next;
    }

    // =====================================================================
    // 提交
    // =====================================================================

    /// <summary>画一帧并以一次 <c>UpdateLayeredWindow</c> 原子提交位置、尺寸与像素。
    /// 与上一帧的整数几何与全部参数完全一致时零操作（拖动挪窗、布局回打都不会引起重画）。
    /// <para>返回 <c>false</c> ＝本帧<b>没有</b>落地（ULW 失败、DIB 分配失败、Skia 建面失败），
    /// 调用方应补排一次同步：此时窗口还显示着上一帧，而交互窗可能已经挪走了（issue #52）。
    /// "几何为空 ⇒ 收起窗口"与"与上一帧逐位相同 ⇒ 零操作"都算落地，返回 <c>true</c>。</para></summary>
    public bool Commit(in PinFrame f)
    {
        if (_disposed || (_image == null && _pictures.Length == 0)) return false;
        var lay = BuildLayout(f);
        if (lay.WinW <= 0 || lay.WinH <= 0)
        {
            HideSurface();
            _hasLast = false;
            return true;
        }
        if (_hasLast && lay.Equals(_last))
        {
            if (!_shown && _ownerVisible) ShowSurface();
            return true;
        }

        long t0 = Stopwatch.GetTimestamp();
        if (lay.ShadowOn)
        {
            _haloRound = EnsureHaloTile(_haloRound, lay.Halo);
            if (lay.SquareMask != 0) _haloSquare = EnsureHaloTile(_haloSquare, lay.Halo with { Radius = 0f });
        }
        if (lay.CheckerOn) EnsureCheckerTile(lay.CheckerBg, lay.CheckerTile, lay.CheckerCell);
        if (!EnsureDib(lay.WinW, lay.WinH)) return false;

        var info = new SKImageInfo(lay.WinW, lay.WinH, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var surface = SKSurface.Create(info, _bits, _capW * 4))
        {
            if (surface == null) return false;
            var c = surface.Canvas;
            c.Clear(SKColors.Transparent);
            c.Translate(-lay.WinL, -lay.WinT);

            if (lay.ShadowOn && _haloRound != null && (lay.SquareMask == 0 || _haloSquare != null))
            {
                c.Save();
                c.ClipRect(lay.HaloClip.ToSk());
                DrawHalo(c, lay.Card, lay.SquareMask);
                c.Restore();
            }

            c.Save();
            c.ClipRect(lay.Clip.ToSk());
            if (lay.BackdropOn)
            {
                using var p = new SKPaint { Color = new SKColor(lay.BackdropArgb), IsAntialias = false };
                c.DrawRect(lay.Backdrop.ToSk(), p);
            }
            if (lay.CheckerOn && _checkerTile != null)
            {
                using var shader = SKShader.CreateImage(_checkerTile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
                    SKMatrix.CreateTranslation(lay.Checker.L, lay.Checker.T));
                using var p = new SKPaint { Shader = shader, IsAntialias = false };
                c.DrawRect(lay.Checker.ToSk(), p);
            }
            using (var ip = new SKPaint
            {
                IsAntialias = false,
                FilterQuality = lay.Downscale ? SKFilterQuality.Medium : SKFilterQuality.Low,
            })
            {
                if (_image != null)
                    c.DrawImage(_image, new SKRect(0, 0, _image.Width, _image.Height), lay.Img.ToSk(), ip);
                foreach (var picture in _pictures)
                {
                    var image = _pictureImages[picture.Image];
                    ip.FilterQuality = picture.W < image.Width || picture.H < image.Height ? SKFilterQuality.Medium : SKFilterQuality.Low;
                    c.DrawImage(image, new SKRect(0, 0, image.Width, image.Height),
                        new SKRect((float)picture.L, (float)picture.T, (float)(picture.L + picture.W), (float)(picture.T + picture.H)), ip);
                }
            }
            if (lay.CardHighlightOn && !lay.CardHighlight.Empty)
            {
                DrawCachedCardHighlight(c, new DrawingRectangleF(lay.CardHighlight.L, lay.CardHighlight.T,
                    lay.CardHighlight.W, lay.CardHighlight.H), lay.CardHighlightRadius,
                    lay.CardHighlightStroke, lay.CardHighlightArgb);
            }
            c.Restore();
            c.Flush();
        }
        double drawMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

        long t1 = Stopwatch.GetTimestamp();
        var dst = new NativeMethods.POINT { X = lay.WinL, Y = lay.WinT };
        var sz = new NativeMethods.SIZE { cx = lay.WinW, cy = lay.WinH };
        var src = new NativeMethods.POINT { X = 0, Y = 0 };
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            SourceConstantAlpha = lay.Alpha,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };
        bool ok = NativeMethods.UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref sz, _hdcMem, ref src,
            0, ref blend, NativeMethods.ULW_ALPHA);
        double commitMs = Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        if (!ok)
        {
            // ⚠ 必须把"上一帧"一并作废（issue #52）：窗口此刻仍停在上一帧的位置与像素上，
            // 若继续把 _last 当真，下一次同步算出同一个矩形就会命中逐位判等而被跳过
            // ⇒ 呈现窗永久停在旧矩形、交互窗已在新矩形＝视觉与点击分家且无自愈路径。
            _hasLast = false;
            AppLogger.Warn("LayeredPinSurface: UpdateLayeredWindow 失败 err=" + Marshal.GetLastWin32Error());
            return false;
        }

        _last = lay;
        _hasLast = true;
        if (!_shown && _ownerVisible) ShowSurface();

        _statFrames++;
        _statDrawMs += drawMs; if (drawMs > _statDrawMax) _statDrawMax = drawMs;
        _statCommitMs += commitMs; if (commitMs > _statCommitMax) _statCommitMax = commitMs;
        long area = (long)lay.WinW * lay.WinH;
        if (area > _statMaxArea) { _statMaxArea = area; _statMaxW = lay.WinW; _statMaxH = lay.WinH; }
        return true;
    }

    // 同一条内容轮廓与原有画笔，只把大圆角路径的模糊限制在四周互不重叠的窄带。
    private void DrawCachedCardHighlight(SKCanvas c, DrawingRectangleF rect, float radius, float stroke, uint argb)
    {
        const int tail = 20; // 与原光晕窄带相同，覆盖3σ、半线宽与抗锯齿尾部。
        int cut = (int)Math.Ceiling(radius + stroke + tail), pad = (int)Math.Ceiling(stroke + tail);
        int size = pad + cut;
        // 短卡片的两边光晕相互影响，不能拉伸；超大半径限制缓存为最多约16MiB，均沿用精确画法。
        if (radius <= 8f || 2 * (radius + stroke + tail * 2) >= Math.Min(rect.Width, rect.Height) || size > 1024)
        {
            DrawCardHighlight(c, rect, radius, stroke, argb);
            return;
        }
        using var path = CreateCardHighlightPath(rect, radius, stroke);
        var tile = _highlightTile;
        if (tile == null || tile.Radius != radius || tile.Stroke != stroke || tile.Argb != argb)
        {
            using var pixels = new SKBitmap(2 * size + 1, 2 * size + 1, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(pixels))
            {
                canvas.Clear(SKColors.Transparent);
                // 复用本帧精确轮廓，仅把四角和四条直边的柔光截入小表面，不再算第二份轮廓。
                using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3.5f);
                using var glow = new SKPaint { Color = new SKColor(argb).WithAlpha(160), Style = SKPaintStyle.Stroke,
                    StrokeWidth = 3, IsAntialias = true, MaskFilter = blur };
                void Part(SKRect source, SKRect destination)
                {
                    canvas.Save(); canvas.ClipRect(destination);
                    canvas.Translate(destination.Left - source.Left, destination.Top - source.Top);
                    canvas.DrawPath(path, glow); canvas.Restore();
                }
                int edge = 2 * size + 1, farEdge = size + 1;
                Part(new SKRect(rect.Left - pad, rect.Top - pad, rect.Left + cut, rect.Top + cut), new SKRect(0, 0, size, size));
                Part(new SKRect(rect.Right - cut, rect.Top - pad, rect.Right + pad, rect.Top + cut), new SKRect(farEdge, 0, edge, size));
                Part(new SKRect(rect.Left - pad, rect.Bottom - cut, rect.Left + cut, rect.Bottom + pad), new SKRect(0, farEdge, size, edge));
                Part(new SKRect(rect.Right - cut, rect.Bottom - cut, rect.Right + pad, rect.Bottom + pad), new SKRect(farEdge, farEdge, edge, edge));
                Part(new SKRect(rect.Left + cut, rect.Top - pad, rect.Left + cut + 1, rect.Top + cut), new SKRect(size, 0, farEdge, size));
                Part(new SKRect(rect.Left + cut, rect.Bottom - cut, rect.Left + cut + 1, rect.Bottom + pad), new SKRect(size, farEdge, farEdge, edge));
                Part(new SKRect(rect.Left - pad, rect.Top + cut, rect.Left + cut, rect.Top + cut + 1), new SKRect(0, size, size, farEdge));
                Part(new SKRect(rect.Right - cut, rect.Top + cut, rect.Right + pad, rect.Top + cut + 1), new SKRect(farEdge, size, edge, farEdge));
            }
            var next = new HighlightTile { Image = SKImage.FromBitmap(pixels), Radius = radius, Stroke = stroke,
                Argb = argb };
            _highlightTile?.Dispose();
            _highlightTile = tile = next;
            _statHighlightBuilds++;
        }
        using var paint = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };
        void Copy(SKRect source, SKRect target)
        {
            c.DrawImage(tile.Image, source, target, paint);
        }
        int end = 2 * size + 1, far = size + 1;
        Copy(new SKRect(0, 0, size, size), new SKRect(rect.Left - pad, rect.Top - pad, rect.Left + cut, rect.Top + cut));
        Copy(new SKRect(far, 0, end, size), new SKRect(rect.Right - cut, rect.Top - pad, rect.Right + pad, rect.Top + cut));
        Copy(new SKRect(0, far, size, end), new SKRect(rect.Left - pad, rect.Bottom - cut, rect.Left + cut, rect.Bottom + pad));
        Copy(new SKRect(far, far, end, end), new SKRect(rect.Right - cut, rect.Bottom - cut, rect.Right + pad, rect.Bottom + pad));
        Copy(new SKRect(size, 0, far, size), new SKRect(rect.Left + cut, rect.Top - pad, rect.Right - cut, rect.Top + cut));
        Copy(new SKRect(size, far, far, end), new SKRect(rect.Left + cut, rect.Bottom - cut, rect.Right - cut, rect.Bottom + pad));
        Copy(new SKRect(0, size, size, far), new SKRect(rect.Left - pad, rect.Top + cut, rect.Left + cut, rect.Bottom - cut));
        Copy(new SKRect(far, size, end, far), new SKRect(rect.Right - cut, rect.Top + cut, rect.Right + pad, rect.Bottom - cut));
        using var line = new SKPaint { Color = new SKColor(argb), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };
        c.DrawPath(path, line);
    }

    private static void DrawCardHighlight(SKCanvas c, DrawingRectangleF rectPx, float radius, float stroke, uint argb)
    {
        var skColor = new SKColor(argb);
        using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3.5f);
        using var glowPaint = new SKPaint
        {
            Color = skColor.WithAlpha(160),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true,
            MaskFilter = blur
        };
        using var linePaint = new SKPaint
        {
            Color = skColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };

        using var path = CreateCardHighlightPath(rectPx, radius, stroke);
        if (!path.IsEmpty)
        {
            DrawHighlightGlow(c, path, glowPaint, rectPx, radius, stroke);
            c.DrawPath(path, linePaint);
        }
    }

    private static SKPath CreateCardHighlightPath(DrawingRectangleF rectPx, float radius, float stroke)
    {
        DrawingPointF[] pts;
        // 采用与截图完全一致的超椭圆（n=5）R角及真等距线，杜绝正圆弧导致的 0.231r 角部错位
        if (stroke > 0f)
        {
            var outer = SelectionEffects.BuildStrokeOuterOutline(rectPx, radius, stroke);
            pts = outer.Length >= 3 ? outer : SelectionEffects.BuildOutline(rectPx, radius);
        }
        else
        {
            pts = SelectionEffects.BuildOutline(rectPx, radius);
        }

        var path = new SKPath();
        if (pts.Length >= 3)
        {
            path.MoveTo(pts[0].X, pts[0].Y);
            for (int i = 1; i < pts.Length; i++)
            {
                path.LineTo(pts[i].X, pts[i].Y);
            }
            path.Close();
        }
        return path;
    }

    private static void DrawHighlightGlow(SKCanvas c, SKPath path, SKPaint paint,
        DrawingRectangleF rect, float radius, float stroke)
    {
        // Skia 对直角矩形有快速模糊；圆角路径则按整个包围框建 mask，1500×1200 实测约 9ms。
        // 四条窄带覆盖全部轮廓与向内／向外的模糊尾部；分界沿直边，不重复合成。
        const float pad = 20f; // 大于 3σ + 半线宽 + 抗锯齿余量（σ=3.5，线宽=3）
        float band = radius + stroke + pad * 2;
        if (radius <= 0.5f || 2 * band >= Math.Min(rect.Width, rect.Height))
        {
            c.DrawPath(path, paint);
            return;
        }
        float left = rect.Left - stroke - pad, top = rect.Top - stroke - pad;
        float right = rect.Right + stroke + pad, bottom = rect.Bottom + stroke + pad;
        void Part(SKRect clip)
        {
            c.Save();
            c.ClipRect(clip);
            c.DrawPath(path, paint);
            c.Restore();
        }
        Part(new SKRect(left, top, right, top + band));
        Part(new SKRect(left, bottom - band, right, bottom));
        Part(new SKRect(left, top + band, left + band, bottom - band));
        Part(new SKRect(right - band, top + band, right, bottom - band));
    }

    /// <summary>拖动挪窗：与交互窗放进<b>同一批</b> <c>DeferWindowPos</c> 只改位置，本窗不重画。
    /// 返回 false 表示本窗当前没有可平移的已提交帧，调用方照旧单独挪交互窗。
    ///
    /// <para>⚠ 批次中途失败一律把 <c>_hasLast</c> 作废（issue #52）：`DeferWindowPos` 失败时这一批里
    /// <b>已经排进去的那些窗口位置变更会被整批丢弃还是部分生效，文档没有保证</b>，而
    /// <c>EndDeferWindowPos</c> 失败更是可能只挪了其中一个。任何一种都可能留下"交互窗挪了、呈现窗没挪"，
    /// 此时若还把 <c>_last</c> 当真，下一次同步会因逐位判等而跳过 ⇒ 错位固化。作废后调用方拿到
    /// <c>false</c> 走单独挪交互窗那条路，随后的同步必然真打一帧，把呈现窗拉回正确位置。</para></summary>
    public bool MoveWithOwner(int ownerX, int ownerY, int dx, int dy)
    {
        if (_disposed || !_hasLast || !_shown) return false;
        const uint flags = NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE;
        IntPtr batch = NativeMethods.BeginDeferWindowPos(2);
        if (batch == IntPtr.Zero) { _hasLast = false; return false; }
        batch = NativeMethods.DeferWindowPos(batch, _owner, IntPtr.Zero, ownerX, ownerY, 0, 0, flags);
        if (batch == IntPtr.Zero) { _hasLast = false; return false; }
        batch = NativeMethods.DeferWindowPos(batch, _hwnd, IntPtr.Zero, _last.WinL + dx, _last.WinT + dy, 0, 0, flags);
        if (batch == IntPtr.Zero) { _hasLast = false; return false; }
        if (!NativeMethods.EndDeferWindowPos(batch)) { _hasLast = false; return false; }
        _last = _last.Shifted(dx, dy);
        // 画布各图层使用屏幕坐标；同批挪窗成功后同步缓存，后续 SetPictures 才不会误判为换帧。
        for (int i = 0; i < _pictures.Length; i++)
            _pictures[i] = _pictures[i] with { L = _pictures[i].L + dx, T = _pictures[i].T + dy };
        _statMoves++;
        return true;
    }

    /// <summary>作废"上一帧"记账，使下一次 <see cref="Commit"/> 必定真打一帧（跳过逐位判等）。
    /// 用于"几何可能已被外力改过、但本侧算出的矩形与上一帧相同"的汇合点——
    /// 判等在那里恰好是错的：相同说明呈现窗本不该动，而它的真实位置已经被挪走了。</summary>
    public void Invalidate() => _hasLast = false;

    /// <summary>呈现窗<b>实际</b>所在的屏幕矩形（直接问系统，不读记账）。供几何对账用。</summary>
    public bool TryGetCommittedRect(out int l, out int t, out int w, out int h)
    {
        l = t = w = h = 0;
        if (_disposed || _hwnd == IntPtr.Zero || !_shown) return false;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rc)) return false;
        l = rc.Left; t = rc.Top; w = rc.Width; h = rc.Height;
        return true;
    }

    /// <summary>这一帧<b>本该</b>落在哪（只算几何、不画不提交）。供几何对账与 <c>TryGetCommittedRect</c> 比对。</summary>
    public bool TryPlanRect(in PinFrame f, out int l, out int t, out int w, out int h)
    {
        l = t = w = h = 0;
        if (_disposed) return false;
        var lay = BuildLayout(f);
        if (lay.WinW <= 0 || lay.WinH <= 0) return false;
        l = lay.WinL; t = lay.WinT; w = lay.WinW; h = lay.WinH;
        return true;
    }

    /// <summary>把本窗钉在交互窗<b>正下方</b>（交互窗 z 序每次变化都要调一次）。</summary>
    public void SyncZOrder()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, _owner, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>交互窗显隐（托盘"隐藏全部贴图"、关窗前的隐藏等）同步到本窗。</summary>
    public void SetOwnerVisible(bool visible)
    {
        _ownerVisible = visible;
        if (!visible) HideSurface();
        else if (_hasLast) ShowSurface();
    }

    /// <summary>退回 Avalonia 呈现（缩略图、文本贴图、格式不支持等）时收起本窗；下一次提交会整帧重画。</summary>
    public void Hide()
    {
        HideSurface();
        _hasLast = false;
    }

    private void ShowSurface()
    {
        if (_shown || _hwnd == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_hwnd, SW_SHOWNA);
        _shown = true;
        SyncZOrder();
    }

    private void HideSurface()
    {
        if (!_shown || _hwnd == IntPtr.Zero) return;
        NativeMethods.ShowWindow(_hwnd, SW_HIDE);
        _shown = false;
    }

    /// <summary>本段采证一行（并复位）。没有任何提交时返回空串，不给日志添噪声。</summary>
    public string TakeStats()
    {
        if (_statFrames == 0 && _statMoves == 0) return string.Empty;
        string s = $"｜分层呈现 帧={_statFrames} 绘制均值={(_statFrames > 0 ? _statDrawMs / _statFrames : 0):F2}ms 最大={_statDrawMax:F2}ms "
                 + $"ULW均值={(_statFrames > 0 ? _statCommitMs / _statFrames : 0):F2}ms 最大={_statCommitMax:F2}ms "
                 + $"阴影片重建={_statTileBuilds} 高亮片重建={_statHighlightBuilds} 同批挪窗={_statMoves} 最大面={_statMaxW}x{_statMaxH}({_statMaxArea / 1e6:F2}MPx)";
        _statFrames = _statTileBuilds = _statHighlightBuilds = _statMoves = 0;
        _statDrawMs = _statDrawMax = _statCommitMs = _statCommitMax = 0;
        _statMaxArea = 0; _statMaxW = _statMaxH = 0;
        return s;
    }

    // =====================================================================
    // 几何（一律整数物理像素；取整用 floor(x+0.5)——整数平移不变，拖动后才能与上一帧逐位判等）
    // =====================================================================

    private readonly record struct IRect(int L, int T, int R, int B)
    {
        public int W => R - L;
        public int H => B - T;
        public bool Empty => R <= L || B <= T;
        public SKRect ToSk() => new(L, T, R, B);
        public IRect Shift(int dx, int dy) => new(L + dx, T + dy, R + dx, B + dy);
        public IRect Inflate(int d) => new(L - d, T - d, R + d, B + d);
        public IRect Intersect(IRect o) => new(Math.Max(L, o.L), Math.Max(T, o.T), Math.Min(R, o.R), Math.Min(B, o.B));
        public IRect Union(IRect o) => Empty ? o : o.Empty ? this
            : new(Math.Min(L, o.L), Math.Min(T, o.T), Math.Max(R, o.R), Math.Max(B, o.B));
    }

    private readonly record struct HaloKey(float Radius, float Stroke, float Extent, float Sigma, uint Argb);

    private readonly record struct Layout(
        int WinL, int WinT, int WinW, int WinH,
        IRect Img, IRect Clip, int ImageId, bool Downscale,
        bool ShadowOn, IRect Card, IRect HaloClip, HaloKey Halo, int SquareMask,
        bool CheckerOn, IRect Checker, uint CheckerBg, uint CheckerTile, int CheckerCell,
        bool BackdropOn, IRect Backdrop, uint BackdropArgb, byte Alpha,
        bool CardHighlightOn, IRect CardHighlight, float CardHighlightRadius, float CardHighlightStroke, uint CardHighlightArgb)
    {
        public Layout Shifted(int dx, int dy) => this with
        {
            WinL = WinL + dx, WinT = WinT + dy,
            Img = Img.Shift(dx, dy), Clip = Clip.Shift(dx, dy),
            // 未启用的可选层由 BuildLayout 保留 default；平移 default 会让每次判等都失败。
            Card = ShadowOn ? Card.Shift(dx, dy) : Card,
            HaloClip = ShadowOn ? HaloClip.Shift(dx, dy) : HaloClip,
            Checker = CheckerOn ? Checker.Shift(dx, dy) : Checker,
            Backdrop = BackdropOn ? Backdrop.Shift(dx, dy) : Backdrop,
            CardHighlight = CardHighlightOn ? CardHighlight.Shift(dx, dy) : CardHighlight,
        };
    }

    private static int Snap(double v) => (int)Math.Floor(v + 0.5);

    private static IRect SnapRect(double l, double t, double w, double h)
    {
        int L = Snap(l), T = Snap(t);
        return new IRect(L, T, L + Math.Max(0, Snap(w)), T + Math.Max(0, Snap(h)));
    }

    /// <summary>光晕相对卡片外沿的像素外扩（与象限片的留边 P 同一口径）。</summary>
    private static int HaloPadPx(double stroke, double extent) => Controls.HaloTiles.PadPx(stroke, extent);

    private Layout BuildLayout(in PinFrame f)
    {
        var img = SnapRect(f.ImgL, f.ImgT, f.ImgW, f.ImgH);
        var clip = new IRect(Snap(f.ClipL), Snap(f.ClipT), Snap(f.ClipR), Snap(f.ClipB));
        var visible = img.Intersect(clip);
        IRect win = visible.Empty ? default : visible;

        IRect card = default, haloClip = default;
        HaloKey halo = default;
        int squareMask = 0;
        bool shadowOn = f.ShadowOn;
        if (shadowOn)
        {
            var sh = SnapRect(f.ShL, f.ShT, f.ShW, f.ShH);
            int inset = Snap(f.InsetPx);
            card = new IRect(sh.L + inset, sh.T + inset, sh.R - inset, sh.B - inset);

            // 平移常驻态（长图/放大越出视口）：卡片比可见内容大，被裁掉的那一侧光晕原本画在屏幕外，
            // 而窗口边距那一条落在卡片**内部**（光晕只画卡片外），于是出现"上下一条空白、空白外两侧还有
            // 光晕"（2026-09-29 用户报）。越出的那一侧卡片边收到可见内容边上，光晕贴着看得见的画面走；
            // 该侧的两个角在屏幕上是直边截断，改用直角象限片。1px 容差：补帧/取整让卡片与裁剪偶有一像素出入，
            // 不能因此把正常贴图的圆角翻成直角。
            bool cutL = card.L < clip.L - 1, cutT = card.T < clip.T - 1;
            bool cutR = card.R > clip.R + 1, cutB = card.B > clip.B + 1;
            if (cutL || cutT || cutR || cutB)
            {
                card = new IRect(cutL ? clip.L : card.L, cutT ? clip.T : card.T,
                                 cutR ? clip.R : card.R, cutB ? clip.B : card.B);
                if (cutL || cutT) squareMask |= 1;   // 左上
                if (cutR || cutT) squareMask |= 2;   // 右上
                if (cutL || cutB) squareMask |= 4;   // 左下
                if (cutR || cutB) squareMask |= 8;   // 右下
            }
            if (card.Empty) shadowOn = false;
            else
            {
                // 圆角半径不得超过卡片短边一半（与超椭圆轮廓生成的前提一致）；量化到 1/4 像素，
                // 补帧期间半径连续变化时减少无谓的象限片重建（1/4 像素的轮廓差在光晕里不可见）。
                double r = Math.Min(Math.Max(0, f.RadiusPx), Math.Min(card.W, card.H) / 2.0);
                halo = new HaloKey(Quarter(r), Quarter(f.StrokePx), (float)f.ExtentPx, (float)f.SigmaPx, f.ShadowArgb);
                int pad = HaloPadPx(halo.Stroke, halo.Extent);
                haloClip = new IRect(Snap(f.HaloClipL), Snap(f.HaloClipT), Snap(f.HaloClipR), Snap(f.HaloClipB));
                var haloBox = card.Inflate(pad).Intersect(haloClip);
                win = win.Union(haloBox);
            }
        }

        IRect ck = default, bd = default;
        if (f.CheckerOn) ck = SnapRect(f.CkL, f.CkT, f.CkW, f.CkH);
        if (f.BackdropOn) bd = SnapRect(f.BdL, f.BdT, f.BdW, f.BdH);

        IRect cardHighlight = default;
        float cardHighlightRadius = 0f;
        float cardHighlightStroke = 0f;
        if (f.CardHighlightOn && f.CardW > 0 && f.CardH > 0)
        {
            cardHighlight = SnapRect(f.CardL, f.CardT, f.CardW, f.CardH);
            cardHighlightRadius = (float)f.CardR;
            cardHighlightStroke = (float)f.CardStroke;
        }

        if (win.Empty) return default;
        return new Layout(win.L, win.T, win.W, win.H,
            img, clip, _imageId, f.Downscale,
            shadowOn, card, haloClip, halo, squareMask,
            f.CheckerOn, ck, f.CheckerBgArgb, f.CheckerTileArgb, Math.Max(1, f.CheckerCellPx),
            f.BackdropOn, bd, f.BackdropArgb, f.ConstantAlpha,
            f.CardHighlightOn, cardHighlight, cardHighlightRadius, cardHighlightStroke, f.CardHighlightArgb);
    }

    private static float Quarter(double v) => (float)(Math.Round(v * 4.0) / 4.0);

    // =====================================================================
    // 阴影：一次象限片 + 镜像/拉伸（口径见类注释）
    // =====================================================================

    private HaloTile EnsureHaloTile(HaloTile? cached, HaloKey key)
    {
        if (cached != null && key.Equals(cached.Key)) return cached;
        cached?.Dispose();
        var built = Controls.HaloTiles.Build(key.Radius, key.Stroke, key.Extent, key.Sigma, key.Argb);
        _statTileBuilds++;
        using (built.Bitmap)
            return new HaloTile { Image = SKImage.FromBitmap(built.Bitmap), Key = key, P = built.P, Rc = built.Rc, T = built.T };
    }

    /// <summary>四角按 <paramref name="squareMask"/>（1 左上 / 2 右上 / 4 左下 / 8 右下）选圆角片或直角片，
    /// 四边取圆角片直边段的剖面拉伸（直边剖面与半径无关，两张片在直边段逐像素相同）。</summary>
    private void DrawHalo(SKCanvas c, IRect card, int squareMask)
    {
        var round = _haloRound!;
        HaloTile Tile(int bit) => (squareMask & bit) != 0 ? _haloSquare! : round;
        float L = card.L, Tp = card.T, R = card.R, B = card.B;
        float midX = (L + R) / 2f, midY = (Tp + B) / 2f;
        using var p = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.None };

        void Corner(HaloTile t, float clipL, float clipT, float clipR, float clipB, float ox, float oy, float sx, float sy)
        {
            int cs = t.T - 1;
            var r = new SKRect(0, 0, cs, cs);
            c.Save();
            c.ClipRect(new SKRect(clipL, clipT, clipR, clipB));
            c.Translate(ox, oy);
            c.Scale(sx, sy);
            c.DrawImage(t.Image, r, r, p);
            c.Restore();
        }

        // 四角：按卡片中线分象限裁开（卡片短边 < 2×圆角时相邻两片会重叠，裁开即不叠加加深）
        var tl = Tile(1); var tr = Tile(2); var bl = Tile(4); var br = Tile(8);
        Corner(tl, L - tl.P - 1, Tp - tl.P - 1, midX, midY, L - tl.P, Tp - tl.P, 1, 1);
        Corner(tr, midX, Tp - tr.P - 1, R + tr.P + 1, midY, R + tr.P, Tp - tr.P, -1, 1);
        Corner(bl, L - bl.P - 1, midY, midX, B + bl.P + 1, L - bl.P, B + bl.P, 1, -1);
        Corner(br, midX, midY, R + br.P + 1, B + br.P + 1, R + br.P, B + br.P, -1, -1);

        int P = round.P, T = round.T;
        var img = round.Image;
        // 上下两边
        float x0 = L + tl.Rc, x1 = R - tr.Rc;
        if (x1 > x0)
        {
            var colSrc = new SKRect(T - 1, 0, T, P);
            c.DrawImage(img, colSrc, new SKRect(x0, Tp - P, x1, Tp), p);
        }
        float bx0 = L + bl.Rc, bx1 = R - br.Rc;
        if (bx1 > bx0)
        {
            var colSrc = new SKRect(T - 1, 0, T, P);
            c.Save(); c.Translate(0, B + P); c.Scale(1, -1);
            c.DrawImage(img, colSrc, new SKRect(bx0, 0, bx1, P), p); c.Restore();
        }
        // 左右两边
        float y0 = Tp + tl.Rc, y1 = B - bl.Rc;
        if (y1 > y0)
        {
            var rowSrc = new SKRect(0, T - 1, P, T);
            c.DrawImage(img, rowSrc, new SKRect(L - P, y0, L, y1), p);
        }
        float ry0 = Tp + tr.Rc, ry1 = B - br.Rc;
        if (ry1 > ry0)
        {
            var rowSrc = new SKRect(0, T - 1, P, T);
            c.Save(); c.Translate(R + P, 0); c.Scale(-1, 1);
            c.DrawImage(img, rowSrc, new SKRect(0, ry0, P, ry1), p); c.Restore();
        }
    }

    // =====================================================================
    // 棋盘格：与 SctCheckerboardBrush 同构（底色满铺 + 左上/右下两块浅格，格边 8 DIP）
    // =====================================================================

    private void EnsureCheckerTile(uint bg, uint tileColor, int cell)
    {
        if (_checkerTile != null && _checkerKey == (bg, tileColor, cell)) return;
        _checkerTile?.Dispose();
        using var bmp = new SKBitmap(new SKImageInfo(cell * 2, cell * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
        {
            canvas.Clear(new SKColor(bg));
            using var p = new SKPaint { Color = new SKColor(tileColor), IsAntialias = false };
            canvas.DrawRect(new SKRect(0, 0, cell, cell), p);
            canvas.DrawRect(new SKRect(cell, cell, cell * 2, cell * 2), p);
        }
        _checkerTile = SKImage.FromBitmap(bmp);
        _checkerKey = (bg, tileColor, cell);
    }

    // =====================================================================
    // DIB
    // =====================================================================

    private bool EnsureDib(int w, int h)
    {
        bool fits = _bits != IntPtr.Zero && w <= _capW && h <= _capH;
        // 缩到容量四分之一以下且容量已超 4MPx 时释放重建，免得放大过一次就长期占着整屏内存
        bool wasteful = fits && (long)_capW * _capH > 4_000_000 && (long)w * h * 4 < (long)_capW * _capH;
        if (fits && !wasteful) return true;

        FreeDib();
        int cw = Math.Max(w, (int)(w * 1.25)), ch = Math.Max(h, (int)(h * 1.25));
        var bi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = cw,
                biHeight = -ch,     // 顶向：行 0 在上，与 Skia 行序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,  // BI_RGB
            },
        };
        _hdcMem = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        _hBitmap = NativeMethods.CreateDIBSection(IntPtr.Zero, ref bi, (uint)NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_hdcMem == IntPtr.Zero || _hBitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            AppLogger.Warn($"LayeredPinSurface: DIB 分配失败 {cw}x{ch}");
            FreeDib();
            return false;
        }
        _hOldBitmap = NativeMethods.SelectObject(_hdcMem, _hBitmap);
        _capW = cw; _capH = ch;
        return true;
    }

    private void FreeDib()
    {
        if (_hdcMem != IntPtr.Zero && _hOldBitmap != IntPtr.Zero) NativeMethods.SelectObject(_hdcMem, _hOldBitmap);
        if (_hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(_hBitmap);
        if (_hdcMem != IntPtr.Zero) NativeMethods.DeleteDC(_hdcMem);
        _hdcMem = _hBitmap = _hOldBitmap = _bits = IntPtr.Zero;
        _capW = _capH = 0;
    }

    /// <summary>把一段裸内存包装成 Avalonia 的帧缓冲目标，供 <c>Bitmap.CopyPixels(ILockedFramebuffer, AlphaFormat)</c> 转码写入。</summary>
    private sealed class RawFramebuffer : Avalonia.Platform.ILockedFramebuffer
    {
        public RawFramebuffer(IntPtr address, Avalonia.PixelSize size, int rowBytes)
        {
            Address = address; Size = size; RowBytes = rowBytes;
        }
        public IntPtr Address { get; }
        public Avalonia.PixelSize Size { get; }
        public int RowBytes { get; }
        public Avalonia.Vector Dpi => new(96, 96);
        public Avalonia.Platform.PixelFormat Format => Avalonia.Platform.PixelFormat.Bgra8888;
        public void Dispose() { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(_hwnd);
        }
        catch { }
        _hwnd = IntPtr.Zero;
        FreeDib();
        _image?.Dispose(); _image = null; _imageRef = null;
        ClearPictures();
        _haloRound?.Dispose(); _haloRound = null;
        _haloSquare?.Dispose(); _haloSquare = null;
        _highlightTile?.Dispose(); _highlightTile = null;
        _checkerTile?.Dispose(); _checkerTile = null;
    }
}
