using System.Threading.Channels;
using Console.Cli.Commands.Copy;
using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Interactive TUI for the copy command. Shows per-blob progress bars
/// with scroll, pause, cancel, and speed/ETA display.
/// Items appear dynamically as the source is enumerated.
/// Multi-source copies show grouped sections with headers.
/// </summary>
internal sealed class CopyTuiApp : IAsyncDisposable
{
    private readonly BlockTransferEngine _engine;
    private readonly ChannelReader<TransferProgressEvent> _progress;
    private readonly List<CopyTransferState> _states = [];

    // Display rows: interleaved headers and items
    private readonly List<DisplayRow> _rows = [];
    private readonly Dictionary<string, GroupInfo> _groups = new(StringComparer.Ordinal);

    private bool _running;
    private bool _multiSource;
    private int _width;
    private int _height;
    private int _selectedRow; // index into _rows (only selects item rows)
    private int _scrollOffset;
    private int _completedCount;
    private int _failedCount;
    private double _aggregateSpeed;
    private int _nameColumnWidth;
    private int _knownItemCount;
    private readonly System.Diagnostics.Stopwatch _elapsed = new();

    /// <summary>Summary stats available after RunAsync completes.</summary>
    public int TotalItems => _knownItemCount;
    public int CompletedItems => _completedCount;
    public int FailedItems => _failedCount;
    public bool IsMultiSource => _multiSource;
    public long TotalBytes =>
        _states.Where(s => s.Status == TransferStatus.Completed).Sum(s => s.Item.Size);
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public IEnumerable<(TransferItem Item, string Error)> Failures =>
        _states
            .Where(s => s.Status == TransferStatus.Failed && s.Error is not null)
            .Select(s => (s.Item, s.Error!));
    public IEnumerable<(string Group, int Total, int Completed, int Failed, long Bytes)> GroupStats =>
        _states
            .GroupBy(s => s.Item.SourceGroup ?? "")
            .Select(g => (
                Group: g.Key,
                Total: g.Count(),
                Completed: g.Count(s => s.Status == TransferStatus.Completed),
                Failed: g.Count(s => s.Status == TransferStatus.Failed),
                Bytes: g.Where(s => s.Status == TransferStatus.Completed).Sum(s => s.Item.Size)
            ));

    public CopyTuiApp(BlockTransferEngine engine)
    {
        _engine = engine;
        _progress = engine.Progress;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _width = System.Console.WindowWidth;
        _height = System.Console.WindowHeight;
        _nameColumnWidth = 12;

        bool prevTreatCtrlC = System.Console.TreatControlCAsInput;
        System.Console.TreatControlCAsInput = true;

        System.Console.Write("\x1b[?1049h"); // alternate screen
        System.Console.Write("\x1b[?25l"); // hide cursor

        try
        {
            _running = true;
            _elapsed.Start();
            Redraw();
            await MainLoop(ct);
        }
        finally
        {
            _elapsed.Stop();
            System.Console.Write("\x1b[?25h");
            System.Console.Write("\x1b[?1049l");
            System.Console.TreatControlCAsInput = prevTreatCtrlC;
        }
    }

    private async Task MainLoop(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            // Pick up newly discovered items from the engine
            SyncItems();

            // Drain progress events
            while (_progress.TryRead(out var evt))
            {
                if (evt.TransferIndex < _states.Count)
                {
                    var state = _states[evt.TransferIndex];
                    state.UpdateProgress(evt.BytesTransferred, evt.Status, evt.Error);

                    if (evt.Status == TransferStatus.Completed)
                        _completedCount++;
                    else if (evt.Status == TransferStatus.Failed)
                        _failedCount++;
                }
            }

            // Detect resize — clear screen to remove artifacts from old dimensions
            if (System.Console.WindowWidth != _width || System.Console.WindowHeight != _height)
            {
                _width = System.Console.WindowWidth;
                _height = System.Console.WindowHeight;
                System.Console.Write("\x1b[2J");
            }

            // Handle keystrokes
            if (System.Console.KeyAvailable)
            {
                var key = System.Console.ReadKey(intercept: true);
                HandleKey(key);
            }

            // Update aggregate speed
            _aggregateSpeed = 0;
            foreach (var s in _states)
            {
                if (s.Status == TransferStatus.InProgress)
                    _aggregateSpeed += s.BytesPerSecond;
            }

            Redraw();

            // Check if all done: enumeration complete + all items transferred
            if (
                _engine.EnumerationComplete
                    && _knownItemCount > 0
                    && _completedCount >= _knownItemCount
                || _progress.Completion.IsCompleted
            )
            {
                // Brief pause to show final state
                await Task.Delay(500, ct);
                SyncItems();
                Redraw();
                _running = false;
                break;
            }

            await Task.Delay(60, ct);
        }
    }

    /// <summary>
    /// Create CopyTransferState entries for any new items the engine has discovered,
    /// and rebuild the display row list when groups change.
    /// </summary>
    private void SyncItems()
    {
        var engineCount = _engine.ItemCount;
        if (engineCount <= _knownItemCount)
            return;

        for (int i = _knownItemCount; i < engineCount; i++)
        {
            var item = _engine.GetItem(i);
            _states.Add(new CopyTransferState(item, new CancellationTokenSource()));

            var group = item.SourceGroup ?? "";
            if (!_groups.ContainsKey(group))
            {
                _groups[group] = new GroupInfo(group);
                if (_groups.Count > 1)
                    _multiSource = true;
            }
        }

        _knownItemCount = engineCount;
        ComputeNameColumnWidth();

        // Rebuild display rows (always — new items may have been added to existing groups)
        RebuildDisplayRows();

        // If this is the first item, select it
        if (_selectedRow == 0 && _rows.Count > 0 && _rows[0].Kind == RowKind.Header)
            _selectedRow = FindNextItemRow(0);
    }

    private void RebuildDisplayRows()
    {
        _rows.Clear();

        if (!_multiSource)
        {
            // Single source: flat list, no headers
            for (int i = 0; i < _states.Count; i++)
                _rows.Add(new DisplayRow(RowKind.Item, i));
            return;
        }

        // Multi-source: group by SourceGroup with headers
        string? currentGroup = null;
        for (int i = 0; i < _states.Count; i++)
        {
            var group = _states[i].Item.SourceGroup ?? "";
            if (group != currentGroup)
            {
                _rows.Add(new DisplayRow(RowKind.Header, -1, group));
                currentGroup = group;
            }
            _rows.Add(new DisplayRow(RowKind.Item, i));
        }
    }

    private void HandleKey(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.C && (key.Modifiers & ConsoleModifiers.Control) != 0)
        {
            _engine.CancelAll();
            _running = false;
            return;
        }

        if (key.Key == ConsoleKey.Escape)
        {
            _engine.CancelAll();
            _running = false;
            return;
        }

        var visibleCount = Math.Max(1, _height - 2);

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _selectedRow = FindPrevItemRow(_selectedRow);
                EnsureVisible();
                break;
            case ConsoleKey.DownArrow:
                _selectedRow = FindNextItemRow(_selectedRow);
                EnsureVisible();
                break;
            case ConsoleKey.PageUp:
                _scrollOffset = Math.Max(0, _scrollOffset - visibleCount);
                _selectedRow = FindNextItemRow(Math.Max(0, _scrollOffset - 1));
                break;
            case ConsoleKey.PageDown:
                _scrollOffset = Math.Min(
                    Math.Max(0, _rows.Count - visibleCount),
                    _scrollOffset + visibleCount
                );
                _selectedRow = FindNextItemRow(Math.Max(0, _scrollOffset - 1));
                break;
            case ConsoleKey.P:
                if (GetSelectedItemIndex() is { } pi)
                    _engine.Pause(pi);
                break;
            case ConsoleKey.R:
                break;
            case ConsoleKey.C:
            case ConsoleKey.Delete:
                if (GetSelectedItemIndex() is { } ci)
                    _engine.Cancel(ci);
                break;
        }
    }

    private int? GetSelectedItemIndex() =>
        _selectedRow >= 0 && _selectedRow < _rows.Count && _rows[_selectedRow].Kind == RowKind.Item
            ? _rows[_selectedRow].ItemIndex
            : null;

    private int FindNextItemRow(int from)
    {
        for (int i = from + 1; i < _rows.Count; i++)
            if (_rows[i].Kind == RowKind.Item)
                return i;
        return _selectedRow; // stay put
    }

    private int FindPrevItemRow(int from)
    {
        for (int i = from - 1; i >= 0; i--)
            if (_rows[i].Kind == RowKind.Item)
                return i;
        return _selectedRow; // stay put
    }

    private void EnsureVisible()
    {
        var visibleCount = Math.Max(1, _height - 2);
        if (_selectedRow < _scrollOffset)
            _scrollOffset = _selectedRow;
        if (_selectedRow >= _scrollOffset + visibleCount)
            _scrollOffset = _selectedRow - visibleCount + 1;
        // If scroll landed on a header, back up to show it
        if (
            _scrollOffset > 0
            && _scrollOffset < _rows.Count
            && _rows[_scrollOffset].Kind == RowKind.Item
        )
        {
            // Check if previous row is a header for this item's group
            if (_scrollOffset - 1 >= 0 && _rows[_scrollOffset - 1].Kind == RowKind.Header)
                _scrollOffset--;
        }
    }

    private void Redraw()
    {
        if (_width < 20 || _height < 3)
            return;

        var visibleCount = _height - 2;
        var buf = new System.Text.StringBuilder(_width * _height);

        // Begin synchronized output
        buf.Append("\x1b[?2026h");
        buf.Append("\x1b[H"); // home

        var rowsDrawn = 0;
        for (int i = _scrollOffset; i < _rows.Count && rowsDrawn < visibleCount; i++)
        {
            var row = _rows[i];
            if (row.Kind == RowKind.Header)
            {
                DrawGroupHeader(buf, row.GroupLabel!);
            }
            else
            {
                var state = _states[row.ItemIndex];
                DrawTransferRow(buf, state, i == _selectedRow);
            }
            rowsDrawn++;
        }

        // Clear remaining rows
        for (int i = rowsDrawn; i < visibleCount; i++)
        {
            buf.Append($"\x1b[{i + 1};1H");
            buf.Append(new string(' ', _width));
        }

        // Status bar
        DrawStatusBar(buf);

        // End synchronized output
        buf.Append("\x1b[?2026l");

        System.Console.Write(buf.ToString());
    }

    private void DrawGroupHeader(System.Text.StringBuilder buf, string group)
    {
        // Count completed / total for this group
        var total = 0;
        var done = 0;
        foreach (var state in _states)
        {
            if ((state.Item.SourceGroup ?? "") == group)
            {
                total++;
                if (state.Status == TransferStatus.Completed)
                    done++;
            }
        }

        var suffix = $" ({done}/{total}) ";
        // Truncate group label to fit: " ── label ── (N/M) "
        var maxLabelLen = _width - suffix.Length - 6; // 6 for " ── " + " ──"
        var label = group;
        if (label.Length > maxLabelLen && maxLabelLen > 5)
            label = "..." + label[^(maxLabelLen - 3)..];
        else if (maxLabelLen <= 5)
            label = "";

        var header = $" \u2500\u2500 {label} \u2500\u2500{suffix}";
        if (header.Length < _width)
            header += new string('\u2500', _width - header.Length);
        if (header.Length > _width)
            header = header[.._width];

        if (Ansi.IsEnabled)
            buf.Append(Ansi.Dim(header));
        else
            buf.Append(header);
        buf.AppendLine();
    }

    private void ComputeNameColumnWidth()
    {
        var maxLen = 0;
        foreach (var state in _states)
        {
            var name = GetDisplayName(state.Item.SourcePath);
            if (name.Length > maxLen)
                maxLen = name.Length;
        }

        // Name column: longest name + 2 padding, clamped to 60% of terminal width
        _nameColumnWidth = Math.Min(maxLen + 2, (int)(_width * 0.6));
        _nameColumnWidth = Math.Max(_nameColumnWidth, 12);
    }

    /// <summary>
    /// Show the last two path segments (parent/file) unless the result exceeds
    /// 40 characters, in which case fall back to just the filename.
    /// </summary>
    private static string GetDisplayName(string path)
    {
        const int maxTwoSegmentLength = 40;

        var segments = path.Split('/');
        if (segments.Length >= 2)
        {
            var twoSegment = $"{segments[^2]}/{segments[^1]}";
            if (twoSegment.Length <= maxTwoSegmentLength)
                return twoSegment;
        }

        var name = segments[^1];
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void DrawTransferRow(
        System.Text.StringBuilder buf,
        CopyTransferState state,
        bool isSelected
    )
    {
        var nameWidth = _nameColumnWidth;

        // Blob name: right-aligned, truncated from left
        var name = GetDisplayName(state.Item.SourcePath);
        if (name.Length > nameWidth - 2)
            name = "..." + name[^(nameWidth - 5)..];
        var namePart = name.PadLeft(nameWidth);

        // Fixed-width fields: size(8) + gap(2) + bar + gap(1) + pct(4) + gap(2) + speed(9) + gap(2) + eta(8)
        var sizeStr = FormatSize(state.Item.Size).PadLeft(8);
        var percent = state.Item.Size > 0 ? (double)state.BytesTransferred / state.Item.Size : 0;
        var pctStr = $"{percent * 100, 3:F0}%";
        var speedStr = FormatSpeed(state.BytesPerSecond).PadLeft(9);
        var etaStr = (state.Eta.HasValue ? FormatTimeSpan(state.Eta.Value) : "--:--:--").PadLeft(8);

        // Layout: name(nameWidth) 2 size(8) 2 bar(flex) 1 pct(4) 2 speed(9) 2 eta(8)
        // Fixed parts after bar: 1 + 4 + 2 + 9 + 2 + 8 = 26
        // Fixed parts before bar: nameWidth + 2 + 8 + 2 = nameWidth + 12
        var barChars = Math.Max(0, _width - nameWidth - 12 - 26);
        var filledWidth = (int)(percent * barChars);
        var emptyWidth = barChars - filledWidth;

        // Color based on status
        var (filledColor, trackColor) = state.Status switch
        {
            TransferStatus.Completed => ("\x1b[32m", "\x1b[32m"), // green
            TransferStatus.InProgress => ("\x1b[35m", "\x1b[90m"), // magenta / dark gray
            TransferStatus.Paused => ("\x1b[2m", "\x1b[90m"), // dim / dark gray
            TransferStatus.Failed => ("\x1b[31m", "\x1b[90m"), // red / dark gray
            TransferStatus.Cancelled => ("\x1b[2m", "\x1b[90m"), // dim / dark gray
            _ => ("", "\x1b[90m"),
        };

        var selOn = isSelected ? "\x1b[7m" : "";
        var selOff = isSelected ? "\x1b[27m" : "";
        var metrics = $" {pctStr}  {speedStr}  {etaStr}";

        if (Ansi.IsEnabled)
        {
            buf.Append(selOn);
            buf.Append(namePart);
            buf.Append(selOff);
            buf.Append("  ");
            buf.Append(Ansi.Dim(sizeStr));
            buf.Append("  ");
            buf.Append(filledColor);
            buf.Append(new string('\u2585', filledWidth));
            buf.Append(trackColor);
            buf.Append(new string('\u2582', emptyWidth));
            buf.Append("\x1b[0m");
            buf.Append(Ansi.Dim(metrics));
        }
        else
        {
            buf.Append(namePart);
            buf.Append("  ");
            buf.Append(sizeStr);
            buf.Append("  ");
            buf.Append(new string('#', filledWidth));
            buf.Append(new string('-', emptyWidth));
            buf.Append(metrics);
        }

        // Pad to full width and newline
        var written = nameWidth + 2 + 8 + 2 + barChars + 26;
        if (written < _width)
            buf.Append(new string(' ', _width - written));
        buf.AppendLine();
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private void DrawStatusBar(System.Text.StringBuilder buf)
    {
        buf.Append($"\x1b[{_height};1H");

        var countLabel = _engine.EnumerationComplete
            ? $"{_completedCount}/{_knownItemCount} done"
            : $"{_completedCount}/{_knownItemCount}+ done";

        var statusText =
            $"  \u2191\u2193 Select \u2502 PgUp/Dn Scroll \u2502 P Pause \u2502 C Cancel \u2502 Esc Exit \u2502 {countLabel} \u2502 {FormatSpeed(_aggregateSpeed)}";

        if (statusText.Length > _width)
            statusText = statusText[.._width];
        else
            statusText = statusText.PadRight(_width);

        buf.Append(Ansi.Color(statusText, "\x1b[7m"));
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        if (bytesPerSec <= 0)
            return "0 B/s";
        if (bytesPerSec < 1024)
            return $"{bytesPerSec:F0} B/s";
        if (bytesPerSec < 1024 * 1024)
            return $"{bytesPerSec / 1024:F0} KB/s";
        if (bytesPerSec < 1024 * 1024 * 1024)
            return $"{bytesPerSec / (1024 * 1024):F0} MB/s";
        return $"{bytesPerSec / (1024 * 1024 * 1024):F1} GB/s";
    }

    private static string FormatTimeSpan(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";

    public ValueTask DisposeAsync()
    {
        _engine.CancelAll();
        return ValueTask.CompletedTask;
    }

    // ── Display row types ─────────────────────────────────────────────────

    private enum RowKind
    {
        Header,
        Item,
    }

    private readonly record struct DisplayRow(
        RowKind Kind,
        int ItemIndex,
        string? GroupLabel = null
    );

    private sealed class GroupInfo(string label)
    {
        public string Label { get; } = label;
    }
}
