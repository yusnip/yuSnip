using System;
using System.Collections.Generic;
using System.Globalization;
using ScreenCaptureTool.Core.Capture;

namespace ScreenCaptureTool.Core.Scripting;

/// <summary>
/// 单次截图动作的外观与交互覆盖项（不修改全局设置）。
/// 支持新式 key=value 可选参数（toolbar/radius/stroke/shadow/detect）并兼容旧版 NoStroke 与内嵌延迟参数。
/// </summary>
public readonly record struct ActionCaptureOptions(
    int DelayMilliseconds = 0,
    bool WithoutStroke = false,
    bool? ShowToolbar = null,
    int? CornerRadiusOverride = null,
    int? StrokeWidthOverride = null,
    bool? ShadowOverride = null,
    string? DetectModeOverride = null)
{
    public bool HasAnyOverride =>
        WithoutStroke || ShowToolbar.HasValue || CornerRadiusOverride.HasValue
        || StrokeWidthOverride.HasValue || ShadowOverride.HasValue || !string.IsNullOrEmpty(DetectModeOverride);

    internal static bool TryRead(string[] arguments, out ActionCaptureOptions options, int startIndex = 1)
    {
        options = default;
        if (arguments.Length <= startIndex) return true;

        int delay = 0;
        bool withoutStroke = false;
        bool? showToolbar = null;
        int? radius = null;
        int? stroke = null;
        bool? shadow = null;
        string? detect = null;

        for (int i = startIndex; i < arguments.Length; i++)
        {
            string raw = arguments[i].Trim();
            if (raw.Length == 0) continue;

            int eq = raw.IndexOf('=');
            if (eq > 0)
            {
                string key = raw[..eq].Trim().ToLowerInvariant();
                string val = raw[(eq + 1)..].Trim();
                string vLow = val.ToLowerInvariant();
                switch (key)
                {
                    case "toolbar":
                        if (vLow is "show" or "on" or "true") showToolbar = true;
                        else if (vLow is "hide" or "off" or "false" or "default") showToolbar = false;
                        else return false;
                        break;
                    case "radius":
                        if (vLow == "default") radius = null;
                        else if (vLow == "none") radius = 0;
                        else if (int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int r)
                                 && r <= SelectionEffects.MaxRadiusParam)
                            radius = r;
                        else return false;
                        break;
                    case "stroke":
                        if (vLow == "default") { stroke = null; withoutStroke = false; }
                        else if (vLow is "none" or "nostroke" or "off") { stroke = 0; withoutStroke = true; }
                        else if (int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int s)
                                 && s <= SelectionEffects.MaxStrokeParam)
                        { stroke = s; withoutStroke = s == 0; }
                        else return false;
                        break;
                    case "shadow":
                        if (vLow == "default") shadow = null;
                        else if (vLow is "on" or "true") shadow = true;
                        else if (vLow is "off" or "false" or "none") shadow = false;
                        else return false;
                        break;
                    case "detect":
                        if (vLow == "default") detect = null;
                        else if (vLow is "none" or "off") detect = "不检测";
                        else if (vLow == "window") detect = "仅窗口";
                        else if (vLow == "element") detect = "检测元素";
                        else if (vLow == "auto") detect = "自动";
                        else return false;
                        break;
                    default:
                        return false;
                }
                continue;
            }

            // 兼容旧版位置参数：NoStroke / Default / 延迟毫秒数
            if (raw.Equals("NoStroke", StringComparison.OrdinalIgnoreCase))
            {
                withoutStroke = true;
                stroke = 0;
            }
            else if (raw.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                withoutStroke = false;
                stroke = null;
            }
            else if (i == startIndex && ScriptExecutor.TryReadWaitMilliseconds(raw, out int legacyDelay))
            {
                delay = legacyDelay;
            }
            else
            {
                return false;
            }
        }

        options = new ActionCaptureOptions(delay, withoutStroke, showToolbar, radius, stroke, shadow, detect);
        return true;
    }

    public static string NormalizeDetectKey(string? detectMode) => detectMode switch
    {
        "不检测" => "none",
        "仅窗口" => "window",
        "检测元素" => "element",
        "自动" => "auto",
        _ => "default"
    };

    public static bool TryParseKeyValues(string[] arguments, int startIndex, out Dictionary<string, string> map)
    {
        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = startIndex; i < arguments.Length; i++)
        {
            string raw = arguments[i];
            int eq = raw.IndexOf('=');
            if (eq <= 0) return false;
            string k = raw[..eq].Trim();
            string v = raw[(eq + 1)..];
            if (k.Length == 0) return false;
            map[k] = v;
        }
        return true;
    }
}
