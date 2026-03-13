using System.CommandLine.Completions;

namespace Console.Cli;

public interface ICliCompletionProvider
{
    ValueTask<IEnumerable<string>> GetCompletionsAsync(CompletionContext context);
}

internal static class CliCompletionProviderBridge
{
    internal static IEnumerable<CompletionItem> GetCompletions<TProvider>(CompletionContext context)
        where TProvider : ICliCompletionProvider, new()
    {
        var values = new TProvider().GetCompletionsAsync(context).AsTask().GetAwaiter().GetResult();
        foreach (var value in values)
            yield return new CompletionItem(value);
    }
}
