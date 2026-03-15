using Azure.Core;
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

    private enum Focus { Editor, Results }

    private bool _running;
    private int _width;
    private int _height;
    private int _splitRow;
    private Focus _focusedPane = Focus.Editor;

    private Task<QueryOutcome>? _queryTask;
    private CancellationTokenSource? _queryCts;
    private Task<List<string>>? _autocompleteTask;

    private int _lastQueryStartLine;   // absolute editor line where the last submitted query began
    private string? _savedDraft;       // saved editor text while browsing history

    public KustoTuiApp(
        LogsQueryClient client,
        string? workspaceId,
        string? resourceId,
        string? initialQuery,
        int historySize = 100)
    {
        _client = client;
        _workspaceId = workspaceId;
        _resourceId = resourceId;
        _schema = new SchemaProvider(client, workspaceId, resourceId);
        _editor = new EditorPane(initialQuery ?? "");
        _results = new ResultsPane();

        var wsLabel = workspaceId ?? (resourceId?.Split('/').LastOrDefault() ?? "");
        if (wsLabel.Length > 30) wsLabel = "…" + wsLabel[^29..];
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
        System.Console.Write("\x1b[?25l");   // hide cursor during setup

        try
        {
            Redraw();
            System.Console.Write("\x1b[?25h"); // show cursor
            await MainLoop(ct);
        }
        finally
        {
            System.Console.Write("\x1b[?25h");   // ensure cursor is visible
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
                if (completions.Count == 1)
                {
                    _editor.ShowAutocomplete(completions);
                    _editor.AutocompleteAccept();
                }
                else if (completions.Count > 1)
                {
                    _editor.ShowAutocomplete(completions);
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
                    _results.SetResults(outcome.Columns!, outcome.Rows!, outcome.Elapsed, outcome.PartialError);
                    _history.Add(new QueryHistoryEntry(
                        outcome.Query, DateTimeOffset.UtcNow,
                        outcome.Columns, outcome.Rows,
                        outcome.Elapsed, outcome.PartialError, null, true));
                }
                else
                {
                    var parsed = AzureErrorParser.Parse(outcome.ErrorMessage!, outcome.Query);
                    _results.SetError(parsed.DisplayMessage, parsed.QueryLine, parsed.LineNumber, parsed.Column);
                    _history.Add(new QueryHistoryEntry(
                        outcome.Query, DateTimeOffset.UtcNow,
                        null, null, TimeSpan.Zero, null, parsed.DisplayMessage, false));
                    if (parsed.LineNumber.HasValue)
                    {
                        int absLine = _lastQueryStartLine + parsed.LineNumber.Value - 1;
                        int absCol  = parsed.Column ?? 0;
                        _editor.SetCursorAt(absLine, absCol);
                        _editor.SetErrorMarker(absLine);
                    }
                }
                Redraw();
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

        // Escape: layered dismiss — autocomplete → history browse → results focus → exit
        if (key.Key == ConsoleKey.Escape)
        {
            if (_editor.DismissAutocomplete()) return;
            if (_history.IsBrowsing) { ExitHistoryBrowse(restore: true); return; }
            if (_focusedPane == Focus.Results) { _focusedPane = Focus.Editor; return; }
            _running = false;
            return;
        }

        // ── Autocomplete navigation (takes priority over everything below) ────
        if (_editor.IsAutocompleteVisible)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:   _editor.AutocompleteUp();     return;
                case ConsoleKey.DownArrow: _editor.AutocompleteDown();   return;
                case ConsoleKey.Tab:
                case ConsoleKey.Enter:     _editor.AutocompleteAccept(); return;
            }
        }

        // ── Results-pane focus: navigation keys scroll the table ──────────────
        if (_focusedPane == Focus.Results)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:   _results.ScrollUp();               return;
                case ConsoleKey.DownArrow: _results.ScrollDown();             return;
                case ConsoleKey.PageUp:    _results.PageUp(ResultsPageSize);  return;
                case ConsoleKey.PageDown:  _results.PageDown(ResultsPageSize); return;
            }
            return;
        }

        // ── Editor focus ──────────────────────────────────────────────────────
        switch (key.Key)
        {
            case ConsoleKey.F5:  StartQuery();          return;
            case ConsoleKey.F6:  _editor.FormatQuery(); return;
            case ConsoleKey.Tab: StartAutocomplete();   return;
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
        if (entry is null) return; // no history

        LoadHistoryEntry(entry);
    }

    private void HistoryBrowseForward()
    {
        if (!_history.IsBrowsing) return;

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

        if (entry.IsSuccess && entry.Columns is not null)
            _results.SetResults(entry.Columns, entry.Rows!, entry.Elapsed, entry.PartialError);
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

    private void StartQuery()
    {
        if (_queryTask is not null && !_queryTask.IsCompleted)
            return; // already running

        // Exit history browse mode — run the query currently in the editor
        if (_history.IsBrowsing)
            ExitHistoryBrowse(restore: false);

        var (queryText, startLine) = _editor.GetActiveQueryInfo();
        queryText = queryText.Trim();
        if (string.IsNullOrWhiteSpace(queryText)) return;

        _lastQueryStartLine = startLine;
        _editor.ClearErrorMarker();

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
        if (string.IsNullOrEmpty(prefix)) return;

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
                    _workspaceId, query, QueryTimeRange.All, opts, ct);
            else if (_resourceId is not null)
                result = await _client.QueryResourceAsync(
                    new ResourceIdentifier(_resourceId), query, QueryTimeRange.All, opts, ct);
            else
                return QueryOutcome.Fail(query, "No workspace or resource ID configured.");

            sw.Stop();
            var columns = result.Table.Columns.Select(c => c.Name).ToList();
            var rows = result.Table.Rows.Select(row =>
            {
                var dict = new Dictionary<string, object?>();
                for (int i = 0; i < columns.Count; i++)
                    dict[columns[i]] = row[i];
                return (IReadOnlyDictionary<string, object?>)dict;
            }).ToList();

            // Capture partial-failure error alongside results
            string? partialError = null;
            if (result.Status == LogsQueryResultStatus.PartialFailure && result.Error?.Message is string errMsg)
            {
                partialError = errMsg
                    .ReplaceLineEndings(" ")
                    .Replace("  ", " ")
                    .Trim();
            }

            return QueryOutcome.Ok(query, columns, rows, sw.Elapsed, partialError);
        }
        catch (OperationCanceledException)
        {
            return QueryOutcome.Fail(query, "Query cancelled.");
        }
        catch (Exception ex)
        {
            return QueryOutcome.Fail(query, ex.Message);
        }
    }


    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Redraw()
    {
        if (_width < 10 || _height < 5) return;

        System.Console.Write("\x1b[?25l"); // hide cursor during redraw

        _splitRow = Math.Clamp(_height * 60 / 100, 4, _height - 6);

        _results.Render(0, 0, _width, _splitRow, _focusedPane == Focus.Results);
        _editor.Render(_splitRow, 0, _width, _height - _splitRow - 1);
        DrawStatusBar(_height - 1);

        if (_focusedPane == Focus.Editor)
        {
            var (curRow, curCol) = _editor.GetCursorScreenPosition(_splitRow);
            curRow = Math.Clamp(curRow, 0, _height - 2);
            curCol = Math.Clamp(curCol, 0, _width - 1);
            System.Console.Write($"\x1b[{curRow + 1};{curCol + 1}H");
            System.Console.Write("\x1b[?25h");
        }
        // Results focused or history browsing: leave cursor hidden
    }

    private void DrawStatusBar(int row)
    {
        System.Console.Write($"\x1b[{row + 1};1H");
        string bar;
        if (_history.IsBrowsing)
            bar = "  F7 Older  │  F8 Newer  │  F5 Run this query  │  Esc Discard  ";
        else if (_focusedPane == Focus.Results)
            bar = "  ↑↓ Scroll  │  PgUp/Dn Page  │  Ctrl+PgUp/Dn Always scroll  │  F2 / Esc  Edit query  ";
        else
            bar = "  F5 Run query block  │  F6 Format  │  Tab Complete  │  F2 Results  │  F7/F8 History  │  Esc Exit  ";
        System.Console.Write(Ansi.Color(bar.PadRight(_width), "\x1b[7m"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? ComputeHistoryPath(string? workspaceId, string? resourceId)
    {
        var id = workspaceId ?? resourceId?.Split('/').LastOrDefault();
        if (string.IsNullOrEmpty(id)) return null;
        var safeName = new string(id.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".maz", "history", "log-analytics-explore");
        return Path.Combine(dir, safeName + ".json");
    }

    public async ValueTask DisposeAsync()
    {
        _queryCts?.Cancel();
        if (_queryTask is not null)
        {
            try { await _queryTask; }
            catch { /* already handled inside ExecuteQueryAsync */ }
        }
        _queryCts?.Dispose();
    }
}

internal sealed record QueryOutcome
{
    public bool IsSuccess { get; private init; }
    public string Query { get; private init; } = "";
    public IReadOnlyList<string>? Columns { get; private init; }
    public IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows { get; private init; }
    public TimeSpan Elapsed { get; private init; }
    public string? ErrorMessage { get; private init; }
    public string? PartialError { get; private init; }

    public static QueryOutcome Ok(
        string query,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        TimeSpan elapsed,
        string? partialError = null) =>
        new() { IsSuccess = true, Query = query, Columns = columns, Rows = rows, Elapsed = elapsed, PartialError = partialError };

    public static QueryOutcome Fail(string query, string message) =>
        new() { IsSuccess = false, Query = query, ErrorMessage = message };
}
