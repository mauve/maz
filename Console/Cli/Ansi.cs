using System.Text.RegularExpressions;

namespace Console.Cli;

internal static partial class Ansi
{
    private static readonly bool Enabled =
        !System.Console.IsOutputRedirected
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && Environment.GetEnvironmentVariable("TERM") != "dumb";

    internal static string Header(string text) => Enabled ? $"\x1b[1;4m{text}\x1b[0m" : text;

    internal static string Dim(string text) => Enabled ? $"\x1b[2m{text}\x1b[0m" : text;

    internal static string StyleOptionDescription(string text)
    {
        if (!Enabled)
            return text;
        text = DefaultValueRegex().Replace(text, m => $"\x1b[33m{m.Value}\x1b[0m");
        text = EnvVarTagRegex().Replace(text, m => $"\x1b[36m{m.Value}\x1b[0m");
        return text;
    }

    [GeneratedRegex(@"\[default: [^\]]+\]")]
    private static partial Regex DefaultValueRegex();

    [GeneratedRegex(@"\[env: [^\]]+\]")]
    private static partial Regex EnvVarTagRegex();
}
