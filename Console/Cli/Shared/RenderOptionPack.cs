using System.Text.Json;
using Console.Rendering;

namespace Console.Cli.Shared;

public partial class RenderOptionPack : OptionPack
{
    /// <summary>The output format. Defaults to 'table'. Allowed: json, table.</summary>
    [CliOption("--output-format", "-f", "--format")]
    public partial string? OutputFormat { get; }

    /// <summary>Whether to output rendered content in an indented format.</summary>
    [CliOption("--output-indented", "-i", "--indent")]
    public partial bool OutputIndented { get; }

    internal override void AddOptionsTo(System.CommandLine.Command cmd)
        => AddGeneratedOptions(cmd);

    public IRendererFactory GetRendererFactory() =>
        OutputFormat switch
        {
            "json" => new JsonRendererFactory(
                OutputIndented ? new JsonSerializerOptions { WriteIndented = true } : JsonSerializerOptions.Default
            ),
            "table" or null => new TableRendererFactory(),
            var fmt => throw new InvocationException(
                $"Unsupported output format '{fmt}'. Supported formats: json, table."
            ),
        };
}
