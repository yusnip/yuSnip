using System;

namespace ScreenCaptureTool.Core.Scripting;

/// <summary>截图成品描边覆盖，以及旧版脚本可能携带的等待时间。</summary>
public readonly record struct ActionCaptureOptions(int DelayMilliseconds, bool WithoutStroke)
{
    internal static bool TryRead(string[] arguments, out ActionCaptureOptions options)
    {
        options = default;
        if (arguments.Length <= 1) return true;
        if (arguments.Length > 3) return false;

        bool withoutStroke = false;
        if (arguments.Length == 2 && TryReadStroke(arguments[1], out withoutStroke))
        {
            options = new(0, withoutStroke);
            return true;
        }

        // 兼容 captureImage('结果名', 毫秒[, '描边模式'])。
        if (!ScriptExecutor.TryReadWaitMilliseconds(arguments[1], out int delay)
            || arguments.Length == 3 && !TryReadStroke(arguments[2], out withoutStroke)) return false;
        options = new(delay, withoutStroke);
        return true;
    }

    private static bool TryReadStroke(string value, out bool withoutStroke)
    {
        withoutStroke = value.Equals("NoStroke", StringComparison.OrdinalIgnoreCase);
        return withoutStroke || value.Equals("Default", StringComparison.OrdinalIgnoreCase);
    }
}
