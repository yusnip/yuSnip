using System;
using System.Threading.Tasks;
using ScreenCaptureTool.Core;

namespace ScreenCaptureTool.LongScroll;

/// <summary>
/// 长截图会话入口。当前长截图框使用原生轻量 Overlay，避免 Avalonia 透明窗口覆盖截图区域。
/// </summary>
public sealed class LongScrollSession
{
    private readonly LongScrollOptions _options;
    private readonly AppSettings _settings;

    public LongScrollSession(LongScrollOptions options, AppSettings? settings = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _settings = settings ?? AppSettings.Load();
    }

    public Task<LongScrollResult> RunAsync()
    {
        return new LegacyLongScrollSession(_options, _settings).RunAsync();
    }
}
