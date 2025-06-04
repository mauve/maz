using System.Text.Json;
using Console.Rendering;
using DotMake.CommandLine;

namespace Console.Shared;

public interface IBasicRenderCommand
{
    [CliOption(
        Description = "The output format for the rendered content. Defaults to 'table'.",
        Aliases = ["-f", "--format"],
        Required = false,
        AllowedValues = ["json", "table"]
    )]
    string? OutputFormat { get; set; }

    [CliOption(
        Description = "Whether to output the rendered content in an indented format.",
        Aliases = ["-i", "--indent"],
        Required = false
    )]
    bool OutputIndented { get; set; }
}

public static class IBasicRenderCommandExtensions
{
    public static IRendererFactory GetRendererFactory(this IBasicRenderCommand self) =>
        self.OutputFormat switch
        {
            "json" => new JsonRendererFactory(
                self.OutputIndented ? new() { WriteIndented = true } : JsonSerializerOptions.Default
            ),
            "table" => new TableRendererFactory(),
            null => new TableRendererFactory(),
            _ => throw new InvocationException(
                $"Unsupported output format '{self.OutputFormat}'. Supported formats are 'json' and 'table'."
            ),
        };
}
