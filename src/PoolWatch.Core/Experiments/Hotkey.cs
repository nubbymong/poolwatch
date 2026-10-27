namespace PoolWatch.Core.Experiments;

/// <summary>"Ctrl+Alt+M" to RegisterHotKey's modifier flags and virtual key (letters, digits, F1-F24).</summary>
public static class Hotkey
{
    public const uint Alt = 1, Control = 2, Shift = 4, Win = 8, NoRepeat = 0x4000;

    /// <summary>Null for an empty or unusable string (a hotkey needs Ctrl, Alt or Win, and one key).</summary>
    public static (uint modifiers, uint vk)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint mods = 0, vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string p = raw.ToUpperInvariant();
            switch (p)
            {
                case "CTRL" or "CONTROL": mods |= Control; break;
                case "ALT": mods |= Alt; break;
                case "SHIFT": mods |= Shift; break;
                case "WIN" or "WINDOWS": mods |= Win; break;
                default:
                    if (vk != 0) return null;
                    if (p.Length == 1 && p[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') vk = p[0];
                    else if (p.Length is 2 or 3 && p[0] == 'F' && int.TryParse(p[1..], out int f) && f is >= 1 and <= 24) vk = (uint)(0x70 + f - 1);
                    else return null;
                    break;
            }
        }
        return vk != 0 && (mods & (Control | Alt | Win)) != 0 ? (mods, vk) : null;
    }
}
