using System.Text;
using Console.Cli;
using Console.Cli.Parsing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CliGenerator.Tests;

[TestClass]
public class CommandSuggesterTests
{
    private static TestCommandDef BuildRoot()
    {
        return new TestCommandDef(
            "maz",
            [
                new TestCommandDef("group"),
                new TestCommandDef("monitor"),
                new TestCommandDef("account"),
            ]
        );
    }

    private static CliParseResult Parse(TestCommandDef root, params string[] args) =>
        CliParser.Parse(args, root);

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
            () => null,
            root
        );

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(stderr.ToString().Contains("group"), $"Expected 'group' in: {stderr}");
    }

    [TestMethod]
    public void TrySuggest_SingleMatch_Interactive_UserConfirms_Reinvokes()
    {
        var root = new TestCommandDef(
            "maz",
            [
                new TestCommandDef("group", handler: _ => Task.FromResult(42)),
                new TestCommandDef("monitor"),
                new TestCommandDef("account"),
            ]
        );

        var result = Parse(root, "grouop");
        var stderr = new StringBuilder();

        var exitCode = CommandSuggester.TrySuggest(
            result,
            ["grouop"],
            interactive: true,
            new StringWriter(stderr),
            () => "y",
            root
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
            () => "n",
            root
        );

        Assert.AreEqual(1, exitCode);
    }

    [TestMethod]
    public void TrySuggest_MultipleMatches_Interactive_UserPicksNumber()
    {
        var root = new TestCommandDef(
            "maz",
            [
                new TestCommandDef("groupa", handler: _ => Task.FromResult(10)),
                new TestCommandDef("groupb", handler: _ => Task.FromResult(20)),
            ]
        );

        var result = Parse(root, "group");
        var stderr = new StringBuilder();

        var exitCode = CommandSuggester.TrySuggest(
            result,
            ["group"],
            interactive: true,
            new StringWriter(stderr),
            () => "1", // pick first match
            root
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
            () => null,
            root
        );

        Assert.AreEqual(-1, exitCode);
    }
}
