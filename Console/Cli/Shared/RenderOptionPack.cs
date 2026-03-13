using System.CommandLine;
using System.Text.Json;
using Console.Rendering;

namespace Console.Cli.Shared;

public class RenderOptionPack : OptionPack
{
    public readonly Option<string?> OutputFormat;
    public readonly Option<bool> OutputIndented;

    public RenderOptionPack()
    {
        OutputFormat = new Option<string?>("--output-format", ["-f", "--format"])
        {
            Description = "The output format. Defaults to 'table'. Allowed: json, table."
        };

        OutputIndented = new Option<bool>("--output-indented", ["-i", "--indent"])
        {
            Description = "Whether to output rendered content in an indented format."
        };
    }

    internal override void AddOptionsTo(Command cmd)
    {
        cmd.Add(OutputFormat);
        cmd.Add(OutputIndented);
    }

    public IRendererFactory GetRendererFactory() =>
        GetValue(OutputFormat) switch
        {
            "json" => new JsonRendererFactory(
                GetValue(OutputIndented) ? new JsonSerializerOptions { WriteIndented = true } : JsonSerializerOptions.Default
            ),
            "table" or null => new TableRendererFactory(),
            var fmt => throw new InvocationException(
                $"Unsupported output format '{fmt}'. Supported formats: json, table."
            ),
        };
}
