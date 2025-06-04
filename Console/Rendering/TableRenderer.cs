using Azure.ResourceManager;
using Azure.ResourceManager.Models;

namespace Console.Rendering;

public class TableRendererFactory : IRendererFactory
{
    public IRenderer CreateRendererForType(Type type)
    {
        if (type.BaseType == typeof(ArmResource))
        {
            return new TableArmResourceRenderer();
        }
        else
        {
            throw new NotSupportedException(
                $"No table renderer available for type {type.FullName}"
            );
        }
    }
}

internal class TableArmResourceRenderer : IRenderer
{
    public Task RenderAsync(TextWriter output, object data, CancellationToken cancellationToken)
    {
        if (data is ArmResource resource)
        {
            RenderArmResource(output, resource);
            return Task.CompletedTask;
        }
        else
        {
            throw new ArgumentException(
                $"No table renderer available for type {data.GetType().FullName}"
            );
        }
    }

    private static void RenderArmResource(TextWriter writer, object resource)
    {
        var type = resource.GetType();
        var propertyInfo =
            type.GetProperty("Data")
            ?? throw new InvalidOperationException(
                $"Resource type {type.FullName} does not have an Data property."
            );
        var dataValue = propertyInfo.GetValue(resource);

        List<KeyValuePair<string, string>> fields = [];
        if (dataValue is TrackedResourceData trackedData)
        {
            // Render the tracked resource data as a table
            fields.AddRange(
                [
                    new("Resource Type", trackedData.ResourceType.ToString()),
                    new("Location", trackedData.Location.ToString()),
                    new("Id", trackedData.Id.ToString()),
                    new("Name", trackedData.Name),
                ]
            );
        }
        else if (dataValue is null)
        {
            return;
        }
        else
        {
            throw new NotSupportedException(
                $"No table renderer available for data type {dataValue.GetType().FullName}"
            );
        }

        PrintTable(writer, fields);
    }

    private static void PrintTable(TextWriter writer, List<KeyValuePair<string, string>> fields)
    {
        var maxKeyLength = fields.Max(f => f.Key.Length);

        KeyValuePair<string, string> header;
        if (fields.Any(f => f.Key == "Resource Type"))
        {
            header = fields.FirstOrDefault(f => f.Key == "Resource Type");
        }
        else
        {
            header = fields.First();
        }

        var headerPadding = new string(' ', maxKeyLength - header.Key.Length + 4);
        writer.WriteLine($"{header.Key}:{headerPadding}{header.Value}");

        foreach (var field in fields.Where(f => f.Key != header.Key))
        {
            var padding = new string(' ', maxKeyLength - field.Key.Length + 4);
            writer.WriteLine($"  {field.Key}:{padding}{field.Value}");
        }
        writer.Flush();
    }
}
