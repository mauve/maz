using Console.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Rendering.Tests;

[TestClass]
public class ColumnRendererTests
{
    private record Item(string Name, int Count);

    [TestMethod]
    public async Task RenderAllAsync_WithItems_ContainsHeaders()
    {
        IRendererFactory factory = new ColumnRendererFactory(new ValueFormatterOptions());
        var renderer = factory.CreateCollectionRenderer<Item>();

        var items = new[] { new Item("alpha", 1), new Item("beta", 2) }
            .ToAsyncEnumerableObjects();

        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "NAME");
        StringAssert.Contains(output, "COUNT");
    }

    [TestMethod]
    public async Task RenderAllAsync_WithItems_ContainsValues()
    {
        IRendererFactory factory = new ColumnRendererFactory(new ValueFormatterOptions());
        var renderer = factory.CreateCollectionRenderer<Item>();

        var items = new[] { new Item("alpha", 42) }.ToAsyncEnumerableObjects();

        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "alpha");
        StringAssert.Contains(output, "42");
    }

    [TestMethod]
    public async Task RenderAllAsync_EmptyCollection_OutputsNoResults()
    {
        IRendererFactory factory = new ColumnRendererFactory(new ValueFormatterOptions());
        var renderer = factory.CreateCollectionRenderer<Item>();

        var items = Array.Empty<Item>().ToAsyncEnumerableObjects();

        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "(no results)");
    }
}
