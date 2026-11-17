using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ScreenCaptureTool.Platform;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>
    /// 单个 ffmpeg 录制子进程的生命周期封装（计划 §3.1，逐段移植参考工程 RecorderHelper）。
    /// 负责：启动、优雅停（stdin 送 'q' 冲刷 moov）、强杀（放弃场景）、stderr 尾部留存、退出事件（含预期退出屏蔽）。
    /// 进程挂到进程级共享 Job Object（KILL_ON_JOB_CLOSE），yuSnip 崩溃时 ffmpeg 必亡。
    /// </summary>
    public sealed class FfmpegProcess : IDisposable
    {
        private static readonly object JobLock = new();
        private static IntPtr _job = IntPtr.Zero;

        private readonly Process _proc;
        private int _expectedExitId;
        private readonly StringBuilder _stderr = new();
        private readonly object _stderrLock = new();
        private DateTime _startTime;

        /// <summary>异常退出（非预期、且处于录制中）时触发；参数 = 是否启动后 2.5s 内即刻崩溃。</summary>
        public event Action<bool>? UnexpectedExited;

        public string ExePath { get; }
        public string Args { get; }
        public bool IsDdaAttempt { get; }
        public string StderrTail { get { lock (_stderrLock) return _stderr.ToString(); } }

        private FfmpegProcess(Process proc, string exe, string args, bool isDda)
        {
            _proc = proc; ExePath = exe; Args = args; IsDdaAttempt = isDda;
        }

        /// <summary>启动一个 ffmpeg 录制进程。启动失败抛异常由上层处理。</summary>
        public static FfmpegProcess Start(string exe, string args, bool isDdaAttempt)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var self = new FfmpegProcess(p, exe, args, isDdaAttempt);

            p.Exited += (s, e) =>
            {
                try { if (p.Id == self._expectedExitId) return; } catch { }
                bool immediate = (DateTime.UtcNow - self._startTime).TotalSeconds < 2.5;
                try { self.UnexpectedExited?.Invoke(immediate); } catch { }
            };
            p.ErrorDataReceived += (s, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (self._stderrLock)
                {
                    self._stderr.AppendLine(e.Data);
                    if (self._stderr.Length > 12000) self._stderr.Remove(0, self._stderr.Length - 12000);
                }
            };

            self._startTime = DateTime.UtcNow;
            p.Start();
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();

            try { AttachToJob(p); } catch { }

            return self;
        }

        /// <summary>优雅停止：stdin 送 'q' 冲刷并正常收尾（写 moov index）。等待 waitMs 未果则强杀。</summary>
        public void GracefulStop(int waitMs)
        {
            try { _expectedExitId = _proc.Id; } catch { }
            try
            {
                if (!_proc.HasExited)
                {
                    try { _proc.StandardInput.WriteLine("q"); } catch { }
                    try { _proc.StandardInput.Close(); } catch { }
                    if (!_proc.WaitForExit(waitMs))
                    {
                        try { _proc.Kill(); } catch { }
                        try { _proc.WaitForExit(200); } catch { }
                    }
                }
            }
            catch { try { _proc.Kill(); } catch { } }
        }

        /// <summary>直接强杀（仅用于放弃场景）。DDA 句柄不果断杀会拖死系统（参考工程 :6411 注释）。</summary>
        public void KillFast()
        {
            try { _expectedExitId = _proc.Id; } catch { }
            try { if (!_proc.HasExited) _proc.Kill(); } catch { }
        }

        /// <summary>把子进程挂进"父进程一没就跟着没"的 Job Object。预览播放的供帧/解码子进程也走这里，不留孤儿。</summary>
        internal static void AttachToJob(Process p)
        {
            lock (JobLock)
            {
                if (_job == IntPtr.Zero)
                {
                    _job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
                    if (_job == IntPtr.Zero) return;
                    var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                    {
                        BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                        {
                            LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        }
                    };
                    NativeMethods.SetInformationJobObject(_job, NativeMethods.JobObjectExtendedLimitInformation,
                        ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
                }
                try { NativeMethods.AssignProcessToJobObject(_job, p.Handle); } catch { }
            }
        }

        public void Dispose()
        {
            try { KillFast(); } catch { }
            try { _proc.Dispose(); } catch { }
        }

        // ================= 静态探测工具（无长生命周期进程） =================

        /// <summary>跑一条一次性命令，true=在 timeoutMs 内退出。回填 stderr。</summary>
        public static bool RunProbe(string exe, string args, out string stderr, int timeoutMs = 4000)
        {
            stderr = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                string so = p.StandardOutput.ReadToEnd();
                string se = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }
                stderr = string.IsNullOrEmpty(se) ? so : se;
                return true;
            }
            catch { return false; }
        }

        /// <summary>一枚 ddagrab 输出：ffmpeg 侧的 output_idx 与该输出的像素尺寸。</summary>
        public readonly record struct DdaOutput(int Idx, int Width, int Height);

        private static readonly object DdaLock = new();
        private static List<DdaOutput>? _ddaCache;
        private static string? _ddaCacheExe;
        private static DateTime _ddaCacheAt;

        /// <summary>
        /// 枚举本机 ddagrab 打得开的输出（<c>output_idx</c> → 像素尺寸），结果按 ffmpeg 路径缓存。
        /// <para>⚠ 只能"直接问 ffmpeg"来定序号：ddagrab 在 ffmpeg 5.0 之后是 <b>lavfi 滤镜</b>而不是
        /// 输入设备，<c>-f ddagrab -list_devices true -i dummy</c> 这种写法在 7.1 上恒报
        /// <c>Unknown input format: 'ddagrab'</c>（2026-09-24 实测），照它解析就永远拿不到序号、
        /// 每次录制都掉回 gdigrab。这里改用 <c>-f lavfi -i ddagrab=output_idx=N</c> 逐号试开一帧，
        /// 读它自己打的 <c>Opened dxgi output N with dimensions WxH</c>。
        /// 序号是"ffmpeg 的 D3D11 设备所在那块显卡"的输出索引、从 0 连续，故遇到第一个失败即停。</para>
        /// </summary>
        public static List<DdaOutput> DdaOutputs(string exe, bool force = false)
        {
            lock (DdaLock)
            {
                if (!force && _ddaCache != null && _ddaCacheExe == exe &&
                    DateTime.UtcNow - _ddaCacheAt < TimeSpan.FromMinutes(10))
                    return _ddaCache;
            }

            var list = new List<DdaOutput>();
            for (int idx = 0; idx < 8; idx++)
            {
                string args = "-hide_banner -loglevel verbose -f lavfi -i \"ddagrab=output_idx=" + idx +
                              "\" -frames:v 1 -f null NUL";
                // ⚠ 必须走带硬超时＋强杀的 RunCollectStderr：这条探测是在"开始录制"那一拍上同步跑的，
                // RunProbe 那种 ReadToEnd 串读一旦碰上 ddagrab 不退出，就会把整次录制卡死在这里。
                if (!RunCollectStderr(exe, args, 3000, out string stderr)) break;   // 打不开＝枚举到头
                var m = Regex.Match(stderr ?? "", @"Opened dxgi output \d+ with dimensions (\d+)x(\d+)");
                if (!m.Success) break;
                list.Add(new DdaOutput(idx, int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value)));
            }

            lock (DdaLock) { _ddaCache = list; _ddaCacheExe = exe; _ddaCacheAt = DateTime.UtcNow; }
            return list;
        }

        /// <summary>清 ddagrab 输出缓存（显示器插拔、手动换 ffmpeg 路径后调用）。</summary>
        public static void InvalidateDdaCache()
        {
            lock (DdaLock) { _ddaCache = null; _ddaCacheExe = null; }
        }

        /// <summary>
        /// 按目标显示器的像素尺寸挑唯一的 ddagrab 序号。返回 -1 时 <paramref name="diag"/> 说明原因
        /// （没匹配上／多块同尺寸无法唯一确定），调用方据此回落 gdigrab 并把它写进取证日志。
        /// </summary>
        public static int PickDdaOutput(string exe, int width, int height, out string diag)
        {
            var outs = DdaOutputs(exe);
            if (outs.Count == 0) { diag = "ddagrab 一个输出都打不开"; return -1; }

            string table = string.Join(", ", outs.ConvertAll(o => $"{o.Idx}={o.Width}x{o.Height}"));
            int hit = -1, hits = 0;
            foreach (var o in outs)
                if (o.Width == width && o.Height == height) { hit = o.Idx; hits++; }

            diag = hits switch
            {
                1 => $"选区屏 {width}x{height} → output_idx={hit}（候选 {table}）",
                0 => $"没有 {width}x{height} 的输出（候选 {table}）",
                _ => $"{hits} 块输出同为 {width}x{height}，按尺寸无法唯一确定（候选 {table}）",
            };
            return hits == 1 ? hit : -1;
        }

        /// <summary>
        /// 跑一条一次性命令并全量收集 stderr，超时即强杀。与 <see cref="RunProbe"/> 的区别是
        /// 这里两条输出流都异步排空，所以长时间运行的解码/自检命令也不会把管道堵死。
        /// </summary>
        public static bool RunCollectStderr(string exe, string args, int timeoutMs, out string stderr)
        {
            stderr = "";
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardError = true, RedirectStandardOutput = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                var sb = new StringBuilder();
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                p.OutputDataReceived += (_, _) => { };
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return false; }
                lock (sb) stderr = sb.ToString();
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 母带画面自检：统计"相邻帧确实变了"的帧数。判据用 <c>tblend=difference</c> 之后整帧的
        /// 平均绝对差 YAVG，阈值 2/255（2026-09-24 用他当天录的 5 段母带实测：整段静止的两段峰值
        /// 0.33 / 0.34，确有动的三段是 5.8 / 6.5 / 9.7，>2 的帧数 27~125 ⇒ 2 落在两群之间且两侧余量最大）。
        /// ⚠ 不要用"按固定间隔抽帧再去重"：±1 灰阶抖动会被数成"很多不同帧"，而抽帧又可能全踩在同一
        /// 相位上误判成"完全相同"（2026-09-24 两个方向都踩过）。
        /// <para>🔴 两处踩过坑的语法，改回来自检就静默失效：<c>metadata</c> 滤镜的打印档是
        /// <c>mode=print</c>，写成 <c>metadata=print</c> 会被 ffmpeg 7.1 直接拒（<c>Error applying option
        /// 'print'</c>）⇒ 一条 YAVG 都取不到、恒返回 (0,0,0)，而调用方只看 motion/total 的话现象就是
        /// "自检永远说没测到"。其二，这些行是**按 av_log 走 stderr 的 INFO 级**，所以本命令绝不能带
        /// <c>-loglevel error</c>（带了就一行不剩），也不能改用只收 stdout 的收集器。</para>
        /// </summary>
        public static (int motionFrames, int totalFrames, double peak) ProbeMotionFrames(string exe, string path, int timeoutMs = 20000)
        {
            string args = "-hide_banner -i \"" + path + "\" -vf "
                + "\"format=gray,tblend=all_mode=difference,signalstats,metadata=mode=print:key=lavfi.signalstats.YAVG\" -f null NUL";
            if (!RunCollectStderr(exe, args, timeoutMs, out string stderr)) return (-1, 0, 0);

            int motion = 0, total = 0; double peak = 0;
            foreach (Match m in Regex.Matches(stderr, @"YAVG=([0-9.]+)"))
            {
                double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                total++;
                if (total == 1) continue;   // 首帧的"上一帧"是空的，差值＝整帧本身，必须丢
                if (v > peak) peak = v;
                if (v > MotionYavgThreshold) motion++;
            }
            return (motion, total, peak);
        }

        /// <summary>YAVG 判"这一帧与上一帧不同"的门限，取值依据见 <see cref="ProbeMotionFrames"/> 的注释。
        /// 单独命名是为了让 <see cref="ProbeMotionFrames"/> 与调用方对"0 帧到底算不算冻结"用同一个数。</summary>
        public const double MotionYavgThreshold = 2.0;

        /// <summary>母带一次探测出来的三项事实（时长与画面物理尺寸）。</summary>
        public readonly record struct MasterInfo(double Duration, int Width, int Height);

        /// <summary>读取媒体时长（秒）。参考 :13978-14006。</summary>
        public static double ProbeDurationSeconds(string exe, string path) => ProbeMasterInfo(exe, path).Duration;

        /// <summary>
        /// 一次 <c>-hide_banner -i</c> 同时读出时长与**画面物理尺寸**（正则取 stderr 的
        /// <c>Duration: HH:MM:SS.xx</c> 与 <c>Stream #0:x … Video: … WxH</c>）。
        /// <para>为什么要尺寸：预览画面卡的尺寸以前按"录的那个框"算，而母带是录制侧另算的一套
        /// （crop 取偶、非原画档降到 1080 高等比缩放）⇒ 两个宽高比不等，Uniform 必然留一条 letterbox
        /// 缝，露出卡的浅底色 #FAFAFA，现象即"预览下面一条白线、有时在上面"（2026-09-24）。
        /// 尺寸与时长同源同一次子进程：探测跑在 Stop() 那拍（后台线程），不上 UI 线程、也不多起进程。</para>
        /// <para>探测失败（文件读不出）返回 (0, 0, 0)，调用方各自兜底：尺寸 0 ⇒ 预览回落到选区算式。</para>
        /// </summary>
        public static MasterInfo ProbeMasterInfo(string exe, string path)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, "-hide_banner -i \"" + path + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p == null) return new MasterInfo(0, 0, 0);
                string output = p.StandardError.ReadToEnd();
                p.WaitForExit();

                double dur = 0;
                var m = Regex.Match(output, @"Duration: (\d{2}):(\d{2}):(\d{2}\.\d{2})");
                if (m.Success)
                {
                    double h = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    double mm = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    double s = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                    dur = h * 3600 + mm * 60 + s;
                }
                // 视频流那行形如：Stream #0:0(und): Video: h264 (…), 1606x1080 [SAR 3753:3754 DAR …], 24 fps
                // 只认"Video:" 之后第一个 WxH，避免把后面的 SAR/DAR 比值当成尺寸。
                var v = Regex.Match(output, @"Video:[^\r\n]*?\b(\d{2,5})x(\d{2,5})\b");
                int w = v.Success ? int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                int hh = v.Success ? int.Parse(v.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                return new MasterInfo(dur, w, hh);
            }
            catch { return new MasterInfo(0, 0, 0); }
        }
    }
}
