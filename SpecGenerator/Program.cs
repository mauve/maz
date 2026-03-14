using SpecGenerator.Config;
using SpecGenerator.Emitting;
using SpecGenerator.Modeling;
using SpecGenerator.Parsing;

// Argument parsing
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
