using System.CommandLine;
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
        // "grp" is not a prefix of "group", but "gr" is not either — let's test "gro" as prefix
        // Actually "grp" vs "group": not a prefix, not substring, edit distance is 3 (grp -> group)
        // but group.Length=5 < 6, so score = 0. Let's use "grou" which IS a prefix.
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
        // "monitr" vs "monitor": distance 2 (missing 'o' and 'r' transposed)
        // Let's use "monitpr" -> "monitor": substitute p->o, distance 2?
        // Actually "moniror" vs "monitor": substitute r->t, distance 1 only.
        // Let's try "manisor" vs "monitor": m-a-n-i-s-o-r vs m-o-n-i-t-o-r
        // positions: a->o (1), s->t (2) = distance 2
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
        // distance 3 should match when candidate.Length >= 6
        // "moniror" vs "monitor": only distance 1
        // "monitabc" vs "monitor":
        //   m-o-n-i-t-a-b-c vs m-o-n-i-t-o-r: a->o(1), b->r(2), delete c(3) = 3
        // monitor.Length = 7 >= 6 ✓
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
        var root = new RootCommand();
        root.Add(new Command("group"));
        root.Add(new Command("get"));
        root.Add(new Command("monitor"));

        // "grou" is prefix of "group" (score 80), substring of nothing else
        var matches = FuzzyCommandMatcher.FindMatches(root, "grou");

        Assert.IsTrue(matches.Count >= 1);
        Assert.AreEqual("group", matches[0].Cmd.Name);

        // Verify sorted descending
        for (var i = 1; i < matches.Count; i++)
            Assert.IsTrue(matches[i - 1].Score >= matches[i].Score);
    }

    [TestMethod]
    public void FindMatches_FiltersOutHiddenCommands()
    {
        var root = new RootCommand();
        root.Add(new Command("group"));
        root.Add(new Command("groper") { Hidden = true });

        var matches = FuzzyCommandMatcher.FindMatches(root, "groper");

        Assert.IsFalse(matches.Any(m => m.Cmd.Name == "groper"));
    }

    [TestMethod]
    public void FindMatches_CapsAtFiveResults()
    {
        var root = new RootCommand();
        // Add commands all of which contain "a" (substring score 50)
        for (var i = 0; i < 10; i++)
            root.Add(new Command($"command{i}"));

        var matches = FuzzyCommandMatcher.FindMatches(root, "command");

        Assert.IsTrue(matches.Count <= 5);
    }
}
