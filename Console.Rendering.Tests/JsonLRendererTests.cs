using Console.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Rendering.Tests;

[TestClass]
public class JsonLRendererTests
{
    private record Item(string Name, int Value);

    [TestMethod]
    public async Task RenderAllAsync_EmitsOneJsonLinePerItem()
    {
        IRendererFactory factory = new JsonLRendererFactory();
        var renderer = factory.CreateCollectionRenderer<Item>();

        var items = new[] { new Item("a", 1), new Item("b", 2) }.ToAsyncEnumerableObjects();
        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(2, lines.Length, "Should emit exactly one line per item");
    }

    [TestMethod]
    public async Task RenderAllAsync_EachLineIsValidJson()
    {
        IRendererFactory factory = new JsonLRendererFactory();
        var renderer = factory.CreateCollectionRenderer<Item>();

        var items = new[] { new Item("hello", 42) }.ToAsyncEnumerableObjects();
        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        var line = writer.ToString().Trim();
        StringAssert.Contains(line, "\"Name\"");
        StringAssert.Contains(line, "\"hello\"");
        StringAssert.Contains(line, "42");
        // Verify it's valid JSON (no exception)
        var parsed = System.Text.Json.JsonSerializer.Deserialize<Item>(line);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("hello", parsed!.Name);
        Assert.AreEqual(42, parsed.Value);
    }

    [TestMethod]
    public async Task RenderAllAsync_EmptyCollection_ProducesNoLines()
    {
        IRendererFactory factory = new JsonLRendererFactory();
        var renderer = factory.CreateCollectionRenderer<Item>();

        var items = Array.Empty<Item>().ToAsyncEnumerableObjects();
        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.AreEqual(0, lines.Length, "Empty collection should produce no JSON lines");
    }

    [TestMethod]
    public async Task SingleItemRenderer_SerializesDirectly()
    {
        var factory = new JsonLRendererFactory();
        var renderer = ((IRendererFactory)factory).CreateRendererForType<Item>();

        var item = new Item("test", 7);
        using var writer = new StringWriter();
        await renderer.RenderAsync(writer, item, CancellationToken.None);

        var output = writer.ToString().Trim();
        StringAssert.Contains(output, "\"test\"");
        StringAssert.Contains(output, "7");
    }
}
