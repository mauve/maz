using Console.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Rendering.Tests;

[TestClass]
public class TextRendererTests
{
    private record PlainItem(string Name, int Count, Uri? Website = null);

    private record AnnotatedItem(string Name, string? Description = null);

    // ── Heuristic: Uri fields are hidden by default ────────────────────────

    [TestMethod]
    public async Task Render_HidesUriFieldByHeuristic()
    {
        var factory = new TextRendererFactory(showAll: false, showEnvelope: false, new ValueFormatterOptions());
        var renderer = factory.CreateRendererForType(typeof(PlainItem));

        var item = new PlainItem("alpha", 42, new Uri("https://example.com"));
        using var writer = new StringWriter();
        await renderer.RenderAsync(writer, item, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "Name");
        StringAssert.Contains(output, "alpha");
        StringAssert.Contains(output, "Count");
        StringAssert.Contains(output, "42");
        Assert.IsFalse(output.Contains("Website"), "Website (Uri) should be hidden by heuristic");
    }

    // ── Heuristic: null values are hidden by default ───────────────────────

    [TestMethod]
    public async Task Render_HidesNullValuesByDefault()
    {
        var factory = new TextRendererFactory(showAll: false, showEnvelope: false, new ValueFormatterOptions());
        var renderer = factory.CreateRendererForType(typeof(AnnotatedItem));

        var item = new AnnotatedItem("alpha");
        using var writer = new StringWriter();
        await renderer.RenderAsync(writer, item, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "Name");
        Assert.IsFalse(output.Contains("Description"), "null Description should be hidden");
    }

    // ── --show-all: all fields, including null and heuristic-hidden ────────

    [TestMethod]
    public async Task Render_ShowAll_IncludesAllFields()
    {
        var factory = new TextRendererFactory(showAll: true, showEnvelope: false, new ValueFormatterOptions());
        var renderer = factory.CreateRendererForType(typeof(PlainItem));

        var item = new PlainItem("beta", 7, null);
        using var writer = new StringWriter();
        await renderer.RenderAsync(writer, item, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "Name");
        StringAssert.Contains(output, "Count");
        StringAssert.Contains(output, "Website");
    }

    [TestMethod]
    public async Task Render_ShowAll_IncludesNullValues()
    {
        var factory = new TextRendererFactory(showAll: true, showEnvelope: false, new ValueFormatterOptions());
        var renderer = factory.CreateRendererForType(typeof(AnnotatedItem));

        var item = new AnnotatedItem("gamma");
        using var writer = new StringWriter();
        await renderer.RenderAsync(writer, item, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "Description");
    }

    // ── TextFieldRegistry: visible-fields whitelist ────────────────────────

    private record MultiItem(string Name, int Count, string Tag);

    [TestMethod]
    public async Task Render_RegistryWhitelist_ShowsOnlyRegisteredFields()
    {
        TextFieldRegistry.RegisterVisibleFields<MultiItem>("Name");

        try
        {
            var factory = new TextRendererFactory(showAll: false, showEnvelope: false, new ValueFormatterOptions());
            var renderer = factory.CreateRendererForType(typeof(MultiItem));

            var item = new MultiItem("delta", 99, "urgent");
            using var writer = new StringWriter();
            await renderer.RenderAsync(writer, item, CancellationToken.None);

            var output = writer.ToString();
            StringAssert.Contains(output, "Name");
            StringAssert.Contains(output, "delta");
            Assert.IsFalse(output.Contains("Count"), "Count not in whitelist");
            Assert.IsFalse(output.Contains("Tag"), "Tag not in whitelist");
        }
        finally
        {
            // Clean up registry state for other tests
            TextFieldRegistry.RegisterVisibleFields<MultiItem>(); // reset to empty — hides all
        }
    }

    // ── TextFieldRegistry: hidden-fields blacklist ─────────────────────────

    private record TaggedItem(string Name, string Tag, int Priority);

    [TestMethod]
    public async Task Render_RegistryHidden_ExcludesRegisteredField()
    {
        TextFieldRegistry.RegisterHiddenFields<TaggedItem>("Tag");

        try
        {
            var factory = new TextRendererFactory(showAll: false, showEnvelope: false, new ValueFormatterOptions());
            var renderer = factory.CreateRendererForType(typeof(TaggedItem));

            var item = new TaggedItem("epsilon", "secret", 1);
            using var writer = new StringWriter();
            await renderer.RenderAsync(writer, item, CancellationToken.None);

            var output = writer.ToString();
            StringAssert.Contains(output, "Name");
            StringAssert.Contains(output, "Priority");
            Assert.IsFalse(output.Contains("Tag"), "Tag is explicitly hidden");
        }
        finally
        {
            TextFieldRegistry.RegisterHiddenFields<TaggedItem>(); // clear (no-op fields)
        }
    }

    // ── --show-envelope warning for non-ArmResource ───────────────────────

    [TestMethod]
    public async Task Render_ShowEnvelope_NonArmResource_WritesWarningToStderr()
    {
        var factory = new TextRendererFactory(showAll: false, showEnvelope: true, new ValueFormatterOptions());
        var renderer = factory.CreateRendererForType(typeof(PlainItem));

        var originalError = System.Console.Error;
        var errorCapture = new StringWriter();
        System.Console.SetError(errorCapture);

        try
        {
            var item = new PlainItem("zeta", 1);
            using var writer = new StringWriter();
            await renderer.RenderAsync(writer, item, CancellationToken.None);

            var errOutput = errorCapture.ToString();
            StringAssert.Contains(errOutput, "warning");
            StringAssert.Contains(errOutput, "--show-envelope");
        }
        finally
        {
            System.Console.SetError(originalError);
        }
    }

    // ── TextRendererFactory collection rendering ───────────────────────────

    [TestMethod]
    public async Task RenderCollection_OutputsEntryPerItem()
    {
        var factory = new TextRendererFactory(showAll: false, showEnvelope: false, new ValueFormatterOptions());
        var renderer = ((IRendererFactory)factory).CreateCollectionRenderer<PlainItem>();

        var items = new[] { new PlainItem("a", 1), new PlainItem("b", 2) }.ToAsyncEnumerableObjects();
        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        var output = writer.ToString();
        StringAssert.Contains(output, "a");
        StringAssert.Contains(output, "b");
    }

    [TestMethod]
    public async Task RenderCollection_EmptyCollection_OutputsNoResults()
    {
        var factory = new TextRendererFactory(showAll: false, showEnvelope: false, new ValueFormatterOptions());
        var renderer = ((IRendererFactory)factory).CreateCollectionRenderer<PlainItem>();

        var items = Array.Empty<PlainItem>().ToAsyncEnumerableObjects();
        using var writer = new StringWriter();
        await renderer.RenderAllAsync(writer, items, CancellationToken.None);

        StringAssert.Contains(writer.ToString(), "(no results)");
    }
}
