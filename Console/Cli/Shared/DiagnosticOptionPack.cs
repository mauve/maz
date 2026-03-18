using System.CommandLine;
using System.CommandLine.Parsing;
using Console.Config;

namespace Console.Cli.Shared;

[CliManualOptions("--detailed-errors", "-v", "-vv", "--verbose-body-limit")]
public class DiagnosticOptionPack : OptionPack
{
    /// <summary>
    /// Static reference so <see cref="CommandDef"/> can read the value from any
    /// <see cref="ParseResult"/> without needing a pack instance.
    /// </summary>
    internal static readonly GlobalOption<bool> DetailedErrorsOption = new(
        "--detailed-errors",
        "Show detailed error output including exception type and stack trace."
    );

    internal static readonly GlobalOption<bool> VerboseOption = new(
        "-v",
        "Diagnostic output: credential interactions and HTTP request/response headers."
    );

    internal static readonly GlobalOption<bool> VeryVerboseOption = new(
        "-vv",
        "Like -v but also includes request and response bodies."
    )
    {
        Hidden = true,
    };

    internal static readonly GlobalOption<int> BodyLimitOption = new(
        "--verbose-body-limit",
        "Maximum bytes of request/response body to display in diagnostics."
    )
    {
        Hidden = true,
        DefaultValueFactory = _ => 8192,
    };

    public bool DetailedErrors => GetValue(DetailedErrorsOption);

    public override string HelpTitle => "Diagnostics";

    protected override void AddManualOptions(Command cmd)
    {
        cmd.Add(DetailedErrorsOption);
        cmd.Add(VerboseOption);
        cmd.Add(VeryVerboseOption);

        cmd.Add(BodyLimitOption);
    }

    /// <summary>
    /// Reads the verbose level from a <see cref="ParseResult"/>.
    /// -vv = 2, -v = 1, otherwise 0.
    /// </summary>
    public static int GetVerboseLevel(ParseResult result)
    {
        if (result.GetValue(VeryVerboseOption)) return 2;
        if (result.GetValue(VerboseOption)) return 1;
        return 0;
    }

    /// <summary>
    /// Creates a <see cref="DiagnosticLog"/> from the current parse result and config.
    /// </summary>
    public static DiagnosticLog GetLog(ParseResult result)
    {
        var level = GetVerboseLevel(result);
        if (level <= 0) return DiagnosticLog.Null;

        var config = MazConfig.Current;
        var bodyLimit = result.GetValue(BodyLimitOption);
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
