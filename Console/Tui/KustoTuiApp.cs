using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Interactive KQL explorer TUI. Owns the terminal session and main event loop.
/// Enters the alternate screen buffer on start and restores it on exit.
/// </summary>
internal sealed partial class KustoTuiApp : IAsyncDisposable
{
    private readonly LogsQueryClient _client;
    private readonly string? _workspaceId;
    private readonly string? _resourceId;
    private readonly SchemaProvider _schema;
    private readonly EditorPane _editor;
    private readonly ResultsPane _results;
    private readonly QueryHistory _history;

    private enum Focus
    {
        Editor,
        Results,
        Schema,
    }

    private bool _running;
    private int _width;
    private int _height;
    private int _splitRow;
    private Focus _focusedPane = Focus.Editor;

    private Task<QueryOutcome>? _queryTask;
    private CancellationTokenSource? _queryCts;
    private Task<List<CompletionItem>>? _autocompleteTask;

    private int _lastQueryStartLine; // absolute editor line where the last submitted query began
    private string? _savedDraft; // saved editor text while browsing history

    // Context menu items parallel to _results.ShowContextMenu labels
    private List<(string label, Action action)> _contextMenuItems = [];

    // Schema pane (initialised after _schema is constructed)
    private readonly SchemaPane _schemaPane;
    private HashSet<string> _activeTables = new();
    private string _lastQueriedText = ""; // for active-table re-detection after schema loads
    private bool _schemaTablesCached = false; // flipped once, triggers active-table re-detection

    public KustoTuiApp(
        LogsQueryClient client,
        string? workspaceId,
        string? resourceId,
        string? initialQuery,
        int historySize = 100,
        TokenCredential? credential = null,
        string? workspaceArmId = null
    )
    {
        _client = client;
        _workspaceId = workspaceId;
        _resourceId = resourceId;
        _schema = new SchemaProvider(client, workspaceId, resourceId, credential, workspaceArmId);
        _schemaPane = new SchemaPane(_schema);
        _editor = new EditorPane(initialQuery ?? "");
        _results = new ResultsPane();

        var wsLabel = workspaceId ?? (resourceId?.Split('/').LastOrDefault() ?? "");
        if (wsLabel.Length > 30)
            wsLabel = "…" + wsLabel[^29..];
        _results.SetWorkspace(wsLabel);

        var historyPath = ComputeHistoryPath(workspaceId, resourceId);
        _history = new QueryHistory(historySize, historyPath);

        // Auto-format the initial query if one was supplied
        if (!string.IsNullOrWhiteSpace(initialQuery))
            _editor.FormatQuery();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _width = System.Console.WindowWidth;
        _height = System.Console.WindowHeight;

        bool prevTreatCtrlC = System.Console.TreatControlCAsInput;
        System.Console.TreatControlCAsInput = true;

        System.Console.Write("\x1b[?1049h"); // enter alternate screen buffer
        System.Console.Write("\x1b[?25l"); // hide cursor during setup

        try
        {
            Redraw();
            System.Console.Write("\x1b[?25h"); // show cursor
            await MainLoop(ct);
        }
        finally
        {
            System.Console.Write("\x1b[?25h"); // ensure cursor is visible
            System.Console.Write("\x1b[?1049l"); // exit alternate screen buffer
            System.Console.TreatControlCAsInput = prevTreatCtrlC;
        }
    }

    private async Task MainLoop(CancellationToken ct)
    {
        _running = true;
        while (_running && !ct.IsCancellationRequested)
        {
            // Drain completed autocomplete
            if (_autocompleteTask is { IsCompleted: true } at)
            {
                var completions = await at;
                _autocompleteTask = null;
                if (_editor.IsAutocompleteVisible)
                {
                    if (completions.Count == 1)
                    {
                        _editor.ShowAutocomplete(completions);
                        _editor.AutocompleteAccept();
                    }
                    else if (completions.Count > 1)
                    {
                        _editor.ShowAutocomplete(completions);
                    }
                    else
                    {
                        _editor.DismissAutocomplete();
                    }
                }
                Redraw();
            }

            // Drain completed query
            if (_queryTask is { IsCompleted: true } qt)
            {
                var outcome = await qt;
                _queryTask = null;
                _results.SetLoading(false);
                if (outcome.IsSuccess)
                {
                    _results.SetResults(
                        outcome.Columns!,
                        outcome.ColumnTypes ?? [],
                        outcome.Rows!,
                        outcome.Elapsed,
                        outcome.PartialError
                    );
                    _lastQueriedText = outcome.Query;
                    _activeTables = KqlAutocomplete.FindAllTables(
                        outcome.Query,
                        _schema.GetCachedTables()
                    );
                    _history.Add(
                        new QueryHistoryEntry(
                            outcome.Query,
                            DateTimeOffset.UtcNow,
                            outcome.Columns,
                            outcome.Rows,
                            outcome.Elapsed,
                            outcome.PartialError,
                            null,
                            true
                        )
                    );
                }
                else
                {
                    var parsed = AzureErrorParser.Parse(outcome.ErrorMessage!, outcome.Query);
                    _results.SetError(
                        parsed.DisplayMessage,
                        parsed.QueryLine,
                        parsed.LineNumber,
                        parsed.Column
                    );
                    _history.Add(
                        new QueryHistoryEntry(
                            outcome.Query,
                            DateTimeOffset.UtcNow,
                            null,
                            null,
                            TimeSpan.Zero,
                            null,
                            parsed.DisplayMessage,
                            false
                        )
                    );
                    if (parsed.LineNumber.HasValue)
                    {
                        int absLine = _lastQueryStartLine + parsed.LineNumber.Value - 1;
                        int absCol = parsed.Column ?? 0;
                        _editor.SetCursorAt(absLine, absCol);
                        _editor.SetErrorMarker(absLine);
                    }
                }
                Redraw();
            }

            // Drain completed schema column loads
            if (_schemaPane.DrainLoads())
                Redraw();

            // Once the table list is fetched, re-detect active tables from the last query
            if (!_schemaTablesCached && _schema.GetCachedTables().Count > 0)
            {
                _schemaTablesCached = true;
                if (!string.IsNullOrEmpty(_lastQueriedText))
                {
                    _activeTables = KqlAutocomplete.FindAllTables(
                        _lastQueriedText,
                        _schema.GetCachedTables()
                    );
                    Redraw();
                }
            }

            // Detect window resize
            if (System.Console.WindowWidth != _width || System.Console.WindowHeight != _height)
            {
                _width = System.Console.WindowWidth;
                _height = System.Console.WindowHeight;
                Redraw();
            }

            // Process available keystrokes
            if (System.Console.KeyAvailable)
            {
                var key = System.Console.ReadKey(intercept: true);
                HandleKey(key);
                Redraw();
            }
            else if (_queryTask is not null)
            {
                // Tick the spinner while a query runs
                _results.TickSpinner();
                Redraw();
                await Task.Delay(80, ct);
            }
            else if (_autocompleteTask is { IsCompleted: false })
            {
                // Tick the autocomplete loading spinner
                _editor.TickAutocompleteSpinner();
                Redraw();
                await Task.Delay(80, ct);
            }
            else
            {
                await Task.Delay(30, ct);
            }
        }
    }

    private int ResultsPageSize => Math.Max(1, _splitRow - 4);

    private void HandleKey(ConsoleKeyInfo key)
    {
        // ── Always-on globals ─────────────────────────────────────────────────
        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _running = false;
            return;
        }

        // Ctrl+PageUp/Down: scroll the results table from any focus state
        if (key.Key == ConsoleKey.PageUp && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _results.PageUp(ResultsPageSize);
            return;
        }
        if (key.Key == ConsoleKey.PageDown && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _results.PageDown(ResultsPageSize);
            return;
        }

        // F2: toggle focus between editor and results table
        if (key.Key == ConsoleKey.F2)
        {
            _focusedPane = _focusedPane == Focus.Editor ? Focus.Results : Focus.Editor;
            _editor.DismissAutocomplete();
            if (_focusedPane == Focus.Results)
                _results.InitSelection();
            return;
        }

        // F3: toggle focus on schema sidebar
        if (key.Key == ConsoleKey.F3)
        {
            _focusedPane = _focusedPane == Focus.Schema ? Focus.Editor : Focus.Schema;
            _editor.DismissAutocomplete();
            return;
        }

        // F7/F8: history navigation (always available)
        if (key.Key == ConsoleKey.F7)
        {
            HistoryBrowseBack();
            return;
        }
        if (key.Key == ConsoleKey.F8)
        {
            HistoryBrowseForward();
            return;
        }

        // Escape: layered dismiss — context menu → autocomplete → history browse → results/schema focus → exit
        if (key.Key == ConsoleKey.Escape)
        {
            if (_results.IsContextMenuVisible)
            {
                _results.DismissContextMenu();
                return;
            }
            if (_editor.DismissAutocomplete())
                return;
            if (_history.IsBrowsing)
            {
                ExitHistoryBrowse(restore: true);
                return;
            }
            if (_focusedPane is Focus.Results or Focus.Schema)
            {
                _focusedPane = Focus.Editor;
                return;
            }
            _running = false;
            return;
        }

        // ── Autocomplete navigation (takes priority over everything below) ────
        if (_editor.IsAutocompleteVisible)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    _editor.AutocompleteUp();
                    return;
                case ConsoleKey.DownArrow:
                    _editor.AutocompleteDown();
                    return;
                case ConsoleKey.Tab:
                case ConsoleKey.Enter:
                    _editor.AutocompleteAccept();
                    return;
            }
        }

        // ── Results-pane focus: context menu or cell navigation ───────────────
        if (_focusedPane == Focus.Results)
        {
            if (_results.IsContextMenuVisible)
            {
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow:
                        _results.ContextMenuUp();
                        return;
                    case ConsoleKey.DownArrow:
                        _results.ContextMenuDown();
                        return;
                    case ConsoleKey.Enter:
                    case ConsoleKey.Spacebar:
                        var idx = _results.AcceptContextMenuItem();
                        if (idx.HasValue)
                            ExecuteContextAction(idx.Value);
                        return;
                    case ConsoleKey.Escape:
                        _results.DismissContextMenu();
                        return;
                }
                return;
            }

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    _results.MoveSelectionUp();
                    return;
                case ConsoleKey.DownArrow:
                    _results.MoveSelectionDown();
                    return;
                case ConsoleKey.LeftArrow:
                    _results.MoveSelectionLeft();
                    return;
                case ConsoleKey.RightArrow:
                    _results.MoveSelectionRight();
                    return;
                case ConsoleKey.Enter:
                    BuildAndShowContextMenu();
                    return;
                case ConsoleKey.PageUp:
                    _results.PageUp(ResultsPageSize);
                    return;
                case ConsoleKey.PageDown:
                    _results.PageDown(ResultsPageSize);
                    return;
            }
            return;
        }

        // ── Schema-pane focus: navigate tree, expand/collapse, toggle columns ─
        if (_focusedPane == Focus.Schema)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    _schemaPane.MoveUp();
                    return;
                case ConsoleKey.DownArrow:
                    _schemaPane.MoveDown();
                    return;
                case ConsoleKey.RightArrow:
                    _schemaPane.ExpandSelected();
                    return;
                case ConsoleKey.LeftArrow:
                    _schemaPane.CollapseSelected();
                    return;
                case ConsoleKey.Enter:
                case ConsoleKey.Spacebar:
                    var colName = _schemaPane.ToggleOrExpand();
                    if (colName is not null)
                        _results.ToggleColumnVisibility(colName);
                    return;
            }
            return;
        }

        // ── Editor focus ──────────────────────────────────────────────────────
        switch (key.Key)
        {
            case ConsoleKey.F5:
                StartQuery();
                return;
            case ConsoleKey.F6:
                _editor.FormatQuery();
                return;
            case ConsoleKey.Tab:
                StartAutocomplete();
                return;
        }

        // Editing while browsing history exits browse mode (historical query becomes new draft)
        if (_history.IsBrowsing && IsContentEditKey(key))
            ExitHistoryBrowse(restore: false);

        _editor.HandleKey(key);
    }

    private static bool IsContentEditKey(ConsoleKeyInfo key) =>
        key.Key is ConsoleKey.Backspace or ConsoleKey.Delete or ConsoleKey.Enter
        || (key.KeyChar >= 32 && key.KeyChar != 127 && key.Modifiers == ConsoleModifiers.None)
        || (key.Modifiers == ConsoleModifiers.Control && key.Key is ConsoleKey.K or ConsoleKey.U);

    // ── History navigation ────────────────────────────────────────────────────

    private void HistoryBrowseBack()
    {
        if (!_history.IsBrowsing)
            _savedDraft = _editor.GetText(); // save live draft before first browse

        var entry = _history.BrowseBack();
        if (entry is null)
            return; // no history

        LoadHistoryEntry(entry);
    }

    private void HistoryBrowseForward()
    {
        if (!_history.IsBrowsing)
            return;

        var entry = _history.BrowseForward();
        if (entry is null)
        {
            ExitHistoryBrowse(restore: true);
            return;
        }
        LoadHistoryEntry(entry);
    }

    private void LoadHistoryEntry(QueryHistoryEntry entry)
    {
        _editor.SetContent(entry.Query);
        _editor.ClearErrorMarker();
        EnsureSchemaLoading();

        if (entry.IsSuccess && entry.Columns is not null)
        {
            _results.SetResults(entry.Columns, [], entry.Rows!, entry.Elapsed, entry.PartialError);
            _lastQueriedText = entry.Query;
            _activeTables = KqlAutocomplete.FindAllTables(entry.Query, _schema.GetCachedTables());
        }
        else if (entry.ErrorMessage is not null)
            _results.SetError(entry.ErrorMessage);
        else
            _results.SetHistoryPlaceholder(); // disk-loaded entry has no in-memory results

        _results.SetHistoryMode(true);
        _results.SetHistoryBadge(_history.BrowseIndex + 1, _history.Count);
    }

    private void ExitHistoryBrowse(bool restore)
    {
        _history.ExitBrowse();
        _results.ClearHistoryBadge();
        _results.SetHistoryMode(false);
        if (restore && _savedDraft is not null)
        {
            _editor.SetContent(_savedDraft);
            _editor.ClearErrorMarker();
        }
        _savedDraft = null;
    }

    // ── Query execution ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background schema table fetch if the cache is empty.
    /// Safe to call multiple times — GetTablesAsync caches internally.
    /// </summary>
    private void EnsureSchemaLoading()
    {
        if (_schema.GetCachedTables().Count == 0)
            _ = _schema.GetTablesAsync();
    }

    private void StartQuery()
    {
        if (_queryTask is not null && !_queryTask.IsCompleted)
            return; // already running

        // Exit history browse mode — run the query currently in the editor
        if (_history.IsBrowsing)
            ExitHistoryBrowse(restore: false);

        var (queryText, startLine) = _editor.GetActiveQueryInfo();
        queryText = queryText.Trim();
        if (string.IsNullOrWhiteSpace(queryText))
            return;

        _lastQueryStartLine = startLine;
        _editor.ClearErrorMarker();

        EnsureSchemaLoading();
        _queryCts?.Cancel();
        _queryCts = new CancellationTokenSource();
        _results.SetLoading(true);
        _queryTask = ExecuteQueryAsync(queryText, _queryCts.Token);
    }

    private void StartAutocomplete()
    {
        if (_autocompleteTask is not null && !_autocompleteTask.IsCompleted)
            return;

        var prefix = _editor.GetWordAtCursor();
        if (string.IsNullOrEmpty(prefix))
            return;

        _editor.ShowAutocompleteLoading();
        _autocompleteTask = KqlAutocomplete.GetCompletionsAsync(prefix, _editor.GetText(), _schema);
    }

    private async Task<QueryOutcome> ExecuteQueryAsync(string query, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var opts = new LogsQueryOptions { AllowPartialErrors = true };
            LogsQueryResult result;
            if (_workspaceId is not null)
                result = await _client.QueryWorkspaceAsync(
                    _workspaceId,
                    query,
                    QueryTimeRange.All,
                    opts,
                    ct
                );
            else if (_resourceId is not null)
                result = await _client.QueryResourceAsync(
                    new ResourceIdentifier(_resourceId),
                    query,
                    QueryTimeRange.All,
                    opts,
                    ct
                );
            else
                return QueryOutcome.Fail(query, "No workspace or resource ID configured.");

            sw.Stop();
            var columns = result.Table.Columns.Select(c => c.Name).ToList();
            var columnTypes = result.Table.Columns.Select(c => c.Type.ToString()).ToList();
            var rows = result
                .Table.Rows.Select(row =>
                {
                    var dict = new Dictionary<string, object?>();
                    for (int i = 0; i < columns.Count; i++)
                        dict[columns[i]] = row[i];
                    return (IReadOnlyDictionary<string, object?>)dict;
                })
                .ToList();

            // Capture partial-failure error alongside results
            string? partialError = null;
            if (
                result.Status == LogsQueryResultStatus.PartialFailure
                && result.Error?.Message is string errMsg
            )
            {
                partialError = errMsg.ReplaceLineEndings(" ").Replace("  ", " ").Trim();
            }

            return QueryOutcome.Ok(query, columns, columnTypes, rows, sw.Elapsed, partialError);
        }
        catch (OperationCanceledException)
        {
            return QueryOutcome.Fail(query, "Query cancelled.");
        }
        catch (AuthenticationFailedException)
        {
            return QueryOutcome.Fail(
                query,
                "Authentication failed. Run 'maz login' to re-authenticate."
            );
        }
        catch (Exception ex)
        {
            return QueryOutcome.Fail(query, ex.Message);
        }
    }

    // ── Context menu ──────────────────────────────────────────────────────────

    private void BuildAndShowContextMenu()
    {
        var (colName, colType, cellValue) = _results.GetSelectedInfo();
        if (string.IsNullOrEmpty(colName))
            return;

        bool isNumeric = colType is "int" or "long" or "real" or "decimal";
        bool isStringOrNumeric = isNumeric || colType is "string" or "dynamic" or "guid" or "bool";

        _contextMenuItems =
        [
            ("Fit to contents", () => _results.FitSelectedColumnToContents(_splitRow)),
            ("Hide column", () => _results.HideSelectedColumn()),
        ];

        if (isStringOrNumeric && cellValue is not null)
        {
            var filterValue = FormatFilterValue(cellValue, colType);
            var whereClause = $"| where {colName} == {filterValue}";
            _contextMenuItems.Add(
                ($"Where {colName} == {filterValue}", () => AppendToActiveQuery(whereClause))
            );
        }

        if (isNumeric)
        {
            foreach (var fn in new[] { "sum", "avg", "min", "max" })
            {
                var f = fn;
                var cn = colName;
                _contextMenuItems.Add(
                    ($"{fn}({colName})", () => AppendToActiveQuery($"| summarize {f}({cn})"))
                );
            }
            _contextMenuItems.Add(("count()", () => AppendToActiveQuery("| summarize count()")));
        }

        _results.ShowContextMenu(_contextMenuItems.Select(i => i.label).ToList());
    }

    private void ExecuteContextAction(int idx)
    {
        if (idx >= 0 && idx < _contextMenuItems.Count)
            _contextMenuItems[idx].action();
    }

    private void AppendToActiveQuery(string clause)
    {
        var (text, _) = _editor.GetActiveQueryInfo();
        _editor.ReplaceActiveQuery(text.TrimEnd() + "\n" + clause);
        _focusedPane = Focus.Editor;
    }

    private static string FormatFilterValue(object? value, string colType)
    {
        if (value is null)
            return "null";
        if (value is DateTimeOffset dt)
            return $"datetime('{dt:yyyy-MM-dd HH:mm:ss}')";
        return colType switch
        {
            "string" or "dynamic" or "guid" => $"\"{value}\"",
            _ => value.ToString() ?? "null",
        };
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Redraw()
    {
        if (_width < 10 || _height < 5)
            return;

        System.Console.Write("\x1b[?25l"); // hide cursor during redraw

        _splitRow = Math.Clamp(_height * 60 / 100, 4, _height - 6);

        // Schema sidebar only when terminal is wide enough
        int schemaPaneWidth = _width >= 80 ? Math.Max(28, Math.Min(36, _width * 27 / 100)) : 0;
        // If terminal too narrow to show sidebar, redirect schema focus to editor
        if (schemaPaneWidth == 0 && _focusedPane == Focus.Schema)
            _focusedPane = Focus.Editor;
        int editorWidth = _width - schemaPaneWidth;

        // Refresh schema pane state
        _schemaPane.UpdateColumns(
            _results.GetColumns(),
            _results.GetColumnTypes(),
            _results.GetHiddenColumns()
        );
        _schemaPane.UpdateTables(_schema.GetCachedTables(), _activeTables);

        _results.Render(0, 0, _width, _splitRow, _focusedPane == Focus.Results);
        _editor.Render(_splitRow, 0, editorWidth, _height - _splitRow - 1);
        if (schemaPaneWidth > 0)
            _schemaPane.Render(
                _splitRow,
                editorWidth,
                schemaPaneWidth,
                _height - _splitRow - 1,
                _focusedPane == Focus.Schema
            );
        DrawStatusBar(_height - 1);

        if (_focusedPane == Focus.Editor)
        {
            var (curRow, curCol) = _editor.GetCursorScreenPosition(_splitRow);
            curRow = Math.Clamp(curRow, 0, _height - 2);
            curCol = Math.Clamp(curCol, 0, editorWidth - 1);
            System.Console.Write($"\x1b[{curRow + 1};{curCol + 1}H");
            System.Console.Write("\x1b[?25h");
        }
        // Results / schema focused or history browsing: leave cursor hidden
    }

    private void DrawStatusBar(int row)
    {
        System.Console.Write($"\x1b[{row + 1};1H");
        string bar;
        if (_history.IsBrowsing)
            bar = "  F7 Older  │  F8 Newer  │  F5 Run this query  │  Esc Discard  ";
        else if (_focusedPane == Focus.Results)
            bar =
                "  ↑↓←→ Cell  │  Enter Menu  │  PgUp/Dn Page  │  Ctrl+PgUp/Dn Always scroll  │  F2 / Esc  Edit  │  F3 Schema  ";
        else if (_focusedPane == Focus.Schema)
            bar =
                "  ↑↓ Navigate  │  ←▶ Collapse/Expand  │  Space/Enter Toggle/Expand  │  F3 / Esc  Back  ";
        else
            bar =
                "  F5 Run query block  │  F6 Format  │  Tab Complete  │  F2 Results  │  F3 Schema  │  F7/F8 History  │  Esc Exit  ";
        System.Console.Write(Ansi.Color(bar.PadRight(_width), "\x1b[7m"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ComputeHistoryPath(string? workspaceId, string? resourceId)
    {
        var id = workspaceId ?? resourceId?.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(id))
            return null;
        var safeName = new string(
            id.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray()
        );
        var configDir = Path.GetDirectoryName(Config.MazConfig.ResolveConfigPath())!;
        var dir = Path.Combine(configDir, "history", "log-analytics-explore");
        return Path.Combine(dir, safeName + ".json");
    }

    public async ValueTask DisposeAsync()
    {
        _queryCts?.Cancel();
        if (_queryTask is not null)
        {
            try
            {
                await _queryTask;
            }
            catch
            { /* already handled inside ExecuteQueryAsync */
            }
        }
        _queryCts?.Dispose();
    }
}

internal sealed record QueryOutcome
{
    public bool IsSuccess { get; private init; }
    public string Query { get; private init; } = "";
    public IReadOnlyList<string>? Columns { get; private init; }
    public IReadOnlyList<string>? ColumnTypes { get; private init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows { get; private init; }
    public TimeSpan Elapsed { get; private init; }
    public string? ErrorMessage { get; private init; }
    public string? PartialError { get; private init; }

    public static QueryOutcome Ok(
        string query,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> columnTypes,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        TimeSpan elapsed,
        string? partialError = null
    ) =>
        new()
        {
            IsSuccess = true,
            Query = query,
            Columns = columns,
            ColumnTypes = columnTypes,
            Rows = rows,
            Elapsed = elapsed,
            PartialError = partialError,
        };

    public static QueryOutcome Fail(string query, string message) =>
        new()
        {
            IsSuccess = false,
            Query = query,
            ErrorMessage = message,
        };
}
