namespace Console.Rendering;

public static class AsyncEnumerableExtensions
{
    /// <summary>Converts IAsyncEnumerable&lt;T&gt; to IAsyncEnumerable&lt;object&gt;.</summary>
    public static async IAsyncEnumerable<object> ToAsyncObjects<T>(
        this IAsyncEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            System.Threading.CancellationToken ct = default
    )
    {
        await foreach (var item in source.WithCancellation(ct))
        {
            if (item is not null)
                yield return item;
        }
    }

    /// <summary>Converts IEnumerable&lt;T&gt; to IAsyncEnumerable&lt;object&gt;.</summary>
    public static async IAsyncEnumerable<object> ToAsyncEnumerableObjects<T>(
        this IEnumerable<T> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
            System.Threading.CancellationToken ct = default
    )
    {
        foreach (var item in source)
        {
            ct.ThrowIfCancellationRequested();
            if (item is not null)
                yield return item;
            await Task.Yield();
        }
    }
}
