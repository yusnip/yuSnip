using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>
    /// 分段合并（计划 §3.1，全量移植参考工程 :6464-6579）。
    /// 约定：返回空字符串＝成功；非空＝错误详情。含碎段（&gt;256 字节才算有效）与单段快路径。
    /// </summary>
    public static class SegmentMerger
    {
        public static string Merge(string ffmpegExe, IReadOnlyList<string> segments, string targetMp4)
        {
            if (segments.Count == 0)
                return "No recording data generated. FFmpeg process might have failed.";

            // 单段快路径：直接 copy 重封装
            if (segments.Count == 1)
            {
                string seg = segments[0];
                if (!string.IsNullOrEmpty(seg) && File.Exists(seg))
                {
                    // ⚠ 必须显式 map：ffmpeg 默认只选"最好的一条音频"，
                    // 分离双轨档的麦克风轨会在重封装时被丢掉。音频用 ? 使无声档不报错。
                    string args = $"-hide_banner -loglevel error -i \"{seg}\" -map 0:v -map 0:a? -c copy -movflags +faststart -y \"{targetMp4}\"";
                    string err = RunAndWait(ffmpegExe, args);
                    if (!string.IsNullOrEmpty(err))
                        return "FFmpeg single segment copy failed.\n\nFFmpeg: " + ffmpegExe + "\nArgs: " + args + "\n\n" + err;
                    return VerifyOutput(targetMp4, "single segment copy");
                }
            }

            string listFile = Path.Combine(Path.GetTempPath(), "list_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                using (var sw = new StreamWriter(listFile, false, new UTF8Encoding(false)))
                {
                    foreach (string seg in segments)
                    {
                        if (string.IsNullOrEmpty(seg) || !File.Exists(seg)) continue;
                        string safe = seg.Replace("\\", "/").Replace("'", "\\'");
                        sw.WriteLine("file '" + safe + "'");
                    }
                }

                string args = $"-hide_banner -loglevel error -f concat -safe 0 -i \"{listFile}\" -map 0:v -map 0:a? -c copy -movflags +faststart -y \"{targetMp4}\"";
                string err = RunAndWait(ffmpegExe, args);
                if (!string.IsNullOrEmpty(err))
                    return "FFmpeg merge failed.\n\nFFmpeg: " + ffmpegExe + "\nArgs: " + args + "\n\n" + err;

                return VerifyOutput(targetMp4, "merge");
            }
            finally
            {
                try { File.Delete(listFile); } catch { }
            }
        }

        private static string VerifyOutput(string target, string what)
        {
            try
            {
                if (!File.Exists(target)) return $"FFmpeg {what} failed.\n\nOutput file was not created.";
                if (new FileInfo(target).Length <= 0) return $"FFmpeg {what} failed.\n\nOutput file is empty.";
            }
            catch { }
            return "";
        }

        /// <summary>阻塞跑到退出；ExitCode==0 返回 ""，否则返回 stderr 摘要。</summary>
        private static string RunAndWait(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                var errLog = new StringBuilder();
                using var p = Process.Start(psi);
                if (p == null) return "Failed to start ffmpeg.";
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) errLog.AppendLine(e.Data); };
                p.BeginErrorReadLine();
                p.OutputDataReceived += (s, e) => { };
                p.BeginOutputReadLine();
                p.WaitForExit();
                if (p.ExitCode == 0) return "";
                return "ExitCode=" + p.ExitCode + "\n--- stderr ---\n" + errLog;
            }
            catch (Exception ex) { return "RunAndWait exception: " + ex.Message; }
        }
    }
}
