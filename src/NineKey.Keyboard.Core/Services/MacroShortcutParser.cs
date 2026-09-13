// 本文件职责：把 "Ctrl+S"、"Win+Shift+S" 等用户输入字符串解析为虚拟键码数组。
// 数据流位置：宏配置 UI / 设置文件 → Parse → ushort[] → TextInjector.InjectShortcut。
// ⚠ 坑 1：只支持单主键，多个主键直接返回空数组。
// ⚠ 坑 2：修饰键必须按 Win/Ctrl/Shift/Alt 固定顺序输出，否则目标应用可能不认组合（§13.28）。
// 相关规格：§13.28。

using System.Globalization;

namespace NineKey.Keyboard.Services;

/// <summary>宏按键组合字符串解析（§13.28）：仅支持修饰键+单主键，如 "Ctrl+S" / "Win+Shift+S"。</summary>
public static class MacroShortcutParser
{
    /// <summary>解析按键组合字符串为虚拟键码数组。解析失败返回空数组。</summary>
    public static ushort[] Parse(string shortcut)
    {
        if (string.IsNullOrWhiteSpace(shortcut))
        {
            return [];
        }

        var parts = shortcut.Split('+', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        if (parts.Count == 0)
        {
            return [];
        }

        var modifiers = new List<ushort>();
        string? mainKey = null;
        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            switch (lower)
            {
                case "ctrl":
                case "control":
                    modifiers.Add(NativeMethods.VkControl);
                    break;
                case "shift":
                    modifiers.Add(NativeMethods.VkShift);
                    break;
                case "alt":
                    modifiers.Add(NativeMethods.VkMenu);
                    break;
                case "win":
                case "windows":
                    modifiers.Add(NativeMethods.VkLWin);
                    break;
                default:
                    if (mainKey is not null)
                    {
                        return []; // 多个主键，不支持
                    }

                    mainKey = lower;
                    break;
            }
        }

        if (mainKey is null || !TryParseMainKey(mainKey, out var mainVk))
        {
            return [];
        }

        // ⚠ 坑：修饰键按 Win / Ctrl / Shift / Alt 固定顺序输出，最后主键；顺序错误会导致目标应用不认组合。
        var result = new List<ushort>();
        if (modifiers.Contains(NativeMethods.VkLWin)) result.Add(NativeMethods.VkLWin);
        if (modifiers.Contains(NativeMethods.VkControl)) result.Add(NativeMethods.VkControl);
        if (modifiers.Contains(NativeMethods.VkShift)) result.Add(NativeMethods.VkShift);
        if (modifiers.Contains(NativeMethods.VkMenu)) result.Add(NativeMethods.VkMenu);
        result.Add(mainVk);
        return result.ToArray();
    }

    private static bool TryParseMainKey(string key, out ushort vk)
    {
        vk = 0;

        // 单字母 A-Z
        if (key.Length == 1)
        {
            var c = key[0];
            if (c is >= 'a' and <= 'z')
            {
                vk = (ushort)(NativeMethods.VkA + (c - 'a'));
                return true;
            }

            if (c is >= '0' and <= '9')
            {
                vk = (ushort)(NativeMethods.Vk0 + (c - '0'));
                return true;
            }
        }

        // 功能键 F1-F12
        if (key.Length >= 2 &&
            key[0] == 'f' &&
            int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var fn) &&
            fn is >= 1 and <= 12)
        {
            vk = (ushort)(NativeMethods.VkF1 + (fn - 1));
            return true;
        }

        vk = key switch
        {
            "space" or "空格" => NativeMethods.VkSpace,
            "enter" or "return" or "回车" => NativeMethods.VkReturn,
            "tab" => NativeMethods.VkTab,
            "back" or "backspace" or "退格" => NativeMethods.VkBack,
            "delete" or "del" or "删除" => NativeMethods.VkDelete,
            "insert" or "ins" => NativeMethods.VkInsert,
            "escape" or "esc" => NativeMethods.VkEscape,
            "home" => NativeMethods.VkHome,
            "end" => NativeMethods.VkEnd,
            "pageup" or "pgup" or "prior" => NativeMethods.VkPrior,
            "pagedown" or "pgdn" or "next" => NativeMethods.VkNext,
            "left" or "左" => NativeMethods.VkLeft,
            "up" or "上" => NativeMethods.VkUp,
            "right" or "右" => NativeMethods.VkRight,
            "down" or "下" => NativeMethods.VkDown,
            _ => (ushort)0,
        };

        return vk != 0;
    }
}
