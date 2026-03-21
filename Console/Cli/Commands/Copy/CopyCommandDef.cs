using System.Diagnostics;
using System.Threading.Channels;
using Console.Cli.Http;
using Console.Cli.Parsing;
using Console.Cli.Shared;
using Console.Tui;

namespace Console.Cli.Commands.Copy;

/// <summary>Copy blobs between local filesystem and Azure Blob Storage.</summary>
/// <remarks>
/// Supports upload, download, and storage-to-storage copy with parallel chunked transfers.
///
/// Path formats:
///   Local:     ./dir, /path, ~/dir, C:\path
///   Shorthand: account/container[/prefix]
///   Full URL:  https://account.blob.core.windows.net/container[/prefix]
///
/// Examples:
///   maz copy ./data myaccount/mycontainer/backup
///   maz copy myaccount/mycontainer/logs ./local-logs --include '*.json'
///   maz copy srcaccount/c1 destaccount/c2
///   maz copy 'myaccount/c1/**/*.json' ./local --exclude 'temp_*'
///   maz copy myaccount/c1 ./local --tag-filter env=prod
///   maz copy acct/c1/folder1 acct/c1/folder2 ./local    (multiple sources)
///
/// File attributes:
///   Downloaded files get blob metadata stored as extended attributes (xattr on
///   Linux/macOS, NTFS Alternate Data Streams on Windows). The following are
///   always written: maz.blob.url, maz.blob.content-type, and maz.blob.tag.{key}
///   for each blob index tag. Use --save-properties to also store ETag, headers,
///   and access tier. On Linux the 'user.' namespace is prepended automatically
///   (e.g. user.maz.blob.url). Read with getfattr -d (Linux), xattr -l (macOS),
///   or Get-Content file -Stream maz.blob.url (Windows).
///
/// Verification:
///   Use --verify to re-read each downloaded file and compare its MD5 hash against
///   the blob's Content-MD5 header. Blobs without Content-MD5 are skipped with a
///   warning. The TUI shows a cyan progress bar during verification.
/// </remarks>
public partial class CopyCommandDef(AuthOptionPack auth, InteractiveOptionPack interactive)
    : CommandDef
{
    public override string Name => "copy";
    public override string[] Aliases => ["cp"];

    public override string Description =>
        "Copy blobs between local filesystem and Azure Blob Storage.";

    protected internal override bool IsManualCommand => true;
    protected internal override bool IsDataPlane => true;

    private readonly AuthOptionPack _auth = auth;
    private readonly InteractiveOptionPack _interactive = interactive;

    // ── Positional arguments ─────────────────────────────────────────────

    public readonly CliArgument<string> Paths = new()
    {
        Name = "paths",
        Description = "Source path(s) followed by the destination. Like cp: src [src...] dest.",
        IsRest = true,
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return Paths;
    }

    // ── Options ──────────────────────────────────────────────────────────

    /// <summary>SAS token for authentication.</summary>
    [CliOption("--sas-token", Advanced = true)]
    public partial string? SasToken { get; }

    /// <summary>Storage account key for SharedKey authentication.</summary>
    [CliOption("--account-key", Advanced = true)]
    public partial string? AccountKey { get; }

    /// <summary>Number of concurrent blob transfers.</summary>
    [CliOption("--parallel", "-p")]
    public partial int Parallel { get; } = 4;

    /// <summary>Block size in bytes for chunked transfers.</summary>
    [CliOption("--block-size", Advanced = true)]
    public partial int BlockSize { get; } = 4 * 1024 * 1024;

    /// <summary>Include glob pattern for filtering source items.</summary>
    [CliOption("--include")]
    public partial string? Include { get; }

    /// <summary>Exclude glob pattern for filtering source items.</summary>
    [CliOption("--exclude")]
    public partial string? Exclude { get; }

    /// <summary>Simple tag filter as key=value pairs (AND logic).</summary>
    [CliOption("--tag-filter")]
    public partial string? TagFilter { get; }

    /// <summary>Raw SQL-like tag query expression.</summary>
    [CliOption("--tag-query", Advanced = true)]
    public partial string? TagQuery { get; }

    /// <summary>Overwrite policy for existing destinations.</summary>
    [CliOption("--overwrite-policy")]
    public partial OverwritePolicy OverwritePolicy { get; } = OverwritePolicy.Skip;

    /// <summary>Force client-side copy instead of server-side for storage-to-storage.</summary>
    [CliOption("--force-client-side", Advanced = true)]
    public partial bool ForceClientSide { get; }

    /// <summary>Path to the journal directory for resume support.</summary>
    [CliOption("--journal-path", Advanced = true)]
    public partial string? JournalPath { get; }

    /// <summary>Disable the resume journal.</summary>
    [CliOption("--no-journal", Advanced = true)]
    public partial bool NoJournal { get; }

    /// <summary>Save extended blob properties (ETag, headers, tier) as file attributes.</summary>
    [CliOption("--save-properties", Advanced = true)]
    public partial bool SaveProperties { get; }

    /// <summary>Verify downloaded files against the blob's Content-MD5 hash.</summary>
    [CliOption("--verify")]
    public partial bool Verify { get; }

    // ── Execution ────────────────────────────────────────────────────────

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var log = DiagnosticOptionPack.GetLog();
        var paths = Paths.Values;

        if (paths.Count < 2)
            throw new InvocationException(
                "At least two paths are required: <source...> <destination>."
            );

        var sourceRaws = paths[..^1];
        var destRaw = paths[^1];
        var dest = CopyPath.Parse(destRaw);

        log.Trace($"Destination: {destRaw} (kind: {dest.Kind})");

        // Build auth strategy from the first blob-storage path we find
        CopyPath firstBlobPath = dest;
        foreach (var s in sourceRaws)
        {
            var p = CopyPath.Parse(s);
            if (p.Kind == CopyPathKind.BlobStorage)
            {
                firstBlobPath = p;
                break;
            }
        }

        var blobAuth = BuildAuthStrategy(firstBlobPath, dest, log);
        var client = new BlobRestClient(blobAuth, log);

        // Build tag query from --tag-filter or --tag-query
        var tagQuery = BuildTagQuery();

        // Journal for resume — keyed on the full argument list
        var journalKey = string.Join("|", paths);
        CopyJournal? journal = null;
        if (!NoJournal)
            journal = CopyJournal.Open(journalKey, destRaw, JournalPath);

        // Build a channel that streams items from all sources
        var itemChannel = Channel.CreateUnbounded<TransferItem>();
        var enumTask = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var sourceRaw in sourceRaws)
                    {
                        var source = CopyPath.Parse(sourceRaw);
                        log.Trace($"Source: {sourceRaw} (kind: {source.Kind})");

                        if (source.Kind == CopyPathKind.Local && dest.Kind == CopyPathKind.Local)
                            throw new InvocationException(
                                $"At least one side of the copy must be Azure Blob Storage. "
                                    + $"Source '{sourceRaw}' and destination '{destRaw}' are both local."
                            );

                        var plan = new TransferPlan(
                            client,
                            source,
                            dest,
                            OverwritePolicy,
                            Include,
                            Exclude,
                            tagQuery,
                            log,
                            sourceGroup: sourceRaw,
                            saveProperties: SaveProperties
                        );

                        await plan.EnumerateAsync(itemChannel.Writer, ForceClientSide, ct);
                    }
                }
                finally
                {
                    itemChannel.Writer.TryComplete();
                }
            },
            ct
        );

        var result = await RunTransferPipeline(client, dest, journal, itemChannel.Reader, ct);

        await enumTask;
        return result;
    }

    private async Task<int> RunTransferPipeline(
        BlobRestClient client,
        CopyPath dest,
        CopyJournal? journal,
        ChannelReader<TransferItem> itemSource,
        CancellationToken ct
    )
    {
        var engine = new BlockTransferEngine(
            client,
            dest,
            itemSource,
            Parallel,
            BlockSize,
            journal,
            ct,
            saveProperties: SaveProperties,
            verify: Verify
        );

        // Start engine in background
        var engineTask = Task.Run(() => engine.RunAsync(), ct);

        // Interactive or non-interactive output
        var isInteractive = InteractiveOptionPack.IsEffectivelyInteractive(
            _interactive.Interactive
        );
        if (isInteractive)
        {
            await using var tui = new CopyTuiApp(engine);
            await tui.RunAsync(ct);

            // Print summary after alternate screen is dismissed
            PrintSummary(tui);
        }
        else
        {
            var sw = Stopwatch.StartNew();
            await CopyNdjsonOutput.DrainAsync(engine, sw, ct);
        }

        await engineTask;

        // Clean up journal on full success
        journal?.Cleanup();

        return 0;
    }

    private static void PrintSummary(CopyTuiApp tui)
    {
        var err = System.Console.Error;

        // 1) Per-source summary (only for multi-source)
        if (tui.IsMultiSource)
        {
            foreach (var g in tui.GroupStats)
            {
                var status =
                    g.Failed > 0 ? Rendering.Ansi.Red($"{g.Completed}/{g.Total}")
                    : g.Completed == g.Total ? Rendering.Ansi.Green($"{g.Completed}/{g.Total}")
                    : $"{g.Completed}/{g.Total}";
                err.WriteLine($"  {status}  {FormatBytes(g.Bytes), 10}  {g.Group}");
            }
            err.WriteLine();
        }

        // 2) All errors
        if (tui.FailedItems > 0)
        {
            foreach (var (item, error) in tui.Failures)
            {
                err.Write(Rendering.Ansi.Red("FAILED"));
                err.WriteLine($"  {item.SourcePath}");
                err.WriteLine($"        {error}");
            }
            err.WriteLine();
        }

        // 3) One-line summary
        var elapsed = tui.Elapsed;
        var totalBytes = tui.TotalBytes;
        var avgSpeed = elapsed.TotalSeconds > 0 ? totalBytes / elapsed.TotalSeconds : 0;
        var timeStr =
            elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}"
                : $"{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";

        var summary =
            $"Copied {tui.CompletedItems}/{tui.TotalItems} item(s), "
            + $"{FormatBytes(totalBytes)} in {timeStr} ({FormatSpeed(avgSpeed)})";

        if (tui.FailedItems > 0)
            summary += $", {tui.FailedItems} failed";

        err.WriteLine(summary);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
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
        return $"{bytesPerSec / (1024.0 * 1024 * 1024):F1} GB/s";
    }

    private IBlobAuthStrategy BuildAuthStrategy(CopyPath source, CopyPath dest, DiagnosticLog log)
    {
        if (!string.IsNullOrEmpty(SasToken))
        {
            log.Credential("Using SAS token authentication");
            return new SasBlobAuth(SasToken);
        }

        var account =
            source.Kind == CopyPathKind.BlobStorage ? source.AccountName! : dest.AccountName!;

        if (!string.IsNullOrEmpty(AccountKey))
        {
            log.Credential($"Using SharedKey authentication for account '{account}'");
            return new SharedKeyBlobAuth(account, AccountKey);
        }

        log.Credential("Using token credential (scope: storage.azure.com)");
        var credential = _auth.GetCredential(log);
        return new TokenBlobAuth(credential);
    }

    private string? BuildTagQuery()
    {
        if (!string.IsNullOrEmpty(TagQuery))
            return TagQuery;

        if (string.IsNullOrEmpty(TagFilter))
            return null;

        // Parse key=value pairs and build AND query
        var parts = TagFilter.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );
        var conditions = new List<string>();
        foreach (var part in parts)
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
                throw new InvocationException(
                    $"Invalid tag filter '{part}'. Expected format: key=value"
                );
            conditions.Add($"\"{kv[0].Trim()}\" = '{kv[1].Trim()}'");
        }

        return string.Join(" AND ", conditions);
    }
}
