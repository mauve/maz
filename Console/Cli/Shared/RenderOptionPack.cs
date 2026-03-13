using System.Text.Json;
using Console.Rendering;

namespace Console.Cli.Shared;

public partial class RenderOptionPack : OptionPack
{
    /// <summary>The output format. Defaults to 'column'. Allowed: json, table, column.</summary>
    [CliOption("--output-format", "-f", "--format")]
    public partial string? OutputFormat { get; }

    /// <summary>Whether to output rendered content in an indented format.</summary>
    [CliOption("--output-indented", "-i", "--indent")]
    public partial bool OutputIndented { get; }

    /// <summary>Date format string for date/time values. [default: yyyy-MM-ddTHH:mm:ssZ]</summary>
    [CliOption("--date-format")]
    public partial string? DateFormat { get; }

    public override string HelpTitle => "Output";

    public IRendererFactory GetRendererFactory() =>
        OutputFormat switch
        {
            "json" => new JsonRendererFactory(
                OutputIndented
                    ? new JsonSerializerOptions { WriteIndented = true }
                    : JsonSerializerOptions.Default
            ),
            "table" => new TableRendererFactory(),
            "column" or null => new ColumnRendererFactory(GetValueFormatterOptions()),
            var fmt => throw new InvocationException(
                $"Unsupported output format '{fmt}'. Supported formats: json, table, column."
            ),
        };

    public ValueFormatterOptions GetValueFormatterOptions() =>
        new(DateFormat ?? "yyyy-MM-ddTHH:mm:ssZ");
}
