namespace Console.Rendering;

public interface IRendererFactory
{
    public IRenderer CreateRendererForType(Type type);

    public IRenderer CreateRendererForType<T>() => CreateRendererForType(typeof(T));
}
