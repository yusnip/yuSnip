using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Reflection;
using System.IO.Compression;
using System.Text.Json;
using OpenCvSharp;

namespace ScreenCaptureTool.Core.Capture;

/// <summary>
/// 提供基于 RapidOCR-json 的外挂文字识别服务。
/// </summary>
public class PaddleOcrService : IDisposable
{
    private Process? _process;
    private readonly object _lock = new object();
    private bool _disposed;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;

    public string RecModel { get; private set; } = "rec_ch_PP-OCRv4_infer.onnx";
    public string DetModel { get; private set; } = "ch_PP-OCRv4_det_infer.onnx";
    public string KeysFile { get; private set; } = "ppocr_keys_v1.txt";

    public PaddleOcrService()
    {
    }

    public void SetLanguage(string langCode)
    {
        lock (_lock)
        {
            switch (langCode.ToLower())
            {
                case "en":
                    RecModel = "en_PP-OCRv4_rec_mobile.onnx";
                    KeysFile = "dict_en.txt";
                    break;
                default:
                    RecModel = "rec_ch_PP-OCRv4_infer.onnx";
                    DetModel = "ch_PP-OCRv4_det_infer.onnx";
                    KeysFile = "ppocr_keys_v1.txt";
                    break;
            }

            // 如果进程已经启动，关闭它，让下次识别时使用新语言参数重启
            if (_process != null && !_process.HasExited)
            {
                try { _process.Kill(); } catch { }
                _process.Dispose();
                _process = null;
            }
        }
    }

    public void WarmUp()
    {
        Task.Run(() =>
        {
            lock (_lock)
            {
                if (_disposed) return;
                try
                {
                    EnsureProcessStarted();
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("PaddleOcrService.WarmUp 异常: " + ex.Message);
                }
            }
        });
    }

    private bool EnsureProcessStarted()
    {
        if (_process == null || _process.HasExited)
        {
            string tempOcrDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "yuSnip", "ocr");
            string exePath = Path.Combine(tempOcrDir, "RapidOCR-json.exe");
            string targetRecPath = Path.Combine(tempOcrDir, "models", RecModel);
            
            if (!File.Exists(exePath) || !File.Exists(targetRecPath))
            {
                AppLogger.Info("正在从内嵌资源释放 RapidOCR 引擎...");
                if (!Directory.Exists(tempOcrDir))
                {
                    Directory.CreateDirectory(tempOcrDir);
                }
                
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    string prefix = "ScreenCaptureTool.Core.RapidOCR_json.";
                    string[] names = assembly.GetManifestResourceNames();
                    bool found = false;
                    
                    foreach (string name in names)
                    {
                        if (name.StartsWith(prefix))
                        {
                            found = true;
                            string relPath = name.Substring(prefix.Length);
                            // LogicalName 包含了原始的相对路径（含有反斜杠）
                            string destFile = Path.Combine(tempOcrDir, relPath);
                            string destDir = Path.GetDirectoryName(destFile)!;
                            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                            using Stream? rs = assembly.GetManifestResourceStream(name);
                            if (rs != null)
                            {
                                using FileStream fs = new FileStream(destFile, FileMode.Create, FileAccess.Write);
                                rs.CopyTo(fs);
                            }
                        }
                    }
                    if (!found) AppLogger.Warn("未找到内嵌的 RapidOCR 资源");
                }
                catch (Exception ex)
                {
                    AppLogger.Error("释放 RapidOCR 资源失败", ex);
                }
            }

            if (!File.Exists(exePath))
            {
                AppLogger.Warn("RapidOCR-json.exe 准备失败: " + exePath);
                return false;
            }

            // 限制 OCR 线程数在 2 到 4 之间，避免过多线程导致 CPU 100% 占用卡死 UI 并带来额外的线程同步开销
            int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--models=models --det={DetModel} --cls=ch_ppocr_mobile_v2.0_cls_infer.onnx --rec={RecModel} --keys={KeysFile} --numThread={threads}",
                WorkingDirectory = tempOcrDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            try
            {
                _process = Process.Start(psi);
                if (_process == null) return false;

                _stdin = _process.StandardInput;
                _stdout = _process.StandardOutput;

                // 读取初始化信息，通常有两行，如 "RapidOCR-json v1.1.0" 和 "OCR init completed."
                while (true)
                {
                    string? initLine = _stdout.ReadLine();
                    if (initLine == null) break;
                    AppLogger.Info("RapidOCR Init: " + initLine);
                    if (initLine.Contains("OCR init completed."))
                    {
                        break;
                    }
                }

                // 解决 RapidOCR-json 启动后第一次 OCR 必定返回 299 未知错误的 bug：
                // 在启动完成后，立即发送一次 1x1 像素的 dummy 识别请求并丢弃其结果，使引擎完成首次预热
                string dummyFile = Path.Combine(Path.GetTempPath(), "ocr_warmup_dummy.bmp");
                try
                {
                    using (var dummyBmp = new Bitmap(1, 1))
                    {
                        dummyBmp.Save(dummyFile, System.Drawing.Imaging.ImageFormat.Bmp);
                    }
                    string requestJson = "{\"image_path\": \"" + dummyFile.Replace("\\", "/") + "\"}";
                    _stdin.WriteLine(requestJson);
                    _stdin.Flush();

                    while (true)
                    {
                        string? line = _stdout.ReadLine();
                        if (string.IsNullOrWhiteSpace(line)) break;
                        if (line.TrimStart().StartsWith("{"))
                        {
                            AppLogger.Info("RapidOCR 预热 Dummy 响应: " + line);
                            break;
                        }
                    }
                }
                catch (Exception warmupEx)
                {
                    AppLogger.Warn("RapidOCR 预热 Dummy 请求异常: " + warmupEx.Message);
                }
                finally
                {
                    if (File.Exists(dummyFile))
                    {
                        try { File.Delete(dummyFile); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("启动 RapidOCR 进程失败", ex);
                return false;
            }
        }
        return true;
    }

    public Task<string> ExtractTextAsync(Bitmap bitmap)
    {
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_disposed) return string.Empty;

                if (!EnsureProcessStarted())
                {
                    return string.Empty;
                }

                string tempFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".bmp");
                try
                {
                    // --- 开始 OpenCV 预处理 ---
                    bool preprocessOk = false;
                    try
                    {
                        // 1. 在内存中将 GDI+ Bitmap 转换为 OpenCV Mat，避免第一次磁盘写入/读取的开销
                        using (var ms = new MemoryStream())
                        {
                            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                            byte[] bytes = ms.ToArray();
                            using (var src = Cv2.ImDecode(bytes, ImreadModes.Color))
                            {
                                if (!src.Empty())
                                {
                                    using (var processed = new Mat())
                                    {
                                        // 2. 放大 (Upscaling) 针对小尺寸选区，提升小字体的特征图分辨率
                                        if (src.Height < 150 || src.Width < 500)
                                        {
                                            Cv2.Resize(src, processed, new OpenCvSharp.Size(src.Width * 2, src.Height * 2), 0, 0, InterpolationFlags.Cubic);
                                        }
                                        else
                                        {
                                            src.CopyTo(processed);
                                        }

                                        // 3. 边缘填充 (Padding) 防止文本紧贴边缘导致检测框被切断
                                        using (var padded = new Mat())
                                        {
                                            Cv2.CopyMakeBorder(processed, padded, 20, 20, 20, 20, BorderTypes.Replicate);
                                            // 写入到临时 BMP 文件，供 OCR 进程读取
                                            Cv2.ImWrite(tempFile, padded);
                                        }
                                    }
                                    preprocessOk = true;
                                }
                            }
                        }
                    }
                    catch (Exception cvEx)
                    {
                        AppLogger.Warn("OpenCV 内存预处理失败，回退到原图文件: " + cvEx.Message);
                    }

                    if (!preprocessOk)
                    {
                        // 回退逻辑：直接保存为 BMP 文件
                        bitmap.Save(tempFile, System.Drawing.Imaging.ImageFormat.Bmp);
                    }
                    // --- 结束 OpenCV 预处理 ---

                    string requestJson = "{\"image_path\": \"" + tempFile.Replace("\\", "/") + "\"}";
                    _stdin!.WriteLine(requestJson);
                    _stdin.Flush();

                    string? resultJson = null;
                    while (true)
                    {
                        string? line = _stdout!.ReadLine();
                        if (string.IsNullOrWhiteSpace(line)) break;
                        
                        // 由于有些 OCR 可能在返回 JSON 之前打印一些 Log 警告，这里只认以 { 开头的那行
                        if (line.TrimStart().StartsWith("{"))
                        {
                            resultJson = line;
                            break;
                        }
                        else
                        {
                            AppLogger.Info("RapidOCR StdOut: " + line);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(resultJson)) return string.Empty;

                    using JsonDocument doc = JsonDocument.Parse(resultJson);
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("code", out JsonElement codeElement) && codeElement.GetInt32() == 100)
                    {
                        if (root.TryGetProperty("data", out JsonElement dataArray) && dataArray.ValueKind == JsonValueKind.Array)
                        {
                            StringBuilder sb = new StringBuilder();
                            foreach (JsonElement item in dataArray.EnumerateArray())
                            {
                                if (item.TryGetProperty("text", out JsonElement textElement))
                                {
                                    sb.AppendLine(textElement.GetString());
                                }
                            }
                            return sb.ToString().Trim();
                        }
                    }
                    else
                    {
                        AppLogger.Warn("RapidOCR-json failed: " + resultJson);
                    }
                }
                catch(Exception ex)
                {
                    AppLogger.Error("RapidOCR-json 识别异常", ex);
                }
                finally
                {
                    if (File.Exists(tempFile))
                    {
                        try { File.Delete(tempFile); } catch { }
                    }
                }

                return string.Empty;
            }
        });
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                if (_process != null && !_process.HasExited)
                {
                    try { _process.Kill(); } catch { }
                    _process.Dispose();
                }
                _process = null;
                _disposed = true;
            }
        }
    }
}
