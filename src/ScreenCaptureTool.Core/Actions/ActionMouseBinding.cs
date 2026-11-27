using System;
using System.Text;
using System.Runtime.InteropServices;
using ScreenCaptureTool.Core.Scripting;

namespace ScreenCaptureTool.Core.Actions;

public enum ActionMouseButton { None, Left, Right, Middle, X1, X2 }
public enum ActionMouseTrigger { Click, Drag }

/// <summary>引导键保留物理扫描码；扫描码为零时明确按逻辑 VK 匹配。</summary>
public readonly record struct ActionMouseKey(uint VirtualKey, uint ScanCode, bool Extended)
{
    public bool IsValid => (VirtualKey is >= 8 and <= 254 || VirtualKey == 3) && ScanCode <= 255;
    public uint ModifierMask => VirtualKey switch { 0x10 or 0xA0 or 0xA1 => 4, 0x11 or 0xA2 or 0xA3 => 2,
        0x12 or 0xA4 or 0xA5 => 1, 0x5B or 0x5C => 8, _ => 0 };
    public bool Matches(ActionMouseKey other) => (VirtualKey == other.VirtualKey
        || (ScanCode == 0 && VirtualKey is 0x10 or 0x11 or 0x12 && ModifierMask == other.ModifierMask)
        || (other.ScanCode == 0 && other.VirtualKey is 0x10 or 0x11 or 0x12 && other.ModifierMask == ModifierMask))
        && (ScanCode == 0 || other.ScanCode == 0 || (ScanCode == other.ScanCode && Extended == other.Extended));
    public override string ToString()
    {
        if (!IsValid) return "未设置";
        string? known = VirtualKey switch { 0x20 => "Space", 0x1B => "Esc", 0x0D => Extended ? "小键盘 Enter" : "Enter",
            0x09 => "Tab", 0x14 => "CapsLock", 0xA0 => "左 Shift", 0xA1 => "右 Shift", 0xA2 => "左 Ctrl",
            0xA3 => "右 Ctrl", 0xA4 => "左 Alt", 0xA5 => "右 Alt", 0x5B => "左 Win", 0x5C => "右 Win", _ => null };
        if (known != null) return known;
        if (VirtualKey is >= 0x70 and <= 0x87) return "F" + (VirtualKey - 0x6F);
        if (VirtualKey is >= 0x30 and <= 0x5A) return ((char)VirtualKey).ToString();
        var text = new StringBuilder(64);
        if (ScanCode != 0 && GetKeyNameTextW((int)(ScanCode << 16 | (Extended ? 1u << 24 : 0)), text, text.Capacity) > 0)
            return text.ToString();
        return "VK " + VirtualKey.ToString("X2");
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetKeyNameTextW(int code, StringBuilder text, int count);
}

/// <summary>动作鼠标能力与绑定文案的统一出口，不依赖界面控件。</summary>
public static class ActionMouseBinding
{
    public static bool SupportsClick(string script)
    {
        return ScriptExecutor.TryParse(script, out _, out _);
    }

    public static bool SupportsDrag(string script)
    {
        if (!ScriptExecutor.TryParse(script, out var calls, out _)) return false;
        calls.RemoveAll(call => call.IsWait);
        if (calls.Count == 0) return false;
        if (calls[0].Method == "captureimage") return true;
        if (calls.Count != 1) return false;
        string method = calls[0].Method;
        var args = calls[0].Arguments;
        string arg = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        return method switch
        {
            "screenshot" or "screenshotandedit" or "screenrecord" or "quickrecord" or "longscroll" => true,
            "translate" => arg is "" or "screen" or "capture" or "imagetoimage" or "image"
                or "imagetotext" or "silent" or "clipboardsilent",
            "matting" => arg is not ("sticker" or "pin"),
            _ => false,
        };
    }

    public static bool IsSupported(ActionDefinition action) => action.HasMouseBinding
        && (action.MouseTrigger == ActionMouseTrigger.Drag ? SupportsDrag(action.Script) : SupportsClick(action.Script));

    /// <summary>组合身份包含物理引导键；有逻辑通配的冲突判断统一使用 Conflicts。</summary>
    public static (uint Modifiers, ActionMouseKey Prefix, ActionMouseButton Button) CombinationKey(ActionDefinition action)
        => (EffectiveModifiers(action), action.MousePrefixKey, action.MouseButton);

    public static uint EffectiveModifiers(ActionDefinition action) => action.MouseModifiers & ~action.MousePrefixKey.ModifierMask;

    public static bool Conflicts(ActionDefinition first, ActionDefinition second) => first.HasMouseBinding && second.HasMouseBinding
        && first.MouseButton == second.MouseButton && EffectiveModifiers(first) == EffectiveModifiers(second)
        && (first.MousePrefixKey == default ? second.MousePrefixKey == default : first.MousePrefixKey.Matches(second.MousePrefixKey));

    public static string Format(ActionDefinition action)
    {
        if (!action.HasMouseBinding) return "未绑定";
        var text = new StringBuilder();
        uint modifiers = EffectiveModifiers(action);
        if ((modifiers & 4) != 0) text.Append("Shift＋");
        if ((modifiers & 2) != 0) text.Append("Ctrl＋");
        if ((modifiers & 1) != 0) text.Append("Alt＋");
        if ((modifiers & 8) != 0) text.Append("Win＋");
        if (action.MousePrefixKey.IsValid) text.Append(action.MousePrefixKey).Append('＋');
        text.Append(action.MouseButton switch
        {
            ActionMouseButton.Left => "左键", ActionMouseButton.Right => "右键",
            ActionMouseButton.Middle => "中键", ActionMouseButton.X1 => "X1", _ => "X2",
        });
        return text.Append(action.MouseTrigger == ActionMouseTrigger.Drag ? "拖动" : "点击").ToString();
    }
}
