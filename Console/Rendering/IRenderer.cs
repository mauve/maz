namespace Console.Rendering;

public interface IRenderer
{
    Task RenderAsync(TextWriter output, object data, CancellationToken cancellationToken);
}
