using Console.Rendering;

namespace Console.Tui;

/// <summary>Renders query results as a scrollable Unicode box table.</summary>
internal sealed class ResultsPane
{
    private IReadOnlyList<string> _columns = [];
    private IReadOnlyList<string> _columnTypes = [];
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _rows = [];
    private int _scrollOffset;
    private string _workspaceName = "";
    private TimeSpan _lastElapsed;
    private bool _isLoading;
    private int _spinnerFrame;
    private string? _errorMessage; // clean display message
    private string? _errorQueryLine; // specific query line with the error
    private int? _errorLineNum; // 1-based line number in submitted query
    private int? _errorCol; // 0-based column within _errorQueryLine
    private string? _partialError;
    private string? _historyBadge;
    private bool _isHistoryMode;

    // Cell selection state
    private int _selectedRow = 0;
    private int _selectedCol = 0;
    private HashSet<string> _hiddenColumns = new();
    private Dictionary<string, int> _manualWidths = new();

    // Saved render dimensions for context menu positioning
    private int _renderTop;
    private int _renderLeft;
    private int _renderWidth;
    private int _renderHeight;

    // Context menu state
    private bool _contextMenuVisible;
    private int _contextMenuIndex;
    private List<string> _contextMenuLabels = [];

    public bool IsContextMenuVisible => _contextMenuVisible;

    private static readonly string[] SpinnerFrames =
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

    public void SetResults(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> columnTypes,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        TimeSpan elapsed,
        string? partialError = null
    )
    {
        _columns = columns;
        _columnTypes = columnTypes;
        _rows = rows;
        _lastElapsed = elapsed;
        _scrollOffset = 0;
        _selectedRow = 0;
        _selectedCol = 0;
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
    public void SetError(
        string message,
        string? queryLine = null,
        int? lineNumber = null,
        int? errorCol = null
    )
    {
        // Collapse any remaining newlines so WordWrap never sends raw \n to the terminal
        _errorMessage = message.ReplaceLineEndings(" ").Replace("  ", " ").Trim();
        _errorQueryLine = queryLine;
        _errorLineNum = lineNumber;
        _errorCol = errorCol;
        _isLoading = false;
        _partialError = null;
    }

    public void SetLoading(bool loading)
    {
        _isLoading = loading;
        if (!loading)
            _spinnerFrame = 0;
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

    public void SetHistoryBadge(int pos, int total) => _historyBadge = $"[↑ {pos}/{total} ↓]";

    public void ClearHistoryBadge() => _historyBadge = null;

    public void SetHistoryMode(bool isHistory) => _isHistoryMode = isHistory;

    public void ScrollUp()
    {
        if (_scrollOffset > 0)
            _scrollOffset--;
    }

    public void ScrollDown()
    {
        if (_scrollOffset < _rows.Count - 1)
            _scrollOffset++;
    }

    public void PageUp(int pageSize) => _scrollOffset = Math.Max(0, _scrollOffset - pageSize);

    public void PageDown(int pageSize) =>
        _scrollOffset = Math.Min(Math.Max(0, _rows.Count - 1), _scrollOffset + pageSize);

    // ── Cell selection ────────────────────────────────────────────────────────

    /// <summary>Called when focus switches to results; resets selection to the first visible cell.</summary>
    public void InitSelection()
    {
        _selectedRow = _scrollOffset;
        _selectedCol = 0;
    }

    public void MoveSelectionUp()
    {
        if (_selectedRow > 0)
            _selectedRow--;
        ClampScroll();
    }

    public void MoveSelectionDown()
    {
        if (_selectedRow < _rows.Count - 1)
            _selectedRow++;
        ClampScroll();
    }

    public void MoveSelectionLeft()
    {
        if (_selectedCol > 0)
            _selectedCol--;
    }

    public void MoveSelectionRight()
    {
        var vis = GetVisibleColumns();
        if (_selectedCol < vis.Count - 1)
            _selectedCol++;
    }

    private void ClampScroll()
    {
        int dataRows = Math.Max(1, _renderHeight - 4);
        if (_selectedRow < _scrollOffset)
            _scrollOffset = _selectedRow;
        if (_selectedRow >= _scrollOffset + dataRows)
            _scrollOffset = _selectedRow - dataRows + 1;
    }

    public void FitSelectedColumnToContents(int paneHeight)
    {
        var vis = GetVisibleColumns();
        if (_selectedCol >= vis.Count)
            return;
        var colName = vis[_selectedCol];
        int maxLen = colName.Length;
        foreach (var row in _rows)
        {
            var len = Ansi.VisibleLength(FormatValue(row.GetValueOrDefault(colName)));
            if (len > maxLen)
                maxLen = len;
        }
        _manualWidths[colName] = maxLen;
    }

    public void HideSelectedColumn()
    {
        var vis = GetVisibleColumns();
        if (_selectedCol >= vis.Count)
            return;
        _hiddenColumns.Add(vis[_selectedCol]);
        var newVis = GetVisibleColumns();
        _selectedCol = Math.Min(_selectedCol, Math.Max(0, newVis.Count - 1));
    }

    public (string colName, string colType, object? cellValue) GetSelectedInfo()
    {
        var vis = GetVisibleColumns();
        if (vis.Count == 0 || _selectedCol >= vis.Count)
            return ("", "", null);
        var colName = vis[_selectedCol];
        int origIdx = -1;
        for (int i = 0; i < _columns.Count; i++)
            if (_columns[i] == colName) { origIdx = i; break; }
        var colType = origIdx >= 0 && origIdx < _columnTypes.Count ? _columnTypes[origIdx] : "";
        var cellValue = _selectedRow < _rows.Count
            ? _rows[_selectedRow].GetValueOrDefault(colName)
            : null;
        return (colName, colType, cellValue);
    }

    public IReadOnlyList<string> GetColumns() => _columns;
    public IReadOnlyList<string> GetColumnTypes() => _columnTypes;
    public IReadOnlySet<string> GetHiddenColumns() => _hiddenColumns;

    public void ToggleColumnVisibility(string colName)
    {
        if (!_hiddenColumns.Remove(colName))
            _hiddenColumns.Add(colName);
        var vis = GetVisibleColumns();
        _selectedCol = Math.Min(_selectedCol, Math.Max(0, vis.Count - 1));
    }

    private List<string> GetVisibleColumns() =>
        _columns.Where(c => !_hiddenColumns.Contains(c)).ToList();

    // ── Context menu ──────────────────────────────────────────────────────────

    public void ShowContextMenu(List<string> labels)
    {
        _contextMenuLabels = labels;
        _contextMenuIndex = 0;
        _contextMenuVisible = true;
    }

    public void DismissContextMenu() => _contextMenuVisible = false;

    public void ContextMenuUp()
    {
        if (_contextMenuIndex > 0)
            _contextMenuIndex--;
    }

    public void ContextMenuDown()
    {
        if (_contextMenuIndex < _contextMenuLabels.Count - 1)
            _contextMenuIndex++;
    }

    /// <summary>Returns selected index and dismisses, or null if not visible.</summary>
    public int? AcceptContextMenuItem()
    {
        if (!_contextMenuVisible)
            return null;
        var idx = _contextMenuIndex;
        _contextMenuVisible = false;
        return idx;
    }

    // Layout (top/left are 0-indexed screen row/col):
    //   top+0 : title bar
    //   top+1 : separator ────
    //   top+2 : column headers   (only when columns exist and height >= 5)
    //   top+3 : header separator ────┼────
    //   top+4+: data rows
    public void Render(int top, int left, int width, int height, bool focused = false)
    {
        _renderTop = top;
        _renderLeft = left;
        _renderWidth = width;
        _renderHeight = height;

        if (height < 2)
            return;

        // ── Title ──
        var spinner = _isLoading ? SpinnerFrames[_spinnerFrame % SpinnerFrames.Length] + " " : "  ";
        var wsInfo = _workspaceName.Length > 0 ? $"[{_workspaceName}]  " : "";
        var rowCount =
            _isLoading ? "running…"
            : _errorMessage is not null ? Ansi.LightRed("error")
            : $"{_rows.Count} row{(_rows.Count == 1 ? "" : "s")}";
        var timeInfo =
            !_isLoading && _errorMessage is null ? $"  ⏱ {_lastElapsed.TotalSeconds:F1}s" : "";
        var partialWarn = _partialError is not null ? $"  {Ansi.Yellow("⚠")}" : "";
        var badge = _historyBadge is not null ? $"  {Ansi.Dim(_historyBadge)}" : "";
        var title = $" {spinner}📊 Results  {wsInfo}{rowCount}{partialWarn}{timeInfo}{badge} ";

        MoveTo(top, left);
        string styledTitle;
        if (_isHistoryMode)
        {
            styledTitle = focused ? Ansi.Color(title, "\x1b[7;2m") : Ansi.Dim(title);
        }
        else
        {
            styledTitle = focused ? Ansi.Color(title, "\x1b[1;7m") : Ansi.Bold(title);
        }
        WriteCell(styledTitle, width);

        // ── Separator ──
        if (height < 2)
            return;
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
                if (row >= errorRows)
                    break;
                MoveTo(top + 2 + row++, left);
                WriteCell("  " + Ansi.LightRed(wl), width);
            }

            // Query extract with caret — needs 3 more rows: blank + query line + caret
            if (_errorQueryLine is not null && _errorCol.HasValue && row + 3 <= errorRows)
            {
                // blank separator
                MoveTo(top + 2 + row++, left);
                ClearLine(width);

                var numStr = _errorLineNum?.ToString() ?? "?";
                var prefix = "  " + numStr.PadLeft(2) + " │ "; // e.g. "   2 │ " — fixed 7 chars for 1–99
                int contentWidth = Math.Max(4, width - prefix.Length);

                var (displayLine, displayCol) = WindowQueryLine(
                    _errorQueryLine,
                    _errorCol.Value,
                    contentWidth
                );

                MoveTo(top + 2 + row++, left);
                WriteCell(Ansi.Dim(prefix) + displayLine, width);

                int caretScreenCol = prefix.Length + displayCol;
                MoveTo(top + 2 + row++, left);
                WriteCell(
                    new string(' ', Math.Min(caretScreenCol, width - 1)) + Ansi.Yellow("^"),
                    width
                );
            }

            // Clear remaining rows
            while (row < errorRows)
            {
                MoveTo(top + 2 + row++, left);
                ClearLine(width);
            }
            return;
        }

        // ── Empty state ──
        if (_columns.Count == 0)
        {
            if (height > 2)
            {
                MoveTo(top + 2, left);
                var msg =
                    _isLoading ? "  Waiting for results…"
                    : _isHistoryMode ? Ansi.Dim("  History entry — press F5 to re-run")
                    : "  No results. Press F5 to run a query.";
                WriteCell(msg, width);
            }
            for (int r = 3; r < height; r++)
            {
                MoveTo(top + r, left);
                ClearLine(width);
            }
            return;
        }

        if (height < 5)
            return;

        var visibleCols = GetVisibleColumns();
        if (visibleCols.Count == 0)
        {
            MoveTo(top + 2, left);
            WriteCell("  All columns hidden.", width);
            for (int r = 3; r < height; r++)
            {
                MoveTo(top + r, left);
                ClearLine(width);
            }
            return;
        }

        // Clamp selected col to visible range
        _selectedCol = Math.Min(_selectedCol, visibleCols.Count - 1);

        // ── Column headers ──
        int[] colWidths = ComputeColumnWidths(width, visibleCols);
        MoveTo(top + 2, left);
        var headerCells = visibleCols
            .Select((c, i) =>
                i == _selectedCol && focused ? Ansi.Color(c, "\x1b[1;4m") : Ansi.Bold(c)
            )
            .ToList();
        RenderRow(headerCells, colWidths, width);

        // ── Header separator ──
        MoveTo(top + 3, left);
        var sepSb = new System.Text.StringBuilder();
        for (int ci = 0; ci < colWidths.Length; ci++)
        {
            if (ci > 0)
                sepSb.Append('┼');
            sepSb.Append(new string('─', colWidths[ci]));
        }
        var sepStr = sepSb.ToString();
        if (sepStr.Length < width)
            sepStr = sepStr.PadRight(width);
        System.Console.Write(sepStr[..Math.Min(sepStr.Length, width)]);

        // ── Data rows ──
        int dataRows = height - 4;
        for (int r = 0; r < dataRows; r++)
        {
            MoveTo(top + 4 + r, left);
            int rowIndex = _scrollOffset + r;
            if (rowIndex < _rows.Count)
            {
                int highlightCol = (focused && rowIndex == _selectedRow) ? _selectedCol : -1;
                RenderRow(
                    visibleCols
                        .Select(c => FormatValue(_rows[rowIndex].GetValueOrDefault(c)))
                        .ToList(),
                    colWidths,
                    width,
                    highlightCol
                );
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

        // ── Context menu overlay ──
        if (_contextMenuVisible && focused)
            RenderContextMenu(colWidths, visibleCols, top, dataRows);
    }

    private void RenderContextMenu(int[] colWidths, List<string> visibleCols, int top, int dataRows)
    {
        // Position below the selected data row
        int dataRowScreenRow = top + 4 + (_selectedRow - _scrollOffset);
        int popupRow = dataRowScreenRow + 1;

        // Compute x offset of the selected column
        int colOffset = _renderLeft;
        for (int i = 0; i < _selectedCol && i < colWidths.Length; i++)
            colOffset += colWidths[i] + 1; // +1 for separator char

        const int PopupWidth = 36;
        int popupLeft = Math.Min(colOffset, Math.Max(0, _renderLeft + _renderWidth - PopupWidth));

        int maxItems = Math.Min(8, _contextMenuLabels.Count);
        for (int i = 0; i < maxItems; i++)
        {
            int screenRow = popupRow + i;
            if (screenRow >= top + _renderHeight)
                break;
            MoveTo(screenRow, popupLeft);
            var label = " " + _contextMenuLabels[i];
            label = label.Length > PopupWidth ? label[..PopupWidth] : label.PadRight(PopupWidth);
            System.Console.Write(
                i == _contextMenuIndex ? Ansi.Color(label, "\x1b[7m") : Ansi.Dim(label)
            );
        }
    }

    private static void RenderRow(
        List<string> cells,
        int[] colWidths,
        int totalWidth,
        int highlightCol = -1
    )
    {
        var sb = new System.Text.StringBuilder();
        for (int ci = 0; ci < colWidths.Length && ci < cells.Count; ci++)
        {
            if (ci > 0)
                sb.Append('│');
            var cell = cells[ci];
            var visLen = Ansi.VisibleLength(cell);
            int w = colWidths[ci];

            if (ci == highlightCol)
            {
                // Strip ANSI codes and apply reverse-video highlight
                var plain = StripAnsi(cell);
                string cellStr;
                if (plain.Length >= w)
                    cellStr = plain[..(w - 1)] + "…";
                else
                    cellStr = plain + new string(' ', w - plain.Length);
                sb.Append($"\x1b[7m{cellStr}\x1b[0m");
            }
            else if (visLen >= w)
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

    private static string FormatValue(object? v) =>
        v switch
        {
            null => Ansi.Dim("∅"),
            DateTimeOffset dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            TimeSpan ts => ts.ToString(@"d\.hh\:mm\:ss"),
            bool b => b ? Ansi.Green("true") : Ansi.Yellow("false"),
            _ => v.ToString() ?? "",
        };

    private int[] ComputeColumnWidths(int totalWidth, List<string> visibleCols)
    {
        if (visibleCols.Count == 0)
            return [];
        int separators = visibleCols.Count - 1;
        int available = Math.Max(visibleCols.Count * 5, totalWidth - separators);

        var natural = new int[visibleCols.Count];
        for (int i = 0; i < visibleCols.Count; i++)
        {
            var col = visibleCols[i];
            if (_manualWidths.TryGetValue(col, out var manual))
            {
                natural[i] = manual; // no cap for manually set widths
                continue;
            }
            natural[i] = Math.Max(col.Length, 5);
            foreach (var row in _rows.Take(50))
            {
                var len = Ansi.VisibleLength(FormatValue(row.GetValueOrDefault(col)));
                if (len > natural[i])
                    natural[i] = len;
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
                if (totalNatural > 0)
                {
                    for (int i = 0; i < natural.Length; i++)
                        natural[i] += (int)((double)natural[i] / totalNatural * extra);
                    // Rounding residual goes to the last column
                    int residual = available - natural.Sum();
                    if (residual > 0)
                        natural[^1] += residual;
                }
            }
            return natural;
        }

        int total = natural.Sum();
        var widths = new int[visibleCols.Count];
        for (int i = 0; i < visibleCols.Count; i++)
            widths[i] = Math.Max(5, (int)((double)natural[i] / total * available));
        return widths;
    }

    /// <summary>
    /// Returns a windowed view of <paramref name="line"/> that keeps <paramref name="col"/> visible,
    /// together with the adjusted column offset for the caret. Adds "…" when truncated.
    /// </summary>
    private static (string display, int col) WindowQueryLine(string line, int col, int maxWidth)
    {
        if (line.Length <= maxWidth)
            return (line, col);

        // Keep at least a few chars of left context before the error token
        const int leftContext = 6;
        int start = Math.Max(0, col - leftContext);
        bool addLeft = start > 0;
        int budget = maxWidth - (addLeft ? 1 : 0);
        bool addRight = start + budget < line.Length;
        if (addRight)
            budget--;

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
        if (maxWidth <= 0 || string.IsNullOrEmpty(text))
            return result;

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
            while (pos < text.Length && text[pos] == ' ')
                pos++;
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
            if (c == '\x1b')
            {
                inEscape = true;
                sb.Append(c);
                continue;
            }
            if (inEscape)
            {
                sb.Append(c);
                if (char.IsLetter(c))
                    inEscape = false;
                continue;
            }
            if (vis >= maxVisible)
                break;
            sb.Append(c);
            vis++;
        }
        if (vis > 0)
            sb.Append("\x1b[0m");
        return sb.ToString();
    }

    private static string StripAnsi(string text)
    {
        var sb = new System.Text.StringBuilder();
        bool inEscape = false;
        foreach (char c in text)
        {
            if (c == '\x1b') { inEscape = true; continue; }
            if (inEscape) { if (char.IsLetter(c)) inEscape = false; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void MoveTo(int row, int col) =>
        System.Console.Write($"\x1b[{row + 1};{col + 1}H");

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

    private static void ClearLine(int width) => System.Console.Write(new string(' ', width));
}
