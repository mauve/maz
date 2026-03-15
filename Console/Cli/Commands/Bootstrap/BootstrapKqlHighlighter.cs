using Console.Rendering;
using Console.Tui;

namespace Console.Cli.Commands.Bootstrap;

/// <summary>
/// Multi-line KQL highlighter for bootstrap demos.
/// Delegates per-line colouring to the shared <see cref="KqlHighlighter"/> and adds
/// multi-line support and optional error annotation.
/// </summary>
internal static class BootstrapKqlHighlighter
{
    public static string Highlight(string query) =>
        Highlight(query, errorLine: null, errorColumn: null, errorMessage: null);

    /// <summary>
    /// Highlights KQL syntax and optionally inserts a caret marker for a positional error,
    /// matching what the interactive TUI renders from <c>AzureErrorParser.ParsedError</c>.
    /// </summary>
    /// <param name="query">The KQL query text (may be multi-line).</param>
    /// <param name="errorLine">1-based line number of the error, or null for no error.</param>
    /// <param name="errorColumn">0-based column of the error within that line, or null for col 0.</param>
    /// <param name="errorMessage">Error code + message, e.g. "SYN0002: Query could not be parsed at '|'"</param>
    public static string Highlight(
        string query,
        int? errorLine,
        int? errorColumn,
        string? errorMessage
    )
    {
        if (!Ansi.IsEnabled)
            return query;

        var lines = query.Split('\n');
        var result = new List<string>(lines.Length + 2);

        for (var i = 0; i < lines.Length; i++)
        {
            var highlighted = KqlHighlighter.Highlight(lines[i]);
            result.Add(highlighted);

            // After the error line insert a caret pointing at the error column.
            if (errorLine.HasValue && i == errorLine.Value - 1 && errorMessage is not null)
            {
                var col = Math.Clamp(errorColumn ?? 0, 0, lines[i].Length);
                result.Add(new string(' ', col) + Ansi.Red("^ " + errorMessage));
            }
        }

        return string.Join('\n', result);
    }
}
