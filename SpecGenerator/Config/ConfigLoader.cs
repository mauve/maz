using System.Text.Json;

namespace SpecGenerator.Config;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static GeneratorConfig Load(string configPath)
    {
        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<GeneratorConfig>(json, _options)
            ?? throw new InvalidOperationException(
                $"Failed to deserialize config at '{configPath}'."
            );
    }
}
