using Console.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests;

[TestClass]
public class CliCompletionHandlerTests
{
    // Tiny known tree for all handler tests
    private static readonly CompletionNode TestRoot = new(
        "maz",
        ["--interactive", "--no-interactive", "--verbose"],
        [
            new("account", ["--output"], [
                new("list", ["--all", "--output"], []),
                new("show", ["--id", "--output"], []),
            ]),
            new("storage", ["--output"], [
                new("blob", ["--container", "--output"], [
                    new("list", ["--prefix", "--output"], []),
                    new("upload", ["--file", "--output"], []),
                ]),
            ]),
        ]
    );

    private sealed class MockSubscriptionProvider : ICliCompletionProvider
    {
        public ValueTask<IEnumerable<string>> GetCompletionsAsync(CliCompletionContext ctx)
            => ValueTask.FromResult<IEnumerable<string>>(["sub-aaa", "sub-bbb"]);
    }

    private static readonly IReadOnlyDictionary<string, ICliCompletionProvider> TestDynamicProviders =
        new Dictionary<string, ICliCompletionProvider>
        {
            ["--subscription-id"] = new MockSubscriptionProvider(),
        };

    private static async Task<string[]> Complete(string line)
    {
        var writer = new StringWriter();
        await CliCompletionHandler.HandleAsync(line, line.Length, TestRoot, TestDynamicProviders, writer);
        return writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    [TestMethod]
    public async Task RootTab_ReturnsAllTopLevelCommands()
    {
        var results = await Complete("maz ");
        CollectionAssert.Contains(results, "account");
        CollectionAssert.Contains(results, "storage");
    }

    [TestMethod]
    public async Task ServiceTab_ReturnsServiceSubcommands()
    {
        var results = await Complete("maz storage ");
        CollectionAssert.Contains(results, "blob");
        Assert.IsFalse(results.Contains("account"), "Sibling commands should not appear");
    }

    [TestMethod]
    public async Task DeepTab_ReturnsLeafOperations()
    {
        var results = await Complete("maz storage blob ");
        CollectionAssert.Contains(results, "list");
        CollectionAssert.Contains(results, "upload");
    }

    [TestMethod]
    public async Task GlobalOptionTab_ReturnsGlobalOptions()
    {
        var results = await Complete("maz -");
        CollectionAssert.Contains(results, "--interactive");
        CollectionAssert.Contains(results, "--verbose");
    }

    [TestMethod]
    public async Task DynamicProviderOption_CallsMockProvider()
    {
        var results = await Complete("maz storage blob list --subscription-id ");
        CollectionAssert.Contains(results, "sub-aaa");
        CollectionAssert.Contains(results, "sub-bbb");
    }

    [TestMethod]
    public async Task PartialCommandName_FiltersResults()
    {
        var results = await Complete("maz st");
        CollectionAssert.Contains(results, "storage");
        Assert.IsFalse(results.Contains("account"), "Non-matching commands should be filtered out");
    }

    [TestMethod]
    public async Task PartialOptionName_FiltersResults()
    {
        var results = await Complete("maz --int");
        CollectionAssert.Contains(results, "--interactive");
        Assert.IsFalse(results.Contains("--verbose"), "Non-matching options should be filtered out");
    }

    [TestMethod]
    public async Task UnknownOptionPrecedingToken_ProducesNoOutput()
    {
        // "--output" has no dynamic provider → nothing printed
        var results = await Complete("maz account list --output ");
        Assert.AreEqual(0, results.Length, "No completions when preceding token has no dynamic provider");
    }

    [TestMethod]
    public async Task OptionTabAfterSubcommand_ReturnsSubcommandOptions()
    {
        var results = await Complete("maz account list -");
        CollectionAssert.Contains(results, "--all");
        CollectionAssert.Contains(results, "--output");
    }

    [TestMethod]
    public async Task EmptyWordAfterRoot_ReturnsAllChildren()
    {
        // Trailing space after "maz" → complete subcommands
        var results = await Complete("maz ");
        Assert.IsTrue(results.Length >= 2, "Should have at least 2 top-level commands");
    }
}
