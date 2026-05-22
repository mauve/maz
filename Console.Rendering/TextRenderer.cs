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
            RenderJsonObject(output, jsonObj, indent: 2);
            output.WriteLine();
            return Task.CompletedTask;
        }

        var dataType = dataValue.GetType();
        var properties = dataType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        {
            var entries = new List<(string Label, string Value)>();
            foreach (var prop in properties)
            {
                if (TryFormatField(dataType, prop, dataValue, out var label, out var formattedValue))
                    entries.Add((label!, formattedValue!));
            }

            if (entries.Count > 0)
                DefinitionList.Write(output, entries);
        }

        output.WriteLine();
        return Task.CompletedTask;
    }

    // ── JsonNode recursive rendering ──────────────────────────────────────

    private void RenderJsonObject(TextWriter output, JsonObject obj, int indent)
    {
        var maxLabelWidth = obj.Select(kv => kv.Key.Length).DefaultIfEmpty(0).Max();
        var consoleWidth = DefinitionList.GetConsoleWidth();

        foreach (var (key, node) in obj)
            RenderJsonProperty(output, key, node, indent, maxLabelWidth, consoleWidth);
    }

    private void RenderJsonProperty(
        TextWriter output,
        string key,
        JsonNode? node,
        int indent,
        int maxLabelWidth,
        int consoleWidth
    )
    {
        if (node is null)
            return;

        var indentStr = new string(' ', indent);
        var styledKey = Ansi.Header(key);
        var pad = maxLabelWidth - key.Length;
        var labelPart = $"{indentStr}{styledKey}: {new string(' ', pad)}";
        var valueStart = indent + maxLabelWidth + 2;

        if (node is JsonObject nestedObj)
        {
            output.WriteLine($"{indentStr}{styledKey}:");
            RenderJsonObject(output, nestedObj, indent + 4);
            return;
        }

        if (node is JsonArray arr)
        {
            RenderJsonArray(output, arr, indent, labelPart, valueStart, consoleWidth);
            return;
        }

        // JsonValue (scalar)
        var value = ExtractJsonValue(node);
        var fv = ValueFormatter.Format(value, fmtOpts);
        var formatted = ApplyAnsi(fv);
        var lines = DefinitionList.WordWrap(formatted, Math.Max(1, consoleWidth - valueStart));
        for (var i = 0; i < lines.Count; i++)
            output.WriteLine(i == 0 ? labelPart + lines[i] : new string(' ', valueStart) + lines[i]);
    }

    private void RenderJsonArray(
        TextWriter output,
        JsonArray arr,
        int indent,
        string labelPart,
        int valueStart,
        int consoleWidth
    )
    {
        if (arr.Count == 0)
        {
            output.WriteLine(labelPart + Ansi.Dim("[]"));
            return;
        }

        output.WriteLine(labelPart.TrimEnd());

        var itemIndent = indent + 4;
        var indexWidth = Math.Max(1, (arr.Count - 1).ToString().Length);

        for (var i = 0; i < arr.Count; i++)
        {
            var item = arr[i];
            var indexStr = Ansi.Dim($"- [{i.ToString().PadLeft(indexWidth)}]");
            var indexPrefix = new string(' ', itemIndent) + indexStr + "  ";
            // visible: itemIndent + "- [" (3) + indexWidth + "]" (1) + "  " (2)
            var indexPrefixVisibleLen = itemIndent + indexWidth + 6;

            if (item is null)
            {
                output.WriteLine(indexPrefix + Ansi.Dim("null"));
            }
            else if (item is JsonObject itemObj)
            {
                // Render object into a buffer with no indent, then inline with the index prefix
                using var buf = new StringWriter();
                RenderJsonObject(buf, itemObj, indent: 0);
                var objLines = buf.ToString()
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                var continuation = new string(' ', indexPrefixVisibleLen);
                for (var j = 0; j < objLines.Length; j++)
                    output.WriteLine(j == 0 ? indexPrefix + objLines[j] : continuation + objLines[j]);
            }
            else if (item is JsonArray nestedArr)
            {
                RenderJsonArray(output, nestedArr, itemIndent, indexPrefix, indexPrefixVisibleLen, consoleWidth);
            }
            else
            {
                var value = ExtractJsonValue(item);
                var fv = ValueFormatter.Format(value, fmtOpts);
                output.WriteLine(indexPrefix + ApplyAnsi(fv));
            }
        }
    }

    private static object? ExtractJsonValue(JsonNode node) =>
        node switch
        {
            JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
            JsonValue jv when jv.TryGetValue<long>(out var l) => l,
            JsonValue jv when jv.TryGetValue<double>(out var d) => d,
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            JsonValue jv when jv.TryGetValue<int>(out var n) => (long)n,
            _ => node.ToJsonString(),
        };

    // ── Reflection-based rendering ────────────────────────────────────────

    private bool TryFormatField(
        Type dataType,
        PropertyInfo prop,
        object dataValue,
        out string? label,
        out string? formattedValue
    )
    {
        label = null;
        formattedValue = null;

        if (showAll)
        {
            label = Ansi.Header(prop.Name);
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
            label = Ansi.Header(prop.Name);
            formattedValue = ApplyAnsi(ValueFormatter.Format(v, fmtOpts));
            return true;
        }

        // registryResult == true: always show
        {
            var v = prop.GetValue(dataValue);
            label = Ansi.Header(prop.Name);
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
