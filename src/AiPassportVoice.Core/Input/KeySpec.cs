using System.Text;

namespace AiPassportVoice.Core.Input;

/// <summary>单个按键定义：虚拟键码 + PS/2 set-1 扫描码 + 是否扩展键。</summary>
public readonly record struct KeyDef(string Name, ushort Vk, ushort Scan, bool Extended);

/// <summary>
/// 热键描述解析。移植自 windows/hotkey_forwarder.py 的 KEY_MAP / resolve_key_spec，
/// 行为与键位表保持一致（含组合键 "win+h" / "ctrl+shift+space"）。
/// </summary>
public static class KeySpec
{
    private static readonly IReadOnlyDictionary<string, KeyDef> Map = BuildMap();

    private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
    {
        "left-ctrl", "right-ctrl", "left-alt", "right-alt",
        "left-shift", "right-shift", "left-win", "right-win",
    };

    private static readonly Dictionary<string, string> ModifierAliases = new(StringComparer.Ordinal)
    {
        ["ctrl"] = "left-ctrl",
        ["alt"] = "left-alt",
        ["shift"] = "left-shift",
        ["win"] = "left-win",
    };

    public static IReadOnlyDictionary<string, KeyDef> All => Map;

    /// <summary>
    /// 解析热键描述 → 按键序列。组合键规则：最后一个必须是普通键，其余必须是修饰键。
    /// </summary>
    public static IReadOnlyList<KeyDef> Resolve(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new ArgumentException("热键为空", nameof(spec));

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.Trim().ToLowerInvariant())
                        .ToArray();
        if (parts.Length == 0)
            throw new ArgumentException("热键为空", nameof(spec));

        var keys = new List<KeyDef>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var name = ModifierAliases.TryGetValue(parts[i], out var alias) ? alias : parts[i];
            if (!Map.TryGetValue(name, out var def))
                throw new ArgumentException($"未知键名 '{parts[i]}'", nameof(spec));
            if (i < parts.Length - 1 && !Modifiers.Contains(name))
                throw new ArgumentException($"组合键里 '{parts[i]}' 必须是修饰键(ctrl/alt/shift/win)，且放在前面", nameof(spec));
            keys.Add(def with { Name = name });
        }
        return keys;
    }

    private static Dictionary<string, KeyDef> BuildMap()
    {
        var map = new Dictionary<string, KeyDef>(StringComparer.Ordinal)
        {
            ["right-shift"] = new("right-shift", 0xA1, 0x36, false),
            ["left-shift"] = new("left-shift", 0xA0, 0x2A, false),
            ["right-ctrl"] = new("right-ctrl", 0xA3, 0x1D, true),
            ["right-alt"] = new("right-alt", 0xA5, 0x38, true),
            ["f6"] = new("f6", 0x75, 0x40, false),
            ["f9"] = new("f9", 0x78, 0x43, false),
            ["f10"] = new("f10", 0x79, 0x44, false),

            ["left-ctrl"] = new("left-ctrl", 0xA2, 0x1D, false),
            ["left-alt"] = new("left-alt", 0xA4, 0x38, false),
            ["left-win"] = new("left-win", 0x5B, 0x5B, true),
            ["right-win"] = new("right-win", 0x5C, 0x5C, true),
            ["caps"] = new("caps", 0x14, 0x3A, false),
            ["space"] = new("space", 0x20, 0x39, false),
            ["enter"] = new("enter", 0x0D, 0x1C, false),
            ["tab"] = new("tab", 0x09, 0x0F, false),
            ["backspace"] = new("backspace", 0x08, 0x0E, false),
            ["escape"] = new("escape", 0x1B, 0x01, false),
        };

        ushort[] fScans = { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58 };
        for (var i = 0; i < fScans.Length; i++)
            map[$"f{i + 1}"] = new($"f{i + 1}", (ushort)(0x70 + i), fScans[i], false);

        const string letters = "qwertyuiopasdfghjklzxcvbnm";
        ushort[] letterScans =
        {
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19,
            0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,
            0x2C, 0x2D, 0x2E, 0x2F, 0x30, 0x31, 0x32,
        };
        for (var i = 0; i < letters.Length; i++)
        {
            var ch = letters[i];
            map[ch.ToString()] = new KeyDef(ch.ToString(), (ushort)(0x41 + (ch - 'a')), letterScans[i], false);
        }

        for (var i = 0; i < 10; i++)
        {
            var digit = (i + 1) % 10;
            map[digit.ToString()] = new KeyDef(digit.ToString(), (ushort)(0x30 + digit), (ushort)(0x02 + i), false);
        }

        return map;
    }

    /// <summary>格式化键名显示(用于日志)。</summary>
    public static string Describe(IReadOnlyList<KeyDef> keys) => string.Join("+", keys.Select(k => k.Name));
}
