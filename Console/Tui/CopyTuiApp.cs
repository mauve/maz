using System.Threading.Channels;
using Console.Cli.Commands.Copy;
using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Interactive TUI for the copy command. Shows per-blob progress bars
/// with scroll, pause, cancel, and speed/ETA display.
/// </summary>
internal sealed class CopyTuiApp : IAsyncDisposable
{
    private readonly IReadOnlyList<TransferItem> _items;
    private readonly CopyTransferState[] _states;
    private readonly ChannelReader<TransferProgressEvent> _progress;
    private readonly BlockTransferEngine _engine;

    private bool _running;
    private int _width;
    private int _height;
    private int _selectedIndex;
    private int _scrollOffset;
    private int _completedCount;
    private double _aggregateSpeed;

    public CopyTuiApp(
        IReadOnlyList<TransferItem> items,
        CopyTransferState[] states,
        ChannelReader<TransferProgressEvent> progress,
        BlockTransferEngine engine
    )
    {
        _items = items;
        _states = states;
        _progress = progress;
        _engine = engine;
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
            _running = true;
            Redraw();
            await MainLoop(ct);
        }
        finally
        {
            System.Console.Write("\x1b[?25h");
            System.Console.Write("\x1b[?1049l");
            System.Console.TreatControlCAsInput = prevTreatCtrlC;
        }
    }

    private async Task MainLoop(CancellationToken ct)
    {
        while (_running && !ct.IsCancellationRequested)
        {
            // Drain progress events
            while (_progress.TryRead(out var evt))
            {
                var state = _states[evt.TransferIndex];
                state.UpdateProgress(evt.BytesTransferred, evt.Status, evt.Error);

                if (evt.Status == TransferStatus.Completed)
                    _completedCount++;
            }

            // Detect resize
            if (System.Console.WindowWidth != _width || System.Console.WindowHeight != _height)
            {
                _width = System.Console.WindowWidth;
                _height = System.Console.WindowHeight;
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

            // Check if all done
            if (_completedCount >= _items.Count || _progress.Completion.IsCompleted)
            {
                // Brief pause to show final state
                await Task.Delay(500, ct);
                Redraw();
                _running = false;
                break;
            }

            await Task.Delay(60, ct);
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
                _selectedIndex = Math.Max(0, _selectedIndex - 1);
                EnsureVisible();
                break;
            case ConsoleKey.DownArrow:
                _selectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1);
                EnsureVisible();
                break;
            case ConsoleKey.PageUp:
                _scrollOffset = Math.Max(0, _scrollOffset - visibleCount);
                _selectedIndex = _scrollOffset;
                break;
            case ConsoleKey.PageDown:
                _scrollOffset = Math.Min(
                    Math.Max(0, _items.Count - visibleCount),
                    _scrollOffset + visibleCount
                );
                _selectedIndex = Math.Min(_items.Count - 1, _scrollOffset);
                break;
            case ConsoleKey.P:
                _engine.Pause(_selectedIndex);
                break;
            case ConsoleKey.R:
                // Resume not implemented (would need new CTS)
                break;
            case ConsoleKey.C:
            case ConsoleKey.Delete:
                _engine.Cancel(_selectedIndex);
                break;
        }
    }

    private void EnsureVisible()
    {
        var visibleCount = Math.Max(1, _height - 2);
        if (_selectedIndex < _scrollOffset)
            _scrollOffset = _selectedIndex;
        if (_selectedIndex >= _scrollOffset + visibleCount)
            _scrollOffset = _selectedIndex - visibleCount + 1;
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

        for (int i = 0; i < visibleCount && i + _scrollOffset < _items.Count; i++)
        {
            var idx = i + _scrollOffset;
            var state = _states[idx];
            var isSelected = idx == _selectedIndex;

            DrawTransferRow(buf, state, isSelected);
        }

        // Clear remaining rows
        for (int i = Math.Min(visibleCount, _items.Count - _scrollOffset); i < visibleCount; i++)
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

    private void DrawTransferRow(System.Text.StringBuilder buf, CopyTransferState state, bool isSelected)
    {
        var nameWidth = _width / 2;
        var barWidth = _width - nameWidth;

        // Blob name: right-aligned, truncated from left
        var name = Path.GetFileName(state.Item.SourcePath);
        if (string.IsNullOrEmpty(name))
            name = state.Item.SourcePath;
        if (name.Length > nameWidth - 2)
            name = "..." + name[^(nameWidth - 5)..];
        var namePart = name.PadLeft(nameWidth);

        // Progress bar
        var percent = state.Item.Size > 0
            ? (double)state.BytesTransferred / state.Item.Size
            : 0;
        var filledWidth = (int)(percent * (barWidth - 30));
        if (filledWidth < 0)
            filledWidth = 0;
        var emptyWidth = Math.Max(0, barWidth - 30 - filledWidth);

        var bar = new string('\u2588', filledWidth) + new string(' ', emptyWidth);
        var speedStr = FormatSpeed(state.BytesPerSecond);
        var etaStr = state.Eta.HasValue ? FormatTimeSpan(state.Eta.Value) : "--:--:--";
        var overlay = $" {percent * 100:F0}% | {speedStr} | {etaStr} ";

        // Color based on status
        var colorCode = state.Status switch
        {
            TransferStatus.Completed => "\x1b[97m", // white
            TransferStatus.InProgress => "\x1b[35m", // magenta
            TransferStatus.Paused => "\x1b[2m", // dim
            TransferStatus.Failed => "\x1b[31m", // red
            TransferStatus.Cancelled => "\x1b[2m", // dim
            _ => "",
        };

        var prefix = isSelected ? "\x1b[7m" : ""; // reverse video for selection
        var suffix = isSelected ? "\x1b[27m" : "";

        if (Ansi.IsEnabled)
        {
            buf.Append(prefix);
            buf.Append(namePart);
            buf.Append(suffix);
            buf.Append("  ");
            buf.Append(colorCode);
            buf.Append(bar);
            buf.Append("\x1b[0m");
            buf.Append(Ansi.Dim($"[{overlay}]"));
        }
        else
        {
            buf.Append(namePart);
            buf.Append("  ");
            buf.Append(bar);
            buf.Append($"[{overlay}]");
        }

        // Pad to full width and newline
        var written = namePart.Length + 2 + bar.Length + overlay.Length + 2;
        if (written < _width)
            buf.Append(new string(' ', _width - written));
        buf.AppendLine();
    }

    private void DrawStatusBar(System.Text.StringBuilder buf)
    {
        buf.Append($"\x1b[{_height};1H");
        var statusText =
            $"  \u2191\u2193 Select \u2502 PgUp/Dn Scroll \u2502 P Pause \u2502 C Cancel \u2502 Esc Exit \u2502 {_completedCount}/{_items.Count} done \u2502 {FormatSpeed(_aggregateSpeed)}";

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
}
