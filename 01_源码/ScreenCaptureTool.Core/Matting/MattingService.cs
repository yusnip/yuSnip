using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ScreenCaptureTool.Core.Deps;

namespace ScreenCaptureTool.Core.Matting;

/// <summary>
/// 离线智能抠图服务（集成 MODNet 人像精细抠图与 U2-NetP 通用主体显著抠图）。
/// 两枚 ONNX 模型不再内嵌，改由依赖包按需下载落盘，这里只按已就绪的包目录取文件。
/// </summary>
public sealed class MattingService : IDisposable
{
    public static MattingService Instance { get; } = new();

    // ---- 依赖包内的模型文件名（与服务器 deps.json 的 files[].name 逐字一致）----
    public const string PortraitModelFileName = "modnet.onnx";
    public const string GeneralModelFileName = "u2netp.onnx";
    /// <summary>缺件文案：抠图入口的 gate 与失败提示共用。</summary>
    public const string MattingNotReadyMessage = "抠图模型未就绪（组件尚未下载）";

    private readonly object _syncLock = new();
    private readonly Dictionary<MattingModelType, InferenceSession> _sessions = new();
    private bool _isDisposed;

    private MattingService() { }

    /// <summary>
    /// 异步执行智能抠图，生成带透明 Alpha 通道的 32 位 ARGB 位图。
    /// </summary>
    /// <param name="sourceBitmap">源位图（调用方保有其所有权）</param>
    /// <param name="type">抠图模型类型</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>抠图处理结果</returns>
    public async Task<MattingResult> CutoutAsync(Bitmap sourceBitmap, MattingModelType type, CancellationToken ct = default)
    {
        if (sourceBitmap == null)
            return MattingResult.Fail("输入图像为空");

        if (sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            return MattingResult.Fail("输入图像尺寸非法");

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();

            InferenceSession session;
            lock (_syncLock)
            {
                if (_isDisposed)
                    return MattingResult.Fail("抠图服务已释放");

                session = GetOrCreateSession(type);
            }

            ct.ThrowIfCancellationRequested();

            try
            {
                using var srcMat = BitmapToMat(sourceBitmap);
                ct.ThrowIfCancellationRequested();

                using var alphaMat = type switch
                {
                    MattingModelType.Portrait => RunModnetInference(session, srcMat, ct),
                    MattingModelType.General => RunU2NetpInference(session, srcMat, ct),
                    _ => throw new NotSupportedException($"不支持的抠图类型: {type}")
                };

                ct.ThrowIfCancellationRequested();

                var resultBitmap = ComposeAlphaBitmap(sourceBitmap, alphaMat);
                sw.Stop();
                AppLogger.Info($"智能抠图完成 [{type}], 尺寸={sourceBitmap.Width}x{sourceBitmap.Height}, 耗时={sw.ElapsedMilliseconds}ms");

                return MattingResult.Ok(resultBitmap, sw.ElapsedMilliseconds);
            }
            catch (OperationCanceledException)
            {
                return MattingResult.Fail("用户取消了抠图操作");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"智能抠图异常 [{type}]", ex);
                return MattingResult.Fail($"抠图失败: {ex.Message}");
            }
        }, ct);
    }

    /// <summary>取已就绪依赖包内的模型绝对路径；缺件即抛（下载由功能入口的 gate 负责）。</summary>
    private static string GetModelPath(MattingModelType type)
    {
        string name = type switch
        {
            MattingModelType.Portrait => PortraitModelFileName,
            MattingModelType.General => GeneralModelFileName,
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
        return DependencyManager.FilePath(DepIds.Matting, name)
            ?? throw new InvalidOperationException(MattingNotReadyMessage);
    }

    private InferenceSession GetOrCreateSession(MattingModelType type)
    {
        if (_sessions.TryGetValue(type, out var session))
        {
            return session;
        }

        string modelPath = GetModelPath(type);

        int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        var options = new SessionOptions
        {
            InterOpNumThreads = threads,
            IntraOpNumThreads = threads,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = false,
            EnableMemoryPattern = false
        };

        session = new InferenceSession(modelPath, options);
        _sessions[type] = session;
        AppLogger.Info($"抠图模型 [{type}] ONNX Runtime 会话初始化成功");
        return session;
    }

    /// <summary>
    /// 将 Bitmap 转换为 24 位 BGR OpenCV Mat
    /// </summary>
    private static Mat BitmapToMat(Bitmap bitmap)
    {
        int w = bitmap.Width;
        int h = bitmap.Height;
        var rect = new Rectangle(0, 0, w, h);
        var bmpData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var bgraMat = Mat.FromPixelData(h, w, MatType.CV_8UC4, bmpData.Scan0, bmpData.Stride);
            var bgrMat = new Mat();
            Cv2.CvtColor(bgraMat, bgrMat, ColorConversionCodes.BGRA2BGR);
            return bgrMat;
        }
        finally
        {
            bitmap.UnlockBits(bmpData);
        }
    }

    /// <summary>
    /// 计算 MODNet 输入分辨率：短边对齐到 512 参考基准（若单边过小或过大），向 32 向下取整对齐，并施加 4M 像素上限保护
    /// </summary>
    public static (int Width, int Height) CalculateModnetInputDimensions(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (512, 512);
        }

        int targetW = sourceWidth;
        int targetH = sourceHeight;

        // 若长边小于 512 或短边大于 512，则将短边缩放至 512 参考基准（保留面部与发丝高分辨率）
        if (Math.Max(sourceWidth, sourceHeight) < 512 || Math.Min(sourceWidth, sourceHeight) > 512)
        {
            if (sourceWidth < sourceHeight)
            {
                targetW = 512;
                targetH = (int)((long)sourceHeight * 512L / sourceWidth);
            }
            else
            {
                targetH = 512;
                targetW = (int)((long)sourceWidth * 512L / sourceHeight);
            }
        }

        // 32 的倍数向下对齐
        targetW = Math.Max(32, targetW - targetW % 32);
        targetH = Math.Max(32, targetH - targetH % 32);

        // 安全保护：单边 <= 8192 且总像素 <= 4194304 (4M)
        long totalPixels = (long)targetW * targetH;
        if (targetW > 8192 || targetH > 8192 || totalPixels > 4194304L)
        {
            double scale = Math.Sqrt(4194304.0 / totalPixels);
            targetW = Math.Max(32, (int)(targetW * scale) - (int)(targetW * scale) % 32);
            targetH = Math.Max(32, (int)(targetH * scale) - (int)(targetH * scale) % 32);
        }

        return (targetW, targetH);
    }

    /// <summary>
    /// MODNet 人像推理：参考尺寸 512，对齐 32，归一化 (x - 127.5) / 127.5，输出 Alpha 图双线性还原
    /// </summary>
    private static Mat RunModnetInference(InferenceSession session, Mat srcMat, CancellationToken ct)
    {
        int origW = srcMat.Width;
        int origH = srcMat.Height;

        var (targetW, targetH) = CalculateModnetInputDimensions(origW, origH);

        using var resizedMat = new Mat();
        Cv2.Resize(srcMat, resizedMat, new OpenCvSharp.Size(targetW, targetH), 0, 0, InterpolationFlags.Linear);

        // 构造 NCHW 浮点 Tensor (RGB 顺序)
        var tensor = new DenseTensor<float>(new[] { 1, 3, targetH, targetW });
        var span = tensor.Buffer.Span;
        int planeSize = targetH * targetW;
        int gOffset = planeSize;
        int bOffset = planeSize * 2;

        resizedMat.GetArray(out Vec3b[] pixels);
        for (int i = 0; i < planeSize; i++)
        {
            var bgr = pixels[i];
            // Vec3b: Item0=B, Item1=G, Item2=R
            span[i] = (bgr.Item2 - 127.5f) / 127.5f;          // R 通道
            span[gOffset + i] = (bgr.Item1 - 127.5f) / 127.5f; // G 通道
            span[bOffset + i] = (bgr.Item0 - 127.5f) / 127.5f; // B 通道
        }

        ct.ThrowIfCancellationRequested();

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", tensor)
        };

        using var results = session.Run(inputs);
        var outputTensor = results.First(r => r.Name == "output").AsTensor<float>();

        ct.ThrowIfCancellationRequested();

        float[] alphaArray = outputTensor.ToArray();
        // 清洗非法值并 Clamp 到 [0, 1]
        for (int i = 0; i < alphaArray.Length; i++)
        {
            float v = alphaArray[i];
            if (!float.IsFinite(v) || v <= 0f)
                alphaArray[i] = 0f;
            else if (v >= 1f)
                alphaArray[i] = 1f;
        }

        using var alphaSmall = new Mat(targetH, targetW, MatType.CV_32FC1);
        Marshal.Copy(alphaArray, 0, alphaSmall.Data, alphaArray.Length);

        var alphaFull = new Mat();
        Cv2.Resize(alphaSmall, alphaFull, new OpenCvSharp.Size(origW, origH), 0, 0, InterpolationFlags.Linear);
        return alphaFull;
    }

    /// <summary>
    /// U2-NetP 通用主体推理：固定 320x320，标准 ImageNet 归一化，输出节点 1959，极值归一化后双线性还原
    /// </summary>
    private static Mat RunU2NetpInference(InferenceSession session, Mat srcMat, CancellationToken ct)
    {
        int origW = srcMat.Width;
        int origH = srcMat.Height;

        const int targetSize = 320;
        using var resizedMat = new Mat();
        Cv2.Resize(srcMat, resizedMat, new OpenCvSharp.Size(targetSize, targetSize), 0, 0, InterpolationFlags.Linear);

        // ImageNet 归一化 (RGB)
        const float meanR = 0.485f, meanG = 0.456f, meanB = 0.406f;
        const float stdR = 0.229f, stdG = 0.224f, stdB = 0.225f;

        var tensor = new DenseTensor<float>(new[] { 1, 3, targetSize, targetSize });
        var span = tensor.Buffer.Span;
        int planeSize = targetSize * targetSize;
        int gOffset = planeSize;
        int bOffset = planeSize * 2;

        resizedMat.GetArray(out Vec3b[] pixels);
        for (int i = 0; i < planeSize; i++)
        {
            var bgr = pixels[i];
            span[i] = ((bgr.Item2 / 255f) - meanR) / stdR;          // R 通道
            span[gOffset + i] = ((bgr.Item1 / 255f) - meanG) / stdG; // G 通道
            span[bOffset + i] = ((bgr.Item0 / 255f) - meanB) / stdB; // B 通道
        }

        ct.ThrowIfCancellationRequested();

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input.1", tensor)
        };

        using var results = session.Run(inputs);
        var outputTensor = results.First(r => r.Name == "1959").AsTensor<float>();

        ct.ThrowIfCancellationRequested();

        float[] alphaArray = outputTensor.ToArray();

        // 极值归一化 (Min-Max Normalization)，与 rembg 官方实现一致
        float minVal = float.MaxValue;
        float maxVal = float.MinValue;
        for (int i = 0; i < alphaArray.Length; i++)
        {
            float v = alphaArray[i];
            if (!float.IsFinite(v)) continue;
            if (v < minVal) minVal = v;
            if (v > maxVal) maxVal = v;
        }

        float range = maxVal - minVal;
        if (range > 1e-6f)
        {
            for (int i = 0; i < alphaArray.Length; i++)
            {
                float v = alphaArray[i];
                if (!float.IsFinite(v))
                    alphaArray[i] = 0f;
                else
                    alphaArray[i] = Math.Clamp((v - minVal) / range, 0f, 1f);
            }
        }
        else
        {
            float fallbackVal = float.IsFinite(maxVal) ? Math.Clamp(maxVal, 0f, 1f) : 0f;
            Array.Fill(alphaArray, fallbackVal);
        }

        using var alphaSmall = new Mat(targetSize, targetSize, MatType.CV_32FC1);
        Marshal.Copy(alphaArray, 0, alphaSmall.Data, alphaArray.Length);

        var alphaFull = new Mat();
        Cv2.Resize(alphaSmall, alphaFull, new OpenCvSharp.Size(origW, origH), 0, 0, InterpolationFlags.Linear);
        return alphaFull;
    }

    /// <summary>
    /// 将原图的 RGB 像素与计算出的 Alpha 图合成，生成 32 位 ARGB 透明位图
    /// </summary>
    private static Bitmap ComposeAlphaBitmap(Bitmap srcBitmap, Mat alphaMat)
    {
        int width = srcBitmap.Width;
        int height = srcBitmap.Height;

        var dstBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var srcRect = new Rectangle(0, 0, width, height);

        var srcData = srcBitmap.LockBits(srcRect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dstData = dstBitmap.LockBits(srcRect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            unsafe
            {
                byte* pSrc = (byte*)srcData.Scan0;
                byte* pDst = (byte*)dstData.Scan0;
                float* pAlpha = (float*)alphaMat.Data;

                int srcStride = srcData.Stride;
                int dstStride = dstData.Stride;
                int alphaStep = (int)(alphaMat.Step() / sizeof(float));

                // 阈值与平滑对比度拉伸：
                // 神经网络（MODNet / U2-NetP）推理输出在背景区域常存在 0.001~0.03 的浮点残余弱噪点，
                // 在主体实心区域易存在 0.98~0.999 的微弱欠饱和。
                // 1) aVal <= 0.02f: 彻底截断为 0，彻底根除漂浮噪点与背景脏雾；
                // 2) aVal >= 0.98f: 饱和拉满为 1.0f，保证主体实心不透明；
                // 3) 中间区间 (0.02, 0.98) 线性平滑映射：保持发丝与半透边缘平滑过渡无断层；
                // 4) 纯透明像素 (targetA == 0) RGB 彻底清零，杜绝预乘/未预乘转换或图片查看器下的漏色与脏底。
                const float lowCutoff = 0.02f;
                const float highCutoff = 0.98f;
                const float invRange = 1.0f / (highCutoff - lowCutoff);

                for (int y = 0; y < height; y++)
                {
                    byte* rowSrc = pSrc + y * srcStride;
                    byte* rowDst = pDst + y * dstStride;
                    float* rowAlpha = pAlpha + y * alphaStep;

                    for (int x = 0; x < width; x++)
                    {
                        int pxOffset = x * 4;
                        byte b = rowSrc[pxOffset];
                        byte g = rowSrc[pxOffset + 1];
                        byte r = rowSrc[pxOffset + 2];
                        byte srcA = rowSrc[pxOffset + 3];

                        float aVal = rowAlpha[x];
                        if (aVal <= lowCutoff)
                        {
                            aVal = 0f;
                        }
                        else if (aVal >= highCutoff)
                        {
                            aVal = 1f;
                        }
                        else
                        {
                            aVal = (aVal - lowCutoff) * invRange;
                        }

                        byte targetA = (byte)(aVal * 255.0f + 0.5f);
                        if (srcA < 255)
                        {
                            targetA = (byte)((targetA * srcA) / 255);
                        }

                        if (targetA == 0)
                        {
                            // 纯透明像素清零 RGB，避免任何预乘合成或渲染器漏色
                            rowDst[pxOffset] = 0;
                            rowDst[pxOffset + 1] = 0;
                            rowDst[pxOffset + 2] = 0;
                            rowDst[pxOffset + 3] = 0;
                        }
                        else
                        {
                            rowDst[pxOffset] = b;
                            rowDst[pxOffset + 1] = g;
                            rowDst[pxOffset + 2] = r;
                            rowDst[pxOffset + 3] = targetA;
                        }
                    }
                }
            }
        }
        finally
        {
            srcBitmap.UnlockBits(srcData);
            dstBitmap.UnlockBits(dstData);
        }

        return dstBitmap;
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            if (_isDisposed) return;
            _isDisposed = true;

            foreach (var session in _sessions.Values)
            {
                try { session.Dispose(); } catch { }
            }
            _sessions.Clear();
        }
    }

    /// <summary>
    /// 只放掉已加载的模型会话、不销毁单例：卸载组件前腾开被占用的 .onnx，
    /// 下一次抠图仍会按新落盘的模型重建会话。与 Dispose 的差别就是不置 _isDisposed。
    /// </summary>
    public void UnloadModels()
    {
        lock (_syncLock)
        {
            foreach (var session in _sessions.Values)
            {
                try { session.Dispose(); } catch { }
            }
            _sessions.Clear();
        }
    }
}
