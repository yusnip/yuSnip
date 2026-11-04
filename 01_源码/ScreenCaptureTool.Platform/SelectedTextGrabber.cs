using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ScreenCaptureTool.Platform
{
    /// <summary>
    /// 「选中文本贴图」的抓词状态。上层（App.HandleQuickSticker）据此决定落哪个分支。
    /// </summary>
    public enum SelectedTextGrabStatus
    {
        /// <summary>抓到新文本：已按下 Ctrl+C、剪贴板序列号确实变了、且新内容含 CF_UNICODETEXT。</summary>
        GrabbedText,

        /// <summary>Ctrl+C 之后序列号没变（没有选中内容，或目标不支持复制）。上层按开关无关的老逻辑继续。</summary>
        NothingChanged,

        /// <summary>序列号变了但新内容不是文本（例如在资源管理器里复制了文件）。<b>禁止还原</b>。</summary>
        ChangedNotText,

        /// <summary>前台窗口不该收到 Ctrl+C：自家窗口 / 桌面壳 / 任务栏。</summary>
        BlockedForeground,

        /// <summary>前台进程权限更高（UIPI 会静默丢掉注入的按键），需提示用户改用「管理员模式」。</summary>
        BlockedElevation,

        /// <summary>注入或剪贴板操作异常。</summary>
        Failed,
    }

    /// <summary><see cref="SelectedTextGrabber.Grab"/> 的返回值。</summary>
    public sealed class SelectedTextGrabResult
    {
        public SelectedTextGrabStatus Status { get; set; } = SelectedTextGrabStatus.Failed;

        /// <summary>抓到的纯文本（仅 <see cref="SelectedTextGrabStatus.GrabbedText"/> 非空）。</summary>
        public string? Text { get; set; }

        /// <summary>抓到的 HTML（可空；用于富文本贴图保留逐 run 颜色）。</summary>
        public string? Html { get; set; }

        /// <summary>原剪贴板是否已还原成功。失败时保持用户新复制的内容（不做破坏性动作）。</summary>
        public bool ClipboardRestored { get; set; }

        /// <summary>给用户看的一句话原因，可空。仅 <see cref="SelectedTextGrabStatus.BlockedElevation"/> 会填。</summary>
        public string? UserHint { get; set; }

        /// <summary>诊断详情（写日志用，不直接展示给用户）。</summary>
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// 「选中文本贴图」取词器：给前台窗口注入一次 Ctrl+C，读回它复制的文本，然后把用户原来的剪贴板还原。
    ///
    /// 三条不变量（改动前务必读，详见 docs/protection.md）：
    /// 1. <b>判据用剪贴板序列号，不用文本比对。</b>文本比对无法区分「重新复制了同一段」与「什么都没发生」，
    ///    只有 <c>GetClipboardSequenceNumber</c> 能给出确定答案。
    /// 2. <b>快照含句柄型格式时禁止还原。</b>半还原（只剩部分格式）比留着新内容破坏性更大。
    /// 3. <b>读新文本必须发生在还原之前。</b>先还原再读，读回来的是用户原来的内容——图文错配。
    ///
    /// 本类不引用 System.Drawing / Avalonia，也不记日志（Platform 层约定），结果一律通过返回值上抛。
    /// <see cref="Grab"/> 内部有最长 <see cref="PollTotalMs"/> 毫秒的轮询等待，
    /// <b>必须在后台线程调用</b>（<c>Task.Run</c>），不得在 UI 线程直接调。
    /// </summary>
    public static class SelectedTextGrabber
    {
        private const int PollTotalMs = 250;
        private const int PollStepMs = 15;

        /// <summary>不该收到 Ctrl+C 的窗口类名：桌面、任务栏。给它们发按键只会惊动 explorer。</summary>
        private static readonly string[] ShellClassNames =
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        };

        private static bool? _selfElevated;

        /// <summary>
        /// 执行一次抓词。<b>只能从后台线程调用。</b>任何异常都被吞掉并折算成
        /// <see cref="SelectedTextGrabStatus.Failed"/>，调用方永远拿到一个可用的返回值。
        /// </summary>
        public static SelectedTextGrabResult Grab()
        {
            var result = new SelectedTextGrabResult();

            try
            {
                // ---------- 1) 前台窗口甄别：不该注入就不注入 ----------
                IntPtr hwnd = NativeMethods.GetForegroundWindow();
                uint pid = 0;
                if (hwnd != IntPtr.Zero) NativeMethods.GetWindowThreadProcessId(hwnd, out pid);

                if (hwnd == IntPtr.Zero || pid == 0)
                {
                    result.Status = SelectedTextGrabStatus.BlockedForeground;
                    result.Detail = "无有效前台窗口";
                    return result;
                }
                if (pid == (uint)Environment.ProcessId)
                {
                    // 自家托盘浮层 / 贴图 / 设置窗口：给自己发 Ctrl+C 没有意义
                    result.Status = SelectedTextGrabStatus.BlockedForeground;
                    result.Detail = "前台是本程序窗口";
                    return result;
                }

                string className = WindowHelper.GetWindowClassName(hwnd);
                if (Array.IndexOf(ShellClassNames, className) >= 0)
                {
                    result.Status = SelectedTextGrabStatus.BlockedForeground;
                    result.Detail = "前台是桌面/任务栏：" + className;
                    return result;
                }

                bool? targetElevated = IsProcessElevated(pid);
                if (targetElevated == true && SelfElevated() == false)
                {
                    // UIPI：低完整性/未提权进程给已提权窗口注入按键会被系统静默丢弃，
                    // 与其等 250ms 拿到 NothingChanged，不如直接说明原因。
                    result.Status = SelectedTextGrabStatus.BlockedElevation;
                    result.UserHint = "目标窗口以管理员权限运行，请在设置里开启「管理员模式」后再用";
                    result.Detail = "前台进程已提权（pid=" + pid + "，class=" + className + "），本进程未提权";
                    return result;
                }

                // ---------- 2) 快照原剪贴板 + 记录序列号基线 ----------
                uint seq0 = NativeMethods.GetClipboardSequenceNumber();
                Win32Clipboard.ClipboardSnapshot snapshot = Win32Clipboard.CaptureAllFormats();
                uint seqAfterCapture = NativeMethods.GetClipboardSequenceNumber();
                if (!snapshot.Captured)
                {
                    result.Status = SelectedTextGrabStatus.Failed;
                    result.Detail = "快照失败：" + (snapshot.Failure ?? "未知");
                    return result;
                }
                if (seqAfterCapture != seq0)
                {
                    // 快照期间别人改了剪贴板 → 手里这份已经过期，还原会造成回滚事故
                    snapshot.CanRestore = false;
                    snapshot.Failure = "快照期间剪贴板被其他程序改写";
                }
                uint seq = seqAfterCapture;

                // ---------- 3) 注入一次 Ctrl+C ----------
                string? injectError = TrySendCtrlC();
                if (injectError != null)
                {
                    result.Status = SelectedTextGrabStatus.Failed;
                    result.Detail = injectError;
                    return result;
                }

                // ---------- 4) 轮询序列号 ----------
                for (int waited = 0; seq == seq0 && waited < PollTotalMs; waited += PollStepMs)
                {
                    Thread.Sleep(PollStepMs);
                    seq = NativeMethods.GetClipboardSequenceNumber();
                }

                if (seq == seq0)
                {
                    result.Status = SelectedTextGrabStatus.NothingChanged;
                    result.Detail = PollTotalMs + "ms 内剪贴板未变化（无选区或目标不支持复制）";
                    // 剪贴板没动过，不需要还原
                    return result;
                }

                // ---------- 5) 还原之前先读走新内容 ----------
                string? text = Win32Clipboard.TryGetText();
                string? html = Win32Clipboard.TryGetHtml();

                if (string.IsNullOrEmpty(text))
                {
                    // 复制的是文件/图片之类：不还原，让上层「图片文件优先」分支照旧工作
                    result.Status = SelectedTextGrabStatus.ChangedNotText;
                    result.Detail = "剪贴板已变但新内容无文本，保持原样不还原";
                    return result;
                }

                result.Text = text;
                result.Html = html;
                result.Status = SelectedTextGrabStatus.GrabbedText;

                // ---------- 6) 还原原剪贴板（仅当快照可完整还原） ----------
                if (!snapshot.CanRestore || snapshot.Entries.Count == 0)
                {
                    result.Detail = "抓到文本 " + text.Length + " 字；剪贴板未还原（"
                                    + (snapshot.Failure ?? "快照不完整") + "）";
                    return result;
                }

                try
                {
                    Win32Clipboard.SetClipboardFormats(snapshot.Entries);
                    result.ClipboardRestored = true;
                    result.Detail = "抓到文本 " + text.Length + " 字；原剪贴板已还原（"
                                    + snapshot.Entries.Count + " 个格式）";
                }
                catch (Exception ex)
                {
                    // 还原失败只是留下用户刚复制的内容，不破坏数据 → 不提示，贴图照常出
                    result.Detail = "抓到文本 " + text.Length + " 字；还原失败：" + ex.Message;
                }
                return result;
            }
            catch (Exception ex)
            {
                result.Status = SelectedTextGrabStatus.Failed;
                result.Detail = "Grab 异常：" + ex.Message;
                return result;
            }
        }

        /// <summary>
        /// 依次注入 Ctrl↓ C↓ C↑ Ctrl↑ 四个键盘事件。
        /// 返回 null 表示成功，否则为失败描述（SendInput 被 UIPI 或桌面切换挡下时不抛异常，只返回 0）。
        /// </summary>
        private static string? TrySendCtrlC()
        {
            uint ctrl = NativeMethods.VK_CONTROL;
            uint c = NativeMethods.VK_C;

            // Ctrl 与 C 的扫描码：MapVirtualKey 返回 0 说明该键盘布局上没有这个键，
            // 但 wVk 仍然有效，所以不把它当致命错误，只是带上能带上。
            ushort scanCtrl = (ushort)NativeMethods.MapVirtualKey(ctrl, NativeMethods.MAPVK_VK_TO_VSC);
            ushort scanC = (ushort)NativeMethods.MapVirtualKey(c, NativeMethods.MAPVK_VK_TO_VSC);

            // SendInput 返回 0 时不抛异常，且「cbSize 与结构真实尺寸不符」和「注入被系统丢弃」
            // 表现完全一样，所以把实测尺寸带进失败描述，日志才分得清是哪一种。
            int cbSize = Marshal.SizeOf<NativeMethods.INPUT>();
            string sizeTag = "（cbSize=" + cbSize + "）";

            if (SendKey(ctrl, scanCtrl, keyUp: false) == 0) return "SendInput(Ctrl↓) 返回 0" + sizeTag;
            if (SendKey(c, scanC, keyUp: false) == 0) return "SendInput(C↓) 返回 0" + sizeTag;
            if (SendKey(c, scanC, keyUp: true) == 0) return "SendInput(C↑) 返回 0" + sizeTag;
            if (SendKey(ctrl, scanCtrl, keyUp: true) == 0) return "SendInput(Ctrl↑) 返回 0" + sizeTag;
            return null;
        }

        private static uint SendKey(uint vk, ushort scan, bool keyUp)
        {
            var input = new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                U = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = (ushort)vk,
                        wScan = scan,
                        dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero,
                    }
                }
            };

            return NativeMethods.SendInput(1, ref input, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        private static bool SelfElevated()
        {
            if (_selfElevated.HasValue) return _selfElevated.Value;
            bool elevated = IsProcessElevated((uint)Environment.ProcessId) ?? false;
            _selfElevated = elevated;
            return elevated;
        }

        /// <summary>
        /// 目标进程是否已提权。null = 查不到（进程受保护或已退出）——调用方按「不阻止注入」处理。
        /// </summary>
        private static bool? IsProcessElevated(uint pid)
        {
            IntPtr hProcess = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero) return null;

            IntPtr token = IntPtr.Zero;
            try
            {
                if (!NativeMethods.OpenProcessToken(hProcess, NativeMethods.TOKEN_QUERY, out token)) return null;

                var info = new NativeMethods.TOKEN_ELEVATION();
                uint size = (uint)Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>();
                if (!NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, ref info, size, out _))
                {
                    return null;
                }
                return info.TokenIsElevated != 0;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (token != IntPtr.Zero)
                {
                    try { NativeMethods.CloseHandle(token); } catch { }
                }
                try { NativeMethods.CloseHandle(hProcess); } catch { }
            }
        }
    }
}
