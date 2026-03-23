using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.ResourceManager;

namespace Console.Rendering;

// ── Factories ────────────────────────────────────────────────────────────────

public class JsonRendererFactory(JsonSerializerOptions options) : IRendererFactory
{
    public IRenderer CreateRendererForType(Type type) =>
        typeof(ArmResource).IsAssignableFrom(type)
            ? new JsonArmResourceRenderer(options)
            : new JsonDirectRenderer(options);

    ICollectionRenderer IRendererFactory.CreateCollectionRenderer<T>() =>
        new JsonCollectionRenderer<T>(options);
}

public class JsonLRendererFactory : IRendererFactory
{
    public IRenderer CreateRendererForType(Type type) =>
        typeof(ArmResource).IsAssignableFrom(type)
            ? new JsonArmResourceRenderer(JsonSerializerOptions.Default)
            : new JsonDirectRenderer(JsonSerializerOptions.Default);

    ICollectionRenderer IRendererFactory.CreateCollectionRenderer<T>() =>
        new JsonLCollectionRenderer<T>();
}

public class JsonPrettyRendererFactory : IRendererFactory
{
    private static readonly JsonSerializerOptions PrettyOptions = new() { WriteIndented = true };

    public IRenderer CreateRendererForType(Type type) =>
        typeof(ArmResource).IsAssignableFrom(type)
            ? new JsonPrettyArmResourceRenderer()
            : new JsonPrettyDirectRenderer();

    ICollectionRenderer IRendererFactory.CreateCollectionRenderer<T>() =>
        new JsonPrettyCollectionRenderer<T>();
}

// ── Per-item renderers ────────────────────────────────────────────────────────

internal class JsonDirectRenderer(JsonSerializerOptions options) : IRenderer
{
    public Task RenderAsync(TextWriter output, object data, CancellationToken ct)
    {
        output.WriteLine(JsonSerializeHelper.SerializeSafe(data, options));
        return Task.CompletedTask;
    }
}

internal class JsonArmResourceRenderer(JsonSerializerOptions options) : IRenderer
{
    public Task RenderAsync(TextWriter output, object data, CancellationToken cancellationToken)
    {
        if (data is not ArmResource)
            throw new ArgumentException($"Expected ArmResource, got {data.GetType().FullName}");

        var type = data.GetType();
        var propertyInfo =
            type.GetProperty("Data")
            ?? throw new InvalidOperationException(
                $"Resource type {type.FullName} does not have a Data property."
            );
        var dataValue = propertyInfo.GetValue(data);
        output.WriteLine(JsonSerializer.Serialize(dataValue, options));
        return Task.CompletedTask;
    }
}

internal class JsonPrettyDirectRenderer : IRenderer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public Task RenderAsync(TextWriter output, object data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data, Options);
        output.WriteLine(JsonSyntaxHighlighter.Colorize(json));
        return Task.CompletedTask;
    }
}

internal class JsonPrettyArmResourceRenderer : IRenderer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public Task RenderAsync(TextWriter output, object data, CancellationToken ct)
    {
        if (data is not ArmResource)
            throw new ArgumentException($"Expected ArmResource, got {data.GetType().FullName}");

        var type = data.GetType();
        var dataProp =
            type.GetProperty("Data")
            ?? throw new InvalidOperationException($"Type {type.FullName} has no Data property.");
        var dataValue = dataProp.GetValue(data);
        var json = JsonSerializeHelper.SerializeSafe(dataValue, Options);
        output.WriteLine(JsonSyntaxHighlighter.Colorize(json));
        return Task.CompletedTask;
    }
}

// ── Collection renderers ──────────────────────────────────────────────────────

internal class JsonCollectionRenderer<T>(JsonSerializerOptions options) : ICollectionRenderer
{
    public async Task RenderAllAsync(
        TextWriter output,
        IAsyncEnumerable<object> items,
        CancellationToken ct
    )
    {
        using var throbber = new Throbber("Fetching…");
        var all = new List<object>();
        await foreach (var item in items.WithCancellation(ct))
            all.Add(item);
        throbber.Dispose();

        var dataItems = all.Select(item =>
            {
                if (item is ArmResource)
                {
                    var dataProp = item.GetType().GetProperty("Data");
                    return dataProp?.GetValue(item);
                }
                return item;
            })
            .ToList();

        output.WriteLine(JsonSerializeHelper.SerializeSafeList(dataItems, options));
    }
}

internal class JsonLCollectionRenderer<T> : ICollectionRenderer
{
    public async Task RenderAllAsync(
        TextWriter output,
        IAsyncEnumerable<object> items,
        CancellationToken ct
    )
    {
        using var throbber = new Throbber("Fetching…");
        var all = new List<object>();
        await foreach (var item in items.WithCancellation(ct))
            all.Add(item);
        throbber.Dispose();

        foreach (var item in all)
        {
            object? dataItem =
                item is ArmResource ? item.GetType().GetProperty("Data")?.GetValue(item) : item;
            output.WriteLine(JsonSerializeHelper.SerializeSafe(dataItem, JsonSerializerOptions.Default));
        }
    }
}

internal class JsonPrettyCollectionRenderer<T> : ICollectionRenderer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task RenderAllAsync(
        TextWriter output,
        IAsyncEnumerable<object> items,
        CancellationToken ct
    )
    {
        using var throbber = new Throbber("Fetching…");
        var all = new List<object>();
        await foreach (var item in items.WithCancellation(ct))
            all.Add(item);
        throbber.Dispose();

        var dataItems = all.Select(item =>
            {
                if (item is ArmResource)
                {
                    var dataProp = item.GetType().GetProperty("Data");
                    return dataProp?.GetValue(item);
                }
                return item;
            })
            .ToList();

        var json = JsonSerializeHelper.SerializeSafeList(dataItems, Options);
        output.WriteLine(JsonSyntaxHighlighter.Colorize(json));
    }
}

// ── Trim-safe serialization helpers ───────────────────────────────────────────

/// <summary>
/// Serializes an object to JSON, handling <see cref="JsonNode"/> items without
/// reflection (which is disabled in trimmed/AOT builds).
/// </summary>
file static class JsonSerializeHelper
{
    public static string SerializeSafe(object? data, JsonSerializerOptions options)
    {
        if (data is JsonNode node)
            return options.WriteIndented
                ? node.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                : node.ToJsonString();
        return JsonSerializer.Serialize(data, data?.GetType() ?? typeof(object), options);
    }

    public static string SerializeSafeList(List<object?> items, JsonSerializerOptions options)
    {
        if (items.Count > 0 && items[0] is JsonNode)
        {
            var array = new JsonArray();
            foreach (var item in items)
            {
                if (item is JsonNode node)
                    array.Add(node.DeepClone());
            }
            return options.WriteIndented
                ? array.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
                : array.ToJsonString();
        }
        return JsonSerializer.Serialize(items, options);
    }
}

// ── Syntax highlighter ────────────────────────────────────────────────────────

public static class JsonSyntaxHighlighter
{
    public static string Colorize(string json)
    {
        if (!Ansi.IsEnabled)
            return json;

        var sb = new StringBuilder(json.Length * 2);
        int i = 0;

        while (i < json.Length)
        {
            char c = json[i];

            if (c == '"')
            {
                var (str, len) = ScanString(json, i);
                i += len;

                // Look ahead past whitespace for ':' to decide if this is a key
                int j = i;
                while (j < json.Length && json[j] is ' ' or '\t' or '\r' or '\n')
                    j++;
                bool isKey = j < json.Length && json[j] == ':';

                sb.Append(
                    isKey
                        ? Ansi.Color(str, "\x1b[36m") // cyan for keys
                        : Ansi.Color(str, "\x1b[32m") // green for string values
                );
            }
            else if (
                c == 't'
                && i + 3 < json.Length
                && json[i + 1] == 'r'
                && json[i + 2] == 'u'
                && json[i + 3] == 'e'
            )
            {
                sb.Append(Ansi.Color("true", "\x1b[32m"));
                i += 4;
            }
            else if (
                c == 'f'
                && i + 4 < json.Length
                && json[i + 1] == 'a'
                && json[i + 2] == 'l'
                && json[i + 3] == 's'
                && json[i + 4] == 'e'
            )
            {
                sb.Append(Ansi.Color("false", "\x1b[31m"));
                i += 5;
            }
            else if (
                c == 'n'
                && i + 3 < json.Length
                && json[i + 1] == 'u'
                && json[i + 2] == 'l'
                && json[i + 3] == 'l'
            )
            {
                sb.Append(Ansi.Color("null", "\x1b[2m"));
                i += 4;
            }
            else if (c == '-' || char.IsDigit(c))
            {
                var start = i;
                while (
                    i < json.Length
                    && (char.IsDigit(json[i]) || json[i] is '.' or '-' or '+' or 'e' or 'E')
                )
                    i++;
                sb.Append(Ansi.Color(json[start..i], "\x1b[33m"));
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }

    private static (string Text, int Length) ScanString(string json, int start)
    {
        var sb = new StringBuilder();
        sb.Append('"');
        int i = start + 1;
        while (i < json.Length)
        {
            char c = json[i];
            if (c == '\\' && i + 1 < json.Length)
            {
                sb.Append(c);
                sb.Append(json[i + 1]);
                i += 2;
            }
            else if (c == '"')
            {
                sb.Append('"');
                i++;
                break;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return (sb.ToString(), i - start);
    }
}
