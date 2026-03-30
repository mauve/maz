using System.Text.RegularExpressions;

namespace Console.Rendering;

public static partial class Ansi
{
    public static readonly bool IsEnabled =
        !System.Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    // Keep backward-compat alias
    private static bool Enabled => IsEnabled;

    public static string Header(string text) => Enabled ? $"\x1b[1;4m{text}\x1b[0m" : text;

    public static string Dim(string text) => Enabled ? $"\x1b[2m{text}\x1b[0m" : text;

    public static string Green(string text) => Enabled ? $"\x1b[32m{text}\x1b[0m" : text;

    public static string Red(string text) => Enabled ? $"\x1b[31m{text}\x1b[0m" : text;

    public static string LightRed(string text) => Enabled ? $"\x1b[91m{text}\x1b[0m" : text;

    public static string Yellow(string text) => Enabled ? $"\x1b[33m{text}\x1b[0m" : text;

    public static string Magenta(string text) => Enabled ? $"\x1b[35m{text}\x1b[0m" : text;

    public static string Bold(string text) => Enabled ? $"\x1b[1m{text}\x1b[0m" : text;

    public static string Cyan(string text) => Enabled ? $"\x1b[96m{text}\x1b[0m" : text;

    public static string White(string text) => Enabled ? $"\x1b[97m{text}\x1b[0m" : text;

    public static string Color(string text, string ansiCode) =>
        Enabled ? $"{ansiCode}{text}\x1b[0m" : text;

    // ── Brand styling ───────────────────────────────────────────────────

    /// <summary>Maz brand color: magenta background with white text.</summary>
    public const string BrandBarCode = "\x1b[97;45m";

    /// <summary>Render a full-width status bar in the maz brand color.</summary>
    public static string BrandBar(string text, int width)
    {
        if (!Enabled)
            return text.Length >= width ? text[..width] : text.PadRight(width);
        var padded = text.Length >= width ? text[..width] : text.PadRight(width);
        return $"{BrandBarCode}{padded}\x1b[0m";
    }

    // ── Throbber / spinner ──────────────────────────────────────────────

    /// <summary>Braille spinner animation frames (10 frames, ~80ms per tick).</summary>
    public static readonly string[] ThrobberFrames =
    [
        "⠋",
        "⠙",
        "⠹",
        "⠸",
        "⠼",
        "⠴",
        "⠦",
        "⠧",
        "⠇",
        "⠏",
    ];

    public static string StyleOptionDescription(string text)
    {
        if (!Enabled)
            return text;
        text = DefaultValueRegex().Replace(text, m => $"\x1b[33m{m.Value}\x1b[0m");
        text = EnvVarTagRegex().Replace(text, m => $"\x1b[36m{m.Value}\x1b[0m");
        text = AllowedValuesTagRegex().Replace(text, m => $"\x1b[32m{m.Value}\x1b[0m");
        text = RequiredTagRegex().Replace(text, m => $"\x1b[91m{m.Value}\x1b[0m");
        return text;
    }

    [GeneratedRegex(@"\[default: [^\]]+\]")]
    private static partial Regex DefaultValueRegex();

    [GeneratedRegex(@"\[env: [^\]]+\]")]
    private static partial Regex EnvVarTagRegex();

    [GeneratedRegex(@"\[allowed: [^\]]+\]")]
    private static partial Regex AllowedValuesTagRegex();

    [GeneratedRegex(@"\[required\]")]
    private static partial Regex RequiredTagRegex();

    public static int VisibleLength(string text) => AnsiEscapeRegex().Replace(text, "").Length;

    [GeneratedRegex(@"\x1b\[[0-9;]*m")]
    private static partial Regex AnsiEscapeRegex();
}
