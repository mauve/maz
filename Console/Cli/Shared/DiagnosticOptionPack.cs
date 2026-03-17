using Console.Cli.Parsing;
using Console.Config;

namespace Console.Cli.Shared;

[CliManualOptions("--detailed-errors", "-v", "-vv", "--verbose-body-limit")]
public class DiagnosticOptionPack : OptionPack
{
    internal readonly CliOption<bool> _detailedErrorsOption = new()
    {
        Name = "--detailed-errors",
        Aliases = ["--verbose"],
        Description = "Show detailed error output including exception type and stack trace.",
        Recursive = true,
    };

    internal static readonly CliOption<bool> VerboseOption = new()
    {
        Name = "-v",
        Description = "Diagnostic output: credential interactions and HTTP request/response headers.",
        Recursive = true,
    };

    internal static readonly CliOption<bool> VeryVerboseOption = new()
    {
        Name = "-vv",
        Description = "Like -v but also includes request and response bodies.",
        Recursive = true,
        Hidden = true,
    };

    internal static readonly CliOption<int> BodyLimitOption = new()
    {
        Name = "--verbose-body-limit",
        Description = "Maximum bytes of request/response body to display in diagnostics.",
        Recursive = true,
        Hidden = true,
        DefaultValue = 8192,
    };

    public bool DetailedErrors => GetValue(_detailedErrorsOption);

    public override string HelpTitle => "Diagnostics";

    internal override IEnumerable<CliOption> EnumerateManualOptions()
    {
        yield return _detailedErrorsOption;
        yield return VerboseOption;
        yield return VeryVerboseOption;
        yield return BodyLimitOption;
    }

    /// <summary>
    /// Reads the verbose level from the parsed options.
    /// -vv = 2, -v = 1, otherwise 0.
    /// </summary>
    public static int GetVerboseLevel()
    {
        if (VeryVerboseOption.Value) return 2;
        if (VerboseOption.Value) return 1;
        return 0;
    }

    /// <summary>
    /// Creates a <see cref="DiagnosticLog"/> from the current parsed options and config.
    /// </summary>
    public static DiagnosticLog GetLog()
    {
        var level = GetVerboseLevel();
        if (level <= 0) return DiagnosticLog.Null;

        var config = MazConfig.Current;
        var bodyLimit = BodyLimitOption.Value;
        if (bodyLimit <= 0) bodyLimit = 8192;

        // Read config overrides (only when CLI didn't explicitly set the limit)
        if (config.GlobalDefaults.TryGetValue("verbose-body-limit", out var bl)
            && int.TryParse(bl, out var configLimit)
            && bodyLimit == 8192)
        {
            bodyLimit = configLimit;
        }

        var absoluteTimestamps = false;
        if (config.GlobalDefaults.TryGetValue("verbose-timestamp", out var ts))
            absoluteTimestamps = ts.Equals("absolute", StringComparison.OrdinalIgnoreCase);

        return DiagnosticLog.Stderr(level, absoluteTimestamps, bodyLimit);
    }
}
