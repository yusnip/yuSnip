using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.Services;

/// <summary>命令转发的轻量通路，兼容旧版 UTF-8／BOM＋断开结束的管道协议。</summary>
internal sealed class CommandIpc : IDisposable
{
    internal const string PipeName = "yuSnip_IPC_Pipe";
    internal const int MaxMessageChars = 1024 * 1024;
    private const int MaxClients = 8;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string> _received;
    private readonly string _pipeName;
    private int _disposed;
    internal Task Completion { get; }

    internal CommandIpc(Action<string> received, string pipeName = PipeName)
    {
        _received = received;
        _pipeName = pipeName;
        // 同步建立首个监听，避免启动成功但管道尚未创建的空窗。
        NamedPipeServerStream? first = null;
        try { first = CreateServer(pipeName); }
        catch (Exception ex) { AppLogger.Error("IPC: 首次监听暂不可用，将自动重试", ex); }
        Completion = Task.Run(() => ListenAsync(first));
    }

    internal static async Task SendAsync(string message, string pipeName = PipeName)
    {
        if (message.Length > MaxMessageChars) throw new IOException("IPC 指令超过 1 Mi 字符上限");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync(1000, timeout.Token).ConfigureAwait(false);
        // 写入也受限时保护；不能在部分写入后重发，否则同一动作可能执行两次。
        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await client.WriteAsync(bytes.AsMemory(), timeout.Token).ConfigureAwait(false);
    }

    private async Task ListenAsync(NamedPipeServerStream? first)
    {
        using var slots = new SemaphoreSlim(MaxClients);
        var readers = new List<Task>();
        NamedPipeServerStream? waiting = first;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                bool ownsSlot = false;
                try
                {
                    await slots.WaitAsync(_stop.Token).ConfigureAwait(false);
                    ownsSlot = true;
                    waiting ??= CreateServer(_pipeName);
                    try { await waiting.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
                    catch (IOException ex) when ((ex.HResult & 0xffff) == 232)
                    {
                        // 客户端已写完并关闭，而 ConnectNamedPipe 尚未发出；内核缓冲区仍保存完整指令。
                        // 只在已断开的 ERROR_NO_DATA 分支读取快照，不能把普通断流／超时的半段指令派发。
                        try { ReceiveClosedClient(waiting); }
                        catch (Exception readError) { AppLogger.Error("IPC: 读取已结束连接失败", readError); }
                        finally { waiting.Dispose(); waiting = null; }
                        continue;
                    }
                    var connected = waiting;
                    waiting = null;
                    readers.RemoveAll(task => task.IsCompleted);
                    readers.Add(ReadAsync(connected, slots));
                    ownsSlot = false; // ReadAsync 负责释放槽位。
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    AppLogger.Error("IPC: 建立命令监听失败", ex);
                    waiting?.Dispose();
                    waiting = null;
                    await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
                }
                finally { if (ownsSlot) slots.Release(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            waiting?.Dispose();
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
    }

    private async Task ReadAsync(NamedPipeServerStream pipe, SemaphoreSlim slots)
    {
        using (pipe)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var watch = Stopwatch.StartNew();
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var message = new StringBuilder();
                var buffer = new char[4096];
                int count;
                while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
                {
                    if (message.Length + count > MaxMessageChars) throw new IOException("IPC 指令超过 1 Mi 字符上限");
                    message.Append(buffer, 0, count);
                }
                string command = message.ToString();
                if (!string.IsNullOrWhiteSpace(command))
                {
                    AppLogger.Info($"IPC: 收到命令，读取={watch.ElapsedMilliseconds}ms，字符数={message.Length}");
                    _received(command);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (OperationCanceledException) { AppLogger.Warn("IPC: 客户端未及时结束指令，已断开；其他连接继续接收"); }
            catch (Exception ex) { AppLogger.Error("IPC: 读取命令失败", ex); }
            finally { slots.Release(); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _ = Completion.ContinueWith(_ => _stop.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal void ReceiveClosedClient(NamedPipeServerStream pipe)
    {
        if (!PeekNamedPipe(pipe.SafePipeHandle, null, 0, out _, out uint available, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (available > MaxMessageChars * 4) throw new IOException("IPC 指令超过字节上限");
        var bytes = new byte[available];
        if (!PeekNamedPipe(pipe.SafePipeHandle, bytes, available, out uint copied, out _, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using var reader = new StreamReader(new MemoryStream(bytes, 0, (int)copied), Encoding.UTF8, true);
        string message = reader.ReadToEnd();
        if (message.Length > MaxMessageChars) throw new IOException("IPC 指令超过 1 Mi 字符上限");
        if (!string.IsNullOrWhiteSpace(message)) _received(message);
    }

    /// <summary>仅当前用户与 SYSTEM 可访问，拒绝远程连接；中完整性标签允许同一用户普通／管理员互通。</summary>
    internal static NamedPipeServerStream CreateServer(string pipeName)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ?? throw new IOException("无法获取 IPC 当前用户身份");
        // PipeSecurity 会丢弃 mandatory label ACE，必须将原始安全描述符直接交给 CreateNamedPipe。
        string sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;{sid})S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor
            };
            // WRITE_OWNER 与 FIRST_PIPE_INSTANCE 共用标志位：仅首个实例设置共享标签。
            var handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName, 0x40080001, 0x8,
                MaxClients, 0, 4096, 0, ref attributes);
            bool firstInstance = true;
            if (handle.IsInvalid && Marshal.GetLastWin32Error() == 5)
            {
                handle.Dispose();
                firstInstance = false;
                handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName, 0x40000001, 0x8,
                    MaxClients, 0, 4096, 0, ref attributes);
            }
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            try
            {
                // NPFS 创建时可能省略传入的标签，必须在首个句柄上显式设置，再开放接收。
                if (firstInstance)
                {
                    if (!GetSecurityDescriptorSacl(descriptor, out _, out var labelAcl, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    uint error = SetSecurityInfo(handle, 6, 0x10, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, labelAcl);
                    if (error != 0) throw new Win32Exception((int)error);
                }
                return new NamedPipeServerStream(PipeDirection.In, isAsync: true, isConnected: false, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string sddl, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
        uint maxInstances, uint outBuffer, uint inBuffer, uint timeout, ref SecurityAttributes attributes);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("advapi32.dll")]
    private static extern uint SetSecurityInfo(SafePipeHandle handle, int type, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafePipeHandle pipe, [Out] byte[]? buffer, uint size,
        out uint copied, out uint available, out uint left);
}
