using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using ScreenCaptureTool.Core;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Windows
{
    /// <summary>
    /// 贴图拖放时的跟随缩略图卡片（Drag Adorner）。
    /// 基于 Win32 原生分层窗口（UpdateLayeredWindow）与 GDI+ 预乘 Alpha 架构，
    /// 具有 100% 鼠标点击穿透（WS_EX_TRANSPARENT）、不夺取焦点（WS_EX_NOACTIVATE）、
    /// 不出现在任务栏/Alt+Tab（WS_EX_TOOLWINDOW）、首帧同步提交 DWM 与独立 100Hz 光标跟随线程，
    /// 实现丝滑轻量、精致半透明且带柔和投影的拖拽视觉反馈。
    /// </summary>
    public sealed class StickerDragAdorner : IDisposable
    {
        private const string ClassName = "yuSnip_StickerDragAdorner";
        private static readonly object _classRegisterLock = new();
        private static bool _classRegistered;
        private static readonly NativeMethods.WndProcDelegate _wndProc = AdornerWndProc;

        private IntPtr _hwnd;
        private CancellationTokenSource? _cts;
        private Thread? _trackerThread;
        private volatile bool _disposed;

        private StickerDragAdorner(IntPtr hwnd, CancellationTokenSource cts, Thread trackerThread)
        {
            _hwnd = hwnd;
            _cts = cts;
            _trackerThread = trackerThread;
        }

        private static IntPtr AdornerWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // WM_NCHITTEST (0x0084)：强制返回 HTTRANSPARENT (-1)，确保 100% 鼠标点击与拖拽穿透
            const uint WM_NCHITTEST = 0x0084;
            const int HTTRANSPARENT = -1;

            if (msg == WM_NCHITTEST)
            {
                return (IntPtr)HTTRANSPARENT;
            }

            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// 尝试从本地图像文件路径创建并启动跟随缩略图卡片。
        /// </summary>
        /// <param name="imageFilePath">临时导出的图像文件路径</param>
        /// <param name="dpiScale">当前显示器 DPI 缩放比例（默认 1.0）</param>
        public static StickerDragAdorner? TryCreate(string? imageFilePath, double dpiScale = 1.0)
        {
            if (string.IsNullOrEmpty(imageFilePath) || !File.Exists(imageFilePath))
            {
                return null;
            }

            try
            {
                // 使用 FileShare.ReadWrite 避免锁定文件，保障外部第三方应用能正常读取
                using var fs = new FileStream(imageFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var img = Image.FromStream(fs);
                return TryCreate(img, dpiScale);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"StickerDragAdorner: 从文件创建缩略图失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 尝试从 GDI+ Image 创建并启动跟随缩略图卡片。
        /// </summary>
        /// <param name="image">源图像对象</param>
        /// <param name="dpiScale">当前显示器 DPI 缩放比例（默认 1.0）</param>
        public static StickerDragAdorner? TryCreate(Image? image, double dpiScale = 1.0)
        {
            if (image == null || image.Width <= 0 || image.Height <= 0)
            {
                return null;
            }

            if (dpiScale <= 0.1) dpiScale = 1.0;

            try
            {
                EnsureWindowClassRegistered();

                // 1. 缩略图基准尺寸（根据 DPI 缩放换算）
                int maxThumbSize = Math.Max(48, (int)Math.Round(120.0 * dpiScale));
                int minThumbSize = Math.Max(32, (int)Math.Round(36.0 * dpiScale));

                int origW = image.Width;
                int origH = image.Height;
                double scale = Math.Min((double)maxThumbSize / origW, (double)maxThumbSize / origH);
                int thumbW = Math.Max(minThumbSize, (int)Math.Round(origW * scale));
                int thumbH = Math.Max(minThumbSize, (int)Math.Round(origH * scale));

                // 2. 边距、圆角与窗口几何（根据 DPI 动态缩放）
                int shadowMargin = Math.Max(8, (int)Math.Round(12.0 * dpiScale)); // 柔和阴影外边距
                int cardPadding = Math.Max(2, (int)Math.Round(3.0 * dpiScale));   // 图片与卡片边框间隙
                int cardRadius = Math.Max(4, (int)Math.Round(8.0 * dpiScale));    // 卡片外框圆角
                int imgRadius = Math.Max(3, (int)Math.Round(5.0 * dpiScale));     // 图片裁剪圆角
                float borderWidth = Math.Max(1.0f, (float)(1.5 * dpiScale));      // 品牌紫边框粗细
                int cursorOffset = Math.Max(12, (int)Math.Round(16.0 * dpiScale)); // 光标右下偏移距离

                int cardW = thumbW + cardPadding * 2;
                int cardH = thumbH + cardPadding * 2;
                int totalW = cardW + shadowMargin * 2;
                int totalH = cardH + shadowMargin * 2;

                // 3. 绘制精致带投影的卡片
                using var bmp = new Bitmap(totalW, totalH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    // A. 外层柔和多层同心阴影（轻柔通透）
                    for (int i = 4; i >= 1; i--)
                    {
                        float spread = i * (float)(2.2 * dpiScale);
                        int alpha = 4 + (5 - i) * 6; // 渐变衰减：10, 16, 22, 28
                        using var shadowPath = CreateRoundedRectanglePath(
                            shadowMargin - spread,
                            shadowMargin - spread + (float)(1.5 * dpiScale),
                            cardW + spread * 2,
                            cardH + spread * 2,
                            cardRadius + i * 2);
                        using var shadowBrush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
                        g.FillPath(shadowBrush, shadowPath);
                    }

                    // B. 卡片白底（微半透 248/255）
                    using var cardPath = CreateRoundedRectanglePath(shadowMargin, shadowMargin, cardW, cardH, cardRadius);
                    using var cardBgBrush = new SolidBrush(Color.FromArgb(248, 255, 255, 255));
                    g.FillPath(cardBgBrush, cardPath);

                    // C. 贴图内容（圆角平滑裁切，避免图片角部溢出卡片）
                    int imgX = shadowMargin + cardPadding;
                    int imgY = shadowMargin + cardPadding;
                    using (var clipPath = CreateRoundedRectanglePath(imgX, imgY, thumbW, thumbH, imgRadius))
                    {
                        var oldClip = g.Clip;
                        g.SetClip(clipPath, CombineMode.Replace);
                        g.DrawImage(image, new Rectangle(imgX, imgY, thumbW, thumbH), 0, 0, origW, origH, GraphicsUnit.Pixel);
                        g.Clip = oldClip;
                    }

                    // D. 品牌紫边框（Token SctAccent 风格 #797EFE）
                    using var borderPen = new Pen(Color.FromArgb(220, 121, 126, 254), borderWidth);
                    g.DrawPath(borderPen, cardPath);
                }

                // 4. 对 32 位 ARGB 像素执行 Alpha 预乘（UpdateLayeredWindow AC_SRC_ALPHA 强制要求）
                PremultiplyBitmapAlpha(bmp);

                // 5. 获取初始鼠标光标位置（相对偏移放置于光标右下方）
                NativeMethods.GetCursorPos(out var curPt);
                int initX = curPt.X + cursorOffset;
                int initY = curPt.Y + cursorOffset;

                // 6. 创建 Win32 原生分层窗口（零激活、零焦点、穿透、工具窗口）
                uint exStyle = (uint)(
                    NativeMethods.WS_EX_LAYERED |
                    NativeMethods.WS_EX_TRANSPARENT |
                    NativeMethods.WS_EX_NOACTIVATE |
                    NativeMethods.WS_EX_TOOLWINDOW |
                    NativeMethods.WS_EX_TOPMOST);
                uint style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE;
                IntPtr hInstance = NativeMethods.GetModuleHandle(null);

                IntPtr hwnd = NativeMethods.CreateWindowExW(
                    exStyle, ClassName, null, style,
                    initX, initY, totalW, totalH,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (hwnd == IntPtr.Zero)
                {
                    AppLogger.Warn("StickerDragAdorner: CreateWindowExW 创建跟随窗口失败。");
                    return null;
                }

                // 7. 提交位图至 DWM 分层表面
                IntPtr hdcScreen = NativeMethods.GetDC(IntPtr.Zero);
                IntPtr hdcMem = NativeMethods.CreateCompatibleDC(hdcScreen);
                IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                IntPtr hOldBmp = NativeMethods.SelectObject(hdcMem, hBitmap);

                var blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 230, // 约 90% 整卡片不透明度，精致通透
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };

                var ptDst = new NativeMethods.POINT { X = initX, Y = initY };
                var size = new NativeMethods.SIZE { cx = totalW, cy = totalH };
                var ptSrc = new NativeMethods.POINT { X = 0, Y = 0 };

                NativeMethods.UpdateLayeredWindow(
                    hwnd, hdcScreen, ref ptDst, ref size, hdcMem, ref ptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);

                NativeMethods.SelectObject(hdcMem, hOldBmp);
                NativeMethods.DeleteObject(hBitmap);
                NativeMethods.DeleteDC(hdcMem);
                NativeMethods.ReleaseDC(IntPtr.Zero, hdcScreen);

                // 8. 启动高频（100Hz）光标跟踪独立后台线程
                var cts = new CancellationTokenSource();
                var token = cts.Token;
                var trackerThread = new Thread(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (NativeMethods.GetCursorPos(out var pt))
                        {
                            NativeMethods.SetWindowPos(
                                hwnd,
                                NativeMethods.HWND_TOPMOST,
                                pt.X + cursorOffset, pt.Y + cursorOffset, 0, 0,
                                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
                        }
                        Thread.Sleep(10);
                    }
                })
                {
                    IsBackground = true,
                    Name = "yuSnip_StickerDragAdornerTracker"
                };

                trackerThread.Start();
                return new StickerDragAdorner(hwnd, cts, trackerThread);
            }
            catch (Exception ex)
            {
                AppLogger.Error("StickerDragAdorner: 缩略图窗口创建异常", ex);
                return null;
            }
        }

        private static void EnsureWindowClassRegistered()
        {
            if (_classRegistered) return;
            lock (_classRegisterLock)
            {
                if (_classRegistered) return;
                IntPtr hInstance = NativeMethods.GetModuleHandle(null);
                var wc = new NativeMethods.WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = hInstance,
                    lpszClassName = ClassName
                };
                NativeMethods.RegisterClassExW(ref wc);
                _classRegistered = true;
            }
        }

        private static GraphicsPath CreateRoundedRectanglePath(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();
            float diameter = radius * 2f;
            if (diameter > width) diameter = width;
            if (diameter > height) diameter = height;

            path.AddArc(x, y, diameter, diameter, 180, 90);
            path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
            path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
            path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static unsafe void PremultiplyBitmapAlpha(Bitmap bitmap)
        {
            int w = bitmap.Width;
            int h = bitmap.Height;
            var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = scan0 + y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        byte b = row[0];
                        byte g = row[1];
                        byte r = row[2];
                        byte a = row[3];
                        if (a == 0)
                        {
                            row[0] = row[1] = row[2] = 0;
                        }
                        else if (a < 255)
                        {
                            row[0] = (byte)((b * a + 127) / 255);
                            row[1] = (byte)((g * a + 127) / 255);
                            row[2] = (byte)((r * a + 127) / 255);
                        }
                        row += 4;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>
        /// 销毁跟随缩略图窗口并停止光标跟踪线程。
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                _cts?.Cancel();
                _trackerThread?.Join(100);
                _cts?.Dispose();
            }
            catch { }

            if (_hwnd != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.DestroyWindow(_hwnd);
                }
                catch { }
                _hwnd = IntPtr.Zero;
            }
        }
    }
}
