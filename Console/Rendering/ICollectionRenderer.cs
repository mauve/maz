namespace Console.Rendering;

public interface ICollectionRenderer
{
    Task RenderAllAsync(TextWriter output, IAsyncEnumerable<object> items, CancellationToken ct);
}
