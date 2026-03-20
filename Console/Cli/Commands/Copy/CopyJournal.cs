using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Console.Cli.Commands.Copy;

/// <summary>
/// Cross-session resume journal for copy operations.
/// Stores progress as append-only NDJSON for crash-safe checkpointing.
/// </summary>
public sealed class CopyJournal : IDisposable
{
    private readonly string _journalPath;
    private readonly StreamWriter _writer;
    private readonly HashSet<int> _completedItems = [];
    private readonly Dictionary<int, HashSet<int>> _completedBlocks = [];

    private CopyJournal(string journalPath, StreamWriter writer)
    {
        _journalPath = journalPath;
        _writer = writer;
    }

    /// <summary>
    /// Open or create a journal for the given source/dest pair.
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

    /// <summary>Write the initial plan entry.</summary>
    public void WritePlan(string source, string dest, int itemCount)
    {
        var json = JsonSerializer.Serialize(
            new
            {
                type = "plan",
                version = 1,
                source,
                destination = dest,
                items = itemCount,
                timestamp = DateTimeOffset.UtcNow,
            }
        );
        _writer.WriteLine(json);
    }

    /// <summary>Record a completed block.</summary>
    public void WriteBlockCompleted(int itemIndex, int blockIndex, long offset, long length)
    {
        if (!_completedBlocks.TryGetValue(itemIndex, out var blocks))
        {
            blocks = [];
            _completedBlocks[itemIndex] = blocks;
        }
        blocks.Add(blockIndex);

        var json = JsonSerializer.Serialize(
            new
            {
                type = "block",
                item = itemIndex,
                block = blockIndex,
                offset,
                length,
                status = "ok",
                timestamp = DateTimeOffset.UtcNow,
            }
        );
        _writer.WriteLine(json);
    }

    /// <summary>Record a completed item.</summary>
    public void WriteItemCompleted(int itemIndex, long bytes)
    {
        _completedItems.Add(itemIndex);

        var json = JsonSerializer.Serialize(
            new
            {
                type = "item",
                item = itemIndex,
                status = "completed",
                bytes,
                timestamp = DateTimeOffset.UtcNow,
            }
        );
        _writer.WriteLine(json);
    }

    /// <summary>Check if a specific item has been completed in a prior session.</summary>
    public bool IsItemCompleted(int itemIndex) => _completedItems.Contains(itemIndex);

    /// <summary>Get the set of completed block indices for an item.</summary>
    public HashSet<int> GetCompletedBlocks(int itemIndex) =>
        _completedBlocks.TryGetValue(itemIndex, out var blocks)
            ? blocks
            : [];

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
                var bItem = root.GetProperty("item").GetInt32();
                var bBlock = root.GetProperty("block").GetInt32();
                if (!_completedBlocks.TryGetValue(bItem, out var blocks))
                {
                    blocks = [];
                    _completedBlocks[bItem] = blocks;
                }
                blocks.Add(bBlock);
                break;

            case "item":
                var iItem = root.GetProperty("item").GetInt32();
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
