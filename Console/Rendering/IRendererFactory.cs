namespace Console.Rendering;

public interface IRendererFactory
{
    public IRenderer CreateRendererForType(Type type);

    public IRenderer CreateRendererForType<T>() => CreateRendererForType(typeof(T));

    public ICollectionRenderer CreateCollectionRenderer<T>() =>
        new StreamingCollectionRenderer<T>(this);
}

/// <summary>
/// Default collection renderer: streams items one-by-one through the per-item renderer,
/// with a throbber on stderr.
/// </summary>
internal class StreamingCollectionRenderer<T>(IRendererFactory factory) : ICollectionRenderer
{
    public async Task RenderAllAsync(
        TextWriter output,
        IAsyncEnumerable<object> items,
        CancellationToken ct
    )
    {
        using var throbber = new Throbber("Fetching…");
        var renderer = factory.CreateRendererForType<T>();
        bool any = false;

        await foreach (var item in items.WithCancellation(ct))
        {
            if (!any)
            {
                throbber.Dispose();
                any = true;
            }
            await renderer.RenderAsync(output, item, ct);
        }

        if (!any)
        {
            throbber.Dispose();
            output.WriteLine("(no results)");
        }
    }
}
