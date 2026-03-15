using Console.Rendering;

namespace Console.Tui;

/// <summary>Multiline KQL editor with syntax highlighting, autocomplete popup, and multi-query margin.</summary>
internal sealed class EditorPane
{
    private List<string> _lines;
    private int _cursorLine;
    private int _cursorCol;
    private int _scrollOffset;
    private int? _errorMarkerLine;

    // Autocomplete state
    private List<string> _completions = [];
    private int _completionIndex;
    private bool _autocompleteVisible;

    public bool IsAutocompleteVisible => _autocompleteVisible;

    public EditorPane(string initialQuery)
    {
        var lines = initialQuery.Split('\n').ToList();
        _lines = lines.Count > 0 ? lines : [""];
        _cursorLine = _lines.Count - 1;
        _cursorCol = _lines[_cursorLine].Length;
    }

    public string GetText() => string.Join("\n", _lines);

    /// <summary>Replace editor content and move cursor to end. Clears error marker and autocomplete.</summary>
    public void SetContent(string text)
    {
        var lines = text.Split('\n').ToList();
        _lines = lines.Count > 0 ? lines : [""];
        _cursorLine = _lines.Count - 1;
        _cursorCol = _lines[_cursorLine].Length;
        _scrollOffset = 0;
        _autocompleteVisible = false;
        _errorMarkerLine = null;
    }

    /// <summary>Move the cursor to the given 0-indexed line/col (clamped to valid range).</summary>
    public void SetCursorAt(int line, int col)
    {
        _cursorLine = Math.Clamp(line, 0, _lines.Count - 1);
        _cursorCol = Math.Clamp(col, 0, _lines[_cursorLine].Length);
    }

    /// <summary>Show a red '!' error marker on the given 0-indexed line.</summary>
    public void SetErrorMarker(int line) => _errorMarkerLine = Math.Clamp(line, 0, _lines.Count - 1);

    /// <summary>Remove the error marker.</summary>
    public void ClearErrorMarker() => _errorMarkerLine = null;

    // ── Active query detection ────────────────────────────────────────────────

    /// <summary>
    /// Returns the line range [start, end] (inclusive, 0-indexed) of the contiguous
    /// non-blank block containing the cursor.  Blank lines act as separators.
    /// </summary>
    public (int start, int end) GetActiveQueryRange()
    {
        // If cursor is on a blank line, the active range is just that line (no query).
        if (string.IsNullOrWhiteSpace(_lines[_cursorLine]))
            return (_cursorLine, _cursorLine);

        int start = _cursorLine;
        while (start > 0 && !string.IsNullOrWhiteSpace(_lines[start - 1])) start--;
        int end = _cursorLine;
        while (end < _lines.Count - 1 && !string.IsNullOrWhiteSpace(_lines[end + 1])) end++;
        return (start, end);
    }

    /// <summary>Returns the text of the active query block and its start line index.</summary>
    public (string text, int startLine) GetActiveQueryInfo()
    {
        var (start, end) = GetActiveQueryRange();
        return (string.Join("\n", _lines[start..(end + 1)]), start);
    }

    // ── Word / completion helpers ─────────────────────────────────────────────

    public string GetWordAtCursor()
    {
        var line = _lines[_cursorLine];
        int start = _cursorCol;
        // Include '-' so hyphenated KQL operators like project-away are matched whole
        while (start > 0 && (char.IsLetterOrDigit(line[start - 1]) || line[start - 1] == '_' || line[start - 1] == '-'))
            start--;
        return line[start.._cursorCol];
    }

    public void ShowAutocomplete(List<string> completions)
    {
        _completions = completions;
        _completionIndex = 0;
        _autocompleteVisible = true;
    }

    public bool DismissAutocomplete()
    {
        if (!_autocompleteVisible) return false;
        _autocompleteVisible = false;
        return true;
    }

    public void AutocompleteUp()
    {
        if (_completionIndex > 0) _completionIndex--;
    }

    public void AutocompleteDown()
    {
        if (_completionIndex < _completions.Count - 1) _completionIndex++;
    }

    public void AutocompleteAccept()
    {
        if (!_autocompleteVisible || _completionIndex >= _completions.Count) return;
        var word = GetWordAtCursor();
        var completion = _completions[_completionIndex];
        var line = _lines[_cursorLine];
        int wordStart = _cursorCol - word.Length;
        _lines[_cursorLine] = line[..wordStart] + completion + line[_cursorCol..];
        _cursorCol = wordStart + completion.Length;
        _autocompleteVisible = false;
    }

    /// <summary>Format the active query block in-place (pipe-per-line).</summary>
    public void FormatQuery()
    {
        var (start, end) = GetActiveQueryRange();
        var text = string.Join("\n", _lines[start..(end + 1)]);
        var formatted = KqlFormatter.Format(text);
        var formattedLines = formatted.Split('\n').ToList();
        if (formattedLines.Count == 0) formattedLines = [""];
        _lines.RemoveRange(start, end - start + 1);
        _lines.InsertRange(start, formattedLines);
        _cursorLine = Math.Clamp(_cursorLine, start, start + formattedLines.Count - 1);
        _cursorCol = Math.Min(_cursorCol, _lines[_cursorLine].Length);
        _autocompleteVisible = false;
    }

    // ── Key handling ──────────────────────────────────────────────────────────

    public void HandleKey(ConsoleKeyInfo key)
    {
        _autocompleteVisible = false;
        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:  MoveCursorLeft();  break;
            case ConsoleKey.RightArrow: MoveCursorRight(); break;
            case ConsoleKey.UpArrow:    MoveCursorUp();    break;
            case ConsoleKey.DownArrow:  MoveCursorDown();  break;
            case ConsoleKey.Home:       _cursorCol = 0;    break;
            case ConsoleKey.End:        _cursorCol = _lines[_cursorLine].Length; break;
            case ConsoleKey.Backspace:  DeleteBack();      break;
            case ConsoleKey.Delete:     DeleteForward();   break;
            case ConsoleKey.Enter:      InsertNewline();   break;
            default:
                if (key.Modifiers == ConsoleModifiers.Control)
                    HandleCtrl(key.Key);
                else if (key.KeyChar >= 32 && key.KeyChar != 127)
                    InsertChar(key.KeyChar);
                break;
        }
    }

    private void HandleCtrl(ConsoleKey key)
    {
        switch (key)
        {
            case ConsoleKey.A: _cursorCol = 0; break;
            case ConsoleKey.E: _cursorCol = _lines[_cursorLine].Length; break;
            case ConsoleKey.K: _lines[_cursorLine] = _lines[_cursorLine][.._cursorCol]; break;
            case ConsoleKey.U:
                _lines[_cursorLine] = _lines[_cursorLine][_cursorCol..];
                _cursorCol = 0;
                break;
        }
    }

    private void InsertChar(char c)
    {
        var line = _lines[_cursorLine];
        _lines[_cursorLine] = line[.._cursorCol] + c + line[_cursorCol..];
        _cursorCol++;
    }

    private void InsertNewline()
    {
        var line = _lines[_cursorLine];
        _lines[_cursorLine] = line[.._cursorCol];
        _lines.Insert(_cursorLine + 1, line[_cursorCol..]);
        _cursorLine++;
        _cursorCol = 0;
    }

    private void DeleteBack()
    {
        if (_cursorCol > 0)
        {
            var line = _lines[_cursorLine];
            _lines[_cursorLine] = line[..(_cursorCol - 1)] + line[_cursorCol..];
            _cursorCol--;
        }
        else if (_cursorLine > 0)
        {
            var prevLine = _lines[_cursorLine - 1];
            _cursorCol = prevLine.Length;
            _lines[_cursorLine - 1] = prevLine + _lines[_cursorLine];
            _lines.RemoveAt(_cursorLine);
            _cursorLine--;
        }
    }

    private void DeleteForward()
    {
        var line = _lines[_cursorLine];
        if (_cursorCol < line.Length)
            _lines[_cursorLine] = line[.._cursorCol] + line[(_cursorCol + 1)..];
        else if (_cursorLine < _lines.Count - 1)
        {
            _lines[_cursorLine] = line + _lines[_cursorLine + 1];
            _lines.RemoveAt(_cursorLine + 1);
        }
    }

    private void MoveCursorLeft()
    {
        if (_cursorCol > 0) _cursorCol--;
        else if (_cursorLine > 0) { _cursorLine--; _cursorCol = _lines[_cursorLine].Length; }
    }

    private void MoveCursorRight()
    {
        if (_cursorCol < _lines[_cursorLine].Length) _cursorCol++;
        else if (_cursorLine < _lines.Count - 1) { _cursorLine++; _cursorCol = 0; }
    }

    private void MoveCursorUp()
    {
        if (_cursorLine > 0)
        {
            _cursorLine--;
            _cursorCol = Math.Min(_cursorCol, _lines[_cursorLine].Length);
        }
    }

    private void MoveCursorDown()
    {
        if (_cursorLine < _lines.Count - 1)
        {
            _cursorLine++;
            _cursorCol = Math.Min(_cursorCol, _lines[_cursorLine].Length);
        }
    }

    /// <summary>Returns the 0-indexed screen position of the cursor (includes 2-char left margin).</summary>
    public (int row, int col) GetCursorScreenPosition(int paneTop)
    {
        int editorContentTop = paneTop + 2; // after title + separator
        int row = editorContentTop + (_cursorLine - _scrollOffset);
        int col = _cursorCol + 2; // +2 for left margin
        return (row, col);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    // Layout within [top, top+height):
    //   top+0 : title "  ✏️  KQL Query"
    //   top+1 : separator ────
    //   top+2+: 2-char margin + editor lines
    public void Render(int top, int left, int width, int height)
    {
        if (height < 2) return;

        int contentHeight = Math.Max(1, height - 2);
        int contentWidth = Math.Max(1, width - 2); // 2-char left margin

        // Keep cursor visible — scroll editor
        if (_cursorLine < _scrollOffset)
            _scrollOffset = _cursorLine;
        if (_cursorLine >= _scrollOffset + contentHeight)
            _scrollOffset = _cursorLine - contentHeight + 1;

        // Determine active query range for ▶ indicator
        var (activeStart, activeEnd) = GetActiveQueryRange();
        bool showActiveMarker = HasMultipleQueryBlocks();

        // ── Title ──
        MoveTo(top, left);
        WriteCell(Ansi.Bold("  ✏️  KQL Query"), width);

        // ── Separator ──
        MoveTo(top + 1, left);
        System.Console.Write(new string('─', width));

        // ── Editor lines with left margin ──
        for (int r = 0; r < contentHeight; r++)
        {
            MoveTo(top + 2 + r, left);
            int lineIndex = _scrollOffset + r;
            if (lineIndex < _lines.Count)
            {
                // Left margin (2 chars)
                System.Console.Write(GetMarginString(lineIndex, activeStart, activeEnd, showActiveMarker));
                // Content
                var highlighted = KqlHighlighter.Highlight(_lines[lineIndex]);
                var vis = Ansi.VisibleLength(highlighted);
                if (vis >= contentWidth)
                {
                    System.Console.Write(ResultsPane.TruncateAnsi(highlighted, contentWidth - 1));
                    System.Console.Write(' ');
                }
                else
                {
                    System.Console.Write(highlighted);
                    System.Console.Write(new string(' ', contentWidth - vis));
                }
            }
            else
            {
                System.Console.Write(new string(' ', width));
            }
        }

        // ── Autocomplete popup ──
        if (_autocompleteVisible && _completions.Count > 0)
            RenderAutocomplete(top, left, width);
    }

    private string GetMarginString(int lineIndex, int activeStart, int activeEnd, bool showActiveMarker)
    {
        if (lineIndex == _errorMarkerLine)
            return Ansi.LightRed("! ");
        if (showActiveMarker && lineIndex >= activeStart && lineIndex <= activeEnd
            && !string.IsNullOrWhiteSpace(_lines[lineIndex]))
            return Ansi.Color("│ ", "\x1b[2;94m"); // dim bright-blue vertical bar
        return "  ";
    }

    private bool HasMultipleQueryBlocks()
    {
        bool seenContent = false;
        bool seenBlankAfterContent = false;
        foreach (var line in _lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (seenContent) seenBlankAfterContent = true;
            }
            else
            {
                if (seenBlankAfterContent) return true;
                seenContent = true;
            }
        }
        return false;
    }

    private void RenderAutocomplete(int paneTop, int left, int width)
    {
        int cursorScreenRow = paneTop + 2 + (_cursorLine - _scrollOffset);

        const int PopupWidth = 32;
        int popupLeft = left + 2 + Math.Min(_cursorCol, Math.Max(0, width - 2 - PopupWidth));
        int maxItems = Math.Min(8, _completions.Count);

        for (int i = 0; i < maxItems; i++)
        {
            MoveTo(cursorScreenRow + 1 + i, popupLeft);
            var item = _completions[i];
            bool selected = i == _completionIndex;

            var text = (" " + item).PadRight(PopupWidth);
            if (text.Length > PopupWidth) text = text[..PopupWidth];

            System.Console.Write(selected
                ? Ansi.Color(text, "\x1b[7m")  // reverse video for selection
                : Ansi.Dim(text));
        }
    }

    private static void MoveTo(int row, int col)
        => System.Console.Write($"\x1b[{row + 1};{col + 1}H");

    private static void WriteCell(string text, int width)
    {
        var vis = Ansi.VisibleLength(text);
        if (vis >= width)
            System.Console.Write(ResultsPane.TruncateAnsi(text, width));
        else
        {
            System.Console.Write(text);
            System.Console.Write(new string(' ', width - vis));
        }
    }
}
