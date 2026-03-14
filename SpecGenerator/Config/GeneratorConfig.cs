namespace SpecGenerator.Config;

public record GeneratorConfig(
    string SpecsRoot,
    string OutputDir,
    string CommandNamespace,
    List<ServiceConfig> Services
);

public record ServiceConfig(
    string ServiceDir,
    string DisplayName,
    string ApiVersion,
    List<string> SpecFiles,
    List<string> Exclude
);
