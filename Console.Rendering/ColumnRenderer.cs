using System.Reflection;
using System.Text.Json.Nodes;
using Azure.ResourceManager;

namespace Console.Rendering;

public record ColumnRendererOptions(ValueFormatterOptions? FormatterOptions = null);

public class ColumnRendererFactory(
    ValueFormatterOptions formatterOptions,
    bool showEnvelope = false
) : IRendererFactory
{
    public IRenderer CreateRendererForType(Type type) =>
        new TextItemRenderer(type, showAll: false, showEnvelope: false, formatterOptions);

    ICollectionRenderer IRendererFactory.CreateCollectionRenderer<T>()
    {
        if (showEnvelope && !typeof(ArmResource).IsAssignableFrom(typeof(T)))
        {
            System.Console.Error.WriteLine(
                Ansi.Yellow(
                    "warning: --show-envelope is not supported with column format for non-ArmResource types"
                )
            );
        }
        return new ColumnRenderer<T>(new ColumnRendererOptions(formatterOptions));
    }
}

internal class ColumnRenderer<T>(ColumnRendererOptions options) : ICollectionRenderer
{
    private static readonly Type[] SkipTypes =
    [
        typeof(object),
        typeof(string), // handled as scalar, not nested
    ];

    public async Task RenderAllAsync(
        TextWriter output,
        IAsyncEnumerable<object> items,
        CancellationToken ct
    )
    {
        using var throbber = new Throbber("Fetching…");

        var collected = new List<T>();
        await foreach (var item in items.WithCancellation(ct))
        {
            if (item is T typed)
                collected.Add(typed);
        }

        throbber.Dispose();

        if (collected.Count == 0)
        {
            output.WriteLine("(no results)");
            return;
        }

        var formatterOptions = options.FormatterOptions ?? new ValueFormatterOptions();
        var columns = DiscoverColumns(typeof(T), collected[0]);
        if (columns.Count == 0)
        {
            output.WriteLine("(no results)");
            return;
        }

        // Compute natural widths
        var headers = columns.Select(c => c.DisplayName).ToList();
        var rows = collected.Select(item => GetRow(item, columns, formatterOptions)).ToList();
        var naturalWidths = columns
            .Select(
                (c, i) =>
                    Math.Max(
                        headers[i].Length,
                        rows.Select(r => r[i].Text.Length).DefaultIfEmpty(0).Max()
                    )
            )
            .ToArray();

        // Fit to console
        var consoleWidth = GetConsoleWidth();
        var widths = FitWidths(naturalWidths, consoleWidth);

        // Render header
        RenderRow(
            output,
            headers.Select((h, i) => (Ansi.Bold(h.PadRight(widths[i])), widths[i])).ToList(),
            isHeader: true
        );
        RenderSeparator(output, widths);

        // Render data rows
        foreach (var row in rows)
        {
            var cells = row.Select(
                    (cell, i) =>
                    {
                        var truncated = ValueFormatter.Truncate(cell.Text, widths[i]);
                        var padded = cell.Alignment switch
                        {
                            TextAlignment.Right => truncated.PadLeft(widths[i]),
                            TextAlignment.Center => truncated
                                .PadLeft((widths[i] + truncated.Length) / 2)
                                .PadRight(widths[i]),
                            _ => truncated.PadRight(widths[i]),
                        };
                        var colored =
                            cell.AnsiCode != null ? Ansi.Color(padded, cell.AnsiCode) : padded;
                        return (colored, widths[i]);
                    }
                )
                .ToList();
            RenderRow(output, cells, isHeader: false);
        }

        output.Flush();
    }

    private static void RenderRow(
        TextWriter output,
        List<(string Text, int Width)> cells,
        bool isHeader
    )
    {
        output.WriteLine(string.Join("  ", cells.Select(c => c.Text)));
    }

    private static void RenderSeparator(TextWriter output, int[] widths)
    {
        output.WriteLine(string.Join("  ", widths.Select(w => new string('─', w))));
    }

    private static int GetConsoleWidth()
    {
        try
        {
            return System.Console.WindowWidth;
        }
        catch
        {
            return 120;
        }
    }

    private static int[] FitWidths(int[] natural, int available)
    {
        var widths = natural.ToArray();
        const int minWidth = 8;
        const int separator = 2;
        var n = widths.Length;

        int TotalWidth() => widths.Sum() + separator * (n - 1);

        // Shrink widest columns iteratively
        while (TotalWidth() > available)
        {
            var maxWidth = widths.Max();
            if (maxWidth <= minWidth)
                break;
            var idx = Array.IndexOf(widths, maxWidth);
            widths[idx] = Math.Max(minWidth, maxWidth - 1);
        }

        // Give last column remaining space (but don't expand beyond natural)
        var remaining = available - (widths[..^1].Sum() + separator * (n - 1));
        if (remaining > 0)
            widths[^1] = Math.Min(natural[^1], remaining);

        return widths;
    }

    private record ColumnDef(string PropertyPath, string DisplayName, Func<object, object?> Getter);

    private static List<ColumnDef> DiscoverColumns(Type type) =>
        DiscoverColumns(type, sample: null);

    /// <summary>
    /// Discovers columns either via reflection (for strongly-typed objects and ArmResources)
    /// or by inspecting a sample <see cref="JsonObject"/> for scalar top-level keys.
    /// </summary>
    private static List<ColumnDef> DiscoverColumns(Type type, object? sample)
    {
        // JsonNode / JsonObject: discover columns from the JSON keys
        if (typeof(JsonNode).IsAssignableFrom(type) && sample is JsonObject sampleObj)
            return DiscoverJsonColumns(sampleObj);

        // For ArmResource types, use the .Data property
        var dataType = type;
        Func<object, object?> dataGetter = o => o;

        if (typeof(ArmResource).IsAssignableFrom(type))
        {
            var dataProp = type.GetProperty("Data");
            if (dataProp != null)
            {
                dataType = dataProp.PropertyType;
                dataGetter = o => dataProp.GetValue(o);
            }
        }

        var columns = new List<ColumnDef>();
        foreach (var prop in dataType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!IsFormattableType(prop.PropertyType))
                continue;

            var getter = dataGetter;
            var p = prop;
            columns.Add(
                new ColumnDef(
                    p.Name,
                    ToDisplayName(p.Name),
                    o =>
                    {
                        var data = getter(o);
                        return data == null ? null : p.GetValue(data);
                    }
                )
            );
        }
        return columns;
    }

    /// <summary>
    /// Discovers columns from a <see cref="JsonObject"/> sample by picking top-level
    /// scalar (string, number, boolean) properties.
    /// </summary>
    private static List<ColumnDef> DiscoverJsonColumns(JsonObject sample)
    {
        var columns = new List<ColumnDef>();
        foreach (var (key, node) in sample)
        {
            if (node is null or JsonObject or JsonArray)
                continue; // skip nulls, nested objects and arrays

            var k = key;
            columns.Add(new ColumnDef(k, ToDisplayName(k), o => (o as JsonObject)?[k]?.ToString()));
        }
        return columns;
    }

    private static bool IsFormattableType(Type t)
    {
        if (t == typeof(string))
            return true;
        if (t == typeof(bool) || t == typeof(bool?))
            return true;
        if (
            t == typeof(int)
            || t == typeof(long)
            || t == typeof(decimal)
            || t == typeof(double)
            || t == typeof(float)
            || t == typeof(short)
            || t == typeof(byte)
            || t == typeof(uint)
            || t == typeof(ulong)
            || t == typeof(int?)
            || t == typeof(long?)
            || t == typeof(decimal?)
            || t == typeof(double?)
            || t == typeof(float?)
            || t == typeof(short?)
            || t == typeof(byte?)
        )
            return true;
        if (
            t == typeof(DateTime)
            || t == typeof(DateTime?)
            || t == typeof(DateTimeOffset)
            || t == typeof(DateTimeOffset?)
        )
            return true;
        if (t == typeof(Guid) || t == typeof(Guid?))
            return true;
        if (t.IsEnum)
            return true;
        // IDictionary / IEnumerable (but not plain object or nested complex types)
        if (typeof(System.Collections.IDictionary).IsAssignableFrom(t))
            return true;
        if (t != typeof(object) && typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
            return true;
        // Check nullable underlying
        var underlying = Nullable.GetUnderlyingType(t);
        if (underlying != null)
            return IsFormattableType(underlying);
        return false;
    }

    private static string ToDisplayName(string propertyName)
    {
        // PascalCase → UPPER SPACE-SEPARATED
        // e.g. ResourceType → RESOURCE TYPE, DisplayName → DISPLAY NAME
        var result = System.Text.RegularExpressions.Regex.Replace(
            propertyName,
            @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
            " "
        );
        return result.ToUpperInvariant();
    }

    private static List<FormattedValue> GetRow(
        T item,
        List<ColumnDef> columns,
        ValueFormatterOptions opts
    )
    {
        return columns.Select(c => ValueFormatter.Format(c.Getter(item!), opts)).ToList();
    }
}
