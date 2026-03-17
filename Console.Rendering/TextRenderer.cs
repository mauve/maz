using System.Reflection;
using System.Text.Json.Nodes;
using Azure.ResourceManager;
using Azure.ResourceManager.Models;

namespace Console.Rendering;

public class TextRendererFactory(bool showAll, bool showEnvelope, ValueFormatterOptions fmtOpts)
    : IRendererFactory
{
    public IRenderer CreateRendererForType(Type type) =>
        new TextItemRenderer(type, showAll, showEnvelope, fmtOpts);
}

internal class TextItemRenderer(
    Type type,
    bool showAll,
    bool showEnvelope,
    ValueFormatterOptions fmtOpts
) : IRenderer
{
    public Task RenderAsync(TextWriter output, object data, CancellationToken cancellationToken)
    {
        var isArmResource = typeof(ArmResource).IsAssignableFrom(type);

        if (showEnvelope && !isArmResource)
        {
            System.Console.Error.WriteLine(
                Ansi.Yellow("warning: --show-envelope has no effect for non-ArmResource types")
            );
        }

        object? dataValue;
        if (isArmResource)
        {
            if (showEnvelope)
                RenderEnvelope(output, data);

            var dataProp =
                type.GetProperty("Data")
                ?? throw new InvalidOperationException(
                    $"ArmResource type {type.FullName} has no Data property."
                );
            dataValue = dataProp.GetValue(data);
        }
        else
        {
            dataValue = data;
        }

        if (dataValue == null)
            return Task.CompletedTask;

        // JsonNode: render JSON properties directly instead of using reflection
        if (dataValue is JsonObject jsonObj)
        {
            var entries = new List<(string Label, string Value)>();
            foreach (var (key, node) in jsonObj)
            {
                if (node is null)
                    continue;
                var formatted = ValueFormatter.Format(node.ToString(), fmtOpts);
                entries.Add((key, ApplyAnsi(formatted)));
            }
            if (entries.Count > 0)
                DefinitionList.Write(output, entries);
            output.WriteLine();
            return Task.CompletedTask;
        }

        var dataType = dataValue.GetType();
        var properties = dataType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        {
            var entries = new List<(string Label, string Value)>();
            foreach (var prop in properties)
            {
                if (TryFormatField(dataType, prop, dataValue, out var formattedValue))
                    entries.Add((prop.Name, formattedValue!));
            }

            if (entries.Count > 0)
                DefinitionList.Write(output, entries);
        }

        output.WriteLine();
        return Task.CompletedTask;
    }

    private bool TryFormatField(
        Type dataType,
        PropertyInfo prop,
        object dataValue,
        out string? formattedValue
    )
    {
        formattedValue = null;

        if (showAll)
        {
            formattedValue = ApplyAnsi(ValueFormatter.Format(prop.GetValue(dataValue), fmtOpts));
            return true;
        }

        var registryResult = TextFieldRegistry.IsFieldVisible(dataType, prop.Name);

        if (registryResult == false)
            return false;

        if (registryResult == null) // heuristic
        {
            if (TextFieldRegistry.IsTypeHiddenByHeuristic(prop.PropertyType))
                return false;
            var v = prop.GetValue(dataValue);
            if (v == null)
                return false;
            formattedValue = ApplyAnsi(ValueFormatter.Format(v, fmtOpts));
            return true;
        }

        // registryResult == true: always show
        {
            var v = prop.GetValue(dataValue);
            formattedValue = ApplyAnsi(ValueFormatter.Format(v, fmtOpts));
            return true;
        }
    }

    private static string ApplyAnsi(FormattedValue fv) =>
        fv.AnsiCode != null ? Ansi.Color(fv.Text, fv.AnsiCode) : fv.Text;

    private void RenderEnvelope(TextWriter output, object resource)
    {
        var dataProp = type.GetProperty("Data");
        if (dataProp == null)
            return;
        var data = dataProp.GetValue(resource);
        if (data == null)
            return;

        var dataType = data.GetType();
        var entries = new List<(string Label, string Value)>();

        var idProp = dataType.GetProperty("Id");
        if (idProp?.GetValue(data) is { } idVal)
        {
            var fv = ValueFormatter.Format(idVal.ToString(), fmtOpts);
            entries.Add(("Id", ApplyAnsi(fv)));
        }

        var rtProp = dataType.GetProperty("ResourceType");
        if (rtProp?.GetValue(data) is { } rtVal)
            entries.Add(("Type", rtVal.ToString() ?? ""));

        var sdProp = dataType.GetProperty("SystemData");
        if (sdProp?.GetValue(data) is SystemData sd)
            entries.Add(("SystemData", FormatSystemData(sd)));

        if (entries.Count > 0)
        {
            DefinitionList.Write(output, entries);
            output.WriteLine(Ansi.Dim(new string('─', 40)));
        }
    }

    private static string FormatSystemData(SystemData sd)
    {
        var parts = new List<string>();
        if (sd.LastModifiedOn.HasValue)
            parts.Add($"modified {sd.LastModifiedOn.Value:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(sd.LastModifiedBy))
            parts.Add($"by {sd.LastModifiedBy}");
        if (parts.Count == 0 && sd.CreatedOn.HasValue)
            parts.Add($"created {sd.CreatedOn.Value:yyyy-MM-dd}");
        return parts.Count > 0 ? string.Join(" ", parts) : "(no data)";
    }
}
