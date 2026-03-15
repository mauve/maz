using System.Text.Json;

namespace Console.Tui;

/// <summary>
/// Strips HTTP noise from Azure SDK error messages and extracts the innermost structured error,
/// including position info for syntax errors.
/// </summary>
internal static class AzureErrorParser
{
    /// <param name="DisplayMessage">Human-readable message, e.g. "SYN0002: Query could not be parsed at '|'"</param>
    /// <param name="QueryLine">The specific line of the submitted query where the error occurred, or null.</param>
    /// <param name="LineNumber">1-based line number within the submitted query, or null.</param>
    /// <param name="Column">0-based column within <see cref="QueryLine"/>, or null.</param>
    internal readonly record struct ParsedError(
        string DisplayMessage,
        string? QueryLine,
        int? LineNumber,
        int? Column
    );

    /// <summary>
    /// Parse a raw Azure SDK exception message and, if the query text is supplied,
    /// extract the specific line + column of the error.
    /// </summary>
    internal static ParsedError Parse(string rawMessage, string? queryText = null)
    {
        // Normalize line endings first. The Azure SDK RequestFailedException puts "Content:" and
        // "Headers:" on their own lines (e.g. "Content:\n{...}\nHeaders:\n..."), and also embeds
        // literal \n inside JSON string values. Collapsing everything to spaces upfront makes the
        // Content: marker findable and the JSON body parseable in one pass.
        var msg0 = rawMessage.ReplaceLineEndings(" ").Replace("  ", " ");

        (string? msg, string? code, int? jsonLine, int? jsonPos) = (null, null, null, null);

        int contentIdx = msg0.IndexOf("Content: {", StringComparison.Ordinal);
        if (contentIdx >= 0)
        {
            var jsonSlice = msg0[(contentIdx + "Content: ".Length)..];

            // Strip " Headers: ..." that follows the JSON body
            int headersIdx = jsonSlice.IndexOf("} Headers:", StringComparison.Ordinal);
            if (headersIdx >= 0)
                jsonSlice = jsonSlice[..(headersIdx + 1)];

            try
            {
                using var doc = JsonDocument.Parse(jsonSlice);
                if (doc.RootElement.TryGetProperty("error", out var errorEl))
                {
                    // Standard Azure Monitor format: {"error":{...}}
                    (msg, code, jsonLine, jsonPos) = ExtractDeepest(errorEl);
                }
                else if (doc.RootElement.TryGetProperty("message", out var rootMsg))
                {
                    // Alternative format: {"code":"...","message":"..."} with no "error" wrapper
                    msg = rootMsg.GetString();
                    code = doc.RootElement.TryGetProperty("code", out var rootCode)
                        ? rootCode.GetString()
                        : null;
                }
            }
            catch
            { /* malformed JSON — fall through to text fallback */
            }
        }

        // Build the display message
        string displayMessage;
        if (msg is not null)
        {
            displayMessage = !string.IsNullOrEmpty(code) ? $"{code}: {msg}" : msg;
        }
        else
        {
            // Fallback: take the text before the first HTTP-noise marker from the normalized string
            displayMessage = msg0;
            foreach (var cut in new[] { " Status:", " Content:", " Headers:" })
            {
                int idx = displayMessage.IndexOf(cut, StringComparison.Ordinal);
                if (idx > 0)
                    displayMessage = displayMessage[..idx];
            }
            displayMessage = displayMessage.Trim();
        }

        // Extract the relevant query line and convert position to 0-based
        string? queryLine = null;
        int? column = null;
        if (jsonLine.HasValue && queryText is not null)
        {
            var lines = queryText.Split('\n');
            int lineIdx = jsonLine.Value - 1; // 1-based → 0-based
            if (lineIdx >= 0 && lineIdx < lines.Length)
            {
                queryLine = lines[lineIdx];
                // KQL reports pos as 1-based; convert to 0-based, clamp to line length
                column = jsonPos.HasValue ? Math.Clamp(jsonPos.Value - 1, 0, queryLine.Length) : 0;
            }
        }

        return new ParsedError(displayMessage, queryLine, jsonLine, column);
    }

    // Recurse into innererror to find the most specific error node (deepest with position or code).
    private static (string? msg, string? code, int? line, int? pos) ExtractDeepest(JsonElement el)
    {
        string? msg = el.TryGetProperty("message", out var m) ? m.GetString() : null;
        string? code = el.TryGetProperty("code", out var c) ? c.GetString() : null;
        int? line = el.TryGetProperty("line", out var l) ? l.GetInt32() : (int?)null;
        int? pos = el.TryGetProperty("pos", out var p) ? p.GetInt32() : (int?)null;

        if (el.TryGetProperty("innererror", out var inner))
        {
            var (innerMsg, innerCode, innerLine, innerPos) = ExtractDeepest(inner);
            // Prefer the inner level when it carries more specific info
            if (innerMsg is not null || innerLine.HasValue || !string.IsNullOrEmpty(innerCode))
            {
                return (innerMsg ?? msg, innerCode ?? code, innerLine ?? line, innerPos ?? pos);
            }
        }

        return (msg, code, line, pos);
    }
}
