using System.Text.Json;
using System.Text.Json.Serialization;

namespace Console.Tui;

internal sealed record QueryHistoryEntry(
    string Query,
    DateTimeOffset Timestamp,
    IReadOnlyList<string>? Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>>? Rows,
    TimeSpan Elapsed,
    string? PartialError,
    string? ErrorMessage,
    bool IsSuccess
);

/// <summary>
/// In-memory LRU ring buffer of query executions, with optional disk persistence
/// (queries only — results are never written to disk).
/// </summary>
internal sealed class QueryHistory
{
    private readonly int _maxSize;
    private readonly string? _persistPath;
    private readonly List<QueryHistoryEntry> _entries = [];
    private int _browseIndex = -1; // -1 = live; 0 = most-recent entry

    public bool IsBrowsing => _browseIndex >= 0;
    public int BrowseIndex => _browseIndex; // 0-based, 0 = most recent
    public int Count => _entries.Count;

    public QueryHistory(int maxSize, string? persistPath)
    {
        _maxSize = maxSize;
        _persistPath = persistPath;
        if (persistPath is not null)
            LoadFromDisk();
    }

    public void Add(QueryHistoryEntry entry)
    {
        _entries.Insert(0, entry);
        while (_entries.Count > _maxSize)
            _entries.RemoveAt(_entries.Count - 1);
        _browseIndex = -1;
        if (_persistPath is not null)
            AppendToDisk(entry);
    }

    /// <summary>Move to an older entry. Returns the entry, or null if no history.</summary>
    public QueryHistoryEntry? BrowseBack()
    {
        if (_entries.Count == 0)
            return null;
        if (_browseIndex < _entries.Count - 1)
            _browseIndex++;
        return _entries[_browseIndex];
    }

    /// <summary>Move to a newer entry. Returns null when past the most-recent (caller should exit browse).</summary>
    public QueryHistoryEntry? BrowseForward()
    {
        if (_browseIndex <= 0)
        {
            _browseIndex = -1;
            return null;
        }
        _browseIndex--;
        return _entries[_browseIndex];
    }

    public void ExitBrowse() => _browseIndex = -1;

    // ── Disk persistence ──────────────────────────────────────────────────────

    private sealed class DiskRecord
    {
        [JsonPropertyName("query")]
        public string Query { get; set; } = "";

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }
    }

    private static readonly JsonSerializerOptions _jsonOpts = new() { WriteIndented = false };

    private void LoadFromDisk()
    {
        if (_persistPath is null || !File.Exists(_persistPath))
            return;
        try
        {
            var records = JsonSerializer.Deserialize<List<DiskRecord>>(
                File.ReadAllText(_persistPath),
                _jsonOpts
            );
            if (records is null)
                return;
            foreach (var r in records.Take(_maxSize))
                _entries.Add(
                    new QueryHistoryEntry(
                        r.Query,
                        r.Timestamp,
                        null,
                        null,
                        TimeSpan.Zero,
                        null,
                        null,
                        false
                    )
                );
        }
        catch
        { /* ignore corrupt history */
        }
    }

    private void AppendToDisk(QueryHistoryEntry entry)
    {
        if (_persistPath is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
            List<DiskRecord> records = [];
            if (File.Exists(_persistPath))
            {
                try
                {
                    records =
                        JsonSerializer.Deserialize<List<DiskRecord>>(
                            File.ReadAllText(_persistPath),
                            _jsonOpts
                        ) ?? [];
                }
                catch
                { /* ignore corrupt */
                }
            }
            records.Insert(0, new DiskRecord { Query = entry.Query, Timestamp = entry.Timestamp });
            while (records.Count > _maxSize)
                records.RemoveAt(records.Count - 1);
            File.WriteAllText(_persistPath, JsonSerializer.Serialize(records, _jsonOpts));
        }
        catch
        { /* ignore write failures — history is best-effort */
        }
    }
}
