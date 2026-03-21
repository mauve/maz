using System.Text.Json;
using Console.Cli.Commands.Copy;
using Console.Cli.Http;
using Console.Cli.Parsing;
using Console.Cli.Shared;

namespace Console.Cli.Commands.Generated;

/// <summary>List blobs as NDJSON, streaming to stdout.</summary>
/// <remarks>
/// Enumerates blobs matching the target and optional glob filter, writing one
/// JSON object per line. Output is the same format as the browse export action
/// and can be piped to maz copy --from-file or processed with jq.
///
/// Examples:
///   maz storage query myaccount                                         (all containers)
///   maz storage query myaccount/container
///   maz storage query myaccount/container/prefix --include '**/*.json'
///   maz storage query myaccount/container --tag-query '"env" = ''prod'''
///   maz storage query myaccount/container | jq -r .blob
/// </remarks>
public partial class StorageQueryCommandDef(AuthOptionPack auth) : CommandDef
{
    public override string Name => "query";
    public override string[] Aliases => ["ls"];
    protected internal override bool IsManualCommand => true;
    protected internal override bool IsDataPlane => true;

    private readonly AuthOptionPack _auth = auth;

    // ── Positional argument ───────────────────────────────────────────

    public readonly CliArgument<string> Target = new()
    {
        Name = "target",
        Description =
            "Storage target: account, account/container, or account/container/prefix.",
    };

    internal override IEnumerable<CliArgument<string>> EnumerateArguments()
    {
        yield return Target;
    }

    // ── Options ───────────────────────────────────────────────────────

    /// <summary>SAS token for authentication.</summary>
    [CliOption("--sas-token", Advanced = true)]
    public partial string? SasToken { get; }

    /// <summary>Storage account key for SharedKey authentication.</summary>
    [CliOption("--account-key", Advanced = true)]
    public partial string? AccountKey { get; }

    /// <summary>Glob filter pattern (e.g. '**/*.json').</summary>
    [CliOption("--include")]
    public partial string? Include { get; }

    /// <summary>Exclude glob pattern.</summary>
    [CliOption("--exclude")]
    public partial string? Exclude { get; }

    /// <summary>Tag query expression (e.g. "env" = 'prod').</summary>
    [CliOption("--tag-query", Advanced = true)]
    public partial string? TagQuery { get; }

    // ── Execution ─────────────────────────────────────────────────────

    protected override async Task<int> ExecuteAsync(CancellationToken ct)
    {
        var targetRaw = Target.Value
            ?? throw new InvocationException(
                "A target is required: account, account/container, or account/container/prefix."
            );

        var log = DiagnosticOptionPack.GetLog();
        var target = Commands.Browse.StorageTargetHelper.ParseTarget(targetRaw);

        var blobAuth = Commands.Browse.StorageTargetHelper.BuildAuthStrategy(
            target.Account, SasToken, AccountKey, _auth, log);
        var client = new BlobRestClient(blobAuth, log);

        var includeGlob = !string.IsNullOrEmpty(Include) ? new GlobMatcher(Include) : null;
        var excludeGlob = !string.IsNullOrEmpty(Exclude) ? new GlobMatcher(Exclude) : null;
        var prefix = target.Prefix is not null ? target.Prefix + "/" : null;

        using var stdout = System.Console.OpenStandardOutput();
        using var writer = new System.IO.StreamWriter(stdout, System.Text.Encoding.UTF8, 4096, leaveOpen: true)
        {
            AutoFlush = false,
        };

        // Determine which containers to enumerate
        var containers = new List<string>();
        if (target.Container is not null)
        {
            containers.Add(target.Container);
        }
        else
        {
            await foreach (var c in client.ListContainersAsync(target.Account, ct))
                containers.Add(c.Name);
        }

        foreach (var container in containers)
        {
            if (!string.IsNullOrEmpty(TagQuery))
            {
                await foreach (var tagItem in client.FindBlobsByTagsAsync(
                    target.Account, container, TagQuery, ct))
                {
                    if (!MatchesFilters(tagItem.Name, prefix, includeGlob, excludeGlob))
                        continue;

                    WriteEntry(writer, target.Account, container,
                        new BlobItem(tagItem.Name, 0, null, null));
                }
            }
            else
            {
                await foreach (var blob in client.ListBlobsAsync(
                    target.Account, container, prefix, ct))
                {
                    if (!MatchesFilters(blob.Name, prefix, includeGlob, excludeGlob))
                        continue;

                    WriteEntry(writer, target.Account, container, blob);
                }
            }
        }

        await writer.FlushAsync(ct);
        return 0;
    }

    private static void WriteEntry(
        System.IO.StreamWriter writer,
        string account,
        string container,
        BlobItem blob
    )
    {
        var entry = new Commands.Browse.BlobExportEntry
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
            Commands.Browse.BlobExportJsonContext.RelaxedEncoding.BlobExportEntry
        ));
    }

    private static bool MatchesFilters(
        string blobName,
        string? prefix,
        GlobMatcher? includeGlob,
        GlobMatcher? excludeGlob
    )
    {
        var relativeName = prefix is not null
            && blobName.StartsWith(prefix, StringComparison.Ordinal)
            ? blobName[prefix.Length..]
            : blobName;

        if (includeGlob is not null && !includeGlob.IsMatch(relativeName))
            return false;
        if (excludeGlob is not null && excludeGlob.IsMatch(relativeName))
            return false;
        return true;
    }
}
