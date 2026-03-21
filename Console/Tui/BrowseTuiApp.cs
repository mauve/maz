using System.Text;
using System.Text.Json;
using Console.Cli.Commands.Browse;
using Console.Cli.Commands.Copy;
using Console.Cli.Http;
using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Interactive TUI for browsing Azure Blob Storage. Owns the terminal session
/// and main event loop. Enters the alternate screen buffer on start and
/// restores it on exit.
/// </summary>
internal sealed class BrowseTuiApp : IAsyncDisposable
{
    private readonly BlobRestClient _client;
    private readonly string _account;
    private readonly string? _container;
    private readonly string? _prefix;
    private readonly string? _initialGlob;
    private readonly string? _initialTagQuery;

    private readonly BlobTreePane _tree;

    private enum Focus
    {
        Tree,
        GlobFilter,
        TagFilter,
        ActionMenu,
        TextPrompt,
    }

    private bool _running;
    private int _width;
    private int _height;
    private Focus _focus = Focus.Tree;

    // ── Filter state ────────────────────────────────────────────────────

    private string _globInput = "";
    private string? _activeGlob;
    private GlobMatcher? _activeGlobMatcher;
    private string _tagInput = "";
    private string? _activeTagQuery;
    private Task? _filterTask;
    private CancellationTokenSource? _filterCts;

    // ── Action menu state ───────────────────────────────────────────────

    private List<(string Label, char Key)> _actionMenuItems = [];
    private int _actionMenuIndex;

    // ── Text prompt state (for download dir, export path, tag key/value) ─

    private string _promptLabel = "";
    private string _promptInput = "";
    private Action<string>? _promptCallback;

    // ── Background action tasks ─────────────────────────────────────────

    private readonly List<Task> _backgroundActions = [];

    // ── Detail pane (blob info/tags) ────────────────────────────────────

    private List<string>? _detailLines;

    // ── Status message ──────────────────────────────────────────────────

    private string? _statusMessage;
    private DateTimeOffset _statusExpiry;

    public BrowseTuiApp(
        BlobRestClient client,
        string account,
        string? container,
        string? prefix,
        string? initialGlob,
        string? initialTagQuery
    )
    {
        _client = client;
        _account = account;
        _container = container;
        _prefix = prefix;
        _initialGlob = initialGlob;
        _initialTagQuery = initialTagQuery;
        _tree = new BlobTreePane(client, account, container, prefix);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _width = System.Console.WindowWidth;
        _height = System.Console.WindowHeight;

        bool prevTreatCtrlC = System.Console.TreatControlCAsInput;
        System.Console.TreatControlCAsInput = true;

        System.Console.Write("\x1b[?1049h"); // alternate screen
        System.Console.Write("\x1b[?25l"); // hide cursor

        try
        {
            bool hasStartupFilter = !string.IsNullOrEmpty(_initialGlob)
                || !string.IsNullOrEmpty(_initialTagQuery);

            if (!string.IsNullOrEmpty(_initialGlob))
            {
                _globInput = _initialGlob;
                _activeGlob = _initialGlob;
                _activeGlobMatcher = new GlobMatcher(_initialGlob);
            }
            if (!string.IsNullOrEmpty(_initialTagQuery))
            {
                _tagInput = _initialTagQuery;
                _activeTagQuery = _initialTagQuery;
            }

            if (hasStartupFilter)
                ApplyFilter(ct);
            else
                _tree.StartInitialLoad(ct);

            Redraw();
            await MainLoop(ct);
        }
        finally
        {
            System.Console.Write("\x1b[?25h"); // show cursor
            System.Console.Write("\x1b[?1049l"); // exit alternate screen
            System.Console.TreatControlCAsInput = prevTreatCtrlC;
        }
    }

    private async Task MainLoop(CancellationToken ct)
    {
        _running = true;
        while (_running && !ct.IsCancellationRequested)
        {
            bool needsRedraw = false;

            // Drain completed async loads
            if (_tree.DrainLoads())
                needsRedraw = true;

            // Drain completed filter task
            if (_filterTask is { IsCompleted: true })
            {
                _filterTask = null;
                needsRedraw = true;
            }

            // Drain completed background actions (download, delete, tag, info)
            if (_backgroundActions.Count > 0)
            {
                _backgroundActions.RemoveAll(t => t.IsCompleted);
                needsRedraw = true; // always redraw while actions are in flight (progress updates)
            }

            // Clear expired status messages
            if (_statusMessage is not null && DateTimeOffset.UtcNow >= _statusExpiry)
            {
                _statusMessage = null;
                needsRedraw = true;
            }

            // Detect window resize
            if (System.Console.WindowWidth != _width || System.Console.WindowHeight != _height)
            {
                _width = System.Console.WindowWidth;
                _height = System.Console.WindowHeight;
                System.Console.Write("\x1b[2J");
                needsRedraw = true;
            }

            // Process keystrokes
            if (System.Console.KeyAvailable)
            {
                var key = System.Console.ReadKey(intercept: true);
                HandleKey(key, ct);
                needsRedraw = true;
            }

            // Tick throbber and counters while loading
            bool isLoading = _tree.IsFilterLoading || _tree.HasPendingLoads
                || _backgroundActions.Count > 0;
            if (isLoading)
                needsRedraw = true;

            if (needsRedraw)
                Redraw();

            await Task.Delay(isLoading ? 80 : 30, ct);
        }
    }

    // ── Key handling ────────────────────────────────────────────────────

    private void HandleKey(ConsoleKeyInfo key, CancellationToken ct)
    {
        // ── Global: Ctrl+C always exits ─────────────────────────────────
        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _running = false;
            return;
        }

        // ── Text prompt mode ────────────────────────────────────────────
        if (_focus == Focus.TextPrompt)
        {
            HandleTextPromptKey(key);
            return;
        }

        // ── Action menu mode ────────────────────────────────────────────
        if (_focus == Focus.ActionMenu)
        {
            HandleActionMenuKey(key, ct);
            return;
        }

        // ── Glob filter input ───────────────────────────────────────────
        if (_focus == Focus.GlobFilter)
        {
            HandleGlobFilterKey(key, ct);
            return;
        }

        // ── Tag filter input ────────────────────────────────────────────
        if (_focus == Focus.TagFilter)
        {
            HandleTagFilterKey(key, ct);
            return;
        }

        // ── Tree mode ───────────────────────────────────────────────────
        if (key.Key == ConsoleKey.Escape)
        {
            if (_detailLines is not null)
            {
                _detailLines = null;
                return;
            }
            if (_activeGlob is not null || _activeTagQuery is not null)
            {
                ClearFilters(ct);
                return;
            }
            _running = false;
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _tree.MoveUp();
                break;
            case ConsoleKey.DownArrow:
                _tree.MoveDown();
                break;
            case ConsoleKey.RightArrow:
                _tree.ExpandSelected(ct);
                break;
            case ConsoleKey.LeftArrow:
                _tree.CollapseSelected();
                break;
            case ConsoleKey.PageUp:
                _tree.PageUp();
                break;
            case ConsoleKey.PageDown:
                _tree.PageDown();
                break;
            case ConsoleKey.Spacebar:
                _tree.ToggleSelection();
                break;
            case ConsoleKey.Enter:
                if (_tree.SelectedCount > 0 || _tree.GetFocusedBlobInfo() is not null)
                    ShowActionMenu();
                else
                    _tree.ToggleOrExpand(ct);
                break;
            case ConsoleKey.A when (key.Modifiers & ConsoleModifiers.Control) != 0:
                _tree.SelectAll();
                break;
            default:
                // Character shortcuts
                if (key.KeyChar == '/')
                {
                    _focus = Focus.GlobFilter;
                    _globInput = _activeGlob ?? "";
                }
                else if (key.KeyChar == 't' && _tree.SelectedCount == 0)
                {
                    _focus = Focus.TagFilter;
                    _tagInput = _activeTagQuery ?? "";
                }
                break;
        }
    }

    // ── Glob filter ─────────────────────────────────────────────────────

    private void HandleGlobFilterKey(ConsoleKeyInfo key, CancellationToken ct)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _focus = Focus.Tree;
                if (_activeGlob is null)
                    _globInput = "";
                break;
            case ConsoleKey.Enter:
                _focus = Focus.Tree;
                if (string.IsNullOrWhiteSpace(_globInput))
                {
                    _activeGlob = null;
                    _activeGlobMatcher = null;
                    if (_activeTagQuery is null)
                        _tree.ReloadHierarchy(ct);
                    else
                        ApplyFilter(ct);
                }
                else
                {
                    _activeGlob = _globInput;
                    _activeGlobMatcher = new GlobMatcher(_globInput);
                    ApplyFilter(ct);
                }
                break;
            case ConsoleKey.Backspace:
                if (_globInput.Length > 0)
                    _globInput = _globInput[..^1];
                break;
            default:
                if (key.KeyChar >= 32 && key.KeyChar != 127)
                    _globInput += key.KeyChar;
                break;
        }
    }

    // ── Tag filter ──────────────────────────────────────────────────────

    private void HandleTagFilterKey(ConsoleKeyInfo key, CancellationToken ct)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _focus = Focus.Tree;
                if (_activeTagQuery is null)
                    _tagInput = "";
                break;
            case ConsoleKey.Enter:
                _focus = Focus.Tree;
                if (string.IsNullOrWhiteSpace(_tagInput))
                {
                    _activeTagQuery = null;
                    if (_activeGlob is null)
                        _tree.ReloadHierarchy(ct);
                    else
                        ApplyFilter(ct);
                }
                else
                {
                    _activeTagQuery = _tagInput;
                    ApplyFilter(ct);
                }
                break;
            case ConsoleKey.Backspace:
                if (_tagInput.Length > 0)
                    _tagInput = _tagInput[..^1];
                break;
            default:
                if (key.KeyChar >= 32 && key.KeyChar != 127)
                    _tagInput += key.KeyChar;
                break;
        }
    }

    private void ApplyFilter(CancellationToken ct)
    {
        _filterCts?.Cancel();
        _filterCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = _filterCts.Token;

        _tree.IsFilterLoading = true;

        if (_activeTagQuery is not null && _container is not null)
        {
            _filterTask = WrapFilterTask(_tree.LoadByTagQueryAsync(
                _container,
                _activeTagQuery,
                _activeGlobMatcher,
                linkedCt
            ));
        }
        else if (_activeGlob is not null)
        {
            _filterTask = WrapFilterTask(_tree.LoadFilteredAsync(
                _container,
                _prefix is not null ? _prefix + "/" : null,
                _activeGlobMatcher,
                linkedCt
            ));
        }
        else
        {
            _tree.IsFilterLoading = false;
        }
    }

    private async Task WrapFilterTask(Task inner)
    {
        try
        {
            await inner;
        }
        finally
        {
            _tree.IsFilterLoading = false;
        }
    }

    private void ClearFilters(CancellationToken ct)
    {
        _activeGlob = null;
        _activeGlobMatcher = null;
        _activeTagQuery = null;
        _globInput = "";
        _tagInput = "";
        _filterCts?.Cancel();
        _filterTask = null;
        _tree.IsFilterLoading = false;
        _tree.ReloadHierarchy(ct);
    }

    // ── Action menu ─────────────────────────────────────────────────────

    private void ShowActionMenu()
    {
        _actionMenuItems =
        [
            ("Download", 'd'),
            ("Export to NDJSON", 'e'),
            ("Delete", 'x'),
            ("Set tag", 't'),
            ("Info / Properties", 'i'),
        ];
        _actionMenuIndex = 0;
        _focus = Focus.ActionMenu;
    }

    private void HandleActionMenuKey(ConsoleKeyInfo key, CancellationToken ct)
    {
        if (key.Key == ConsoleKey.Escape)
        {
            _focus = Focus.Tree;
            return;
        }

        if (key.Key == ConsoleKey.UpArrow)
        {
            _actionMenuIndex = Math.Max(0, _actionMenuIndex - 1);
            return;
        }
        if (key.Key == ConsoleKey.DownArrow)
        {
            _actionMenuIndex = Math.Min(_actionMenuItems.Count - 1, _actionMenuIndex + 1);
            return;
        }

        // Accept by Enter or by shortcut key
        char? shortcut = key.Key == ConsoleKey.Enter
            ? _actionMenuItems[_actionMenuIndex].Key
            : key.KeyChar;

        _focus = Focus.Tree;

        switch (shortcut)
        {
            case 'd':
                StartDownloadAction(ct);
                break;
            case 'e':
                StartExportAction();
                break;
            case 'x':
                StartDeleteAction(ct);
                break;
            case 't':
                StartSetTagAction();
                break;
            case 'i':
                StartInfoAction(ct);
                break;
        }
    }

    // ── Text prompt helpers ─────────────────────────────────────────────

    private void ShowTextPrompt(string label, string defaultValue, Action<string> callback)
    {
        _promptLabel = label;
        _promptInput = defaultValue;
        _promptCallback = callback;
        _focus = Focus.TextPrompt;
    }

    private void HandleTextPromptKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Escape:
                _focus = Focus.Tree;
                _promptCallback = null;
                break;
            case ConsoleKey.Enter:
                _focus = Focus.Tree;
                var cb = _promptCallback;
                var val = _promptInput;
                _promptCallback = null;
                cb?.Invoke(val);
                break;
            case ConsoleKey.Backspace:
                if (_promptInput.Length > 0)
                    _promptInput = _promptInput[..^1];
                break;
            default:
                if (key.KeyChar >= 32 && key.KeyChar != 127)
                    _promptInput += key.KeyChar;
                break;
        }
    }

    private void SetStatus(string message, int seconds = 5)
    {
        _statusMessage = message;
        _statusExpiry = DateTimeOffset.UtcNow.AddSeconds(seconds);
    }

    // ── Actions ─────────────────────────────────────────────────────────

    private void StartDownloadAction(CancellationToken ct)
    {
        var blobs = GetActionBlobs();
        if (blobs.Count == 0)
            return;

        ShowTextPrompt("Download to directory:", ".", dir =>
        {
            _backgroundActions.Add(DownloadBlobsAsync(blobs, dir, ct));
        });
    }

    private async Task DownloadBlobsAsync(
        List<(string Account, string Container, BlobItem Blob)> blobs,
        string directory,
        CancellationToken ct
    )
    {
        directory = directory.StartsWith('~')
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                directory[1..].TrimStart('/', '\\')
            )
            : directory;

        int done = 0;
        int failed = 0;

        foreach (var (account, container, blob) in blobs)
        {
            try
            {
                var relativePath = blob.Name.Replace('/', Path.DirectorySeparatorChar);
                var localPath = Path.Combine(directory, relativePath);
                var localDir = Path.GetDirectoryName(localPath);
                if (localDir is not null)
                    Directory.CreateDirectory(localDir);

                await using var stream = await _client.GetBlobAsync(account, container, blob.Name, ct);
                await using var file = File.Create(localPath);
                await stream.CopyToAsync(file, ct);
                done++;
            }
            catch
            {
                failed++;
            }
            SetStatus($"Downloading {done + failed}/{blobs.Count}...");
        }

        SetStatus(failed > 0
            ? $"Downloaded {done}/{blobs.Count}, {failed} failed"
            : $"Downloaded {done} blob(s) to {directory}");
    }

    private void StartExportAction()
    {
        var blobs = GetActionBlobs();
        if (blobs.Count == 0)
            return;

        ShowTextPrompt("Export NDJSON to file:", "blobs.jsonl", path =>
        {
            try
            {
                path = path.StartsWith('~')
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        path[1..].TrimStart('/', '\\')
                    )
                    : path;

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
                foreach (var (account, container, blob) in blobs)
                {
                    var entry = new BlobExportEntry
                    {
                        Account = account,
                        Container = container,
                        Blob = blob.Name,
                        Url = $"https://{account}.blob.core.windows.net/{container}/{blob.Name}",
                        Size = blob.Size,
                        ContentType = blob.ContentType,
                        ContentMd5 = blob.ContentMD5,
                        CreatedOn = blob.CreationTime?.ToString("o"),
                        LastModified = blob.LastModified?.ToString("o"),
                    };
                    writer.WriteLine(JsonSerializer.Serialize(
                        entry,
                        BlobExportJsonContext.RelaxedEncoding.BlobExportEntry
                    ));
                }

                SetStatus($"Exported {blobs.Count} blob(s) to {path}");
            }
            catch (Exception ex)
            {
                SetStatus($"Export failed: {ex.Message}");
            }
        });
    }

    private void StartDeleteAction(CancellationToken ct)
    {
        var blobs = GetActionBlobs();
        if (blobs.Count == 0)
            return;

        ShowTextPrompt($"Delete {blobs.Count} blob(s)? Type 'yes' to confirm:", "", input =>
        {
            if (input.Equals("yes", StringComparison.OrdinalIgnoreCase))
                _backgroundActions.Add(DeleteBlobsAsync(blobs, ct));
            else
                SetStatus("Delete cancelled");
        });
    }

    private async Task DeleteBlobsAsync(
        List<(string Account, string Container, BlobItem Blob)> blobs,
        CancellationToken ct
    )
    {
        int done = 0;
        int failed = 0;
        var deletedKeys = new HashSet<string>();

        foreach (var (account, container, blob) in blobs)
        {
            try
            {
                await _client.DeleteBlobAsync(account, container, blob.Name, ct);
                deletedKeys.Add($"{container}/{blob.Name}");
                done++;
            }
            catch
            {
                failed++;
            }
            SetStatus($"Deleting {done + failed}/{blobs.Count}...");
        }

        _tree.RemoveBlobs(deletedKeys);
        SetStatus(failed > 0
            ? $"Deleted {done}/{blobs.Count}, {failed} failed"
            : $"Deleted {done} blob(s)");
    }

    private void StartSetTagAction()
    {
        var blobs = GetActionBlobs();
        if (blobs.Count == 0)
            return;

        ShowTextPrompt("Tag key=value:", "", input =>
        {
            var parts = input.Split('=', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]))
            {
                SetStatus("Invalid format. Use: key=value");
                return;
            }
            _backgroundActions.Add(SetTagAsync(blobs, parts[0].Trim(), parts[1].Trim()));
        });
    }

    private async Task SetTagAsync(
        List<(string Account, string Container, BlobItem Blob)> blobs,
        string tagKey,
        string tagValue
    )
    {
        int done = 0;
        int failed = 0;

        foreach (var (account, container, blob) in blobs)
        {
            try
            {
                // Get existing tags, merge, and set
                var tags = await _client.GetBlobTagsAsync(account, container, blob.Name, default);
                tags[tagKey] = tagValue;
                await _client.SetBlobTagsAsync(account, container, blob.Name, tags, default);
                done++;
            }
            catch
            {
                failed++;
            }
            SetStatus($"Tagging {done + failed}/{blobs.Count}...");
        }

        SetStatus(failed > 0
            ? $"Tagged {done}/{blobs.Count}, {failed} failed"
            : $"Set '{tagKey}={tagValue}' on {done} blob(s)");
    }

    private void StartInfoAction(CancellationToken ct)
    {
        var focused = _tree.GetFocusedBlobInfo();
        if (focused is null)
            return;

        _detailLines = [Ansi.Dim("  Loading…")];
        var (account, container, blob) = focused.Value;
        _backgroundActions.Add(LoadInfoAsync(account, container, blob.Name, ct));
    }

    private async Task LoadInfoAsync(
        string account,
        string container,
        string blobName,
        CancellationToken ct
    )
    {
        try
        {
            var props = await _client.GetBlobPropertiesAsync(account, container, blobName, ct);
            Dictionary<string, string> tags;
            try
            {
                tags = await _client.GetBlobTagsAsync(account, container, blobName, ct);
            }
            catch
            {
                tags = [];
            }

            var lines = new List<string>
            {
                $"  {Ansi.Bold("Name")}          {blobName}",
                $"  {Ansi.Bold("URL")}           https://{account}.blob.core.windows.net/{container}/{blobName}",
                $"  {Ansi.Bold("Size")}          {FormatSize(props.Size)} ({props.Size:N0} bytes)",
                $"  {Ansi.Bold("Content-Type")}  {props.ContentType ?? "—"}",
                $"  {Ansi.Bold("Last-Modified")} {props.LastModified?.ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—"}",
            };

            if (tags.Count > 0)
            {
                lines.Add($"  {Ansi.Bold("Tags")}");
                foreach (var (key, value) in tags)
                    lines.Add($"    {Ansi.Cyan(key)} = {value}");
            }

            _detailLines = lines;
        }
        catch (Exception ex)
        {
            _detailLines = [$"  {Ansi.Red("Error")}: {ex.Message}"];
        }
    }

    private List<(string Account, string Container, BlobItem Blob)> GetActionBlobs()
    {
        if (_tree.SelectedCount > 0)
            return _tree.GetSelectedBlobInfo();

        // Fallback to focused blob
        var focused = _tree.GetFocusedBlobInfo();
        if (focused is not null)
            return [focused.Value];

        return [];
    }

    // ── Rendering ───────────────────────────────────────────────────────

    private void Redraw()
    {
        if (_width < 20 || _height < 5)
            return;

        System.Console.Write("\x1b[?25l"); // hide cursor during redraw
        System.Console.Write("\x1b[?2026h"); // begin synchronized output

        // Layout: filter bar (0-1) + tree (flex) + detail pane (0-N) + status bar (1)
        int filterBarHeight = (_activeGlob is not null || _activeTagQuery is not null
            || _focus == Focus.GlobFilter || _focus == Focus.TagFilter) ? 1 : 0;
        int detailPaneHeight = _detailLines is not null
            ? Math.Min(_detailLines.Count + 2, Math.Max(4, _height / 3)) // +2 for border+title
            : 0;
        int statusBarRow = _height - 1;
        int treeTop = filterBarHeight;
        int treeHeight = _height - filterBarHeight - detailPaneHeight - 1;

        // Filter bar
        if (filterBarHeight > 0)
            DrawFilterBar(0);

        // Tree pane
        _tree.Render(treeTop, 0, _width, treeHeight, _focus == Focus.Tree);

        // Detail pane (blob properties/tags)
        if (_detailLines is not null)
            DrawDetailPane(treeTop + treeHeight, detailPaneHeight);

        // Action menu overlay
        if (_focus == Focus.ActionMenu)
            DrawActionMenu();

        // Text prompt overlay
        if (_focus == Focus.TextPrompt)
            DrawTextPrompt();

        // Status bar
        DrawStatusBar(statusBarRow);

        // Show cursor for input modes
        if (_focus is Focus.GlobFilter or Focus.TagFilter or Focus.TextPrompt)
            System.Console.Write("\x1b[?25h");

        System.Console.Write("\x1b[?2026l"); // end synchronized output
    }

    private void DrawFilterBar(int row)
    {
        System.Console.Write($"\x1b[{row + 1};1H");

        var parts = new List<string>();

        if (_focus == Focus.GlobFilter)
            parts.Add($"Glob: {_globInput}\u2588");
        else if (_activeGlob is not null)
            parts.Add($"Glob: {_activeGlob}");

        if (_focus == Focus.TagFilter)
            parts.Add($"Tag: {TagQueryHighlighter.Highlight(_tagInput)}\u2588");
        else if (_activeTagQuery is not null)
            parts.Add($"Tag: {TagQueryHighlighter.Highlight(_activeTagQuery)}");

        // Show scanned/matched counts when filtering, plain count otherwise
        string countInfo;
        if (_tree.IsFilterLoading || _tree.ScannedBlobCount > 0 && _tree.ScannedBlobCount != _tree.TotalBlobCount)
            countInfo = $" ({_tree.ScannedBlobCount} scanned) {_tree.TotalBlobCount} matched";
        else
            countInfo = $" {_tree.TotalBlobCount} blobs";

        if (_tree.SelectedCount > 0)
            countInfo += $" ({_tree.SelectedCount} selected)";

        var filterText = " " + string.Join("  │  ", parts);
        var maxFilter = _width - Ansi.VisibleLength(countInfo) - 1;

        if (Ansi.VisibleLength(filterText) > maxFilter && maxFilter > 5)
            filterText = ResultsPane.TruncateAnsi(filterText, maxFilter);

        var padding = Math.Max(0, _width - Ansi.VisibleLength(filterText) - Ansi.VisibleLength(countInfo));
        System.Console.Write(Ansi.BrandBar(
            filterText + new string(' ', padding) + countInfo, _width
        ));
    }

    private void DrawDetailPane(int top, int height)
    {
        if (_detailLines is null || height < 2)
            return;

        // Top border with title
        System.Console.Write($"\x1b[{top + 1};1H");
        var title = " Properties ";
        var close = " Esc to close ";
        var borderLen = _width - title.Length - close.Length;
        if (borderLen > 0)
            System.Console.Write(
                Ansi.Dim("─" + title + new string('─', borderLen) + close + "─")
            );
        else
            System.Console.Write(Ansi.Dim(new string('─', _width)));

        // Content lines
        int contentRows = height - 1; // -1 for border
        for (int i = 0; i < contentRows; i++)
        {
            System.Console.Write($"\x1b[{top + 2 + i};1H");
            if (i < _detailLines.Count)
            {
                var line = _detailLines[i];
                var vis = Ansi.VisibleLength(line);
                if (vis >= _width)
                    System.Console.Write(ResultsPane.TruncateAnsi(line, _width));
                else
                {
                    System.Console.Write(line);
                    System.Console.Write(new string(' ', _width - vis));
                }
            }
            else
                System.Console.Write(new string(' ', _width));
        }
    }

    private void DrawActionMenu()
    {
        // Draw centered overlay
        int menuWidth = 30;
        int menuHeight = _actionMenuItems.Count + 2;
        int menuTop = Math.Max(0, (_height - menuHeight) / 2);
        int menuLeft = Math.Max(0, (_width - menuWidth) / 2);

        // Top border
        System.Console.Write($"\x1b[{menuTop + 1};{menuLeft + 1}H");
        System.Console.Write("┌" + new string('─', menuWidth - 2) + "┐");

        // Items
        for (int i = 0; i < _actionMenuItems.Count; i++)
        {
            System.Console.Write($"\x1b[{menuTop + 2 + i};{menuLeft + 1}H");
            var (label, shortcut) = _actionMenuItems[i];
            var line = $" {shortcut}) {label}";
            if (line.Length < menuWidth - 2)
                line = line.PadRight(menuWidth - 2);
            if (i == _actionMenuIndex)
                System.Console.Write("│" + Ansi.Color(line, "\x1b[7m") + "│");
            else
                System.Console.Write("│" + line + "│");
        }

        // Bottom border
        System.Console.Write($"\x1b[{menuTop + menuHeight};{menuLeft + 1}H");
        System.Console.Write("└" + new string('─', menuWidth - 2) + "┘");
    }

    private void DrawTextPrompt()
    {
        int promptRow = _height / 2;
        int promptWidth = Math.Min(60, _width - 4);
        int promptLeft = Math.Max(0, (_width - promptWidth) / 2);

        System.Console.Write($"\x1b[{promptRow};{promptLeft + 1}H");
        System.Console.Write("┌" + new string('─', promptWidth - 2) + "┐");

        System.Console.Write($"\x1b[{promptRow + 1};{promptLeft + 1}H");
        var label = $" {_promptLabel}";
        if (label.Length > promptWidth - 2)
            label = label[..(promptWidth - 2)];
        System.Console.Write("│" + label.PadRight(promptWidth - 2) + "│");

        System.Console.Write($"\x1b[{promptRow + 2};{promptLeft + 1}H");
        var input = $" {_promptInput}\u2588";
        if (Ansi.VisibleLength(input) > promptWidth - 2)
            input = input[..(promptWidth - 2)];
        System.Console.Write("│" + Ansi.Color(input.PadRight(promptWidth - 2), "\x1b[1m") + "│");

        System.Console.Write($"\x1b[{promptRow + 3};{promptLeft + 1}H");
        System.Console.Write("└" + new string('─', promptWidth - 2) + "┘");
    }

    private void DrawStatusBar(int row)
    {
        System.Console.Write($"\x1b[{row + 1};1H");

        string bar;
        if (_statusMessage is not null)
        {
            bar = $"  {_statusMessage}";
        }
        else if (_focus == Focus.GlobFilter)
        {
            bar = "  Type glob pattern │ Enter Apply │ Esc Cancel";
        }
        else if (_focus == Focus.TagFilter)
        {
            bar = "  Type tag query (\"key\" = 'value' AND ...) │ Enter Apply │ Esc Cancel";
        }
        else if (_focus == Focus.ActionMenu)
        {
            bar = "  ↑↓ Navigate │ Enter/Key Select │ Esc Cancel";
        }
        else if (_focus == Focus.TextPrompt)
        {
            bar = "  Type value │ Enter Confirm │ Esc Cancel";
        }
        else
        {
            bar = "  ↑↓ Navigate │ ←→ Collapse/Expand │ Space Select │ Ctrl+A All │ / Glob │ t Tag │ Enter Act │ Esc Exit";
        }

        System.Console.Write(Ansi.BrandBar(bar, _width));
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "0 B";
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    public ValueTask DisposeAsync()
    {
        _filterCts?.Cancel();
        _filterCts?.Dispose();
        return ValueTask.CompletedTask;
    }
}

