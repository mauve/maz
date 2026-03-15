using Console.Rendering;

namespace Console.Tui;

/// <summary>Renders query results as a scrollable Unicode box table.</summary>
internal sealed class ResultsPane
{
    private IReadOnlyList<string> _columns = [];
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows = [];
    private int _scrollOffset;
    private string _workspaceName = "";
    private TimeSpan _lastElapsed;
    private bool _isLoading;
    private int _spinnerFrame;
    private string? _errorMessage;    // clean display message
    private string? _errorQueryLine;  // specific query line with the error
    private int?    _errorLineNum;    // 1-based line number in submitted query
    private int?    _errorCol;        // 0-based column within _errorQueryLine
    private string? _partialError;
    private string? _historyBadge;
    private bool _isHistoryMode;

    private static readonly string[] SpinnerFrames =
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public void SetResults(
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        TimeSpan elapsed,
        string? partialError = null)
    {
        _columns = columns;
        _rows = rows;
        _lastElapsed = elapsed;
        _scrollOffset = 0;
        _isLoading = false;
        _errorMessage = null;
        _partialError = partialError is not null
            ? partialError.ReplaceLineEndings(" ").Replace("  ", " ").Trim()
            : null;
    }

    /// <param name="message">Clean, human-readable error text (no HTTP headers).</param>
    /// <param name="queryLine">The specific query line where the error occurred, if known.</param>
    /// <param name="lineNumber">1-based line number within the submitted query.</param>
    /// <param name="errorCol">0-based column within <paramref name="queryLine"/>.</param>
    public void SetError(string message, string? queryLine = null, int? lineNumber = null, int? errorCol = null)
    {
        // Collapse any remaining newlines so WordWrap never sends raw \n to the terminal
        _errorMessage   = message.ReplaceLineEndings(" ").Replace("  ", " ").Trim();
        _errorQueryLine = queryLine;
        _errorLineNum   = lineNumber;
        _errorCol       = errorCol;
        _isLoading      = false;
        _partialError   = null;
    }

    public void SetLoading(bool loading)
    {
        _isLoading = loading;
        if (!loading) _spinnerFrame = 0;
    }

    /// <summary>Show as empty with a "re-run from history" hint (disk-loaded entry, no in-memory results).</summary>
    public void SetHistoryPlaceholder()
    {
        _columns = [];
        _rows = [];
        _lastElapsed = TimeSpan.Zero;
        _isLoading = false;
        _errorMessage = null;
        _partialError = null;
    }

    public void TickSpinner() => _spinnerFrame++;
    public void SetWorkspace(string name) => _workspaceName = name;

    public void SetHistoryBadge(int pos, int total) =>
        _historyBadge = $"[↑ {pos}/{total} ↓]";
    public void ClearHistoryBadge() => _historyBadge = null;
    public void SetHistoryMode(bool isHistory) => _isHistoryMode = isHistory;

    public void ScrollUp() { if (_scrollOffset > 0) _scrollOffset--; }
    public void ScrollDown() { if (_scrollOffset < _rows.Count - 1) _scrollOffset++; }
    public void PageUp(int pageSize) => _scrollOffset = Math.Max(0, _scrollOffset - pageSize);
    public void PageDown(int pageSize) => _scrollOffset = Math.Min(Math.Max(0, _rows.Count - 1), _scrollOffset + pageSize);

    // Layout (top/left are 0-indexed screen row/col):
    //   top+0 : title bar
    //   top+1 : separator ────
    //   top+2 : column headers   (only when columns exist and height >= 5)
    //   top+3 : header separator ────┼────
    //   top+4+: data rows
    public void Render(int top, int left, int width, int height, bool focused = false)
    {
        if (height < 2) return;

        // ── Title ──
        var spinner = _isLoading
            ? SpinnerFrames[_spinnerFrame % SpinnerFrames.Length] + " "
            : "  ";
        var wsInfo = _workspaceName.Length > 0 ? $"[{_workspaceName}]  " : "";
        var rowCount = _isLoading
            ? "running…"
            : _errorMessage is not null
                ? Ansi.LightRed("error")
                : $"{_rows.Count} row{(_rows.Count == 1 ? "" : "s")}";
        var timeInfo = !_isLoading && _errorMessage is null
            ? $"  ⏱ {_lastElapsed.TotalSeconds:F1}s"
            : "";
        var partialWarn = _partialError is not null ? $"  {Ansi.Yellow("⚠")}" : "";
        var badge = _historyBadge is not null ? $"  {Ansi.Dim(_historyBadge)}" : "";
        var title = $" {spinner}📊 Results  {wsInfo}{rowCount}{partialWarn}{timeInfo}{badge} ";

        MoveTo(top, left);
        string styledTitle;
        if (_isHistoryMode)
        {
            // Dim title when browsing history to make it visually distinct
            styledTitle = focused
                ? Ansi.Color(title, "\x1b[7;2m")
                : Ansi.Dim(title);
        }
        else
        {
            styledTitle = focused
                ? Ansi.Color(title, "\x1b[1;7m")
                : Ansi.Bold(title);
        }
        WriteCell(styledTitle, width);

        // ── Separator ──
        if (height < 2) return;
        MoveTo(top + 1, left);
        System.Console.Write(new string('─', width));

        // ── Error state ──
        if (_errorMessage is not null)
        {
            int errorRows = height - 2;
            int row = 0;

            // Word-wrapped error message
            var wrapped = WordWrap(_errorMessage, Math.Max(10, width - 2));
            foreach (var wl in wrapped)
            {
                if (row >= errorRows) break;
                MoveTo(top + 2 + row++, left);
                WriteCell("  " + Ansi.LightRed(wl), width);
            }

            // Query extract with caret — needs 3 more rows: blank + query line + caret
            if (_errorQueryLine is not null && _errorCol.HasValue && row + 3 <= errorRows)
            {
                // blank separator
                MoveTo(top + 2 + row++, left); ClearLine(width);

                var numStr = _errorLineNum?.ToString() ?? "?";
                var prefix = "  " + numStr.PadLeft(2) + " │ "; // e.g. "   2 │ " — fixed 7 chars for 1–99
                int contentWidth = Math.Max(4, width - prefix.Length);

                var (displayLine, displayCol) = WindowQueryLine(_errorQueryLine, _errorCol.Value, contentWidth);

                MoveTo(top + 2 + row++, left);
                WriteCell(Ansi.Dim(prefix) + displayLine, width);

                int caretScreenCol = prefix.Length + displayCol;
                MoveTo(top + 2 + row++, left);
                WriteCell(new string(' ', Math.Min(caretScreenCol, width - 1)) + Ansi.Yellow("^"), width);
            }

            // Clear remaining rows
            while (row < errorRows) { MoveTo(top + 2 + row++, left); ClearLine(width); }
            return;
        }

        // ── Empty state ──
        if (_columns.Count == 0)
        {
            if (height > 2)
            {
                MoveTo(top + 2, left);
                var msg = _isLoading
                    ? "  Waiting for results…"
                    : _isHistoryMode
                        ? Ansi.Dim("  History entry — press F5 to re-run")
                        : "  No results. Press F5 to run a query.";
                WriteCell(msg, width);
            }
            for (int r = 3; r < height; r++) { MoveTo(top + r, left); ClearLine(width); }
            return;
        }

        if (height < 5) return;

        // ── Column headers ──
        int[] colWidths = ComputeColumnWidths(width);
        MoveTo(top + 2, left);
        RenderRow(_columns.Select(c => Ansi.Bold(c)).ToList(), colWidths, width);

        // ── Header separator ──
        MoveTo(top + 3, left);
        var sepSb = new System.Text.StringBuilder();
        for (int ci = 0; ci < colWidths.Length; ci++)
        {
            if (ci > 0) sepSb.Append('┼');
            sepSb.Append(new string('─', colWidths[ci]));
        }
        var sepStr = sepSb.ToString();
        if (sepStr.Length < width) sepStr = sepStr.PadRight(width);
        System.Console.Write(sepStr[..Math.Min(sepStr.Length, width)]);

        // ── Data rows ──
        int dataRows = height - 4;
        for (int r = 0; r < dataRows; r++)
        {
            MoveTo(top + 4 + r, left);
            int rowIndex = _scrollOffset + r;
            if (rowIndex < _rows.Count)
            {
                RenderRow(
                    _columns.Select(c => FormatValue(_rows[rowIndex].GetValueOrDefault(c))).ToList(),
                    colWidths,
                    width);
            }
            else if (_partialError is not null && rowIndex == _rows.Count)
            {
                // Show partial error warning on the first row after data
                WriteCell("  " + Ansi.Yellow("⚠  " + _partialError), width);
            }
            else
            {
                ClearLine(width);
            }
        }
    }

    private static void RenderRow(List<string> cells, int[] colWidths, int totalWidth)
    {
        var sb = new System.Text.StringBuilder();
        for (int ci = 0; ci < colWidths.Length && ci < cells.Count; ci++)
        {
            if (ci > 0) sb.Append('│');
            var cell = cells[ci];
            var visLen = Ansi.VisibleLength(cell);
            int w = colWidths[ci];
            if (visLen >= w)
            {
                sb.Append(TruncateAnsi(cell, w - 1));
                sb.Append('…');
            }
            else
            {
                sb.Append(cell);
                sb.Append(new string(' ', w - visLen));
            }
        }
        int totalUsed = colWidths.Sum() + Math.Max(0, colWidths.Length - 1);
        if (totalUsed < totalWidth)
            sb.Append(new string(' ', totalWidth - totalUsed));
        System.Console.Write(sb);
    }

    private static string FormatValue(object? v) => v switch
    {
        null => Ansi.Dim("∅"),
        DateTimeOffset dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
        TimeSpan ts => ts.ToString(@"d\.hh\:mm\:ss"),
        bool b => b ? Ansi.Green("true") : Ansi.Yellow("false"),
        _ => v.ToString() ?? "",
    };

    private int[] ComputeColumnWidths(int totalWidth)
    {
        if (_columns.Count == 0) return [];
        int separators = _columns.Count - 1;
        int available = Math.Max(_columns.Count * 5, totalWidth - separators);

        var natural = new int[_columns.Count];
        for (int i = 0; i < _columns.Count; i++)
        {
            natural[i] = Math.Max(_columns[i].Length, 5);
            foreach (var row in _rows.Take(50))
            {
                var len = Ansi.VisibleLength(FormatValue(row.GetValueOrDefault(_columns[i])));
                if (len > natural[i]) natural[i] = len;
            }
            natural[i] = Math.Min(natural[i], 40); // cap at 40
        }

        if (natural.Sum() <= available)
        {
            // Stretch columns proportionally to fill the full available width.
            int extra = available - natural.Sum();
            if (extra > 0)
            {
                int totalNatural = natural.Sum();
                for (int i = 0; i < natural.Length; i++)
                    natural[i] += (int)((double)natural[i] / totalNatural * extra);
                // Rounding residual goes to the last column
                int residual = available - natural.Sum();
                if (residual > 0)
                    natural[^1] += residual;
            }
            return natural;
        }

        int total = natural.Sum();
        var widths = new int[_columns.Count];
        for (int i = 0; i < _columns.Count; i++)
            widths[i] = Math.Max(5, (int)((double)natural[i] / total * available));
        return widths;
    }

    /// <summary>
    /// Returns a windowed view of <paramref name="line"/> that keeps <paramref name="col"/> visible,
    /// together with the adjusted column offset for the caret. Adds "…" when truncated.
    /// </summary>
    private static (string display, int col) WindowQueryLine(string line, int col, int maxWidth)
    {
        if (line.Length <= maxWidth) return (line, col);

        // Keep at least a few chars of left context before the error token
        const int leftContext = 6;
        int start = Math.Max(0, col - leftContext);
        bool addLeft  = start > 0;
        int budget    = maxWidth - (addLeft ? 1 : 0);
        bool addRight = start + budget < line.Length;
        if (addRight) budget--;

        var slice = line[start..Math.Min(line.Length, start + budget)];
        var display = (addLeft ? "…" : "") + slice + (addRight ? "…" : "");
        int adjustedCol = Math.Clamp(col - start + (addLeft ? 1 : 0), 0, display.Length);
        return (display, adjustedCol);
    }

    /// <summary>
    /// Splits <paramref name="text"/> into lines of at most <paramref name="maxWidth"/> visible
    /// characters, breaking at word boundaries (spaces) where possible.
    /// </summary>
    private static List<string> WordWrap(string text, int maxWidth)
    {
        var result = new List<string>();
        if (maxWidth <= 0 || string.IsNullOrEmpty(text)) return result;

        int pos = 0;
        while (pos < text.Length)
        {
            if (text.Length - pos <= maxWidth)
            {
                result.Add(text[pos..]);
                break;
            }

            // Find the last space within the allowed width
            int end = pos + maxWidth;
            int breakAt = text.LastIndexOf(' ', end - 1, end - pos);
            if (breakAt <= pos)
            {
                // No space found — hard-break at maxWidth
                result.Add(text[pos..end]);
                pos = end;
            }
            else
            {
                result.Add(text[pos..breakAt]);
                pos = breakAt + 1; // skip the space
            }

            // Skip any leading spaces at the start of the next visual line
            while (pos < text.Length && text[pos] == ' ') pos++;
        }
        return result;
    }

    internal static string TruncateAnsi(string text, int maxVisible)
    {
        int vis = 0;
        var sb = new System.Text.StringBuilder();
        bool inEscape = false;
        foreach (char c in text)
        {
            if (c == '\x1b') { inEscape = true; sb.Append(c); continue; }
            if (inEscape) { sb.Append(c); if (char.IsLetter(c)) inEscape = false; continue; }
            if (vis >= maxVisible) break;
            sb.Append(c);
            vis++;
        }
        if (vis > 0) sb.Append("\x1b[0m");
        return sb.ToString();
    }

    private static void MoveTo(int row, int col)
        => System.Console.Write($"\x1b[{row + 1};{col + 1}H");

    private static void WriteCell(string text, int width)
    {
        var vis = Ansi.VisibleLength(text);
        if (vis >= width)
            System.Console.Write(TruncateAnsi(text, width));
        else
        {
            System.Console.Write(text);
            System.Console.Write(new string(' ', width - vis));
        }
    }

    private static void ClearLine(int width)
        => System.Console.Write(new string(' ', width));
}
