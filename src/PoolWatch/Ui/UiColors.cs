namespace PoolWatch.Ui;

/// <summary>
/// The app's one palette, as the approved designs have it (accent blue, the green of "safe", amber for a watch, the
/// impact scale's yellow, orange and red, and the greys of text, lines and panels): the main window, setup, Active
/// changes and the Services and Tasks and startup apps tabs all take their colours from here, and each uses the system's
/// own colours under high contrast.
/// </summary>
internal static class UiColors
{
    public static readonly Color Accent = Color.FromArgb(15, 108, 189), AccentSoft = Color.FromArgb(238, 245, 252);
    /// <summary>A filled accent button under the mouse, and pressed.</summary>
    public static readonly Color AccentHover = Color.FromArgb(17, 94, 163), AccentDown = Color.FromArgb(12, 74, 128);
    public static readonly Color Good = Color.FromArgb(26, 127, 55), GoodSoft = Color.FromArgb(240, 248, 242);
    public static readonly Color Watch = Color.FromArgb(181, 71, 8), WatchSoft = Color.FromArgb(255, 244, 229);
    public static readonly Color Risk = Color.FromArgb(178, 34, 34), RiskSoft = Color.FromArgb(253, 236, 236);
    /// <summary>A caution in text ("cannot be put back"), and a red button for what cannot be undone.</summary>
    public static readonly Color Warn = Color.FromArgb(154, 103, 0), Danger = Color.FromArgb(164, 14, 38);
    public static readonly Color Ink = Color.FromArgb(31, 35, 40), Muted = Color.FromArgb(89, 99, 110), Faint = Color.FromArgb(102, 112, 122);
    /// <summary>Outline icons and glyphs.</summary>
    public static readonly Color Icon = Color.FromArgb(68, 80, 92);
    public static readonly Color Line = Color.FromArgb(227, 230, 234), Panel = Color.FromArgb(246, 247, 249), Track = Color.FromArgb(223, 227, 232);
    /// <summary>The line between a list's rows; a control's border, and a disabled one's.</summary>
    public static readonly Color RowLine = Color.FromArgb(238, 240, 242), Border = Color.FromArgb(198, 204, 211), DisabledBorder = Color.FromArgb(208, 215, 222);
    /// <summary>A plain button under the mouse (and a disabled one's fill), and pressed.</summary>
    public static readonly Color Hover = Color.FromArgb(243, 244, 246), Pressed = Color.FromArgb(232, 234, 237);
    /// <summary>The impact scale: Low, Medium, High.</summary>
    public static readonly Color Low = Color.FromArgb(212, 167, 44), Medium = Color.FromArgb(232, 89, 12), High = Color.FromArgb(201, 42, 42);

    public static bool HighContrast => SystemInformation.HighContrast;

    /// <summary>Text: the design's ink, or the system's text colour under high contrast.</summary>
    public static Color Text => HighContrast ? SystemColors.WindowText : Ink;

    /// <summary>Secondary text: the design's grey, or the system's grey text under high contrast.</summary>
    public static Color Secondary => HighContrast ? SystemColors.GrayText : Muted;

    /// <summary>A rule or a border.</summary>
    public static Color Rule => HighContrast ? SystemColors.GrayText : Line;
}
