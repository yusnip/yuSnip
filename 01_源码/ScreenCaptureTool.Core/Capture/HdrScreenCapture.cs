using System.Drawing;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenCaptureTool.Core.Capture;

/// <summary>
/// HDR 自动补帧：SDR 仍由 GDI 采集，只以 FP16 桌面复制替换 HDR 输出相交的像素。
/// 不启动常驻取帧线程；长截图按会话复用纹理，静态截图用完释放。裸 COM 调用兼容 NativeAOT。
/// </summary>
public sealed unsafe class HdrScreenCapture : IDisposable
{
    private readonly List<OutputCapture> _outputs = new();
    private readonly Rectangle _bounds;
    private bool _disposed;
    private HdrScreenCapture(Rectangle bounds) => _bounds = bounds;

    /// <summary>仅在当前输出确实启用 HDR 且提供 SDR 白电平时创建；失败保留调用方原有 GDI 像素。</summary>
    public static HdrScreenCapture? TryCreate(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        HdrScreenCapture? result = null;
        nint factory = 0;
        try
        {
            using var detection = PerformanceTimer.Measure("HDR.Detect");
            var displays = ReadDisplays();
            if (displays.Count == 0) return null;
            result = new HdrScreenCapture(bounds);
            Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            Check(CreateDXGIFactory1(&iid, &factory));
            for (uint adapterIndex = 0; adapterIndex < 32; adapterIndex++)
            {
                nint adapter = 0;
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, adapterIndex, &adapter);
                if (hr == NotFound) break;
                Check(hr);
                try
                {
                    for (uint outputIndex = 0; outputIndex < 32; outputIndex++)
                    {
                        nint output = 0, output6 = 0;
                        hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(adapter, 7))(adapter, outputIndex, &output);
                        if (hr == NotFound) break;
                        Check(hr);
                        try
                        {
                            output6 = Query(output, new Guid("068346e8-aaec-4b84-add7-137f513f77a1"));
                            OutputDesc desc = default;
                            Check(((delegate* unmanaged[Stdcall]<nint, OutputDesc*, int>)Slot(output6, 27))(output6, &desc));
                            // 仅“支持 HDR”不够；必须是当前启用的 PQ/BT.2020 输出，排除 SDR 自动色彩管理。
                            if (desc.Attached == 0 || desc.ColorSpace != 12) continue;
                            string name = new(desc.DeviceName);
                            var display = displays.Find(d => d.Name == name);
                            if (display == null) continue;
                            var monitor = Rectangle.FromLTRB(desc.Left, desc.Top, desc.Right, desc.Bottom);
                            var intersection = Rectangle.Intersect(bounds, monitor);
                            if (intersection.IsEmpty) continue;
                            var local = new Rectangle(intersection.X - monitor.X, intersection.Y - monitor.Y, intersection.Width, intersection.Height);
                            var target = new Point(intersection.X - bounds.X, intersection.Y - bounds.Y);
                            result._outputs.Add(OutputCapture.Create(adapter, output6, local, target, display.WhiteScale, name));
                        }
                        catch (Exception ex) { AppLogger.Warn("HDR 输出初始化失败，保留 GDI：" + ErrorCode(ex)); }
                        finally { Release(output6); Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
            if (result._outputs.Count > 0) return result;
        }
        catch (Exception ex) { AppLogger.Warn("HDR 初始化失败，保留 GDI：" + ErrorCode(ex)); }
        finally { Release(factory); }
        result?.Dispose();
        return null;
    }

    /// <summary>调用前须 GdiFlush，确保队列中的 GDI 写入不会晚于 HDR 替换；只接受正步长 32 位自有 DIB。</summary>
    public bool TryCopyTo(nint destination, int stride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination == 0 || stride < checked(_bounds.Width * 4)) throw new ArgumentException("HDR 目标 DIB 非法");
        bool copied = false;
        foreach (var output in _outputs) copied |= output.TryCopyTo(destination, stride);
        return copied;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var output in _outputs) output.Dispose();
        _outputs.Clear();
    }

    internal sealed record Display(string Name, float WhiteScale);
    internal static List<Display> ReadDisplays()
    {
        var result = new List<Display>();
        // 只查询元数据，SDR 机器不创建 D3D 设备。拓扑竞态仅重试一次，避免截图入口无限等待。
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (GetDisplayConfigBufferSizes(2, out uint pathsCount, out uint modesCount) != 0
                || pathsCount == 0 || pathsCount > 128 || modesCount > 512) return result;
            var paths = new PathInfo[pathsCount];
            var modes = new ModeInfo[modesCount];
            int hr;
            fixed (PathInfo* p = paths)
            fixed (ModeInfo* m = modes) hr = QueryDisplayConfig(2, ref pathsCount, p, ref modesCount, m, 0);
            if (hr == 122) continue;
            if (hr != 0) return result;
            for (int i = 0; i < pathsCount; i++)
            {
                var path = paths[i];
                AdvancedColor color = new() { Header = new DeviceHeader(9, (uint)sizeof(AdvancedColor), path.TargetAdapter, path.TargetId) };
                if (DisplayConfigGetDeviceInfo(&color.Header) != 0 || (color.Flags & 2) == 0) continue;
                WhiteLevel white = new() { Header = new DeviceHeader(11, (uint)sizeof(WhiteLevel), path.TargetAdapter, path.TargetId) };
                SourceName source = new() { Header = new DeviceHeader(1, (uint)sizeof(SourceName), path.SourceAdapter, path.SourceId) };
                // 不猜一个固定亮度值；没有白电平时宁可回退，也不把 SDR 桌面错误压暗。
                int whiteResult = DisplayConfigGetDeviceInfo(&white.Header);
                int sourceResult = DisplayConfigGetDeviceInfo(&source.Header);
                if (whiteResult != 0 || white.Level == 0 || sourceResult != 0)
                {
                    AppLogger.Warn($"HDR 高级颜色元数据不完整，保留 GDI：whiteResult={whiteResult} whiteLevel={white.Level} sourceResult={sourceResult}");
                    continue;
                }
                result.Add(new Display(new string(source.Name), white.Level / 1000f));
            }
            break;
        }
        return result;
    }

    internal sealed class OutputCapture : IDisposable
    {
        private nint _device, _context, _duplicate;
        private FrameConverter? _mapper;
        private readonly Rectangle _local;
        private readonly Point _target;
        private readonly float _white;
        private readonly byte[] _pixels;
        private bool _hasFrame, _disabled;
        private bool _initialWaitDone, _waitingLogged, _protectedLogged;
        private int _pointerOnlyFrames, _timeouts;
        private readonly string _name;
        private uint _rotation;
        // 原生指针所有权移交到实例；普通创建分步初始化，自测可使用受控复制接口。
        internal OutputCapture(Rectangle local, Point target, float white, nint device = 0, nint context = 0,
            nint duplication = 0, uint rotation = 1, string name = "")
        {
            _local = local; _target = target; _white = white;
            _device = device; _context = context; _duplicate = duplication; _rotation = rotation; _name = name;
            _pixels = new byte[checked(local.Width * local.Height * 4)];
        }

        internal static OutputCapture Create(nint adapter, nint output, Rectangle local, Point target, float white, string name)
        {
            var capture = new OutputCapture(local, target, white, name: name);
            try
            {
                using var timing = PerformanceTimer.Measure("HDR.CreateDevice");
                Check(D3D11CreateDevice(adapter, 0, 0, 0x20, 0, 0, 7, out nint device, out uint feature, out nint context), "D3D11CreateDevice");
                capture._device = device; capture._context = context;
                // BGRA 为接口要求的兼容格式；实际帧必须确认为 FP16，绝不把已裁掉高光的 BGRA 当成 HDR。
                uint* formats = stackalloc uint[] { 10, 87 };
                nint duplication = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint*, nint*, int>)Slot(output, 26))
                    (output, device, 0, 2, formats, &duplication), "DuplicateOutput1");
                capture._duplicate = duplication;
                DuplicationDesc desc = default;
                ((delegate* unmanaged[Stdcall]<nint, DuplicationDesc*, void>)Slot(duplication, 7))(duplication, &desc);
                capture._rotation = desc.Rotation;
                if (capture._rotation is < 1 or > 4) throw new InvalidOperationException("HDR 输出旋转无效");
                AppLogger.Info($"HDR 输出就绪：{name} feature=0x{feature:X} duplicationFormat={desc.Format} rotation={desc.Rotation} white={white:F3} region={local}");
                return capture;
            }
            catch { capture.Dispose(); throw; }
        }

        internal bool TryCopyTo(nint target, int stride)
        {
            if (_disabled) return false;
            nint resource = 0, texture = 0;
            bool acquired = false;
            string stage = "AcquireNextFrame";
            try
            {
                using var timing = PerformanceTimer.Measure("HDR.AcquireAndMap");
                bool initialWait = !_initialWaitDone;
                _initialWaitDone = true;
                long started = Stopwatch.GetTimestamp();
                FrameInfo info = default;
                // 首次最多等 240ms；只有鼠标更新不建立桌面缓存。后续无帧立即让出，不永久禁用 HDR。
                for (int attempt = 0; ; attempt++)
                {
                    int remaining = initialWait ? Math.Max(0, 240 - (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds) : 0;
                    uint wait = (uint)Math.Min(80, remaining);
                    int hr = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)Slot(_duplicate, 8))
                        (_duplicate, wait, &info, &resource);
                    if (hr == WaitTimeout)
                    {
                        _timeouts++;
                        if (_hasFrame) { CopyCached(target, stride); return true; }
                        if (attempt < 7 && remaining > 0) continue;
                        LogWaiting(); return false;
                    }
                    Check(hr); acquired = true;
                    if (info.LastPresentTime != 0) break;
                    _pointerOnlyFrames++;
                    if (_hasFrame) { CopyCached(target, stride); return true; }
                    Release(resource); resource = 0;
                    int releaseHr = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_duplicate, 14))(_duplicate);
                    acquired = false; Check(releaseHr);
                    if (attempt >= 7 || remaining <= 0) { LogWaiting(); return false; }
                }
                if (info.ProtectedContentMaskedOut != 0 && !_protectedLogged)
                { _protectedLogged = true; AppLogger.Warn($"HDR 输出含系统已遮黑的受保护内容：{_name}；不绕过系统保护"); }
                stage = "Texture.GetDesc";
                if (resource == 0) throw new InvalidOperationException("HDR 成功取帧但未返回桌面纹理");
                texture = Query(resource, new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c")); // ID3D11Texture2D
                TextureDesc desc = default;
                ((delegate* unmanaged[Stdcall]<nint, TextureDesc*, void>)Slot(texture, 10))(texture, &desc);
                if (desc.Format != 10 || desc.SampleCount != 1)
                    throw new InvalidOperationException($"HDR 复制未返回单采样 FP16：format={desc.Format} samples={desc.SampleCount} size={desc.Width}x{desc.Height}");
                var source = HdrToneMapper.SourceRect(_local, checked((int)desc.Width), checked((int)desc.Height), _rotation);
                if (source.Width <= 0 || source.Height <= 0 || source.Left < 0 || source.Top < 0
                    || source.Right > desc.Width || source.Bottom > desc.Height) throw new InvalidOperationException("HDR 输出尺寸已变化");
                stage = "ToneMap";
                _mapper ??= new FrameConverter(_device, _context, source.Width, source.Height, _rotation, _white);
                using (PerformanceTimer.Measure("HDR.ToneMap"))
                    _mapper.Convert(texture, source, _pixels);
                if (!_hasFrame)
                    AppLogger.Info($"HDR 首帧完成：{_name} format={desc.Format} size={desc.Width}x{desc.Height} rotation={_rotation} white={_white:F3} cpuFallback={_mapper.UsesCpu} pointerOnly={_pointerOnlyFrames} timeouts={_timeouts}");
                _hasFrame = true;
                CopyCached(target, stride);
                return true;
            }
            catch (Exception ex)
            {
                // 会话内失败一次即停用，不在高频长截图循环中反复建设备／等待，保留当前 GDI 结果。
                _disabled = true;
                AppLogger.Warn($"HDR 捕获回退 GDI：output={_name} stage={stage} region={_local} " + ErrorCode(ex));
                return false;
            }
            finally
            {
                Release(texture); Release(resource);
                if (acquired) ((delegate* unmanaged[Stdcall]<nint, int>)Slot(_duplicate, 14))(_duplicate);
                if (_disabled) Dispose();
            }
        }

        private void LogWaiting()
        {
            if (_waitingLogged) return;
            _waitingLogged = true;
            AppLogger.Warn($"HDR 暂无有效桌面帧，本次保留 GDI、下次继续采集：{_name} pointerOnly={_pointerOnlyFrames} timeouts={_timeouts}");
        }

        private void CopyCached(nint target, int stride)
        {
            fixed (byte* pixels = _pixels)
                for (int y = 0; y < _local.Height; y++)
                    Buffer.MemoryCopy(pixels + (long)y * _local.Width * 4,
                        (byte*)target + (long)(_target.Y + y) * stride + _target.X * 4L, _local.Width * 4L, _local.Width * 4L);
        }

        public void Dispose()
        {
            _disabled = true;
            _mapper?.Dispose(); _mapper = null;
            Release(_duplicate); _duplicate = 0;
            Release(_context); _context = 0;
            Release(_device); _device = 0;
        }
    }

    /// <summary>同一张 FP16 原帧优先 GPU；失败后会话内固定 CPU，不把失败转换写入目标 DIB。</summary>
    internal sealed class FrameConverter : IDisposable
    {
        private readonly nint _device, _context;
        private readonly int _width, _height;
        private readonly uint _rotation;
        private readonly float _white;
        private readonly Func<GpuMapper>? _createGpu;
        private GpuMapper? _gpu;
        private CpuMapper? _cpu;
        private bool _disposed;
        internal bool UsesCpu { get; private set; }

        internal FrameConverter(nint device, nint context, int width, int height, uint rotation, float white,
            Func<GpuMapper>? createGpu = null)
        { _device = device; _context = context; _width = width; _height = height; _rotation = rotation; _white = white; _createGpu = createGpu; }

        internal void Convert(nint texture, Rectangle source, Span<byte> pixels)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!UsesCpu)
            {
                try
                {
                    _gpu ??= _createGpu != null ? _createGpu() : new GpuMapper(_device, _context, _width, _height, _rotation, _white);
                    _gpu.Convert(texture, source, pixels);
                    return;
                }
                catch (Exception ex)
                {
                    UsesCpu = true;
                    _gpu?.Dispose(); _gpu = null;
                    AppLogger.Warn("HDR GPU 转换失败，改用同帧 FP16 CPU 转换：" + ErrorCode(ex));
                }
            }
            _cpu ??= new CpuMapper(_device, _context, _width, _height, _rotation, _white);
            _cpu.Convert(texture, source, pixels);
        }
        public void Dispose()
        { if (_disposed) return; _disposed = true; _gpu?.Dispose(); _cpu?.Dispose(); }
    }

    /// <summary>计算着色器／UAV 不兼容时，只读回局部 FP16，再复用同一色调公式和旋转。</summary>
    internal sealed class CpuMapper : IDisposable
    {
        private readonly nint _context;
        private readonly int _width, _height;
        private readonly uint _rotation;
        private readonly float _white;
        private TextureReadback? _readback;
        private nint _staging;
        private bool _disposed;
        internal CpuMapper(nint device, nint context, int width, int height, uint rotation, float white)
        {
            _context = context; _width = width; _height = height; _rotation = rotation; _white = white;
            try
            {
                _readback = new TextureReadback(device, context);
                _staging = GpuMapper.CreateTexture(device, new TextureDesc { Width = (uint)width, Height = (uint)height,
                    MipLevels = 1, ArraySize = 1, Format = 10, SampleCount = 1, Usage = 3, CpuAccessFlags = 0x20000 });
            }
            catch { Dispose(); throw; }
        }
        internal void Convert(nint texture, Rectangle source, Span<byte> pixels)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (source.Width != _width || source.Height != _height || source.X < 0 || source.Y < 0)
                throw new ArgumentException("HDR CPU 输入尺寸不匹配");
            Box box = new() { Left = (uint)source.Left, Top = (uint)source.Top, Right = (uint)source.Right, Bottom = (uint)source.Bottom, Back = 1 };
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Slot(_context, 46))
                (_context, _staging, 0, 0, 0, 0, texture, 0, &box);
            MappedSubresource data = _readback!.Map(_staging);
            try { HdrToneMapper.Convert(data.Data, checked((int)data.RowPitch), _width, _height, _rotation, _white, pixels); }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 15))(_context, _staging, 0); }
        }
        public void Dispose()
        { if (_disposed) return; _disposed = true; Release(_staging); _staging = 0; _readback?.Dispose(); }
    }

    /// <summary>GPU／CPU 转换共用有界完成等待，不引入无限同步 Map 或高频轮询。</summary>
    private sealed class TextureReadback : IDisposable
    {
        private readonly nint _context;
        private nint _dxgiDevice, _completion;
        private bool _hasMapped;
        internal TextureReadback(nint device, nint context)
        {
            _context = context;
            try
            {
                _dxgiDevice = Query(device, new Guid("05008617-fbfd-4051-a790-144884b4f6a9"));
                _completion = CreateEventW(0, 0, 0, 0);
                if (_completion == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            catch { Dispose(); throw; }
        }
        internal MappedSubresource Map(nint texture)
        {
            MappedSubresource data = default;
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Slot(_context, 14))
                (_context, texture, 0, 1, 0x100000, &data);
            if (hr == StillDrawing)
            {
                ((delegate* unmanaged[Stdcall]<nint, void>)Slot(_context, 111))(_context);
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(_dxgiDevice, 16))(_dxgiDevice, _completion));
                if (WaitForSingleObject(_completion, _hasMapped ? 80u : 500u) != 0) throw new TimeoutException("HDR GPU 读回超时");
                hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, MappedSubresource*, int>)Slot(_context, 14))
                    (_context, texture, 0, 1, 0, &data);
            }
            Check(hr); _hasMapped = true;
            return data;
        }
        public void Dispose()
        { Release(_dxgiDevice); _dxgiDevice = 0; if (_completion != 0) { CloseHandle(_completion); _completion = 0; } }
    }

    /// <summary>局部 FP16 纹理 → GPU 色调映射 → 8 位读回。设备／纹理按会话复用，无后台渲染循环。</summary>
    internal sealed class GpuMapper : IDisposable
    {
        private readonly nint _context;
        private readonly int _width, _height, _sourceWidth, _sourceHeight;
        private nint _input, _inputView, _result, _resultView, _staging, _shader, _parameters;
        private TextureReadback? _readback;
        private bool _disposed;
        private static readonly Lazy<byte[]> Bytecode = new(CompileShader);
        internal GpuMapper(nint device, nint context, int width, int height, uint rotation, float white)
        {
            if (width <= 0 || height <= 0 || rotation is < 1 or > 4 || !(white > 0) || !float.IsFinite(white))
                throw new ArgumentException("HDR GPU 参数非法");
            _context = context; _sourceWidth = width; _sourceHeight = height;
            _width = rotation is 2 or 4 ? height : width;
            _height = rotation is 2 or 4 ? width : height;
            try
            {
                _readback = new TextureReadback(device, context);
                TextureDesc inputDesc = new() { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = 10, SampleCount = 1, BindFlags = 8 };
                _input = CreateTexture(device, inputDesc);
                nint view = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, 7))(device, _input, 0, &view));
                _inputView = view;
                TextureDesc outputDesc = new() { Width = (uint)_width, Height = (uint)_height, MipLevels = 1, ArraySize = 1, Format = 28, SampleCount = 1, BindFlags = 0x80 };
                _result = CreateTexture(device, outputDesc);
                view = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(device, 8))(device, _result, 0, &view));
                _resultView = view;
                outputDesc.Usage = 3; outputDesc.BindFlags = 0; outputDesc.CpuAccessFlags = 0x20000;
                _staging = CreateTexture(device, outputDesc);
                byte[] bytecode = Bytecode.Value;
                nint shader = 0;
                fixed (byte* code = bytecode)
                    Check(((delegate* unmanaged[Stdcall]<nint, void*, nuint, nint, nint*, int>)Slot(device, 18))(device, code, (nuint)bytecode.Length, 0, &shader), "CreateComputeShader");
                _shader = shader;
                ShaderParams parameters = new() { InverseWhite = 1 / white, Rotation = rotation, Width = (uint)width, Height = (uint)height };
                BufferDesc bufferDesc = new() { ByteWidth = 16, Usage = 1, BindFlags = 4 };
                SubresourceData initial = new() { Data = (nint)(&parameters) };
                nint buffer = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, BufferDesc*, SubresourceData*, nint*, int>)Slot(device, 3))(device, &bufferDesc, &initial, &buffer));
                _parameters = buffer;
            }
            catch { Dispose(); throw; }
        }

        internal static nint CreateTexture(nint device, TextureDesc desc, SubresourceData* initial = null)
        {
            nint result = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, SubresourceData*, nint*, int>)Slot(device, 5))(device, &desc, initial, &result), "CreateTexture2D");
            return result;
        }

        internal void Convert(nint source, Rectangle region, Span<byte> pixels)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (region.Width != _sourceWidth || region.Height != _sourceHeight || region.X < 0 || region.Y < 0
                || pixels.Length < checked(_width * _height * 4)) throw new ArgumentException("HDR GPU 输入尺寸不匹配");
            Box box = new() { Left = (uint)region.Left, Top = (uint)region.Top, Right = (uint)region.Right, Bottom = (uint)region.Bottom, Back = 1 };
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, Box*, void>)Slot(_context, 46))
                (_context, _input, 0, 0, 0, 0, source, 0, &box);
            nint srv = _inputView, uav = _resultView, parameters = _parameters;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, 67))(_context, 0, 1, &srv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(_context, 68))(_context, 0, 1, &uav, null);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(_context, 69))(_context, _shader, 0, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, 71))(_context, 0, 1, &parameters);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Slot(_context, 41))(_context, (uint)(_width + 7) / 8, (uint)(_height + 7) / 8, 1);
            // 解绑读写视图再复制，避免将同一资源同时绑定为 UAV 与复制源。
            Unbind();
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(_context, 47))(_context, _staging, _result);
            MappedSubresource data = _readback!.Map(_staging);
            try
            {
                if (data.Data == 0 || data.RowPitch < _width * 4) throw new InvalidOperationException("HDR GPU 行距非法");
                fixed (byte* destination = pixels)
                    for (int y = 0; y < _height; y++)
                        Buffer.MemoryCopy((byte*)data.Data + (long)y * data.RowPitch, destination + (long)y * _width * 4, _width * 4L, _width * 4L);
                ValidateOpaqueResult(pixels[..checked(_width * _height * 4)]);
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(_context, 15))(_context, _staging, 0); }
        }

        private void Unbind()
        {
            nint empty = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, 67))(_context, 0, 1, &empty);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(_context, 68))(_context, 0, 1, &empty, null);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(_context, 71))(_context, 0, 1, &empty);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, void>)Slot(_context, 69))(_context, 0, 0, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Unbind();
            Release(_parameters); _parameters = 0; Release(_shader); _shader = 0;
            Release(_inputView); _inputView = 0; Release(_resultView); _resultView = 0;
            Release(_input); _input = 0; Release(_result); _result = 0; Release(_staging); _staging = 0;
            _readback?.Dispose(); _readback = null;
        }

        private static byte[] CompileShader()
        {
            nint code = 0, errors = 0;
            try
            {
                byte[] source = System.Text.Encoding.UTF8.GetBytes(Shader);
                fixed (byte* input = source)
                    Check(D3DCompile(input, (nuint)source.Length, null, 0, 0, "main", "cs_5_0", 0x48000, 0, &code, &errors));
                nint bytes = ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(code, 3))(code);
                nuint count = ((delegate* unmanaged[Stdcall]<nint, nuint>)Slot(code, 4))(code);
                return new ReadOnlySpan<byte>((void*)bytes, checked((int)count)).ToArray();
            }
            finally { Release(code); Release(errors); }
        }

        private const string Shader = """
            Texture2D<float4> Input : register(t0);
            RWTexture2D<float4> Output : register(u0);
            cbuffer Parameters : register(b0) { float inverseWhite; uint rotation; uint sourceWidth; uint sourceHeight; };
            float clean(float x) { return isfinite(x) ? x : (x > 0 ? 65504 : 0); }
            float encode(float x) { x = saturate(x); return x <= 0.0031308 ? 12.92*x : 1.055*pow(x, 1.0/2.4)-0.055; }
            [numthreads(8,8,1)]
            void main(uint3 id : SV_DispatchThreadID) {
                uint width, height; Output.GetDimensions(width, height);
                if (id.x >= width || id.y >= height) return;
                uint2 p = id.xy;
                if (rotation == 2) p = uint2(id.y, sourceHeight-1-id.x);
                else if (rotation == 3) p = uint2(sourceWidth-1-id.x, sourceHeight-1-id.y);
                else if (rotation == 4) p = uint2(sourceWidth-1-id.y, id.x);
                float3 c = Input.Load(int3(p, 0)).rgb;
                c = float3(clean(c.r), clean(c.g), clean(c.b)) * inverseWhite;
                float y = dot(c, float3(0.2126, 0.7152, 0.0722));
                if (y <= 0) { Output[id.xy] = float4(0,0,0,1); return; }
                float peak = max(c.r, max(c.g, c.b));
                float gain = 1/max(1.0, peak);
                c *= gain;
                float mapped = y*gain;
                float lo = min(c.r, min(c.g, c.b));
                float saturation = 1;
                if (lo < 0) saturation = min(saturation, mapped/(mapped-lo));
                c = mapped + (c-mapped)*saturation;
                // RGBA UAV 以 BGRA 顺序写入，读回可直接复制到既有 32 位 DIB。
                Output[id.xy] = float4(encode(c.b), encode(c.g), encode(c.r), 1);
            }
            """;
    }

    // 着色器承诺 Alpha=1。只查少量完整性标记，不按 RGB 黑色判断失败，合法全黑画面必须保留。
    internal static void ValidateOpaqueResult(ReadOnlySpan<byte> pixels)
    {
        int count = pixels.Length / 4;
        if (count == 0 || pixels.Length % 4 != 0) throw new ArgumentException("HDR 转换缓冲非法");
        for (int i = 0; i < 5; i++)
            if (pixels[(int)((long)(count - 1) * i / 4) * 4 + 3] != 255)
                throw new InvalidOperationException("HDR GPU 转换输出不完整：不透明 Alpha 标记缺失");
    }

    private static string ErrorCode(Exception ex) => ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + " " + ex.Message;
    private static void Check(int hr, [System.Runtime.CompilerServices.CallerMemberName] string operation = "")
    { if (hr < 0) throw new COMException($"HDR 原生调用失败（{operation}）", hr); }
    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];
    private static nint Query(nint obj, Guid iid)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &result));
        return result;
    }
    internal static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj); }
    private const int NotFound = unchecked((int)0x887A0002), WaitTimeout = unchecked((int)0x887A0027), StillDrawing = unchecked((int)0x887A000A);

    // 以下布局与 Windows SDK dxgi1_2.h／dxgi1_6.h／d3d11.h／wingdi.h 对齐；不使用运行时 COM 封送。
    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceHeader(uint type, uint size, long adapter, uint id)
    {
        internal uint Type = type, Size = size;
        // LUID 原生按两个 32 位值对齐，不能用 long 字段改变后续结构的对齐。
        internal uint AdapterLow = (uint)adapter;
        internal int AdapterHigh = (int)(adapter >> 32);
        internal uint Id = id;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    internal struct PathInfo
    {
        internal long SourceAdapter;
        internal uint SourceId, SourceMode, SourceStatus;
        internal long TargetAdapter;
        internal uint TargetId, TargetMode, OutputTechnology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLineOrdering;
        internal int TargetAvailable;
        internal uint TargetStatus, Flags;
    }
    [StructLayout(LayoutKind.Sequential, Size = 64)] internal struct ModeInfo { }
    [StructLayout(LayoutKind.Sequential)] internal struct AdvancedColor { internal DeviceHeader Header; internal uint Flags, Encoding, Bits; }
    [StructLayout(LayoutKind.Sequential)] internal struct WhiteLevel { internal DeviceHeader Header; internal uint Level; }
    [StructLayout(LayoutKind.Sequential)] internal struct SourceName { internal DeviceHeader Header; internal fixed char Name[32]; }
    [StructLayout(LayoutKind.Sequential)] internal struct OutputDesc
    {
        internal fixed char DeviceName[32];
        internal int Left, Top, Right, Bottom, Attached;
        internal uint Rotation;
        internal nint Monitor;
        internal uint Bits, ColorSpace;
        internal fixed float Primaries[8];
        internal float MinLuminance, MaxLuminance, MaxFullFrameLuminance;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct TextureDesc
    {
        internal uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Box { internal uint Left, Top, Front, Right, Bottom, Back; }
    [StructLayout(LayoutKind.Sequential)] internal struct BufferDesc { internal uint ByteWidth, Usage, BindFlags, CpuAccessFlags, MiscFlags, StructureByteStride; }
    [StructLayout(LayoutKind.Sequential)] internal struct SubresourceData { internal nint Data; internal uint RowPitch, SlicePitch; }
    [StructLayout(LayoutKind.Sequential)] internal struct ShaderParams { internal float InverseWhite; internal uint Rotation, Width, Height; }
    [StructLayout(LayoutKind.Sequential)] internal struct MappedSubresource { internal nint Data; internal uint RowPitch, DepthPitch; }
    [StructLayout(LayoutKind.Sequential)] internal struct DuplicationDesc
    {
        internal uint Width, Height, RefreshNumerator, RefreshDenominator, Format, ScanLineOrdering, Scaling, Rotation;
        internal int DesktopInSystemMemory;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct FrameInfo
    {
        internal long LastPresentTime, LastMouseUpdateTime;
        internal uint AccumulatedFrames;
        internal int RectsCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible;
        internal uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(Guid* iid, nint* factory);
    [DllImport("d3d11.dll", ExactSpelling = true)] internal static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint featureLevels, uint featureLevelsCount, uint sdkVersion, out nint device, out uint featureLevel, out nint context);
    [DllImport("d3dcompiler_47.dll", ExactSpelling = true)] private static extern int D3DCompile(byte* source, nuint length, [MarshalAs(UnmanagedType.LPStr)] string? name, nint defines, nint include, [MarshalAs(UnmanagedType.LPStr)] string entry, [MarshalAs(UnmanagedType.LPStr)] string target, uint flags, uint flags2, nint* code, nint* errors);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)] private static extern nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern int CloseHandle(nint handle);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int QueryDisplayConfig(uint flags, ref uint pathsCount, PathInfo* paths, ref uint modesCount, ModeInfo* modes, nint topology);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int DisplayConfigGetDeviceInfo(DeviceHeader* request);
}
