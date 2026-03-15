using SpecGenerator.Config;
using SpecGenerator.Modeling;

namespace SpecGenerator.Emitting;

/// <summary>
/// Orchestrates code emission: creates output directories and writes all generated C# files.
/// </summary>
public sealed class FileEmitter
{
    private readonly GeneratorConfig _config;
    private readonly string _repoRoot;

    public FileEmitter(GeneratorConfig config, string repoRoot)
    {
        _config = config;
        _repoRoot = repoRoot;
    }

    public void Emit(List<ServiceModel> services)
    {
        var outputRoot = Path.IsPathRooted(_config.OutputDir)
            ? _config.OutputDir
            : Path.GetFullPath(Path.Combine(_repoRoot, _config.OutputDir));

        // Emit per-service files
        foreach (var service in services)
        {
            var serviceName = NamingEngine.KebabToPascal(service.CliName);
            var serviceDir = Path.Combine(outputRoot, serviceName);

            // Delete stale files from previous generator runs
            if (Directory.Exists(serviceDir))
                Directory.Delete(serviceDir, recursive: true);

            Directory.CreateDirectory(serviceDir);

            // Leaf operation commands and resource group commands
            foreach (var resource in service.Resources)
                EmitResource(resource, serviceName, serviceDir, service.DataplaneOptionPack, service.ResourceOptionPack);

            // Service command
            var serviceContent = ServiceCommandEmitter.Emit(service, _config.CommandNamespace);
            var serviceFile = Path.Combine(serviceDir, $"{service.ClassName}.cs");
            WriteIfChanged(serviceFile, serviceContent);
        }

        // Root patch
        var rootContent = RootPatchEmitter.Emit(services, _config.CommandNamespace);
        var rootFile = Path.Combine(outputRoot, "RootCommandDefGenerated.cs");
        WriteIfChanged(rootFile, rootContent);

        System.Console.WriteLine($"Generated {services.Count} service(s) → {outputRoot}");
    }

    private void EmitResource(
        ResourceGroupModel resource,
        string serviceClassName,
        string outputDir,
        DataplaneOptionPackConfig? packConfig = null,
        ResourceOptionPackConfig? resourcePackConfig = null
    )
    {
        var isDataPlane = packConfig is not null;

        // Leaf operation commands
        foreach (var op in resource.Operations)
        {
            var content = OperationCommandEmitter.Emit(
                op,
                serviceClassName,
                _config.CommandNamespace,
                packConfig,
                resourcePackConfig
            );
            var fileName = Path.Combine(outputDir, $"{op.ClassName}.cs");
            WriteIfChanged(fileName, content);
        }

        // Resource group command (includes subgroup fields)
        var resourceContent = ResourceCommandEmitter.Emit(
            resource,
            _config.CommandNamespace,
            isDataPlane
        );
        var resourceFile = Path.Combine(outputDir, $"{resource.ClassName}.cs");
        WriteIfChanged(resourceFile, resourceContent);

        // Recurse into subgroups
        foreach (var sub in resource.Subgroups ?? [])
            EmitResource(sub, serviceClassName, outputDir, packConfig, resourcePackConfig);
    }

    private static void WriteIfChanged(string filePath, string content)
    {
        if (File.Exists(filePath))
        {
            var existing = File.ReadAllText(filePath);
            if (existing == content)
                return;
        }

        File.WriteAllText(filePath, content);
        System.Console.WriteLine($"  wrote {filePath}");
    }
}
