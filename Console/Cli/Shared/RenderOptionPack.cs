using System.Text.Json;
using Console.Rendering;

namespace Console.Cli.Shared;

public partial class RenderOptionPack : OptionPack
{
    /// <summary>The output format. Defaults to 'column'.</summary>
    [CliOption("-f", "--format")]
    public partial OutputFormat? Format { get; }

    /// <summary>Show all fields, including those hidden by default.</summary>
    [CliOption("--show-all")]
    public partial bool ShowAll { get; }

    /// <summary>Show ArmResource envelope fields (Id, Type, SystemData) before the data block.</summary>
    [CliOption("--show-envelope")]
    public partial bool ShowEnvelope { get; }

    /// <summary>Date format string for date/time values. [default: yyyy-MM-ddTHH:mm:ssZ]</summary>
    [CliOption("--date-format")]
    public partial string? DateFormat { get; }

    public override string HelpTitle => "Output";

    public IRendererFactory GetRendererFactory() =>
        Format switch
        {
            OutputFormat.Json => new JsonRendererFactory(JsonSerializerOptions.Default),
            OutputFormat.JsonL => new JsonLRendererFactory(),
            OutputFormat.JsonPretty => new JsonPrettyRendererFactory(),
            OutputFormat.Text => new TextRendererFactory(ShowAll, ShowEnvelope, GetValueFormatterOptions()),
            OutputFormat.Column or null => new ColumnRendererFactory(GetValueFormatterOptions(), ShowEnvelope),
            var fmt => throw new InvocationException($"Unsupported output format '{fmt}'."),
        };

    public ValueFormatterOptions GetValueFormatterOptions() =>
        new(DateFormat ?? "yyyy-MM-ddTHH:mm:ssZ");
}
