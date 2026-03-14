using System.Text.Json;
using System.Text.Json.Nodes;
using SpecGenerator.Config;
using SpecGenerator.Emitting;
using SpecGenerator.Modeling;
using SpecGenerator.Parsing;

// ── analyze subcommand ──────────────────────────────────────────────────────
if (args.Length > 0 && args[0].Equals("analyze", StringComparison.OrdinalIgnoreCase))
{
    return RunAnalyze(args[1..]);
}

// ── generate (default) ─────────────────────────────────────────────────────

string configPath = "specgen.json";
bool verbose = false;

for (var i = 0; i < args.Length; i++)
{
    if (args[i] is "--config" or "-c" && i + 1 < args.Length)
        configPath = args[++i];
    else if (args[i] is "--verbose" or "-v")
        verbose = true;
    else if (!args[i].StartsWith('-'))
        configPath = args[i];
}

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Config file not found: {configPath}");
    Console.Error.WriteLine("Usage: specgen [--config <path>] [--verbose]");
    Console.Error.WriteLine("       specgen analyze --spec-file <path> --display-name <name> --api-version <version>");
    return 1;
}

var repoRoot = Path.GetDirectoryName(Path.GetFullPath(configPath))
    ?? Directory.GetCurrentDirectory();

Console.WriteLine($"Loading config from {configPath}");
var config = ConfigLoader.Load(configPath);

var loader = new SpecLoader(config.SpecsRoot);
var allServiceModels = new List<ServiceModel>();

foreach (var service in config.Services)
{
    Console.WriteLine($"Processing service: {service.DisplayName}");

    var docs = loader.Load(service);

    if (docs.Count == 0)
    {
        Console.Error.WriteLine($"  Warning: no spec files loaded for service '{service.DisplayName}'");
        continue;
    }

    if (verbose)
        Console.WriteLine($"  Loaded {docs.Count} spec file(s)");

    var builder = new ModelBuilder(loader.Resolver, service);
    var model = builder.Build(docs);

    if (verbose)
    {
        Console.WriteLine($"  Found {model.Resources.Count} resource group(s):");
        foreach (var resource in model.Resources)
        {
            Console.WriteLine($"    {resource.CliName} ({resource.Operations.Count} operations)");
            foreach (var op in resource.Operations)
                Console.WriteLine($"      {op.CliName} ({op.HttpMethod})");
        }
    }

    allServiceModels.Add(model);
}

if (allServiceModels.Count == 0)
{
    Console.Error.WriteLine("No service models were built. Check your spec files.");
    return 1;
}

var emitter = new FileEmitter(config, repoRoot);
emitter.Emit(allServiceModels);

Console.WriteLine("Done.");
return 0;

// ── analyze implementation ──────────────────────────────────────────────────

static int RunAnalyze(string[] args)
{
    string? specFile = null;
    string displayName = "service";
    string apiVersion = "2024-01-01";

    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--spec-file" && i + 1 < args.Length)
            specFile = args[++i];
        else if (args[i] is "--display-name" && i + 1 < args.Length)
            displayName = args[++i];
        else if (args[i] is "--api-version" && i + 1 < args.Length)
            apiVersion = args[++i];
    }

    if (specFile is null)
    {
        Console.Error.WriteLine("Usage: specgen analyze --spec-file <path> --display-name <name> --api-version <version>");
        return 1;
    }

    if (!File.Exists(specFile))
    {
        Console.Error.WriteLine($"Spec file not found: {specFile}");
        return 1;
    }

    var absSpec = Path.GetFullPath(specFile);
    var specsRoot = Path.GetDirectoryName(absSpec) ?? Directory.GetCurrentDirectory();

    // Build a minimal service config for analysis (no excludes, auto-detect on)
    var service = new ServiceConfig(
        ServiceDir: displayName,
        DisplayName: displayName,
        ApiVersion: apiVersion,
        SpecFiles: [absSpec],
        Exclude: []
    );

    var specLoader = new SpecLoader(specsRoot);
    var docs = specLoader.Load(service);

    if (docs.Count == 0)
    {
        Console.Error.WriteLine("No documents loaded from spec file.");
        return 1;
    }

    // Collect raw operations for analysis (before any config-driven transforms)
    var rawOps = new List<(string OperationId, string Resource, string ActionCli, string UrlTemplate, bool IsPaged)>();
    var serviceClassName = NamingEngine.KebabToPascal(displayName);

    foreach (var doc in docs)
    {
        foreach (var (path, method, opNode) in doc.GetOperations())
        {
            var operationId = opNode["operationId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(operationId))
                continue;

            var (resourceCli, actionCli) = NamingEngine.SplitOperationId(operationId, displayName);
            var isPaged = opNode["x-ms-pageable"] is not null;
            rawOps.Add((operationId, resourceCli, actionCli, path, isPaged));
        }
    }

    // Detect action renames: get-properties → show, get-*-stats → show
    var suggestedRenames = new Dictionary<string, string>();
    foreach (var op in rawOps)
    {
        if (op.ActionCli is "get-properties" or "get-service-stats" or "get-service-properties")
            suggestedRenames[op.OperationId] = "show";
    }

    // Auto-detect merge pairs
    var suggestedMerges = new List<object>();
    var opsByResource = rawOps.GroupBy(o => o.Resource).ToDictionary(g => g.Key, g => g.ToList());

    foreach (var (resource, ops) in opsByResource)
    {
        var subOps = ops.Where(o =>
            o.IsPaged &&
            o.UrlTemplate.Contains("{subscriptionId}", StringComparison.OrdinalIgnoreCase) &&
            !o.UrlTemplate.Contains("{resourceGroupName}", StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var subOp in subOps)
        {
            var expectedRgAction = subOp.ActionCli + "-by-resource-group";
            var rgMatch = ops.FirstOrDefault(o =>
                o.ActionCli.Equals(expectedRgAction, StringComparison.OrdinalIgnoreCase));

            if (rgMatch == default) continue;

            suggestedMerges.Add(new
            {
                subscriptionOperationId = subOp.OperationId,
                resourceGroupOperationId = rgMatch.OperationId,
                cliAction = subOp.ActionCli,
            });
        }
    }

    // Suggest subgroups: find ops that share a common secondary noun across the same resource
    var suggestedSubgroups = new List<object>();
    var mergedOpIds = suggestedMerges
        .Select(m => (dynamic)m)
        .SelectMany(m => new[] { (string)m.subscriptionOperationId })
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    foreach (var (resource, ops) in opsByResource)
    {
        // Extract secondary nouns from action names (last significant word)
        // e.g. "list-keys" → "keys", "regenerate-key" → "key"
        var nounToOps = new Dictionary<string, List<(string OperationId, string ActionCli)>>(StringComparer.OrdinalIgnoreCase);
        var standardVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "list", "create", "delete", "update", "get", "show", "check", "restore",
              "abort", "failover", "migration" };

        foreach (var op in ops)
        {
            if (mergedOpIds.Contains(op.OperationId))
                continue;

            var words = op.ActionCli.Split('-');
            if (words.Length < 2) continue;

            // Look for a non-verb word in the action (first or last significant word)
            string? noun = null;
            // Check last word first
            var lastWord = words[^1].TrimEnd('s'); // normalize plural
            if (!standardVerbs.Contains(words[^1]) && !standardVerbs.Contains(lastWord))
                noun = lastWord;
            // If last word was a standard verb, check second-to-last
            else if (words.Length >= 2)
            {
                var secondLast = words[^2].TrimEnd('s');
                if (!standardVerbs.Contains(words[^2]) && !standardVerbs.Contains(secondLast))
                    noun = secondLast;
            }

            if (noun is null) continue;

            if (!nounToOps.TryGetValue(noun, out var nounOps))
            {
                nounOps = [];
                nounToOps[noun] = nounOps;
            }
            nounOps.Add((op.OperationId, op.ActionCli));
        }

        foreach (var (noun, nounOps) in nounToOps.Where(kv => kv.Value.Count >= 2))
        {
            suggestedSubgroups.Add(new
            {
                resource,
                subgroupCliName = noun,
                operationIds = nounOps.Select(o => o.OperationId).ToArray(),
            });
        }
    }

    // Build the proposed config snippet
    var proposed = new
    {
        serviceDir = displayName,
        displayName,
        apiVersion,
        specFiles = new[] { absSpec },
        exclude = Array.Empty<string>(),
        actionRenames = suggestedRenames.Count > 0 ? suggestedRenames : null,
        merges = suggestedMerges.Count > 0 ? suggestedMerges : null,
        subgroups = suggestedSubgroups.Count > 0 ? suggestedSubgroups : null,
    };

#pragma warning disable CA1869
    Console.WriteLine(JsonSerializer.Serialize(proposed, new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    }));
#pragma warning restore CA1869

    // Also print a summary of detected operations to stderr
    Console.Error.WriteLine($"\nDetected {rawOps.Count} operations across {opsByResource.Count} resource group(s):");
    foreach (var (resource, ops) in opsByResource.OrderBy(kv => kv.Key))
    {
        Console.Error.WriteLine($"  {resource}:");
        foreach (var op in ops)
        {
            var renamed = suggestedRenames.TryGetValue(op.OperationId, out var newName) ? $" → {newName}" : "";
            Console.Error.WriteLine($"    {op.ActionCli}{renamed}  ({op.OperationId})");
        }
    }

    return 0;
}
