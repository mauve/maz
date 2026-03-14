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
            Directory.CreateDirectory(serviceDir);

            // Leaf operation commands
            foreach (var resource in service.Resources)
            {
                foreach (var op in resource.Operations)
                {
                    var content = OperationCommandEmitter.Emit(op, serviceName, _config.CommandNamespace);
                    var fileName = Path.Combine(serviceDir, $"{op.ClassName}.cs");
                    WriteIfChanged(fileName, content);
                }

                // Resource group command
                var resourceContent = ResourceCommandEmitter.Emit(resource, _config.CommandNamespace);
                var resourceFile = Path.Combine(serviceDir, $"{resource.ClassName}.cs");
                WriteIfChanged(resourceFile, resourceContent);
            }

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
