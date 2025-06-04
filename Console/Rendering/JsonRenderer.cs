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
