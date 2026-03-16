using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Sidebar pane showing the current result's columns (with hide/show checkboxes) and
/// all known tables from the schema cache (tables appearing in the active query come first).
/// </summary>
internal sealed class SchemaPane
{
    // State — refreshed via Update* before each render
    private IReadOnlyList<string> _columns = [];
    private IReadOnlyList<string> _columnTypes = [];
    private IReadOnlySet<string> _hiddenColumns = new HashSet<string>();
    private IReadOnlyList<string> _sortedTables = [];
    private IReadOnlySet<string> _activeTables = new HashSet<string>();

    // Navigation
    private int _selectedVisualIndex;
    private int _scrollOffset;
    private int _lastRenderHeight;

    // ── Visual item list ──────────────────────────────────────────────────────

    private abstract record VisualItem;
    private sealed record SectionHeader(string Label) : VisualItem;
    private sealed record ColumnItem(string Name, string Type, bool IsHidden) : VisualItem;
    private sealed record TableItem(string Name, bool IsActive) : VisualItem;

    private List<VisualItem> _items = [];

    // ── Public update API ─────────────────────────────────────────────────────

    public void UpdateColumns(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> columnTypes,
        IReadOnlySet<string> hiddenColumns
    )
    {
        _columns = columns;
        _columnTypes = columnTypes;
        _hiddenColumns = hiddenColumns;
        Rebuild();
    }

    public void UpdateTables(IReadOnlyList<string> tables, IReadOnlySet<string> activeTables)
    {
        _activeTables = activeTables;
        // Active tables first (sorted), then remainder (sorted)
        _sortedTables =
        [
            .. tables
                .Where(t => activeTables.Contains(t, StringComparer.OrdinalIgnoreCase))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
            .. tables
                .Where(t => !activeTables.Contains(t, StringComparer.OrdinalIgnoreCase))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
        ];
        Rebuild();
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public void MoveUp()
    {
        for (int i = _selectedVisualIndex - 1; i >= 0; i--)
            if (IsSelectable(_items[i]))
            {
                _selectedVisualIndex = i;
                ClampScroll();
                return;
            }
    }

    public void MoveDown()
    {
        for (int i = _selectedVisualIndex + 1; i < _items.Count; i++)
            if (IsSelectable(_items[i]))
            {
                _selectedVisualIndex = i;
                ClampScroll();
                return;
            }
    }

    /// <summary>Returns the column name if the current selection is a column item, else null.</summary>
    public string? GetSelectedColumnName() =>
        _selectedVisualIndex < _items.Count && _items[_selectedVisualIndex] is ColumnItem col
            ? col.Name
            : null;

    // ── Rendering ─────────────────────────────────────────────────────────────

    public void Render(int top, int left, int width, int height, bool focused)
    {
        _lastRenderHeight = height;
        if (height < 2 || width < 6)
            return;

        // ── Title ──
        MoveTo(top, left);
        var title = " Schema";
        WriteCell(focused ? Ansi.Color(title, "\x1b[1;7m") : Ansi.Bold(title), width);

        // ── Separator ──
        MoveTo(top + 1, left);
        System.Console.Write(new string('─', width));

        if (height < 3)
            return;

        int displayRows = height - 2;
        int row = 0;
        for (int i = _scrollOffset; i < _items.Count && row < displayRows; i++, row++)
        {
            MoveTo(top + 2 + row, left);
            RenderItem(_items[i], i == _selectedVisualIndex && focused, width);
        }
        while (row < displayRows)
        {
            MoveTo(top + 2 + row++, left);
            System.Console.Write(new string(' ', width));
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Rebuild()
    {
        _items = [];

        if (_columns.Count > 0)
        {
            _items.Add(new SectionHeader("Columns"));
            for (int i = 0; i < _columns.Count; i++)
            {
                var type = i < _columnTypes.Count ? _columnTypes[i] : "";
                _items.Add(new ColumnItem(_columns[i], type, _hiddenColumns.Contains(_columns[i])));
            }
        }

        if (_sortedTables.Count > 0)
        {
            _items.Add(new SectionHeader("Tables"));
            foreach (var t in _sortedTables)
                _items.Add(new TableItem(t, _activeTables.Contains(t, StringComparer.OrdinalIgnoreCase)));
        }

        ClampSelection();
    }

    private static bool IsSelectable(VisualItem item) => item is ColumnItem or TableItem;

    private void ClampSelection()
    {
        if (_items.Count == 0)
        {
            _selectedVisualIndex = 0;
            return;
        }
        _selectedVisualIndex = Math.Clamp(_selectedVisualIndex, 0, _items.Count - 1);
        if (!IsSelectable(_items[_selectedVisualIndex]))
        {
            // Prefer first selectable item below, then above
            for (int i = _selectedVisualIndex + 1; i < _items.Count; i++)
                if (IsSelectable(_items[i])) { _selectedVisualIndex = i; return; }
            for (int i = _selectedVisualIndex - 1; i >= 0; i--)
                if (IsSelectable(_items[i])) { _selectedVisualIndex = i; return; }
        }
    }

    private void ClampScroll()
    {
        int rows = Math.Max(1, _lastRenderHeight - 2);
        if (_selectedVisualIndex < _scrollOffset)
            _scrollOffset = _selectedVisualIndex;
        if (_selectedVisualIndex >= _scrollOffset + rows)
            _scrollOffset = _selectedVisualIndex - rows + 1;
        _scrollOffset = Math.Max(0, _scrollOffset);
    }

    private static void RenderItem(VisualItem item, bool selected, int width)
    {
        switch (item)
        {
            case SectionHeader h:
            {
                // " Label ─────────"
                var label = " " + h.Label + " ";
                var dashes = new string('─', Math.Max(0, width - label.Length));
                WriteCell(Ansi.Dim(label + dashes), width);
                break;
            }
            case ColumnItem col:
            {
                // " [x] Name      type"  or  " [ ] Name      type" (dim when hidden)
                var checkbox = col.IsHidden ? "[ ] " : "[x] ";
                int prefixLen = 5; // " " + 4 for checkbox
                int typeMaxLen = col.Type.Length > 0 ? Math.Min(col.Type.Length, 8) : 0;
                int typePartLen = typeMaxLen > 0 ? typeMaxLen + 1 : 0; // " type"
                int namePartLen = Math.Max(0, width - prefixLen - typePartLen);
                var name = col.Name.Length > namePartLen
                    ? col.Name[..namePartLen]
                    : col.Name.PadRight(namePartLen);
                var typePart = typeMaxLen > 0
                    ? " " + (col.Type.Length > typeMaxLen ? col.Type[..typeMaxLen] : col.Type)
                    : "";
                var line = $" {checkbox}{name}{typePart}";

                if (col.IsHidden)
                    WriteCell(selected ? Ansi.Color(line, "\x1b[2;7m") : Ansi.Dim(line), width);
                else
                    WriteCell(selected ? Ansi.Color(line, "\x1b[7m") : line, width);
                break;
            }
            case TableItem tbl:
            {
                // " ▶ TableName" (active, bold) or "   TableName"
                var line = tbl.IsActive ? " ▶ " + tbl.Name : "   " + tbl.Name;
                if (tbl.IsActive)
                    WriteCell(selected ? Ansi.Color(line, "\x1b[1;7m") : Ansi.Bold(line), width);
                else
                    WriteCell(selected ? Ansi.Color(line, "\x1b[7m") : line, width);
                break;
            }
        }
    }

    private static void MoveTo(int row, int col) =>
        System.Console.Write($"\x1b[{row + 1};{col + 1}H");

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
