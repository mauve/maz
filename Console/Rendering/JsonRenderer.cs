using System.Text.Json;
using Azure.ResourceManager;

namespace Console.Rendering;

public class JsonRendererFactory(JsonSerializerOptions options) : IRendererFactory
{
    public IRenderer CreateRendererForType(Type type)
    {
        if (type.BaseType == typeof(ArmResource))
        {
            return new JsonArmResourceRenderer(options);
        }
        else
        {
            throw new NotSupportedException($"No JSON renderer available for type {type.FullName}");
        }
    }

    ICollectionRenderer IRendererFactory.CreateCollectionRenderer<T>() =>
        new JsonCollectionRenderer<T>(options);
}

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

        // Extract .Data if ArmResource
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

        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(dataItems, options));
    }
}

internal class JsonArmResourceRenderer(JsonSerializerOptions options) : IRenderer
{
    public Task RenderAsync(TextWriter output, object data, CancellationToken cancellationToken)
    {
        if (data is ArmResource resource)
        {
            return RenderArmResource(output, resource);
        }
        else
        {
            throw new ArgumentException(
                $"No JSON renderer available for type {data.GetType().FullName}"
            );
        }
    }

    private Task RenderArmResource(TextWriter output, object resource)
    {
        var type = resource.GetType();
        var propertyInfo =
            type.GetProperty("Data")
            ?? throw new InvalidOperationException(
                $"Resource type {type.FullName} does not have an Data property."
            );
        var dataValue = propertyInfo.GetValue(resource);

        output.WriteLine(JsonSerializer.Serialize(dataValue, options));

        return Task.CompletedTask;
    }
}
