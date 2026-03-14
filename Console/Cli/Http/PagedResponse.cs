using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Console.Cli.Http;

/// <summary>Extension methods for paginated Azure REST API responses.</summary>
public static class PagedResponse
{
    /// <summary>
    /// Fetches all pages of a list operation and yields each item individually.
    /// Follows <c>nextLink</c> URLs across pages.
    /// </summary>
    public static async IAsyncEnumerable<object> GetAllAsync(
        this AzureRestClient client,
        string path,
        string apiVersion,
        string itemsProperty,
        string nextLinkProperty,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        string? currentUrl = path;

        while (currentUrl is not null)
        {
            ct.ThrowIfCancellationRequested();

            var page = await client.SendAsync(HttpMethod.Get, currentUrl, apiVersion, null, ct);

            var items = page[itemsProperty]?.AsArray();
            if (items is not null)
            {
                foreach (var item in items)
                {
                    if (item is not null)
                        yield return item;
                }
            }

            currentUrl = page[nextLinkProperty]?.GetValue<string>();
        }
    }
}
