using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using ScreenCaptureTool.Core.Deps;

namespace ScreenCaptureTool.Core.Capture;

/// <summary>
/// 基于 ONNX Runtime 的进程内 OCR 识别服务（PP-OCRv6 small）。
/// 检测（det）+ 识别（rec）两阶段流水线，全部在 yuSnip 进程内完成，无需外部 exe。
/// </summary>
public class PaddleOcrService : IDisposable
{
    private readonly object _lock = new object();
    private bool _disposed;

    // ONNX 推理会话
    private InferenceSession? _detSession;
    private InferenceSession? _recSession;

    // ---- 依赖包内的三个文件名（与服务器 deps.json 的 files[].name 逐字一致）----
    public const string DetModelFileName = "PP-OCRv6_small_det_infer.onnx";
    public const string RecModelFileName = "PP-OCRv6_small_rec_infer.onnx";
    public const string DictFileName = "ppocr_keys_v6.txt";
    /// <summary>缺件文案：三处入口的 gate 与后台补识别共用。</summary>
    public const string OcrNotReadyMessage = "OCR 识别引擎未就绪（组件尚未下载）";
    private List<string>? _dictChars;

    // ---- 检测模型参数（与 PP-OCRv6 官方推理配置一致）----
    private const float DetDbThresh = 0.2f;       // 概率图二值化阈值
    private const float DetDbBoxThresh = 0.45f;   // 框内概率均值阈值（过滤低置信框）
    private const float DetUnclipRatio = 1.4f;    // 框扩张系数（DB 输出为收缩区域，需扩张还原）
    // resize_long 模式：长边缩放目标。
    //
    // 保持 960（PaddleOCR 默认值）。曾据「960 会把海报小字压到几像素高而漏检」的推测
    // 提高到 1600，但**实测推翻了这个假设**：同一张 895×1256 海报（含 12~14px 正文）
    // 在 895/1790/2685 三档分辨率下，960 与 1600 的检出结果完全一致（均 34 行、
    // 均无框粘连），而 1600 慢 19~23%（2551/2592/2672ms vs 3028/3182/3228ms）。
    // 原因是 `RunOcrPipelineCore` 已有小图放大逻辑，且 DB 检测对缩放本身有鲁棒性。
    // 结论：提高此值只增耗时、无检出收益。若将来遇到确凿的小字漏检个案，
    // 应先用离屏基准复现并对比两档数据，不要凭推测调整。
    private const int DetMaxSideLen = 960;
    private const int DetMinSideLen = 32;         // 最小尺寸
    private const int DetMaxCandidates = 3000;    // 最大候选框数

    // ---- 识别模型参数 ----
    private const int RecImgHeight = 48;
    private const int RecMaxWidth = 320;           // 基准宽度（窄行 pad 到该宽度）
    private const int RecMaxDynamicWidth = 3200;   // 模型动态 shape 支持的最大宽度

    // ---- 字典与 CTC 布局 ----
    // 模型输出类别数 = blank(1) + 字典(18708) + 空格(1) = 18710
    // 解码时索引 0 = blank，1..N = 字典字符，N+1 = 空格
    // 注意：字典第 1749 行是全角空格 U+3000，加载时不能按"空白行"过滤掉
    private const int CtcBlankIndex = 0;

    public PaddleOcrService()
    {
    }

    /// <summary>
    /// 用依赖包里的模型文件初始化 ONNX Runtime 会话。
    /// 模型不再内嵌：缺件时这里只抛，下载由功能入口的 gate（UI 侧）负责，Core 不联网。
    /// </summary>
    private void EnsureInitialized()
    {
        if (_detSession != null && _recSession != null) return;

        string? detModelPath = DependencyManager.FilePath(DepIds.Ocr, DetModelFileName);
        string? recModelPath = DependencyManager.FilePath(DepIds.Ocr, RecModelFileName);
        string? dictPath = DependencyManager.FilePath(DepIds.Ocr, DictFileName);
        if (detModelPath == null || recModelPath == null || dictPath == null)
            throw new InvalidOperationException(OcrNotReadyMessage);

        // 加载字典。字典由依赖包按 SHA-256 分发，历史版本那枚缺 U+3000 行的坏字典不会再被读到，
        // 故取消"每次启动强制重释放"的兜底（它当初只为对冲内嵌资源解压实的不完整文件）。
        if (_dictChars == null)
        {
            _dictChars = LoadDictionary(dictPath);
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PP-OCRv6 字典加载完成，共 {_dictChars.Count} 个字符");
        }

        // 创建 ONNX Runtime 会话
        try
        {
            // 限制线程数，避免 CPU 占满
            int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            var sessionOptions = new SessionOptions
            {
                InterOpNumThreads = threads,
                IntraOpNumThreads = threads,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                // 禁用 CPU 内存池（Memory Arena）与静态内存模式，推理完成后即时释放原生堆缓冲，杜绝后台常驻 200MB+ 空闲内存池
                EnableCpuMemArena = false,
                EnableMemoryPattern = false
            };

            _detSession = new InferenceSession(detModelPath, sessionOptions);
            _recSession = new InferenceSession(recModelPath, sessionOptions);

            // 校验字典与模型输出类别数：应为 blank(1) + 字典 + 空格(1)
            // 若不匹配（例如字典缺行），识别结果会整体错位
            if (_dictChars != null)
            {
                var recOutMeta = _recSession.OutputMetadata.Values.FirstOrDefault();
                if (recOutMeta != null && recOutMeta.Dimensions.Length == 3 && recOutMeta.Dimensions[2] > 0)
                {
                    int classes = recOutMeta.Dimensions[2];
                    if (classes != _dictChars.Count + 2)
                        AppLogger.Error($"字典字符数({_dictChars.Count})与模型类别数({classes})不匹配（应为 类别数-2），识别结果将整体错位！", null);
                    else
                        { if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"字典校验通过: {classes} = 1(blank) + {_dictChars.Count}(字典) + 1(空格)"); }
                }
            }

            AppLogger.Info("PP-OCRv6 ONNX Runtime 会话初始化完成");
        }
        catch (Exception ex)
        {
            AppLogger.Error("创建 ONNX Runtime 会话失败", ex);
            // 所有 OCR 调用路径（全文识别 / 行框 / 逐字区域 / 贴图取字 / 翻译）都汇聚到这里，
            // 故把可执行指引写在日志里一处即可全覆盖——toast 只有 220px 宽、1.5 秒，装不下完整说明。
            if (IsNativeRuntimeFailure(ex)) LogVcRuntimeGuide(ex);
            throw;
        }
    }

    /// <summary>
    /// 把 OCR 失败异常翻译成用户能照着做的提示。
    ///
    /// onnxruntime.dll 加载失败（典型 Win32 1114 / 0x462 ERROR_DLL_INIT_FAILED）与截图内容、
    /// 软件设置全都无关，成因是本机 VC++ 运行库缺失或"半更新"混装（老 msvcp140 核心挂着
    /// 新 msvcp140_1 卫星）。把原生错误码原样抛给办公用户等于没说，这里换成一句带短链的指引。
    /// 调用方前缀（如"OCR 识别失败: "）由各处 toast 自行拼接。
    /// </summary>
    public static string DescribeError(Exception? ex)
    {
        if (ex != null && IsNativeRuntimeFailure(ex))
        {
            return "系统 VC++ 运行库缺失或版本混装，需安装后重启 yuSnip；下载 aka.ms/vc14/vc_redist.x64.exe";
        }
        return ex?.Message ?? "未知错误";
    }

    /// <summary>
    /// 沿 InnerException 链判断是否为"原生库/运行库加载失败"。
    /// 只认确定性特征（异常类型 + 具体错误码），不用"onnxruntime"之类的宽泛关键字，
    /// 以免把模型/字典等业务错误误报成运行库问题、误导用户去装根本用不上的东西。
    /// 错误码：0x462=1114 DLL 初始化例程失败；0x7e=126 找不到依赖模块；0x7f=127 入口点缺失（老 CRT 缺新导出）。
    /// </summary>
    private static bool IsNativeRuntimeFailure(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is DllNotFoundException or BadImageFormatException or TypeInitializationException) return true;
            string m = e.Message;
            if (m.Contains("0x462", StringComparison.OrdinalIgnoreCase)
                || m.Contains("0x7e)", StringComparison.OrdinalIgnoreCase)
                || m.Contains("0x7f)", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Win32 1114", StringComparison.OrdinalIgnoreCase)
                || m.Contains("initialization routine failed", StringComparison.OrdinalIgnoreCase)
                || m.Contains("DLL 初始化", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>首次命中运行库类失败时写一份完整处置指引；进程内只写一次，避免每次识别刷屏。</summary>
    private static int _vcGuideLogged;

    private static void LogVcRuntimeGuide(Exception ex)
    {
        if (System.Threading.Interlocked.Exchange(ref _vcGuideLogged, 1) != 0) return;
        AppLogger.Error(
            "【OCR 启动失败：疑似 VC++ 运行库问题】" + Environment.NewLine +
            "原始错误：" + ex.GetType().Name + Environment.NewLine +
            "原因：onnxruntime.dll 静态导入 MSVCP140 / MSVCP140_1 / VCRUNTIME140 / VCRUNTIME140_1。" +
            "本机这几件若缺失或版本混装（例如 msvcp140 还是 VS2015 的 14.00，vcruntime140_1 却是 VS2022 的新版），" +
            "新卫星挂在老核心上，CRT 自初始化直接在 DllMain 失败 → Win32 1114。" + Environment.NewLine +
            "处置：下载并安装最新版 VC++ 运行库(x64)，装完重启 yuSnip 即可恢复：" + Environment.NewLine +
            "      https://aka.ms/vc14/vc_redist.x64.exe" + Environment.NewLine +
            "官方说明页：https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist" + Environment.NewLine +
            "注：单 exe 版已自带一份版本一致的 CRT（app-local 优先加载），一般无需再装运行库；" +
            "若仍报 1114，请运行「OCR诊断工具.bat」并把生成的报告发给支持人员。", ex);
    }

    /// <summary>
    /// 从 inference.yml 中提取 character_dict 列表，或从 ppocr_keys_v6.txt 直接逐行读取。
    /// </summary>
    private static List<string> LoadDictionary(string dictPath)
    {
        var chars = new List<string>();
        if (!File.Exists(dictPath))
        {
            AppLogger.Warn($"字典文件不存在: {dictPath}，尝试从 inference.yml 提取...");
            return ExtractDictFromInferenceYml();
        }

        // 如果是逐行字符文件
        string[] lines = File.ReadAllLines(dictPath, Encoding.UTF8);
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            // 每行一个字符（可能多 codepoint）
            chars.Add(line);
        }

        if (chars.Count == 0)
        {
            AppLogger.Warn("字典文件为空，回退到 inference.yml 提取");
            return ExtractDictFromInferenceYml();
        }

        return chars;
    }

    /// <summary>
    /// 从内嵌的 inference.yml 提取 character_dict。
    /// </summary>
    private static List<string> ExtractDictFromInferenceYml()
    {
        var chars = new List<string>();
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            string[] names = assembly.GetManifestResourceNames();
            string ymlName = "ScreenCaptureTool.Core.Models.inference.yml";

            string? ymlContent = null;
            foreach (string name in names)
            {
                if (name == ymlName)
                {
                    using Stream? rs = assembly.GetManifestResourceStream(name);
                    if (rs != null)
                    {
                        using StreamReader sr = new StreamReader(rs, Encoding.UTF8);
                        ymlContent = sr.ReadToEnd();
                    }
                    break;
                }
            }

            if (ymlContent == null)
            {
                // 尝试从临时目录读取
                string tempYml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YuSnip", "ocr", "inference.yml");
                if (File.Exists(tempYml))
                    ymlContent = File.ReadAllText(tempYml, Encoding.UTF8);
            }

            if (ymlContent == null)
            {
                AppLogger.Error("无法找到 inference.yml", null);
                return chars;
            }

            // 解析 YAML 中的 character_dict 部分
            bool inDict = false;
            string[] ymlLines = ymlContent.Split('\n');
            foreach (string rawLine in ymlLines)
            {
                string line = rawLine.TrimEnd('\r');
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("character_dict:"))
                {
                    inDict = true;
                    continue;
                }

                if (inDict)
                {
                    // character_dict 下的条目格式: "- X" 或 "- 'X'"
                    if (trimmed.StartsWith("- "))
                    {
                        string val = trimmed.Substring(2);
                        // 去除引号（YAML 单引号字符串中 '' 是转义的单引号）
                        if (val.StartsWith("'") && val.EndsWith("'") && val.Length >= 2)
                            val = val.Substring(1, val.Length - 2).Replace("''", "'");
                        else if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                            val = val.Substring(1, val.Length - 2);
                        chars.Add(val);
                    }
                    else if (trimmed.Length > 0 && !trimmed.StartsWith("-"))
                    {
                        // 遇到非列表项，字典部分结束
                        break;
                    }
                }
            }

            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"从 inference.yml 提取了 {chars.Count} 个字典字符");
        }
        catch (Exception ex)
        {
            AppLogger.Error("从 inference.yml 提取字典失败", ex);
        }

        return chars;
    }

    // ========================================================
    //  公共 API（保持与旧版本签名兼容）
    // ========================================================

    public void SetLanguage(string langCode)
    {
        // PP-OCRv6 small 目前只支持中英文混合模型，不支持切换语言
        // 保留方法签名以兼容调用方
    }

    public Task<string> ExtractTextAsync(Bitmap bitmap)
    {
        return Task.Run(async () =>
        {
            var result = await ExtractFullResultAsync(bitmap);
            return result.Text;
        });
    }

    /// <summary>
    /// 完整 OCR 识别：检测 → 识别 → 拼接文本 + 行级框。
    /// </summary>
    public Task<OcrResultData> ExtractFullResultAsync(Bitmap bitmap)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_disposed) return new OcrResultData();

                try
                {
                    EnsureInitialized();
                    return RunOcrPipeline(bitmap);
                }
                catch (Exception ex)
                {
                    AppLogger.Error("PP-OCRv6 识别异常", ex);
                    // 原生库加载失败等底层错误码用户看不懂，统一过一道 DescribeError 换成可执行指引
                    return new OcrResultData { Error = DescribeError(ex) };
                }
            }
        });
    }

    public Task<List<OcrWordRegion>> ExtractTextRegionsAsync(Bitmap bitmap)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_disposed) return new List<OcrWordRegion>();

                try
                {
                    EnsureInitialized();
                    var result = RunOcrPipeline(bitmap);
                    var regions = new List<OcrWordRegion>();

                    int ctcLines = 0, uniformLines = 0;
                    foreach (var box in result.Boxes)
                    {
                        var characters = SplitCharacters(box.Text);
                        int n = characters.Count;
                        if (n <= 0) continue;

                        // 🔴 逐字刀口优先取识别器自己的 CTC 帧位（模型读到某个字时它落在输入的第几列）。
                        // 这条路不猜间距：空格被识别吞掉、拉丁宽窄悬殊、字号混排都不会让后面的字整体挪位——
                        // 而那正是"想选 BC 却选中 AB"的成因。拿不到帧位（竖排行／字数与解码不符）才按字数均分。
                        float[] edges;
                        float[]? charX = box.CharX;
                        if (charX != null && charX.Length == n + 1)
                        {
                            edges = charX;
                            ctcLines++;
                        }
                        else
                        {
                            edges = new float[n + 1];
                            for (int i = 0; i <= n; i++) edges[i] = box.Bounds.Left + box.Bounds.Width * i / n;
                            uniformLines++;
                        }

                        for (int i = 0; i < n; i++)
                        {
                            float left = Math.Min(edges[i], edges[i + 1]);
                            float right = Math.Max(edges[i + 1], left + 0.5f);   // 永不留零宽：零宽的字永远点不到
                            regions.Add(new OcrWordRegion
                            {
                                Text = characters[i],
                                Bounds = RectangleF.FromLTRB(left, box.Bounds.Top, right, box.Bounds.Bottom),
                                LineIndex = box.LineIndex
                            });
                        }
                    }

                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"PP-OCRv6 字框定位：{ctcLines} 行按 CTC 帧位、{uniformLines} 行按字数均分");

                    return regions;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("PP-OCRv6 ExtractTextRegionsAsync 异常", ex);
                    return new List<OcrWordRegion>();
                }
            }
        });
    }

    public Task<List<OcrLineRegion>> ExtractLinesWithBoxesAsync(Bitmap bitmap)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_disposed) return new List<OcrLineRegion>();

                try
                {
                    EnsureInitialized();
                    var result = RunOcrPipeline(bitmap);
                    var lines = new List<OcrLineRegion>();

                    foreach (var box in result.Boxes)
                    {
                        lines.Add(new OcrLineRegion
                        {
                            Text = box.Text,
                            Bounds = box.Bounds,
                            LineIndex = box.LineIndex,
                            Score = box.Score,
                            // 多边形与逐字刀口原样透出：图片翻译要靠它们把抹字收到真正有墨的像素，
                            // 只留轴对齐矩形会让渐变背景上的补图失去依据（2026-10-27 智能补图批 1）。
                            Polygon = box.Polygon,
                            CharX = box.CharX
                        });
                    }

                    return lines;
                }
                catch (Exception ex)
                {
                    AppLogger.Error("PP-OCRv6 ExtractLinesWithBoxesAsync 异常", ex);
                    return new List<OcrLineRegion>();
                }
            }
        });
    }

    /// <summary>
    /// 清理历史残留的 RapidOCR-json 进程（旧版兼容，新版无外部进程）。
    /// 保留方法签名以兼容 App.axaml.cs 启动调用。
    /// </summary>
    public static void KillStaleOcrProcesses()
    {
        // 进程内推理不需要清理外部进程，但保留方法以兼容调用方
        // 仍然清理可能存在的旧版 RapidOCR-json 残留进程
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("RapidOCR-json"))
            {
                try { p.Kill(); } catch { }
                p.Dispose();
            }
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _detSession?.Dispose();
                _recSession?.Dispose();
                _detSession = null;
                _recSession = null;
                _disposed = true;
            }
        }
    }

    // ========================================================
    //  核心 OCR 流水线
    // ========================================================

    private OcrResultData RunOcrPipeline(Bitmap bitmap)
    {
        // 透明背景贴图修复：位图存 BMP 再解码时 alpha 会被丢弃，透明像素变黑。
        // ClearType 文字在透明底上 RGB 本身接近黑色 → 黑底黑字，det 完全检测不到。
        // 含真实 alpha 的图先合成到白底；白底仍无结果（如白字透明底）再合成黑底重试。
        if (HasPartialAlpha(bitmap))
        {
            using (var whiteBg = FlattenAlpha(bitmap, Color.White))
            {
                var result = RunOcrPipelineCore(whiteBg);
                if (result.Boxes.Count > 0) return result;
            }
            using (var blackBg = FlattenAlpha(bitmap, Color.Black))
            {
                return RunOcrPipelineCore(blackBg);
            }
        }
        return RunOcrPipelineCore(bitmap);
    }

    /// <summary>检测位图是否含 alpha 通道且存在非完全不透明的像素（有任何透明/半透明区域）。</summary>
    private static bool HasPartialAlpha(Bitmap bitmap)
    {
        if ((bitmap.PixelFormat & System.Drawing.Imaging.PixelFormat.Alpha) == 0) return false;
        // 仅 32bppArgb / 32bppPArgb 逐字节扫描 alpha；其余带 alpha 的罕见格式保守视为含透明
        if (bitmap.PixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppArgb &&
            bitmap.PixelFormat != System.Drawing.Imaging.PixelFormat.Format32bppPArgb)
        {
            return true;
        }
        try
        {
            var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, bitmap.PixelFormat);
            try
            {
                int bytesPerPixel = System.Drawing.Image.GetPixelFormatSize(bitmap.PixelFormat) / 8;
                int alphaOffset = 3; // 32bppArgb 布局为 B,G,R,A
                unsafe
                {
                    byte* basePtr = (byte*)data.Scan0;
                    for (int y = 0; y < data.Height; y++)
                    {
                        byte* row = basePtr + y * data.Stride;
                        for (int x = 0; x < data.Width; x++)
                        {
                            if (row[x * bytesPerPixel + alphaOffset] < 255) return true;
                        }
                    }
                }
                return false;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }
        catch
        {
            // 读取失败按无 alpha 处理，走原有路径
            return false;
        }
    }

    /// <summary>把含 alpha 的位图合成到指定纯色背景上（alpha 正确参与混合）。</summary>
    private static Bitmap FlattenAlpha(Bitmap source, Color background)
    {
        var flattened = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(flattened))
        {
            g.Clear(background);
            g.DrawImage(source, 0, 0, source.Width, source.Height);
        }
        return flattened;
    }

    private OcrResultData RunOcrPipelineCore(Bitmap bitmap)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool detailed = AppLogger.DetailedLoggingEnabled;
        long allocatedBefore = detailed ? GC.GetAllocatedBytesForCurrentThread() : 0;
        int gc0Before = detailed ? GC.CollectionCount(0) : 0;
        int gc1Before = detailed ? GC.CollectionCount(1) : 0;
        int gc2Before = detailed ? GC.CollectionCount(2) : 0;
        OcrResultData? result = null;
        try
        {
            result = RunOcrPipelineCoreImpl(bitmap);
            return result;
        }
        finally
        {
            double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            AppLogger.Info($"PERF OCR.Pipeline elapsed={elapsedMs:F2}ms input={bitmap.Width}x{bitmap.Height} "
                + $"lines={result?.Boxes.Count ?? 0} success={result != null && string.IsNullOrEmpty(result.Error)}");
            if (detailed)
                AppLogger.Debug($"PERF OCR.Allocations threadAllocated={(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1048576.0:F3}MB "
                    + $"gc0={GC.CollectionCount(0) - gc0Before} gc1={GC.CollectionCount(1) - gc1Before} gc2={GC.CollectionCount(2) - gc2Before} "
                    + "(GC计数为进程级，分配为当前线程托管内存，不含模型初始化和原生堆)");
        }
    }

    private OcrResultData RunOcrPipelineCoreImpl(Bitmap bitmap)
    {
        int origWidth = bitmap.Width;
        int origHeight = bitmap.Height;
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"========== OCR 开始 ==========");
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"输入图像: {origWidth}x{origHeight}");

        // --- 预处理：小图放大 + 边缘填充 ---
        float scale = 1f;
        int padTop, padBottom, padLeft, padRight;
        padTop = padBottom = padLeft = padRight = 20;

        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Bmp);
            byte[] bytes = ms.ToArray();
            using var src = Cv2.ImDecode(bytes, ImreadModes.Color);
            if (src.Empty())
            {
                AppLogger.Warn("图像解码失败");
                return new OcrResultData();
            }

            using var processed = new Mat();
            if (src.Height < 150 || src.Width < 500)
            {
                Cv2.Resize(src, processed, new OpenCvSharp.Size(src.Width * 2, src.Height * 2), 0, 0, InterpolationFlags.Cubic);
                scale = 2f;
            }
            else
            {
                src.CopyTo(processed);
            }

            using var padded = new Mat();
            Cv2.CopyMakeBorder(processed, padded, padTop, padBottom, padLeft, padRight, BorderTypes.Replicate);

            // --- 阶段1：文本检测 ---
            var detBoxes = RunDetection(padded);
            if (detBoxes == null || detBoxes.Count == 0)
                return new OcrResultData();

            // --- 阶段2：文本识别 ---
            var results = new List<OcrResultBox>();
            int lineIndex = 0;
            int totalLines = 0, verticalLines = 0, cwWins = 0, ccwWins = 0;

            foreach (var quad in detBoxes)
            {
                // 从 padded 图像中裁剪文字区域（透视变换为矩形）。
                // 倾斜 ≤45° 时裁剪结果已被透视变换回正为水平条；
                // 倾斜 >45°（含垂直文本）时裁剪结果为竖条，需旋转后再识别。
                using var crop = CropRegion(padded, quad);
                if (crop == null || crop.Empty()) continue;
                totalLines++;

                string text;
                float score;
                float[]? charEdges = null;
                bool vertical = crop.Height > crop.Width * 1.5f;
                if (vertical)
                {
                    // 竖向文本行：顺时针 / 逆时针各旋转识别一次，取置信度更高者。
                    // （无法预知整页是顺时针还是逆时针旋转了 90°，双向尝试最稳妥）
                    // 竖向行的字沿 Y 排布，裁剪图上的横向帧位换不回原图的横向刀口，故不取字位。
                    verticalLines++;
                    using var rotCw = new Mat();
                    Cv2.Rotate(crop, rotCw, RotateFlags.Rotate90Clockwise);
                    using var rotCcw = new Mat();
                    Cv2.Rotate(crop, rotCcw, RotateFlags.Rotate90Counterclockwise);

                    string tCw = RunRecognition(rotCw, out float cCw, out _);
                    string tCcw = RunRecognition(rotCcw, out float cCcw, out _);

                    if (cCw >= cCcw) { text = tCw; score = cCw; cwWins++; }
                    else { text = tCcw; score = cCcw; ccwWins++; }
                    if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"竖向文本行: CW字符={tCw.Length}({cCw:F3}) / CCW字符={tCcw.Length}({cCcw:F3}) → 采用{(cCw >= cCcw ? "CW" : "CCW")}");
                }
                else
                {
                    text = RunRecognition(crop, out score, out charEdges);
                }

                if (string.IsNullOrWhiteSpace(text)) continue;

                // 坐标还原：padded → processed → 原图
                var originalBox = new PointF[4];
                for (int i = 0; i < 4; i++)
                {
                    float ox = (quad[i].X - padLeft) / scale;
                    float oy = (quad[i].Y - padTop) / scale;
                    ox = Math.Clamp(ox, 0f, origWidth);
                    oy = Math.Clamp(oy, 0f, origHeight);
                    originalBox[i] = new PointF(ox, oy);
                }

                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var pt in originalBox)
                {
                    if (pt.X < minX) minX = pt.X;
                    if (pt.Y < minY) minY = pt.Y;
                    if (pt.X > maxX) maxX = pt.X;
                    if (pt.Y > maxY) maxY = pt.Y;
                }

                RectangleF bounds = (minX != float.MaxValue && maxX > minX && maxY > minY)
                    ? RectangleF.FromLTRB(minX, minY, maxX, maxY)
                    : RectangleF.Empty;

                results.Add(new OcrResultBox
                {
                    Text = text,
                    Polygon = originalBox,
                    Bounds = bounds,
                    Score = score,
                    CharX = MapCharEdgesToOriginal(quad, crop.Width, charEdges, padLeft, scale, origWidth),
                    LineIndex = lineIndex++
                });
            }

            // --- 阅读顺序 ---
            // 竖向行占多数 → 整页被旋转了约 90°：按 X 排序恢复行序。
            //   顺时针旋转的裁剪胜出 → 原页面逆时针转（首行在最左）→ X 升序；
            //   逆时针旋转的裁剪胜出 → 原页面顺时针转（首行在最右）→ X 降序。
            if (verticalLines > 0 && verticalLines * 2 > totalLines && results.Count > 1)
            {
                bool pageRotatedCcw = cwWins >= ccwWins;
                results.Sort((a, b) => pageRotatedCcw
                    ? a.Bounds.X.CompareTo(b.Bounds.X)
                    : b.Bounds.X.CompareTo(a.Bounds.X));
                for (int i = 0; i < results.Count; i++) results[i].LineIndex = i;
                if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"竖排版面（{verticalLines}/{totalLines} 行竖向）→ 按 X {(pageRotatedCcw ? "升序" : "降序")} 重排阅读顺序");
            }

            // 启发式自然语句流与智能段落换行：无缝合并段内自然折行，消除物理框硬切断词句
            string fullText = OcrNaturalFlow.FormatNaturalText(results);
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"========== OCR 完成 ==========");
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"共识别 {results.Count} 行, 字符数={fullText.Length}");

            return new OcrResultData
            {
                Text = fullText,
                Boxes = results
            };
        }
    }

    // ========================================================
    //  检测（DET）阶段
    // ========================================================

    /// <summary>
    /// 运行检测模型，返回文字区域的四点框列表（在预处理后的图像坐标系中）。
    /// 每个元素是 4 个 Point2f 组成的数组，表示一个文字框的四个顶点。
    /// </summary>
    private List<Point2f[]> RunDetection(Mat image)
    {
        if (_detSession == null) return new List<Point2f[]>();

        var (inputTensor, ratio, targetH, targetW) = PreprocessDet(image);
        if (inputTensor == null) return new List<Point2f[]>();

        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"DET 输入: image={image.Width}x{image.Height}, resize={targetW}x{targetH}, ratio={ratio:F3}");

        var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", inputTensor) };
        using var results = _detSession.Run(feeds);
        var output = results[0].AsTensor<float>();

        // 输出 shape: [1, 1, H, W] —— 概率图
        int outH = output.Dimensions[2];
        int outW = output.Dimensions[3];
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"DET 输出 shape: [{output.Dimensions[0]}, {output.Dimensions[1]}, {output.Dimensions[2]}, {output.Dimensions[3]}]");

        var probMap = new float[outH * outW];
        // 普通 DenseTensor 按连续内存读；特殊布局用复用的 Span 多维索引。
        // 不能用 output[i] 冒充扁平索引，也不能逐像素构造 params int[]，否则大量临时数组触发 GC。
        ReadOnlySpan<float> outputValues = output is DenseTensor<float> denseOutput && !output.IsReversedStride
            ? denseOutput.Buffer.Span : default;
        Span<int> indices = stackalloc int[4];
        indices.Clear();
        float minProb = 1f, maxProb = 0f;
        for (int y = 0; y < outH; y++)
        {
            indices[2] = y;
            for (int x = 0; x < outW; x++)
            {
                indices[3] = x;
                float v = outputValues.IsEmpty ? output[(ReadOnlySpan<int>)indices] : outputValues[y * outW + x];
                probMap[y * outW + x] = v;
                if (v < minProb) minProb = v;
                if (v > maxProb) maxProb = v;
            }
        }
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"DET 概率图: {outW}x{outH}, min={minProb:F3} max={maxProb:F3} thresh={DetDbThresh}");

        // 二值化 → 轮廓 → 最小外接矩形 → 四点框
        float scaleX = (float)image.Width / outW;
        float scaleY = (float)image.Height / outH;
        var detBoxes = PostprocessDet(probMap, outH, outW, scaleX, scaleY, image.Width, image.Height);
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"DET 后处理: 检测到 {detBoxes.Count} 个文本框");
        for (int i = 0; AppLogger.DetailedLoggingEnabled && i < detBoxes.Count; i++)
        {
            var b = detBoxes[i];
            if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"  框[{i}]: ({b[0].X:F0},{b[0].Y:F0}) ({b[1].X:F0},{b[1].Y:F0}) ({b[2].X:F0},{b[2].Y:F0}) ({b[3].X:F0},{b[3].Y:F0})");
        }
        return detBoxes;
    }

    /// <summary>
    /// 检测预处理：resize_long（长边缩放到 960）→ ImageNet 归一化 → NCHW tensor。
    /// 与 PP-OCRv6 官方推理配置一致（scale=1/255, mean=[0.485,0.456,0.406], std=[0.229,0.224,0.225], BGR 通道序）。
    /// </summary>
    private (DenseTensor<float>, float, int, int) PreprocessDet(Mat image)
    {
        // resize_long 模式：长边缩放到 DetMaxSideLen（小图放大，利于小字号文本检测）
        int srcW = image.Width;
        int srcH = image.Height;
        float ratio = (float)DetMaxSideLen / Math.Max(srcW, srcH);

        // 对齐到 32 的倍数（DB 模型下采样要求）
        int targetW = Math.Max(DetMinSideLen, (int)Math.Round(srcW * ratio / 32f) * 32);
        int targetH = Math.Max(DetMinSideLen, (int)Math.Round(srcH * ratio / 32f) * 32);

        using var resized = new Mat();
        Cv2.Resize(image, resized, new OpenCvSharp.Size(targetW, targetH), 0, 0, InterpolationFlags.Linear);

        // ImageNet 归一化（注意 mean/std 按官方约定直接作用于 B/G/R 通道）
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };

        var tensor = new DenseTensor<float>(new[] { 1, 3, targetH, targetW });
        Span<float> tensorValues = tensor.Buffer.Span;
        int planeSize = targetH * targetW;

        resized.GetArray(out Vec3b[] pixels);
        for (int y = 0; y < targetH; y++)
        {
            int rowOff = y * targetW;
            for (int x = 0; x < targetW; x++)
            {
                var bgr = pixels[rowOff + x];
                int offset = rowOff + x;
                tensorValues[offset] = (bgr.Item0 / 255f - mean[0]) / std[0]; // B
                tensorValues[planeSize + offset] = (bgr.Item1 / 255f - mean[1]) / std[1]; // G
                tensorValues[2 * planeSize + offset] = (bgr.Item2 / 255f - mean[2]) / std[2]; // R
            }
        }

        float actualRatio = (float)targetW / srcW; // 实际缩放比（可能因 32 对齐略有偏差）
        return (tensor, actualRatio, targetH, targetW);
    }

    /// <summary>
    /// 检测后处理：概率图 → 二值化 → 轮廓 → 置信度过滤 → unclip 扩张 → 四点框。
    /// 参数与 PP-OCRv6 官方推理配置一致（thresh=0.2, box_thresh=0.45, unclip_ratio=1.4）。
    /// </summary>
    private List<Point2f[]> PostprocessDet(float[] probMap, int mapH, int mapW, float scaleX, float scaleY, int origW, int origH)
    {
        var boxes = new List<Point2f[]>();

        // 概率图 → OpenCV Mat（一维数组 → 二维 Mat）
        using var probMat = new Mat(mapH, mapW, MatType.CV_32FC1);
        Marshal.Copy(probMap, 0, probMat.Data, probMap.Length);
        // 二值化
        using var binMat = new Mat();
        Cv2.Threshold(probMat, binMat, DetDbThresh, 255, ThresholdTypes.Binary);
        using var binU8 = new Mat();
        binMat.ConvertTo(binU8, MatType.CV_8UC1);

        // 查找轮廓
        Cv2.FindContours(binU8, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);

        int candidates = 0;
        foreach (var contour in contours)
        {
            if (++candidates > DetMaxCandidates) break;

            // 过滤太小的区域
            double area = Cv2.ContourArea(contour);
            if (area < 10) continue;

            // box_thresh：轮廓内概率均值过滤低置信框
            using var mask = new Mat(mapH, mapW, MatType.CV_8UC1, Scalar.Black);
            Cv2.DrawContours(mask, new[] { contour }, -1, Scalar.White, -1);
            double score = Cv2.Mean(probMat, mask).Val0;
            if (score < DetDbBoxThresh) continue;

            // 最小外接矩形 + unclip 扩张（DB 输出的是向内收缩的文字区域，需扩张还原）
            RotatedRect minRect = Cv2.MinAreaRect(contour);
            double perimeter = Cv2.ArcLength(contour, true);
            if (perimeter > 1e-6)
            {
                float offset = (float)(area * DetUnclipRatio / perimeter);
                minRect.Size = new OpenCvSharp.Size2f(
                    minRect.Size.Width + 2 * offset,
                    minRect.Size.Height + 2 * offset);
            }
            Point2f[] rectPoints = minRect.Points();

            // 获取四点并转换回原图坐标（夹紧到图像范围内）
            var box = new Point2f[4];
            for (int i = 0; i < 4; i++)
            {
                box[i] = new Point2f(
                    Math.Clamp(rectPoints[i].X * scaleX, 0f, origW),
                    Math.Clamp(rectPoints[i].Y * scaleY, 0f, origH)
                );
            }

            // 过滤过大的区域（可能是整张图被当作文字）
            float bw = Math.Max(
                Math.Abs(box[0].X - box[2].X),
                Math.Abs(box[1].X - box[3].X));
            float bh = Math.Max(
                Math.Abs(box[0].Y - box[2].Y),
                Math.Abs(box[1].Y - box[3].Y));
            if (bw > origW * 0.95f && bh > origH * 0.95f) continue;

            // 按顺序排列四点（左上、右上、右下、左下）
            var ordered = OrderBoxPoints(box);
            boxes.Add(new Point2f[]
            {
                new(ordered[0].X, ordered[0].Y),
                new(ordered[1].X, ordered[1].Y),
                new(ordered[2].X, ordered[2].Y),
                new(ordered[3].X, ordered[3].Y)
            });
        }

        // 按视觉阅读顺序排序：主排序 top-Y + 次排序 top-X + 冒泡修正同视觉行 X 顺序
        // 解决中英 / 中-代码混排时因 unclip 扩张差异或框高不同导致 top-Y 微差被拆行错排的问题
        SortBoxesByReadingOrder(boxes);
        return boxes;
    }

    /// <summary>
    /// 将四点按左上→右上→右下→左下排序（PaddleOCR order_points_clockwise 算法）。
    /// TL = min(x+y)，BR = max(x+y)，TR = min(y-x)，BL = max(y-x)。
    /// 对 0°~±45° 任意倾斜的文本框都能正确排序；
    /// 倾斜超过 ±45° 时长短边对调（裁剪结果为竖条），由识别阶段的旋转回正逻辑处理。
    /// （历史教训：按极角排序会把倾斜框整体旋转 90°；按 y 分组排序在倾斜 ≈30° 时
    ///   因右端两角的 y 都小于左端而顺序错乱，同样导致裁剪旋转 90°、识别出碎片乱码）
    /// </summary>
    private static PointF[] OrderBoxPoints(Point2f[] pts)
    {
        Point2f tl = pts[0], tr = pts[0], br = pts[0], bl = pts[0];
        float minSum = float.MaxValue, maxSum = float.MinValue;
        float minDiff = float.MaxValue, maxDiff = float.MinValue;
        foreach (var p in pts)
        {
            float sum = p.X + p.Y;
            float diff = p.Y - p.X;
            if (sum < minSum) { minSum = sum; tl = p; }
            if (sum > maxSum) { maxSum = sum; br = p; }
            if (diff < minDiff) { minDiff = diff; tr = p; }
            if (diff > maxDiff) { maxDiff = diff; bl = p; }
        }
        return new PointF[]
        {
            new(tl.X, tl.Y),
            new(tr.X, tr.Y),
            new(br.X, br.Y),
            new(bl.X, bl.Y)
        };
    }

    /// <summary>
    /// 按视觉阅读顺序排序检测框（DB 后处理）。
    /// 1) 主排序按 top-Y（上到下），次排序按 top-X（左到右）；
    /// 2) 冒泡修正：当前框与前一同带框 top-Y 差 &lt; min(两框高) × 0.5 且左右倒序，则交换。
    /// 与 PaddleOCR 官方 sorted_boxes 同思路，比例阈值自适应任意图像分辨率与 unclip 扩张差。
    /// 修复中英混排 / 行内等宽代码片段因框高不同、unclip 偏移不同导致的 top-Y 微差被拆行错排问题。
    /// 竖排页保持原行为：每个字/词竖直堆叠时 top-Y 严格递增且不同带，冒泡不触发交换。
    /// </summary>
    private static void SortBoxesByReadingOrder(List<Point2f[]> boxes)
    {
        if (boxes.Count <= 1) return;

        // ① 先按横向空白带把版面切成若干「条带」，再对每个条带单独判断是否分栏。
        //
        // 为什么不能整幅图直接分栏：单栏逻辑（Y 相近就按 X 排）在双栏版面上会把
        // 左右栏交错成「左1 左2 右1 右2 左3…」，这串乱序原文送去翻译后，译文按序
        // 回填就落到别栏（实测：右栏正文译文跑到左栏电话位置）。但若对**整幅图**
        // 一刀切两栏，上半部本来是单栏的区域也会被强行拆开——实测该海报会把
        // 「GET/70%/BEST ORDER」排到整个左栏内容之后，同样造成错位。
        // 故必须先分带：带内才谈分栏，带间保持自上而下。
        var bands = SplitIntoBands(boxes);
        var ordered = new List<Point2f[]>(boxes.Count);

        foreach (var band in bands)
        {
            var columns = SplitIntoColumns(band);
            foreach (var col in columns)
            {
                SortSingleColumn(col);
                ordered.AddRange(col);
            }
        }

        boxes.Clear();
        boxes.AddRange(ordered);
    }

    /// <summary>
    /// 按 Y 投影的空白带把检测框切成横向条带。
    /// 空白高度需超过行高中位数的 <see cref="BandGapFactor"/> 倍才算带间分隔，
    /// 避免把正常行距误判成分隔。返回顺序即自上而下。
    /// </summary>
    private static List<List<Point2f[]>> SplitIntoBands(List<Point2f[]> boxes)
    {
        var single = new List<List<Point2f[]>> { boxes };
        if (boxes.Count < 4) return single;

        float minY = float.MaxValue, maxY = float.MinValue;
        var heights = new List<float>(boxes.Count);
        foreach (var b in boxes)
        {
            minY = Math.Min(minY, BoxTop(b));
            maxY = Math.Max(maxY, BoxBottom(b));
            heights.Add(BoxHeight(b));
        }
        int span = (int)Math.Ceiling(maxY - minY);
        if (span < 40) return single;

        heights.Sort();
        float medianH = heights[heights.Count / 2];
        int gapMin = Math.Max(8, (int)(medianH * BandGapFactor));

        var covered = new bool[span];
        foreach (var b in boxes)
        {
            int a = Math.Max(0, (int)(BoxTop(b) - minY));
            int z = Math.Min(span, (int)Math.Ceiling(BoxBottom(b) - minY));
            for (int y = a; y < z; y++) covered[y] = true;
        }

        // 收集覆盖段，间隔不足 gapMin 的相邻段合并为同一带
        var segments = new List<(int top, int bottom)>();
        for (int y = 0; y < span;)
        {
            if (!covered[y]) { y++; continue; }
            int z = y;
            while (z < span && covered[z]) z++;
            if (segments.Count > 0 && y - segments[^1].bottom < gapMin)
                segments[^1] = (segments[^1].top, z);
            else
                segments.Add((y, z));
            y = z;
        }

        if (segments.Count <= 1) return single;

        var result = new List<List<Point2f[]>>();
        foreach (var (top, bottom) in segments)
        {
            float y0 = minY + top, y1 = minY + bottom;
            var band = new List<Point2f[]>();
            foreach (var b in boxes)
            {
                float c = (BoxTop(b) + BoxBottom(b)) / 2f;
                if (c >= y0 - 1f && c <= y1 + 1f) band.Add(b);
            }
            if (band.Count > 0) result.Add(band);
        }

        // 归属有遗漏（浮点边界）→ 保守退回整幅
        int total = 0;
        foreach (var b in result) total += b.Count;
        if (total != boxes.Count) return single;

        return result;
    }

    /// <summary>带间空白需超过行高中位数的该倍数才算分隔带。</summary>
    private const float BandGapFactor = 1.2f;

    /// <summary>框的最小 Y。</summary>
    private static float BoxTop(Point2f[] box)
    {
        float v = box[0].Y;
        for (int i = 1; i < 4; i++) if (box[i].Y < v) v = box[i].Y;
        return v;
    }

    /// <summary>框的最大 Y。</summary>
    private static float BoxBottom(Point2f[] box)
    {
        float v = box[0].Y;
        for (int i = 1; i < 4; i++) if (box[i].Y > v) v = box[i].Y;
        return v;
    }

    /// <summary>栏间空白带的最小宽度（相对整幅宽度）。窄于此值视为字间距而非栏间距。</summary>
    private const float MinColumnGapRatio = 0.04f;

    /// <summary>
    /// 按 X 方向投影的空白带把检测框分栏。
    /// 只在版面中部（20%~80%）搜索空白带——边缘留白不是栏间距。
    /// 返回顺序即从左到右；未检出多栏时返回单个分组。
    /// </summary>
    private static List<List<Point2f[]>> SplitIntoColumns(List<Point2f[]> boxes)
    {
        var single = new List<List<Point2f[]>> { boxes };
        if (boxes.Count < 4) return single;      // 框太少，分栏无意义且易误判

        float minX = float.MaxValue, maxX = float.MinValue;
        foreach (var b in boxes)
        {
            minX = Math.Min(minX, BoxLeft(b));
            maxX = Math.Max(maxX, BoxRight(b));
        }
        int span = (int)Math.Ceiling(maxX - minX);
        if (span < 40) return single;

        // X 投影覆盖表
        var covered = new bool[span];
        foreach (var b in boxes)
        {
            int a = Math.Max(0, (int)(BoxLeft(b) - minX));
            int z = Math.Min(span, (int)Math.Ceiling(BoxRight(b) - minX));
            for (int x = a; x < z; x++) covered[x] = true;
        }

        // 在中部找最宽的连续空白
        int searchFrom = (int)(span * 0.20f);
        int searchTo = (int)(span * 0.80f);
        int bestWidth = 0, bestStart = 0, bestEnd = 0;
        for (int x = searchFrom; x < searchTo;)
        {
            if (covered[x]) { x++; continue; }
            int y = x;
            while (y < searchTo && !covered[y]) y++;
            if (y - x > bestWidth) { bestWidth = y - x; bestStart = x; bestEnd = y; }
            x = y;
        }

        if (bestWidth < span * MinColumnGapRatio) return single;

        // 以空白带中线为界分两栏，按框中心归属
        float cut = minX + (bestStart + bestEnd) / 2f;
        var left = new List<Point2f[]>();
        var right = new List<Point2f[]>();
        foreach (var b in boxes)
        {
            float center = (BoxLeft(b) + BoxRight(b)) / 2f;
            (center < cut ? left : right).Add(b);
        }

        // 任一栏为空或过少 → 判定不可靠，退回单栏
        if (left.Count < 2 || right.Count < 2) return single;

        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"版面分栏: 检出栏间空白 {bestWidth}px（阈值 {span * MinColumnGapRatio:F0}px），" +
                       $"分割线 x≈{cut:F0}，左栏 {left.Count} 框 / 右栏 {right.Count} 框");
        return new List<List<Point2f[]>> { left, right };
    }

    /// <summary>单栏内按视觉阅读顺序排序（原有逻辑）。</summary>
    private static void SortSingleColumn(List<Point2f[]> boxes)
    {
        if (boxes.Count <= 1) return;

        boxes.Sort((a, b) =>
        {
            int cmp = a[0].Y.CompareTo(b[0].Y);
            return cmp != 0 ? cmp : a[0].X.CompareTo(b[0].X);
        });

        int n = boxes.Count;
        for (int i = 0; i < n - 1; i++)
        {
            for (int j = i; j >= 0; j--)
            {
                float yDiff = Math.Abs(boxes[j + 1][0].Y - boxes[j][0].Y);
                float minH = Math.Min(BoxHeight(boxes[j + 1]), BoxHeight(boxes[j]));
                float threshold = minH * 0.5f;
                if (yDiff < threshold && boxes[j + 1][0].X < boxes[j][0].X)
                {
                    (boxes[j + 1], boxes[j]) = (boxes[j], boxes[j + 1]);
                }
                else
                {
                    break;
                }
            }
        }
    }

    /// <summary>框的最小 X（四点极值，兼容倾斜框）。</summary>
    private static float BoxLeft(Point2f[] box)
    {
        float v = box[0].X;
        for (int i = 1; i < 4; i++) if (box[i].X < v) v = box[i].X;
        return v;
    }

    /// <summary>框的最大 X。</summary>
    private static float BoxRight(Point2f[] box)
    {
        float v = box[0].X;
        for (int i = 1; i < 4; i++) if (box[i].X > v) v = box[i].X;
        return v;
    }

    /// <summary>框的竖直跨度（四点 Y 极差）。倾斜框也覆盖到真实视觉高度。</summary>
    private static float BoxHeight(Point2f[] box)
    {
        float minY = box[0].Y, maxY = box[0].Y;
        for (int i = 1; i < 4; i++)
        {
            if (box[i].Y < minY) minY = box[i].Y;
            if (box[i].Y > maxY) maxY = box[i].Y;
        }
        return maxY - minY;
    }

    // ========================================================
    //  识别（REC）阶段
    // ========================================================

    /// <summary>
    /// 运行识别模型，返回识别文本与平均置信度。
    /// <paramref name="charEdges"/> 是识别器<b>自己</b>给出的逐字刀口（裁剪图像素，升序，长度＝字数+1），
    /// 由 CTC 帧位换算；不可用时为 null，调用端退回按字数均分。
    /// </summary>
    private string RunRecognition(Mat crop, out float confidence, out float[]? charEdges)
    {
        confidence = 0f;
        charEdges = null;
        if (_recSession == null || _dictChars == null) return string.Empty;

        var inputTensor = PreprocessRec(crop, out int recInputW, out int recResizedW);
        if (inputTensor == null) return string.Empty;

        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"REC 输入: crop={crop.Width}x{crop.Height}, tensor=[{inputTensor.Dimensions[0]}, {inputTensor.Dimensions[1]}, {inputTensor.Dimensions[2]}, {inputTensor.Dimensions[3]}]");

        var feeds = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("x", inputTensor) };
        using var results = _recSession.Run(feeds);
        var output = results[0].AsTensor<float>();

        // 输出 shape: [N, T, C] —— CTC 解码
        int tLen = output.Dimensions[1];
        int cLen = output.Dimensions[2];
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"REC 输出 shape: [{output.Dimensions[0]}, {output.Dimensions[1]}, {output.Dimensions[2]}], T={tLen}, C={cLen}, dictCount={_dictChars.Count}");

        // argmax 序列可经字典还原原文，详细日志也只记录维度／步数，不收集序列。
        string text = CtcDecode(output, _dictChars, out confidence, out var runs, null);
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"REC argmax 步数={tLen}");
        charEdges = CharEdgesFromRuns(runs, tLen, recInputW, recResizedW, crop.Width);
        if (AppLogger.DetailedLoggingEnabled) AppLogger.Debug($"REC 解码字符数={text.Length} (置信度 {confidence:F3})");
        return text;
    }

    /// <summary>
    /// CTC 帧区间 → 裁剪图的逐字刀口列。一帧＝模型在输入宽度上扫过的一小段列，
    /// 某个字的刀口就取"这个字最后一帧"与"下一个字第一帧"的中点——字的宽窄、中间被吞掉的空格
    /// 都已经体现在帧号里，不需要再假设等宽。
    /// </summary>
    private static float[]? CharEdgesFromRuns(List<(int Start, int End)>? runs, int frameCount,
                                              int recInputW, int resizedW, int cropW)
    {
        if (runs == null || runs.Count < 2 || cropW < 4 || frameCount < 2) return null;
        float frameCols = (float)recInputW / frameCount;          // 每帧占 rec 输入的第几列宽
        float toCrop = cropW / (float)Math.Max(1, resizedW);      // rec 列 → 裁剪图像素

        var e = new float[runs.Count + 1];
        e[0] = 0f;
        for (int i = 0; i < runs.Count - 1; i++)
        {
            float right = runs[i].End * frameCols * toCrop;
            float nextLeft = runs[i + 1].Start * frameCols * toCrop;
            e[i + 1] = Math.Clamp((right + nextLeft) / 2f, 1f, cropW - 1f);
        }
        e[runs.Count] = cropW;

        for (int i = 1; i < e.Length; i++)
            if (e[i] <= e[i - 1]) e[i] = e[i - 1] + 0.5f;         // 刀口必须严格递增，否则字框会互相吞并
        return e[^1] <= cropW + 1f ? e : null;
    }

    /// <summary>
    /// 裁剪图上的逐字刀口 → <b>原图像素横坐标</b>。刀口先化成"沿行中线的第几个分数位置"，
    /// 再落在四点框<b>左沿中点→右沿中点</b>这条中线上按分数取 X，因此倾斜的行也对得上；
    /// 最后与行框同样地撤掉 padding/缩放。任何不单调或出界的情况返回 null（宁可不给字位）。
    /// </summary>
    private static float[]? MapCharEdgesToOriginal(Point2f[] quad, int cropW, float[]? charEdges,
                                                   float padLeft, float scale, float origWidth)
    {
        if (charEdges == null || charEdges.Length < 3 || cropW <= 0 || quad.Length < 4 || scale <= 0f) return null;

        float leftMidX = (quad[0].X + quad[3].X) / 2f;
        float rightMidX = (quad[1].X + quad[2].X) / 2f;
        float midSpan = rightMidX - leftMidX;
        if (midSpan < 2f) return null;

        var xs = new float[charEdges.Length];
        for (int i = 0; i < charEdges.Length; i++)
        {
            float fraction = Math.Clamp(charEdges[i] / cropW, 0f, 1f);
            float paddedX = leftMidX + midSpan * fraction;
            xs[i] = Math.Clamp((paddedX - padLeft) / scale, 0f, origWidth);
        }
        for (int i = 1; i < xs.Length; i++)
            if (xs[i] <= xs[i - 1]) return null;

        return xs;
    }

    /// <summary>
    /// 识别预处理：保持纵横比缩放到高 48（宽行不压缩，动态宽度最大 3200）
    /// → (x/255-0.5)/0.5 归一化 → NCHW tensor。
    /// 与 PP-OCRv6 官方 RecResizeImg 实现一致。
    /// </summary>
    private DenseTensor<float>? PreprocessRec(Mat crop, out int recInputW, out int recResizedW)
    {
        recInputW = 0;
        recResizedW = 0;
        int srcH = crop.Height;
        int srcW = crop.Width;
        if (srcH == 0 || srcW == 0) return null;

        // 官方逻辑：输入宽度 = int(48 * max(320/48, 宽高比))，上限 3200；
        // 实际缩放宽度 = ceil(48 * 宽高比)（不超过输入宽度）
        float whRatio = (float)srcW / srcH;
        int imgW = (int)(RecImgHeight * Math.Max(whRatio, (float)RecMaxWidth / RecImgHeight));
        imgW = Math.Min(imgW, RecMaxDynamicWidth);
        int resizedW = (int)Math.Ceiling(RecImgHeight * whRatio);
        resizedW = Math.Clamp(resizedW, 4, imgW);
        recInputW = imgW;
        recResizedW = resizedW;

        using var resized = new Mat();
        Cv2.Resize(crop, resized, new OpenCvSharp.Size(resizedW, RecImgHeight), 0, 0, InterpolationFlags.Linear);

        // PP-OCRv6 rec 归一化：(x/255 - 0.5) / 0.5，BGR 通道序
        var tensor = new DenseTensor<float>(new[] { 1, 3, RecImgHeight, imgW });
        Span<float> tensorValues = tensor.Buffer.Span;
        int planeSize = RecImgHeight * imgW;

        resized.GetArray(out Vec3b[] pixels);
        for (int y = 0; y < RecImgHeight; y++)
        {
            int rowOff = y * resizedW;
            for (int x = 0; x < resizedW; x++)
            {
                var bgr = pixels[rowOff + x];
                // 源行宽是 resizedW，目标行宽是 imgW；右侧 pad 保持 0。
                int offset = y * imgW + x;
                tensorValues[offset] = (bgr.Item0 / 255f - 0.5f) / 0.5f; // B
                tensorValues[planeSize + offset] = (bgr.Item1 / 255f - 0.5f) / 0.5f; // G
                tensorValues[2 * planeSize + offset] = (bgr.Item2 / 255f - 0.5f) / 0.5f; // R
            }
        }
        // resizedW 右侧的 pad 区域保持 0（归一化空间的中性灰），与官方实现一致

        return tensor;
    }

    /// <summary>
    /// CTC 解码：argmax → 去重 → 去 blank → 映射字典。
    /// 同时输出平均置信度（参与解码的帧的 argmax 概率均值，PaddleOCR 口径），
    /// 用于竖向文本双向旋转识别时的择优。
    /// <paramref name="runs"/> 给出每个胜出字符占据的连续帧区间 [Start, End)，
    /// 与文本逐字对应（区间数＝字符数）；出现无法映射的类别时置 null（帧位与文本对不上就不硬套）。
    /// </summary>
    private static string CtcDecode(Tensor<float> output, List<string> dictChars, out float confidence,
                                    out List<(int Start, int End)>? runs, StringBuilder? argmaxTrace = null)
    {
        // output shape: [batch=1, T, C]
        int tLen = output.Dimensions[1];
        int cLen = output.Dimensions[2];
        ReadOnlySpan<float> outputValues = output is DenseTensor<float> denseOutput && !output.IsReversedStride
            ? denseOutput.Buffer.Span : default;
        Span<int> indices = stackalloc int[3];
        indices.Clear();

        var sb = new StringBuilder();
        var charRuns = new List<(int Start, int End)>();
        bool runsOk = true;
        float probSum = 0f;
        int probCount = 0;
        int prevIdx = -1;
        int runStart = -1, runEnd = 0;

        void CloseRun(int endFrame)
        {
            if (!runsOk || runStart < 0) return;
            charRuns.Add((runStart, endFrame));
            runStart = -1;
        }

        for (int t = 0; t < tLen; t++)
        {
            // argmax
            indices[1] = t;
            indices[2] = 0;
            int rowOffset = t * cLen;
            int maxIdx = 0;
            float maxVal = outputValues.IsEmpty ? output[(ReadOnlySpan<int>)indices] : outputValues[rowOffset];
            for (int c = 1; c < cLen; c++)
            {
                indices[2] = c;
                float val = outputValues.IsEmpty ? output[(ReadOnlySpan<int>)indices] : outputValues[rowOffset + c];
                if (val > maxVal)
                {
                    maxVal = val;
                    maxIdx = c;
                }
            }
            if (argmaxTrace != null) argmaxTrace.Append(maxIdx).Append(' ');

            // CTC 解码：跳过 blank 和重复
            if (maxIdx == CtcBlankIndex)
            {
                CloseRun(t);
                prevIdx = maxIdx;
                continue;
            }
            if (maxIdx == prevIdx)
            {
                runEnd = t + 1;
                continue;
            }

            CloseRun(t);
            prevIdx = maxIdx;
            runStart = t;
            runEnd = t + 1;

            // 映射到字典字符
            // CTC 类别布局：索引 0 = blank，1..N = 字典字符，N+1 = 空格
            int dictIdx = maxIdx - 1;
            if (dictIdx >= 0 && dictIdx < dictChars.Count)
            {
                sb.Append(dictChars[dictIdx]);
            }
            else if (maxIdx == dictChars.Count + 1)
            {
                sb.Append(' ');
            }
            else
            {
                runsOk = false;   // 该类别没有对应字符：帧位与文本必然错位
            }

            probSum += maxVal;
            probCount++;
        }

        CloseRun(tLen);
        runs = runsOk && charRuns.Count == sb.Length ? charRuns : null;
        confidence = probCount > 0 ? probSum / probCount : 0f;
        return sb.ToString();
    }

    // ========================================================
    //  辅助方法
    // ========================================================

    /// <summary>
    /// 从图像中裁剪四点区域（透视变换为矩形）。
    /// </summary>
    private static Mat CropRegion(Mat image, Point2f[] quad)
    {
        // 计算目标宽高
        float w1 = Distance(quad[0], quad[1]); // 上边
        float w2 = Distance(quad[3], quad[2]); // 下边
        float h1 = Distance(quad[0], quad[3]); // 左边
        float h2 = Distance(quad[1], quad[2]); // 右边

        int targetW = (int)Math.Max(w1, w2);
        int targetH = (int)Math.Max(h1, h2);
        if (targetW < 4) targetW = 4;
        if (targetH < 4) targetH = 4;

        // 透视变换
        var srcPts = new Point2f[]
        {
            new(quad[0].X, quad[0].Y),
            new(quad[1].X, quad[1].Y),
            new(quad[2].X, quad[2].Y),
            new(quad[3].X, quad[3].Y)
        };
        var dstPts = new Point2f[]
        {
            new(0, 0),
            new(targetW - 1, 0),
            new(targetW - 1, targetH - 1),
            new(0, targetH - 1)
        };

        using var m = Cv2.GetPerspectiveTransform(srcPts, dstPts);
        var dst = new Mat();
        Cv2.WarpPerspective(image, dst, m, new OpenCvSharp.Size(targetW, targetH));
        return dst;
    }

    private static float Distance(Point2f a, Point2f b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// 将文本拆分为字符列表（正确处理 surrogate pair 和 emoji）。
    /// </summary>
    private static List<string> SplitCharacters(string text)
    {
        var chars = new List<string>();
        for (int i = 0; i < text.Length; i++)
        {
            if (i < text.Length - 1 && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1]))
            {
                chars.Add(text.Substring(i, 2));
                i++;
            }
            else
            {
                chars.Add(text[i].ToString());
            }
        }
        return chars;
    }
}

/// <summary>
/// OCR 识别结果中的单行或单块信息，包含文本与原图像素坐标系的 4 点多边形。
/// </summary>
public sealed class OcrResultBox
{
    /// <summary>识别文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// 4 个顶点的原图像素坐标（支持旋转/倾斜），通常按顺时针/逆时针排列：左上、右上、右下、左下。
    /// </summary>
    public PointF[] Polygon { get; init; } = Array.Empty<PointF>();

    /// <summary>外接轴对齐矩形（原图像素空间）。</summary>
    public RectangleF Bounds { get; init; }

    /// <summary>置信度得分（0.0 ~ 1.0）。</summary>
    public float Score { get; init; } = 1.0f;

    /// <summary>
    /// 逐字刀口的<b>原图像素横坐标</b>（升序，长度＝识别字数+1），由识别器的 CTC 帧位换算而来。
    /// 竖排行、字数与解码不一致或帧位不可用时为 null，调用端按字数均分。
    /// </summary>
    public float[]? CharX { get; init; }

    /// <summary>行索引（从 0 开始）。竖排版面重排后会更新。</summary>
    public int LineIndex { get; set; }
}

/// <summary>
/// 完整的 OCR 识别结果集合。
/// </summary>
public sealed class OcrResultData
{
    /// <summary>拼接后的完整文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>各行文本及其多边形外接框列表。</summary>
    public List<OcrResultBox> Boxes { get; init; } = new();

    /// <summary>识别失败时的异常描述（若成功则为 null）。</summary>
    public string? Error { get; init; }
}
