using System.CommandLine;
using System.Text;
using Console.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CliGenerator.Tests;

[TestClass]
public class CommandSuggesterTests
{
    private static RootCommand BuildRoot()
    {
        var root = new RootCommand();
        root.Add(new Command("group", "Manage resource groups"));
        root.Add(new Command("monitor", "Monitoring commands"));
        root.Add(new Command("account", "Manage accounts"));
        return root;
    }

    private static ParseResult Parse(RootCommand root, params string[] args) =>
        root.Parse(args);

    [TestMethod]
    public void GetUnknownToken_UnmatchedCommand_ReturnsToken()
    {
        var root = BuildRoot();
        var result = Parse(root, "grp");

        var token = CommandSuggester.GetUnknownToken(result);

        Assert.AreEqual("grp", token);
    }

    [TestMethod]
    public void GetUnknownToken_OnlyOptions_ReturnsNull()
    {
        var root = BuildRoot();
        var result = Parse(root, "--unknown-option");

        // --unknown-option starts with '-' so it should not be returned
        var token = CommandSuggester.GetUnknownToken(result);

        Assert.IsNull(token);
    }

    [TestMethod]
    public void TrySuggest_SingleMatch_NonInteractive_PrintsSuggestion()
    {
        var root = BuildRoot();
        var result = Parse(root, "grouop"); // edit distance 2 from "group"
        var stderr = new StringBuilder();

        var exitCode = CommandSuggester.TrySuggest(
            result,
            ["grouop"],
            interactive: false,
            new StringWriter(stderr),
            () => null
        );

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(stderr.ToString().Contains("group"), $"Expected 'group' in: {stderr}");
    }

    [TestMethod]
    public void TrySuggest_SingleMatch_Interactive_UserConfirms_Reinvokes()
    {
        var root = BuildRoot();

        // Add a handler so invocation returns 0
        var matched = root.Subcommands.First(c => c.Name == "group");
        matched.SetAction(_ => 42);

        var result = Parse(root, "grouop");
        var stderr = new StringBuilder();

        var exitCode = CommandSuggester.TrySuggest(
            result,
            ["grouop"],
            interactive: true,
            new StringWriter(stderr),
            () => "y"
        );

        // The re-invoked command returns 42
        Assert.AreEqual(42, exitCode);
    }

    [TestMethod]
    public void TrySuggest_SingleMatch_Interactive_UserDeclines_Returns1()
    {
        var root = BuildRoot();
        var result = Parse(root, "grouop");
        var stderr = new StringBuilder();

        var exitCode = CommandSuggester.TrySuggest(
            result,
            ["grouop"],
            interactive: true,
            new StringWriter(stderr),
            () => "n"
        );

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public void TrySuggest_MultipleMatches_Interactive_UserPicksNumber()
    {
        var root = new RootCommand();
        // Both "groupA" and "groupB" will match "group" (substring)
        var cmdA = new Command("groupa", "Group A");
        var cmdB = new Command("groupb", "Group B");
        cmdA.SetAction(_ => 10);
        cmdB.SetAction(_ => 20);
        root.Add(cmdA);
        root.Add(cmdB);

        var result = Parse(root, "group");
        var stderr = new StringBuilder();

        var exitCode = CommandSuggester.TrySuggest(
            result,
            ["group"],
            interactive: true,
            new StringWriter(stderr),
            () => "1"  // pick first match
        );

        // Should have re-invoked one of the commands
        Assert.IsTrue(exitCode == 10 || exitCode == 20, $"Unexpected exit code: {exitCode}");
    }

    [TestMethod]
    public void TrySuggest_NoMatches_ReturnsMinusOne()
    {
        var root = BuildRoot();
        var result = Parse(root, "zzzzz");
        var stderr = new StringBuilder();

        var exitCode = CommandSuggester.TrySuggest(
            result,
            ["zzzzz"],
            interactive: false,
            new StringWriter(stderr),
            () => null
        );

        Assert.AreEqual(-1, exitCode);
    }
}
