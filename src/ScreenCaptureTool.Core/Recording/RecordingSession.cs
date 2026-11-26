using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Drawing;
using ScreenCaptureTool.Platform;
using ScreenCaptureTool.Platform.Audio;

namespace ScreenCaptureTool.Core.Recording
{
    /// <summary>中途异常降级类别（计划 §3.7）。</summary>
    public enum DegradeReason { MicLost, SystemAudioLost, DiskLow, ScreenLocked, CaptureStalled, EngineFallback }

    /// <summary>
    /// 录制会话总编排（计划 §3.1）。零 UI：UI 层（M2 浮层）只订阅事件。
    /// 单 ffmpeg 子进程录视频分段（TS），音频进程内 WASAPI 采集→混音→命名管道喂入；
    /// 暂停=优雅停当前段+起新段，停止=优雅停+concat 合并为 MP4。
    /// </summary>
    public sealed class RecordingSession : IRecordingSession
    {
        private readonly RecordingOptions _opt;
        public RecordEngine Engine => RecordEngine.Ffmpeg;
        private readonly QualityProfile _profile;

        private string _ffmpegExe = "";
        private string _sessionDir = "";
        private Rectangle _monitorRect;      // DDA 用：选区所在屏（虚拟屏绝对坐标）
        private int _ddaOutputIdx = -1;
        private bool _useDda;
        private string _ddaDiag = "";        // 本次为什么走/没走 DDA，进取证日志

        // 收尾自检要用的音频侧计数：必须在 _feed 释放前抓下来
        private (int sys, int mic) _starved;
        private long _pumped;
        private bool _allStarved;

        private readonly List<string> _segments = new();
        private int _segIndex;
        private string _currentSegPath = "";
        private FfmpegProcess? _proc;

        private WasapiDualCapture? _capture;
        private AudioMixerFeed? _feed;

        private readonly object _stateLock = new();
        private bool _isRecording, _isPaused, _isStopping;
        private volatile bool _endingSegment;   // 正在主动收尾分段（管道已给 EOF，ffmpeg 自行退出属预期）
        private long _activeMs;
        private Stopwatch _sw = new();
        private Timer? _ticker;

        private bool _warnedDisk;

        // ---- 事件（后台线程触发，UI 侧自行 marshal） ----
        public event Action<TimeSpan>? ProgressChanged;
        public event Action<bool>? PauseStateChanged;
        public event Action<DegradeReason, string?>? Degraded;
        public event Action<string, TimeSpan>? Completed;   // mp4 路径, 时长
        public event Action<string>? Failed;

        public bool IsRecording { get { lock (_stateLock) return _isRecording; } }
        public bool IsPaused { get { lock (_stateLock) return _isPaused; } }
        public string MasterMp4Path { get; private set; } = "";
        /// <summary>母带画面的<b>物理像素</b>尺寸（Stop 那拍一次探测得到；(0,0)＝探测失败）。
        /// 预览画面卡要按它来摆，不能按选区摆：crop 取偶与"非原画档降到 1080 高等比缩放"两处都会让
        /// 母带尺寸 ≠ 选区尺寸，两个宽高比一不等，Uniform 就留一条露底色的缝（2026-09-24 的"预览白线"）。
        /// 消费方：<c>RecordFlow.ShowPreviewSticker</c> → <c>VideoStickerWindow.SetupPreview</c>。</summary>
        public (int W, int H) MasterSizePx { get; private set; } = (0, 0);

        public RecordingSession(RecordingOptions opt)
        {
            _opt = opt;
            _profile = QualityProfiles.Get(opt.EffectivePresetId);
        }

        /// <summary>开始录制。返回 false 时 error 说明原因（ffmpeg 缺失等）。</summary>
        public bool Start(out string? error)
        {
            error = null;
            string? exe = FfmpegLocator.TryFind(_opt.ManualFfmpegPath);
            if (exe == null) { error = FfmpegLocator.UnavailableMessage(_opt.ManualFfmpegPath); return false; }
            _ffmpegExe = exe;

            _sessionDir = TempSpace.CreateSessionDir();

            // DDA 能力探测（单屏 + 滤镜可用 + 能解析到 output_idx）
            ProbeCapturePath();

            // 音频搭建
            if (!SetupAudio()) return FailStart(out error, "所选录音设备均不可用，请检查设备或关闭录音后重试。" + _capture?.LastStartError);

            // 取证：本次到底走了哪条采集通道、依据是什么。缺这一行时只能把母带搬到命令行反推。
            AppLogger.Info("RecordingSession: 采集=" + (_useDda ? $"DDA(ddagrab) idx={_ddaOutputIdx}" : "GDI(gdigrab)")
                + $" 选区={_opt.Region} 所在屏={_monitorRect.Width}x{_monitorRect.Height}"
                + $" 通道依据={_ddaDiag} 画质={_opt.EffectivePresetId}({_profile.Fps}fps/{_profile.ScaleMaxHeight}p/{_profile.ApproxVideoBitsPerSec / 1000}k) 音频={_opt.Audio}");

            _segIndex = 0;
            _activeMs = 0;
            _sw = Stopwatch.StartNew();
            lock (_stateLock) { _isRecording = true; _isPaused = false; }

            StartSegment();
            _ticker = new Timer(_ => Tick(), null, 250, 250);
            return true;
        }

        private bool FailStart(out string? error, string msg)
        {
            error = msg;
            try { _feed?.Dispose(); _capture?.Dispose(); } catch { }
            _feed = null; _capture = null;
            try { TempSpace.DeleteNow(_sessionDir); } catch { }
            return false;
        }

        private void ProbeCapturePath()
        {
            _useDda = false;
            var region = _options_region;
            if (_opt.CaptureSource == RecordCaptureSource.GdiOnly)
            {
                _ddaDiag = "采集方式=兼容(GDI)，按设置强制老通道";
                return;
            }
            if (!MonitorHelper.IsRegionOnSingleMonitor(region))
            {
                _ddaDiag = "选区跨屏，ddagrab 只能复制单块输出";
                return;
            }
            var mi = MonitorHelper.GetMonitorInfoAtPoint(region.X + region.Width / 2, region.Y + region.Height / 2);
            if (mi == null) { _ddaDiag = "取不到选区所在显示器"; return; }
            _monitorRect = mi.Value.rect;
            int idx = FfmpegProcess.PickDdaOutput(_ffmpegExe, _monitorRect.Width, _monitorRect.Height, out _ddaDiag);
            if (idx < 0) return;
            _ddaOutputIdx = idx;
            _useDda = true;
        }

        private Rectangle _options_region => _opt.Region;

        private bool SetupAudio()
        {
            var a = _opt.Audio;
            if (a == AudioLayout.None) return true;

            bool useSys = a is AudioLayout.SystemOnly or AudioLayout.Mixed or AudioLayout.Split;
            bool useMic = a is AudioLayout.MicOnly or AudioLayout.Mixed or AudioLayout.Split;
            bool split = a == AudioLayout.Split;

            _capture = new WasapiDualCapture();
            if (useSys && !_capture.StartStream(WasapiStreamKind.SystemLoopback))
            {
                useSys = false;
                Degraded?.Invoke(DegradeReason.SystemAudioLost, "系统声音不可用：" + _capture.LastStartError);
            }
            if (useMic && !_capture.StartStream(WasapiStreamKind.Microphone))
            {
                useMic = false;
                Degraded?.Invoke(DegradeReason.MicLost, "麦克风不可用：" + _capture.LastStartError);
            }
            if (!useSys && !useMic)
            {
                // 与 Windows 引擎一致：已选择录音时，不能静默交付无声录制。
                AppLogger.Warn("RecordingSession.SetupAudio: 所选录音设备均不可用。成因: " + _capture.LastStartError);
                return false;
            }

            _feed = new AudioMixerFeed(_capture, useSys, useMic, _opt.MicVolume01, split);
            _feed.ChannelLost += OnChannelLost;
            AppLogger.Info($"RecordingSession: 音频 布局={_opt.Audio} 系统路={useSys}"
                + $"({_capture.SystemRate}Hz/{_capture.SystemChannels}ch) 麦克风={useMic}"
                + $"({_capture.MicRate}Hz/{_capture.MicChannels}ch) 系数={_opt.MicVolume01:F2}"
                + (_capture.LastStartError == null ? "" : " 起不来的路=" + _capture.LastStartError));
            return true;
        }

        private void OnChannelLost(WasapiStreamKind kind)
        {
            if (kind == WasapiStreamKind.Microphone)
                Degraded?.Invoke(DegradeReason.MicLost, "麦克风已断开 · 继续以系统声音单路录制");
            else
                Degraded?.Invoke(DegradeReason.SystemAudioLost, "系统声音通道失效 · 继续以麦克风单路录制");
        }

        // ================= 分段生命周期 =================

        private void StartSegment()
        {
            string segPath = Path.Combine(_sessionDir, $"Seg_{_segIndex:D4}.ts");
            string audioIn = "", audioMap = "-an";
            if (_feed != null)
            {
                // ⚠ 必须显式压掉探测：ffmpeg 默认 analyzeduration=5s / probesize=5MB，对实时管道输入
                // 它会先"攒够再走"，表现为输出 .ts 迟迟不创建；这段时间内暂停或停止等于把一个
                // 还没开始写盘（0 字节）的分段直接丢掉。这两个是输入级选项，必须写在 -i 之前。
                const string probeOff = "-analyzeduration 0 -probesize 32";
                // 🔴 `-shortest` 是"成片末尾约 1 秒无声"的正解，别再摘掉：收尾是"先关音频管道造 EOF、再往
                // stdin 送 'q'"，而 'q' 从送出到 ffmpeg 真停住抓屏有实测 198ms（暂停档最长 540ms）的空窗——
                // 这段时间音频已经没有了，画面却还在录，于是每一段都长出一条"有画无声"的尾巴（三段录制累出
                // 约 0.9 秒，就是用户听到的"末尾没声音"）。加上 -shortest 后 muxer 在音频 EOF 那刻收尾，
                // 视频轨与音频轨同点结束（实验台实测差 −60ms＝末帧取整，听不出来）。
                // 只在有音频输入时加：无声档（-an）唯一的输入是永不 EOF 的实时抓屏，加了等于没加。
                const string shortest = " -shortest";
                string mainPath = _feed.StartNewSegment();
                if (_opt.Audio == AudioLayout.Split)
                {
                    string pSys = _feed.PipePathSystem, pMic = _feed.PipePathMic;
                    audioIn = $"{probeOff} -f f32le -ar 48000 -ac 2 -i \"{pSys}\" {probeOff} -f f32le -ar 48000 -ac 2 -i \"{pMic}\"";
                    audioMap = "-map 0:v:0 -map 1:a:0 -map 2:a:0 -c:a aac -b:a 192k" + shortest;
                }
                else
                {
                    audioIn = $"{probeOff} -f f32le -ar 48000 -ac 2 -i \"{mainPath}\"";
                    audioMap = "-map 0:v:0 -map 1:a:0 -c:a aac -b:a 192k" + shortest;
                }
            }

            string args = BuildArgs(segPath, audioIn, audioMap);
            _currentSegPath = segPath;
            bool dda = _useDda;
            AppLogger.Info($"RecordingSession: 段{_segIndex} 起进程：{_ffmpegExe} {args}");
            _proc = FfmpegProcess.Start(_ffmpegExe, args, dda);
            _proc.UnexpectedExited += immediate => OnUnexpectedExit(dda, immediate);
        }

        private string BuildArgs(string segOut, string audioIn, string audioMap)
        {
            var region = _opt.Region;
            int rw = region.Width, rh = region.Height;
            if (rw % 2 != 0) rw--; if (rh % 2 != 0) rh--;
            if (rw < 2) rw = 2; if (rh < 2) rh = 2;
            string dm = _opt.DrawCursor ? "1" : "0";
            int fps = _profile.Fps;
            string input, vf;

            if (_useDda)
            {
                int relX = region.X - _monitorRect.X, relY = region.Y - _monitorRect.Y;
                if (relX < 0) relX = 0; if (relY < 0) relY = 0;
                int cw = rw, ch = rh;
                if (relX + cw > _monitorRect.Width) cw = _monitorRect.Width - relX;
                if (relY + ch > _monitorRect.Height) ch = _monitorRect.Height - relY;
                if (cw % 2 != 0) cw--; if (ch % 2 != 0) ch--;
                var (sw, sh) = QualityProfiles.ScaledDims(_profile, cw, ch);
                string scale = (sw != cw || sh != ch) ? $",scale={sw}:{sh}" : "";
                vf = $"hwdownload,format=bgra,crop={cw}:{ch}:{relX}:{relY},setpts=PTS-STARTPTS{scale},format=yuv420p";
                input = $"-f lavfi -i \"ddagrab=framerate={fps}:draw_mouse={dm}:output_idx={_ddaOutputIdx}\"";
            }
            else
            {
                var (sw, sh) = QualityProfiles.ScaledDims(_profile, rw, rh);
                string scale = (sw != rw || sh != rh) ? $",scale={sw}:{sh}" : "";
                vf = $"setpts=PTS-STARTPTS{scale},format=yuv420p";
                // gdigrab 已知尺寸和帧率，默认 5MB 探测会先攒视频再打开音频管道，
                // 每次开录／恢复都让系统声时间轴晚近一秒，-shortest 随后把整段截短。
                input = $"-analyzeduration 0 -probesize 32 -f gdigrab -framerate {fps} -draw_mouse {dm} -offset_x {region.X} -offset_y {region.Y} -video_size {rw}x{rh} -i desktop";
            }

            return $"-hide_banner -loglevel error -y {input} {audioIn} -vf \"{vf}\" " +
                   $"-c:v libx264 -r {fps} -preset ultrafast -tune zerolatency -pix_fmt yuv420p -color_range pc {_profile.BitrateArgs} " +
                   $"{audioMap} -f mpegts \"{segOut}\"";
        }

        private void OnUnexpectedExit(bool wasDda, bool immediate)
        {
            lock (_stateLock) { if (_isStopping || !_isRecording) return; }
            if (_endingSegment) return;   // 收尾分段时管道给了 EOF，ffmpeg 自行退出属预期
            if (wasDda && immediate)
            {
                // DDA 起进程即崩 → 回落 GDI，重建同一段（不推进 segIndex、不收录碎段）
                _useDda = false;
                try { _proc?.Dispose(); } catch { }
                _proc = null;
                StartSegment();
                return;
            }
            // 其余异常：整场失败
            string stderrTail = _proc?.StderrTail ?? "";
            try { _proc?.Dispose(); } catch { }
            _proc = null;
            _ticker?.Dispose(); _ticker = null;
            try { _feed?.Dispose(); _capture?.Dispose(); } catch { }
            _feed = null; _capture = null;
            lock (_stateLock) { _isRecording = false; }
            Failed?.Invoke("录制进程异常退出。若为 DDA 采集失败已尝试自动回落，仍失败请改用区域尽量落在单块屏幕。" +
                           (stderrTail.Length > 0 ? "\n" + stderrTail : ""));
        }

        // ================= 计时 + 磁盘看门狗 =================

        private void Tick()
        {
            var el = Elapsed();
            ProgressChanged?.Invoke(el);

            // 磁盘将满预警/止损（§3.7 ③）
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(_sessionDir) ?? "C:\\");
                long free = drive.AvailableFreeSpace;
                long needBytes = _profile.ApproxVideoBitsPerSec / 8;
                if (free < needBytes * 15) // 约剩 15 秒：真满临界，自动优雅停止保留已完成分段
                {
                    _ticker?.Change(Timeout.Infinite, Timeout.Infinite);
                    Degraded?.Invoke(DegradeReason.DiskLow, "磁盘空间不足 · 已自动完成并保留前段");
                    Stop();
                }
                else if (!_warnedDisk && free < needBytes * 60)
                {
                    _warnedDisk = true;
                    Degraded?.Invoke(DegradeReason.DiskLow, "磁盘空间偏低，即将自动收尾");
                }
            }
            catch { }
        }

        public TimeSpan Elapsed()
        {
            lock (_stateLock)
            {
                if (!_isRecording) return TimeSpan.Zero;
                long ms = _activeMs + (_isPaused ? 0 : _sw.ElapsedMilliseconds);
                return TimeSpan.FromMilliseconds(ms);
            }
        }

        // ================= 暂停 / 恢复 / 停止 / 放弃 =================

        public void Pause()
        {
            lock (_stateLock) { if (!_isRecording || _isPaused || _isStopping) return; _isPaused = true; _activeMs += _sw.ElapsedMilliseconds; _sw.Stop(); }
            _feed?.Pause();
            GracefulEndSegment();
            PauseStateChanged?.Invoke(true);
        }

        public void Resume()
        {
            lock (_stateLock) { if (!_isRecording || !_isPaused || _isStopping) return; }
            _segIndex++;
            StartSegment();
            _feed?.Resume();
            _sw.Restart();
            lock (_stateLock) _isPaused = false;
            PauseStateChanged?.Invoke(false);
        }

        /// <summary>锁屏/会话切换触发（M4）：等同用户暂停，不新增 UI 语义。</summary>
        public void AutoPause()
        {
            if (!IsPaused) { Degraded?.Invoke(DegradeReason.ScreenLocked, "检测到锁屏 · 已自动暂停"); Pause(); }
        }

        private void GracefulEndSegment()
        {
            // 先关闭本段音频管道给 ffmpeg 制造 EOF：它此刻很可能正阻塞在管道读上，
            // 那状态下 stdin 送 'q' 根本没人处理，等待超时后只能强杀 → 分段缓冲未冲刷＝0 字节被丢弃。
            _endingSegment = true;
            try { _feed?.EndSegment(); } catch { }
            var p = _proc; _proc = null;
            if (p != null)
            {
                p.GracefulStop(600);
                p.Dispose();
            }
            _endingSegment = false;
            AddCurrentSegmentIfValid();
        }

        private void AddCurrentSegmentIfValid()
        {
            try
            {
                if (!string.IsNullOrEmpty(_currentSegPath) && File.Exists(_currentSegPath)
                    && new FileInfo(_currentSegPath).Length > 256)
                    _segments.Add(_currentSegPath);
            }
            catch { }
            _currentSegPath = "";
        }

        /// <summary>优雅停止并合并为 MP4，成功触发 Completed。</summary>
        public void Stop()
        {
            lock (_stateLock) { if (!_isRecording || _isStopping) return; _isStopping = true; }
            _ticker?.Dispose(); _ticker = null;

            GracefulEndSegment();
            _starved = _feed?.StarveTicks ?? (0, 0);
            _pumped = _feed?.PumpTicks ?? 0;
            _allStarved = _feed?.AllStarved ?? false;
            try { _feed?.Dispose(); _capture?.Dispose(); } catch { }
            _feed = null; _capture = null;
            lock (_stateLock) { _isRecording = false; _isPaused = false; }

            string master = Path.Combine(_sessionDir, "Rec_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".mp4");
            string err = SegmentMerger.Merge(_ffmpegExe, _segments, master);
            if (!string.IsNullOrEmpty(err))
            {
                MasterMp4Path = "";
                Failed?.Invoke(err);
                return;
            }
            MasterMp4Path = master;
            // 时长与画面尺寸同源同一次探测（这条跑在 Stop 的后台拍上，不上 UI 线程）：
            // 尺寸给预览摆卡用，见 MasterSizePx 注释。
            var info = FfmpegProcess.ProbeMasterInfo(_ffmpegExe, master);
            MasterSizePx = (info.Width, info.Height);
            Completed?.Invoke(master, TimeSpan.FromSeconds(info.Duration));

            // 录后取证自检（整片视频全帧差值统计）耗时与视频长度成正比（50s 约需 10s），
            // 严禁阻塞母带交付与预览弹出，放入后台异步静默取证。
            _ = Task.Run(() =>
            {
                try { SelfCheckAndLog(master); } catch { }
            });
            // 成功后由上层决定保存/放弃；此处不删目录（保存成功→上层调 TempSpace.DeleteDelayed）。
        }

        /// <summary>
        /// 录完自检：把"到底有没有录到画面变化""音频是不是被填成静音"写进取证日志；
        /// 整片静止时明确告知用户。以前这里会默默交付一个死文件，用户只能靠"看着不动"反推，
        /// 而日志里连本次走的是哪条采集通道都没记（2026-09-24 的排查就卡在这）。
        /// </summary>
        private void SelfCheckAndLog(string master)
        {
            var (motion, total, peak) = FfmpegProcess.ProbeMotionFrames(_ffmpegExe, master);
            // ⚠ "没测到帧"与"测到了、画面是静的"是两件事，混成一句话就会拿探测故障去吓人（2026-09-24
            // 就交付过一次：滤镜语法错导致恒 0 帧，而判据 total>1 让"冻结"提示永远弹不出来）。
            bool probeFailed = motion < 0 || total <= 1;
            string motionText = motion < 0 ? "未完成（解码超时）"
                : total <= 1 ? $"未取到可比对的帧（total={total}，探测失败）"
                : $"{motion}/{total} 帧有变化，峰值差 {peak:F1}/255";
            AppLogger.Info($"RecordingSession: 自检 采集={(_useDda ? "DDA" : "GDI")} 画面[{motionText}] "
                + $"尺寸={MasterSizePx.W}x{MasterSizePx.H} "
                + $"音频 写出拍={_pumped} 读空拍 系统={_starved.sys} 麦克风={_starved.mic} 母带=" + master);

            bool stalled = !probeFailed && motion == 0;
            bool silent = _opt.Audio != AudioLayout.None && _allStarved;
            if (!stalled && !silent) return;

            string what = stalled
                ? "本次未捕获到任何画面变化（成品是一帧静止）· 采集通道 " + (_useDda ? "DDA" : "GDI")
                : "本次音轨为纯静音（两路都没供上数）";
            Degraded?.Invoke(DegradeReason.CaptureStalled, what + " · 成因见日志");
        }

        /// <summary>放弃：强杀当前进程 + 立即删整目录。</summary>
        public void Abort()
        {
            lock (_stateLock) { if (_isStopping) return; _isStopping = true; _isRecording = false; }
            _ticker?.Dispose(); _ticker = null;
            try { _proc?.KillFast(); _proc?.Dispose(); } catch { }
            _proc = null;
            try { _feed?.Dispose(); _capture?.Dispose(); } catch { }
            _feed = null; _capture = null;
            TempSpace.DeleteNow(_sessionDir);
        }

        public void Dispose()
        {
            if (IsRecording) Abort();
        }
    }
}
