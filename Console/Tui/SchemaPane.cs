using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Schema sidebar showing a collapsible tree of tables and their columns.
/// Active tables appear first (separated by a line) and show result columns.
/// Inactive tables show schema columns, loaded lazily on expand.
/// Columns in the result can be hidden/shown via Space/Enter when focused.
/// </summary>
internal sealed class SchemaPane
{
    private readonly SchemaProvider _schema;

    // Data refreshed from KustoTuiApp
    private IReadOnlyList<string> _resultColumns = [];
    private IReadOnlyList<string> _resultColumnTypes = [];
    private IReadOnlySet<string> _hiddenColumns = new HashSet<string>();
    private IReadOnlyList<string> _allTables = [];
    private IReadOnlySet<string> _activeTables = new HashSet<string>();

    // Tree state
    private readonly HashSet<string> _expandedTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<IReadOnlyList<ColumnInfo>>> _pendingLoads =
        new(StringComparer.OrdinalIgnoreCase);

    // Visual
    private List<VisualItem> _items = [];
    private int _selectedVisualIndex;
    private int _scrollOffset;
    private int _lastRenderHeight;

    // ── Visual item types ─────────────────────────────────────────────────────

    private abstract record VisualItem;
    private sealed record TableRow(string Name, bool IsActive, bool IsExpanded) : VisualItem;
    private sealed record ColumnRow(string ColName, string ColType, bool IsHidden, bool ShowCheckbox) : VisualItem;
    private sealed record SeparatorRow() : VisualItem;
    private sealed record LoadingRow() : VisualItem;

    public SchemaPane(SchemaProvider schema) => _schema = schema;

    // ── Public update API ─────────────────────────────────────────────────────

    public void UpdateColumns(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> columnTypes,
        IReadOnlySet<string> hiddenColumns
    )
    {
        _resultColumns = columns;
        _resultColumnTypes = columnTypes;
        _hiddenColumns = hiddenColumns;
        Rebuild();
    }

    public void UpdateTables(IReadOnlyList<string> tables, IReadOnlySet<string> activeTables)
    {
        _allTables = tables;
        _activeTables = activeTables;
        // Auto-expand active tables (they show result columns — no schema loading needed)
        foreach (var t in activeTables)
            _expandedTables.Add(t);
        Rebuild();
    }

    /// <summary>
    /// Drains completed column-load tasks for inactive tables.
    /// Returns true if any completed (caller should redraw).
    /// </summary>
    public bool DrainLoads()
    {
        if (_pendingLoads.Count == 0)
            return false;
        var completed = _pendingLoads
            .Where(kv => kv.Value.IsCompleted)
            .Select(kv => kv.Key)
            .ToList();
        if (completed.Count == 0)
            return false;
        foreach (var t in completed)
            _pendingLoads.Remove(t);
        Rebuild();
        return true;
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

    public void ExpandSelected()
    {
        if (_selectedVisualIndex >= _items.Count)
            return;
        if (_items[_selectedVisualIndex] is TableRow { IsExpanded: false } t)
        {
            _expandedTables.Add(t.Name);
            if (!_activeTables.Contains(t.Name))
                EnsureLoading(t.Name);
            Rebuild();
        }
    }

    public void CollapseSelected()
    {
        if (_selectedVisualIndex >= _items.Count)
            return;
        var tableName = _items[_selectedVisualIndex] switch
        {
            TableRow t => t.Name,
            ColumnRow => FindParentTable(_selectedVisualIndex),
            _ => null,
        };
        if (tableName is null || !_expandedTables.Contains(tableName))
            return;
        _expandedTables.Remove(tableName);
        Rebuild();
        // Move selection to the collapsed table row
        for (int i = 0; i < _items.Count; i++)
            if (_items[i] is TableRow tr && tr.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase))
            {
                _selectedVisualIndex = i;
                ClampScroll();
                break;
            }
    }

    /// <summary>
    /// Toggles expand/collapse for a table row, or returns the column name to toggle
    /// visibility for a result column row. Returns null when handled internally.
    /// </summary>
    public string? ToggleOrExpand()
    {
        if (_selectedVisualIndex >= _items.Count)
            return null;
        switch (_items[_selectedVisualIndex])
        {
            case TableRow t:
                if (_expandedTables.Contains(t.Name))
                    _expandedTables.Remove(t.Name);
                else
                {
                    _expandedTables.Add(t.Name);
                    if (!_activeTables.Contains(t.Name))
                        EnsureLoading(t.Name);
                }
                Rebuild();
                return null;
            case ColumnRow { ShowCheckbox: true } col:
                return col.ColName;
            default:
                return null;
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public void Render(int top, int left, int width, int height, bool focused)
    {
        _lastRenderHeight = height;
        if (height < 2 || width < 6)
            return;

        MoveTo(top, left);
        var title = " Schema";
        WriteCell(focused ? Ansi.Color(title, "\x1b[1;7m") : Ansi.Bold(title), width);

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

    private void EnsureLoading(string tableName)
    {
        if (_schema.GetCachedColumns(tableName).Count > 0)
            return;
        if (_pendingLoads.ContainsKey(tableName))
            return;
        _pendingLoads[tableName] = _schema.GetColumnsAsync(tableName);
    }

    private void Rebuild()
    {
        _items = [];

        // Active tables: those in _activeTables, plus any in _allTables that match.
        // Extra active = active tables not yet in the full schema list.
        List<string> activeTables =
        [
            .. _allTables
                .Where(t => _activeTables.Contains(t))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
            .. _activeTables
                .Where(t => !_allTables.Contains(t, StringComparer.OrdinalIgnoreCase))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
        ];

        List<string> inactiveTables =
        [
            .. _allTables
                .Where(t => !_activeTables.Contains(t))
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase),
        ];

        // ── Active section ────────────────────────────────────────────────────
        foreach (var table in activeTables)
        {
            bool isExpanded = _expandedTables.Contains(table);
            _items.Add(new TableRow(table, IsActive: true, isExpanded));

            if (!isExpanded || _resultColumns.Count == 0)
                continue;

            // Active tables show the columns that came back in the result, not schema columns
            for (int i = 0; i < _resultColumns.Count; i++)
            {
                var colName = _resultColumns[i];
                var colType = i < _resultColumnTypes.Count ? _resultColumnTypes[i] : "";
                bool isHidden = _hiddenColumns.Contains(colName);
                _items.Add(new ColumnRow(colName, colType, isHidden, ShowCheckbox: true));
            }
        }

        // ── Separator ─────────────────────────────────────────────────────────
        if (activeTables.Count > 0 && inactiveTables.Count > 0)
            _items.Add(new SeparatorRow());

        // ── Inactive section ──────────────────────────────────────────────────
        foreach (var table in inactiveTables)
        {
            bool isExpanded = _expandedTables.Contains(table);
            _items.Add(new TableRow(table, IsActive: false, isExpanded));

            if (!isExpanded)
                continue;

            if (_pendingLoads.ContainsKey(table))
            {
                _items.Add(new LoadingRow());
            }
            else
            {
                var cols = _schema.GetCachedColumns(table);
                foreach (var col in cols)
                    _items.Add(new ColumnRow(col.Name, col.Type, IsHidden: false, ShowCheckbox: false));
            }
        }

        ClampSelection();
    }

    private string? FindParentTable(int itemIndex)
    {
        for (int i = itemIndex - 1; i >= 0; i--)
            if (_items[i] is TableRow t)
                return t.Name;
        return null;
    }

    private static bool IsSelectable(VisualItem item) => item is TableRow or ColumnRow;

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
            case TableRow tbl:
            {
                var arrow = tbl.IsExpanded ? "▼ " : "▶ ";
                var maxName = Math.Max(0, width - 3);
                var name = tbl.Name.Length > maxName ? tbl.Name[..maxName] : tbl.Name;
                var line = " " + arrow + name;
                if (tbl.IsActive)
                    WriteCell(selected ? Ansi.Color(line, "\x1b[1;7m") : Ansi.Bold(line), width);
                else
                    WriteCell(selected ? Ansi.Color(line, "\x1b[7m") : line, width);
                break;
            }
            case ColumnRow col:
            {
                int typeMaxLen = col.ColType.Length > 0 ? Math.Min(col.ColType.Length, 8) : 0;
                int typePartLen = typeMaxLen > 0 ? typeMaxLen + 1 : 0;
                string line;
                if (col.ShowCheckbox)
                {
                    // "  [x] Name  type"  or  "  [ ] Name  type"
                    int nameMaxLen = Math.Max(0, width - 7 - typePartLen); // "  [x] " = 7
                    var namePart = col.ColName.Length > nameMaxLen
                        ? col.ColName[..nameMaxLen]
                        : col.ColName.PadRight(nameMaxLen);
                    var typePart = typeMaxLen > 0
                        ? " " + (col.ColType.Length > typeMaxLen ? col.ColType[..typeMaxLen] : col.ColType)
                        : "";
                    var checkbox = col.IsHidden ? "[ ] " : "[x] ";
                    line = $"  {checkbox}{namePart}{typePart}";
                }
                else
                {
                    // "    Name  type"  (no checkbox, schema-only column)
                    int nameMaxLen = Math.Max(0, width - 4 - typePartLen); // "    " = 4
                    var namePart = col.ColName.Length > nameMaxLen
                        ? col.ColName[..nameMaxLen]
                        : col.ColName.PadRight(nameMaxLen);
                    var typePart = typeMaxLen > 0
                        ? " " + (col.ColType.Length > typeMaxLen ? col.ColType[..typeMaxLen] : col.ColType)
                        : "";
                    line = $"    {namePart}{typePart}";
                }
                if (col.IsHidden)
                    WriteCell(selected ? Ansi.Color(line, "\x1b[2;7m") : Ansi.Dim(line), width);
                else
                    WriteCell(selected ? Ansi.Color(line, "\x1b[7m") : line, width);
                break;
            }
            case SeparatorRow:
            {
                System.Console.Write(Ansi.Dim(new string('─', width)));
                break;
            }
            case LoadingRow:
            {
                WriteCell(Ansi.Dim("  ⠋ loading…"), width);
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
