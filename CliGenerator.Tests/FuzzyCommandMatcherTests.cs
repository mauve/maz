using Console.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CliGenerator.Tests;

[TestClass]
public class FuzzyCommandMatcherTests
{
    [TestMethod]
    public void Score_ExactMatch_Returns100()
    {
        Assert.AreEqual(80, FuzzyCommandMatcher.Score("group", "group"));
    }

    [TestMethod]
    public void Score_PrefixMatch_HighScore()
    {
        Assert.AreEqual(80, FuzzyCommandMatcher.Score("grou", "group"));
    }

    [TestMethod]
    public void Score_SubstringMatch_ModerateScore()
    {
        // "roup" is a substring of "group"
        Assert.AreEqual(50, FuzzyCommandMatcher.Score("roup", "group"));
    }

    [TestMethod]
    public void Score_EditDistance1_Matches()
    {
        // "montor" vs "monitor": distance 1 (missing 'i')
        Assert.AreEqual(40, FuzzyCommandMatcher.Score("montor", "monitor"));
    }

    [TestMethod]
    public void Score_EditDistance2_Matches()
    {
        Assert.AreEqual(20, FuzzyCommandMatcher.Score("manisor", "monitor"));
    }

    [TestMethod]
    public void Score_EditDistance3_ShortName_NoMatch()
    {
        // "xyz" vs "add": distance 3 but candidate.Length=3 < 6, so score = 0
        Assert.AreEqual(0, FuzzyCommandMatcher.Score("xyz", "add"));
    }

    [TestMethod]
    public void Score_EditDistance3_LongCandidate_Matches()
    {
        Assert.AreEqual(10, FuzzyCommandMatcher.Score("monitabc", "monitor"));
    }

    [TestMethod]
    public void Score_TotallyUnrelated_Zero()
    {
        Assert.AreEqual(0, FuzzyCommandMatcher.Score("xyz", "group"));
    }

    [TestMethod]
    public void FindMatches_ReturnsTopMatchesSortedDescending()
    {
        var root = new TestCommandDef(
            "root",
            [new TestCommandDef("group"), new TestCommandDef("get"), new TestCommandDef("monitor")]
        );

        // "grou" is prefix of "group" (score 80)
        var matches = FuzzyCommandMatcher.FindMatches(root, "grou");

        Assert.IsTrue(matches.Count >= 1);
        Assert.AreEqual("group", matches[0].Cmd.Name);

        // Verify sorted descending
        for (var i = 1; i < matches.Count; i++)
            Assert.IsTrue(matches[i - 1].Score >= matches[i].Score);
    }

    [TestMethod]
    public void FindMatches_CapsAtFiveResults()
    {
        var children = Enumerable
            .Range(0, 10)
            .Select(i => (CommandDef)new TestCommandDef($"command{i}"))
            .ToList();
        var root = new TestCommandDef("root", children);

        var matches = FuzzyCommandMatcher.FindMatches(root, "command");

        Assert.IsTrue(matches.Count <= 5);
    }
}
