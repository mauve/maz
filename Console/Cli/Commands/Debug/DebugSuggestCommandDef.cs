using Console.Cli.Parsing;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Debug;

/// <summary>Run the autocompletion logic for a command line and print the suggestions with diagnostic output.</summary>
/// <remarks>
/// The command line argument is the raw shell input to complete.
/// The cursor position is either the end of the string, or the index of the first '^'
/// character (which is then stripped before the completion logic runs).
///
/// Examples:
///   maz debug suggest "maz keyvault secret show "
///   maz debug suggest "maz keyvault secret show my-vau^lt"
/// </remarks>
public partial class DebugSuggestCommandDef : CommandDef
{
    public override string Name => "suggest";
    protected internal override bool IsManualCommand => true;

    public readonly CliArgument<string> CommandLine = new()
    {
        Name = "command-line",
        Description =
            "The command line to complete. "
            + "Insert '^' at the cursor position, or omit to place the cursor at the end.",
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return CommandLine;
    }

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        if (!CommandLine.WasProvided)
            throw new InvocationException("A command line argument is required.");

        var raw = CommandLine.Value!;

        // Determine cursor position from '^' marker, then strip it.
        int caretIndex = raw.IndexOf('^');
        string line;
        int cursorPos;
        if (caretIndex >= 0)
        {
            line = raw.Remove(caretIndex, 1);
            cursorPos = caretIndex;
        }
        else
        {
            line = raw;
            cursorPos = raw.Length;
        }

        // Always log at level 1 — this is a diagnostic command.
        var log = DiagnosticLog.Stderr(level: 1);
        log.Trace($"[debug-suggest] line=\"{line}\" cursor={cursorPos}");

        await CliCompletionHandler.HandleAsync(
            line,
            cursorPos,
            CompletionTree.Root,
            CompletionTree.DynamicProviders,
            System.Console.Out,
            log
        );

        return 0;
    }
}
