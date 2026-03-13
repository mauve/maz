using System.Text.RegularExpressions;

namespace Console.Cli;

internal static partial class Ansi
{
    internal static readonly bool IsEnabled =
        !System.Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    // Keep backward-compat alias
    private static bool Enabled => IsEnabled;

    internal static string Header(string text) => Enabled ? $"\x1b[1;4m{text}\x1b[0m" : text;

    internal static string Dim(string text) => Enabled ? $"\x1b[2m{text}\x1b[0m" : text;

    internal static string Green(string text) => Enabled ? $"\x1b[32m{text}\x1b[0m" : text;

    internal static string Red(string text) => Enabled ? $"\x1b[31m{text}\x1b[0m" : text;

    internal static string Yellow(string text) => Enabled ? $"\x1b[33m{text}\x1b[0m" : text;

    internal static string Magenta(string text) => Enabled ? $"\x1b[35m{text}\x1b[0m" : text;

    internal static string Bold(string text) => Enabled ? $"\x1b[1m{text}\x1b[0m" : text;

    internal static string Color(string text, string ansiCode) =>
        Enabled ? $"{ansiCode}{text}\x1b[0m" : text;

    internal static string StyleOptionDescription(string text)
    {
        if (!Enabled)
            return text;
        text = DefaultValueRegex().Replace(text, m => $"\x1b[33m{m.Value}\x1b[0m");
        text = EnvVarTagRegex().Replace(text, m => $"\x1b[36m{m.Value}\x1b[0m");
        text = AllowedValuesTagRegex().Replace(text, m => $"\x1b[32m{m.Value}\x1b[0m");
        return text;
    }

    [GeneratedRegex(@"\[default: [^\]]+\]")]
    private static partial Regex DefaultValueRegex();

    [GeneratedRegex(@"\[env: [^\]]+\]")]
    private static partial Regex EnvVarTagRegex();

    [GeneratedRegex(@"\[allowed: [^\]]+\]")]
    private static partial Regex AllowedValuesTagRegex();
}
