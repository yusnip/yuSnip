using System.Collections.Generic;

namespace ScreenCaptureTool.Core.Actions;

/// <summary>动作键盘绑定的附加辅助键；高位仅供内部匹配，不能传给 RegisterHotKey。</summary>
public static class ActionKeyboardBinding
{
    public const uint CapsLock = 0x10000, Tab = 0x20000, Space = 0x40000;
    public const uint ExtendedMask = CapsLock | Tab | Space;
    public const uint NativeMask = 15;

    public static uint ModifierOf(uint key) => key switch
    {
        0x14 => CapsLock, 0x09 => Tab, 0x20 => Space,
        0x10 or 0xA0 or 0xA1 => 4, 0x11 or 0xA2 or 0xA3 => 2,
        0x12 or 0xA4 or 0xA5 => 1, 0x5B or 0x5C => 8, _ => 0
    };

    public static bool IsExtended(uint modifiers) => (modifiers & ExtendedMask) != 0;
    public static bool IsValid(uint modifiers, uint key) => key is >= 8 and <= 254
        && (modifiers & ~(NativeMask | ExtendedMask)) == 0
        && (!IsExtended(modifiers) || ModifierOf(key) == 0);

    public static bool Conflicts(uint firstModifiers, uint firstKey, uint secondModifiers, uint secondKey)
        => firstModifiers == secondModifiers && firstKey == secondKey
        || PrefixConflict(firstModifiers, firstKey, secondModifiers)
        || PrefixConflict(secondModifiers, secondKey, firstModifiers);

    private static bool PrefixConflict(uint modifiers, uint key, uint extendedModifiers)
        => !IsExtended(modifiers) && IsExtended(extendedModifiers)
        && (ModifierOf(key) & extendedModifiers & ExtendedMask) != 0
        && (modifiers & ~(extendedModifiers & NativeMask)) == 0;

    public static void AddModifierNames(ICollection<string> parts, uint modifiers)
    {
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        if ((modifiers & CapsLock) != 0) parts.Add("CapsLock");
        if ((modifiers & Tab) != 0) parts.Add("Tab");
        if ((modifiers & Space) != 0) parts.Add("Space");
    }
}
