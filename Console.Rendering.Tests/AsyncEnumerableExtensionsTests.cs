using Console.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Rendering.Tests;

[TestClass]
public class AsyncEnumerableExtensionsTests
{
    private static readonly string?[] WithNulls = ["a", null, "b", null, "c"];
    private static readonly string[] WithNullsExpected = ["a", "b", "c"];
    private static readonly string?[] WithNullsXY = ["x", null, "y"];
    private static readonly string[] WithNullsXYExpected = ["x", "y"];

    [TestMethod]
    public async Task ToAsyncObjects_FiltersNulls_YieldsNonNull()
    {
        var source = ToAsync(WithNulls);
        var results = new List<object>();
        await foreach (var item in source.ToAsyncObjects())
            results.Add(item);

        CollectionAssert.AreEqual(WithNullsExpected, results);
    }

    [TestMethod]
    public async Task ToAsyncObjects_EmptySource_YieldsNothing()
    {
        var source = ToAsync(Array.Empty<string>());
        var results = new List<object>();
        await foreach (var item in source.ToAsyncObjects())
            results.Add(item);

        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task ToAsyncEnumerableObjects_FiltersNulls_YieldsNonNull()
    {
        var results = new List<object>();
        await foreach (var item in WithNullsXY.ToAsyncEnumerableObjects())
            results.Add(item);

        CollectionAssert.AreEqual(WithNullsXYExpected, results);
    }

    [TestMethod]
    public async Task ToAsyncEnumerableObjects_EmptySource_YieldsNothing()
    {
        var source = Array.Empty<string>();
        var results = new List<object>();
        await foreach (var item in source.ToAsyncEnumerableObjects())
            results.Add(item);

        Assert.AreEqual(0, results.Count);
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
