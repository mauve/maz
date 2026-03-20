using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Console.Cli.Commands.Copy;

/// <summary>
/// Cross-session resume journal for copy operations.
/// Stores progress as append-only NDJSON for crash-safe checkpointing.
/// Items are keyed by source path (stable across runs) rather than index.
/// </summary>
public sealed class CopyJournal : IDisposable
{
    private readonly string _journalPath;
    private readonly StreamWriter _writer;
    private readonly HashSet<string> _completedItems = [];
    private readonly Dictionary<string, HashSet<int>> _completedBlocks = new(
        StringComparer.Ordinal
    );

    private CopyJournal(string journalPath, StreamWriter writer)
    {
        _journalPath = journalPath;
        _writer = writer;
    }

    /// <summary>
    /// Open or create a journal for the given arguments.
    /// If a matching journal exists, parse its completed state for resume.
    /// </summary>
    public static CopyJournal Open(string source, string dest, string? journalDir)
    {
        var dir = journalDir ?? GetDefaultJournalDir();
        Directory.CreateDirectory(dir);

        var hash = ComputeHash(source, dest);
        var path = Path.Combine(dir, $"{hash}.jsonl");

        var journal = new CopyJournal(
            path,
            new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true }
        );

        // Parse existing entries for resume
        if (File.Exists(path))
        {
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;
                    journal.ParseEntry(line);
                }
            }
            catch
            {
                // Corrupt journal — start fresh
                journal._completedItems.Clear();
                journal._completedBlocks.Clear();
            }
        }

        return journal;
    }

    /// <summary>Record a completed block.</summary>
    public void WriteBlockCompleted(string sourcePath, int blockIndex, long offset, long length)
    {
        if (!_completedBlocks.TryGetValue(sourcePath, out var blocks))
        {
            blocks = [];
            _completedBlocks[sourcePath] = blocks;
        }
        blocks.Add(blockIndex);

        var json = JsonSerializer.Serialize(
            new JournalBlockEntry
            {
                Item = sourcePath,
                Block = blockIndex,
                Offset = offset,
                Length = length,
                Timestamp = DateTimeOffset.UtcNow,
            },
            JournalJsonContext.Default.JournalBlockEntry
        );
        _writer.WriteLine(json);
    }

    /// <summary>Record a completed item.</summary>
    public void WriteItemCompleted(string sourcePath, long bytes)
    {
        _completedItems.Add(sourcePath);

        var json = JsonSerializer.Serialize(
            new JournalItemEntry
            {
                Item = sourcePath,
                Bytes = bytes,
                Timestamp = DateTimeOffset.UtcNow,
            },
            JournalJsonContext.Default.JournalItemEntry
        );
        _writer.WriteLine(json);
    }

    /// <summary>Check if a specific item has been completed in a prior session.</summary>
    public bool IsItemCompleted(string sourcePath) => _completedItems.Contains(sourcePath);

    /// <summary>Get the set of completed block indices for an item.</summary>
    public HashSet<int> GetCompletedBlocks(string sourcePath) =>
        _completedBlocks.TryGetValue(sourcePath, out var blocks) ? blocks : [];

    /// <summary>Whether any items were completed in a prior session.</summary>
    public bool HasCompletedItems => _completedItems.Count > 0;

    /// <summary>Number of items completed in prior sessions.</summary>
    public int CompletedItemCount => _completedItems.Count;

    /// <summary>Delete the journal file on full success.</summary>
    public void Cleanup()
    {
        _writer.Dispose();
        try
        {
            File.Delete(_journalPath);
        }
        catch
        {
            // Best effort
        }
    }

    public void Dispose() => _writer.Dispose();

    private void ParseEntry(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "block":
                var bItem = root.GetProperty("item").GetString()!;
                var bBlock = root.GetProperty("block").GetInt32();
                if (!_completedBlocks.TryGetValue(bItem, out var blocks))
                {
                    blocks = [];
                    _completedBlocks[bItem] = blocks;
                }
                blocks.Add(bBlock);
                break;

            case "item":
                var iItem = root.GetProperty("item").GetString()!;
                var status = root.GetProperty("status").GetString();
                if (status == "completed")
                    _completedItems.Add(iItem);
                break;
        }
    }

    private static string ComputeHash(string source, string dest)
    {
        var input = $"{source}|{dest}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string GetDefaultJournalDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".maz", "copy-journal");
    }
}

#pragma warning disable CA1822 // constant properties must be instance for JSON serialization
internal record JournalBlockEntry
{
    [JsonPropertyName("type")]
    public string Type => "block";

    [JsonPropertyName("item")]
    public required string Item { get; init; }

    [JsonPropertyName("block")]
    public required int Block { get; init; }

    [JsonPropertyName("offset")]
    public required long Offset { get; init; }

    [JsonPropertyName("length")]
    public required long Length { get; init; }

    [JsonPropertyName("status")]
    public string Status => "ok";

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }
}

internal record JournalItemEntry
{
    [JsonPropertyName("type")]
    public string Type => "item";

    [JsonPropertyName("item")]
    public required string Item { get; init; }

    [JsonPropertyName("status")]
    public string Status => "completed";

    [JsonPropertyName("bytes")]
    public required long Bytes { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }
}
#pragma warning restore CA1822

[JsonSerializable(typeof(JournalBlockEntry))]
[JsonSerializable(typeof(JournalItemEntry))]
internal partial class JournalJsonContext : JsonSerializerContext;
