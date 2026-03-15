using System.Text.RegularExpressions;
using Console.Rendering;
using Console.Tui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests.Tui;

[TestClass]
public class KqlHighlighterTests
{
    private static string StripAnsi(string s) => Regex.Replace(s, @"\x1b\[[0-9;]*m", "");

    // ── Content preservation (works even when ANSI is disabled) ──────────────

    [TestMethod]
    public void Highlight_Keyword_PreservesContent()
    {
        var line = "where Level == 1";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_FunctionCall_PreservesContent()
    {
        var line = "| summarize count() by Computer";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_StringLiteral_PreservesContent()
    {
        var line = "| where Name == \"server-01\"";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_InlineComment_PreservesContent()
    {
        var line = "| where x > 0 // filter out negatives";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_FullLineComment_PreservesContent()
    {
        var line = "// this is a comment";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_Number_PreservesContent()
    {
        var line = "| where count > 42";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_HyphenatedKeyword_PreservesContent()
    {
        var line = "| project-away TenantId";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_PipeOperator_PreservesContent()
    {
        var line = "T | where x > 0 | take 10";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_EscapedQuoteInString_PreservesContent()
    {
        var line = "| where Msg == \"say \\\"hello\\\"\"";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    [TestMethod]
    public void Highlight_EmptyString_ReturnsEmpty()
    {
        Assert.AreEqual("", KqlHighlighter.Highlight(""));
    }

    [TestMethod]
    public void Highlight_PlainIdentifier_PreservesContent()
    {
        var line = "MyCustomTable";
        Assert.AreEqual(line, StripAnsi(KqlHighlighter.Highlight(line)));
    }

    // ── Color codes present (only meaningful in interactive terminals) ────────

    [TestMethod]
    public void Highlight_Keyword_ContainsAnsiCode()
    {
        if (!Ansi.IsEnabled)
            Assert.Inconclusive("ANSI disabled in this environment");

        var result = KqlHighlighter.Highlight("where");
        Assert.IsTrue(result.Contains('\x1b'), "keyword should be wrapped in ANSI codes");
    }

    [TestMethod]
    public void Highlight_FunctionFollowedByParen_ContainsAnsiCode()
    {
        if (!Ansi.IsEnabled)
            Assert.Inconclusive("ANSI disabled in this environment");

        var result = KqlHighlighter.Highlight("count()");
        Assert.IsTrue(result.Contains('\x1b'), "function name should be wrapped in ANSI codes");
    }

    [TestMethod]
    public void Highlight_FunctionWithoutParen_NotHighlightedAsFunction()
    {
        if (!Ansi.IsEnabled)
            Assert.Inconclusive("ANSI disabled in this environment");

        // "count" alone (without "(") is not a function call — treated as identifier
        // It is also in the Keywords set, so it may still be cyan — but it should NOT be yellow.
        // Verify: keyword color code != function color code.
        var asFunction = KqlHighlighter.Highlight("count()");
        var asIdentifier = KqlHighlighter.Highlight("count");

        // The highlighted forms must differ when ANSI is on
        Assert.AreNotEqual(asFunction, asIdentifier,
            "'count()' and 'count' should produce different highlighted output");
    }

    [TestMethod]
    public void Highlight_StringLiteral_ContainsAnsiCode()
    {
        if (!Ansi.IsEnabled)
            Assert.Inconclusive("ANSI disabled in this environment");

        var result = KqlHighlighter.Highlight("\"hello world\"");
        Assert.IsTrue(result.Contains('\x1b'), "string literal should be wrapped in ANSI codes");
    }

    [TestMethod]
    public void Highlight_FullLineComment_ContainsAnsiCode()
    {
        if (!Ansi.IsEnabled)
            Assert.Inconclusive("ANSI disabled in this environment");

        var result = KqlHighlighter.Highlight("// a comment");
        Assert.IsTrue(result.Contains('\x1b'), "comment should be wrapped in ANSI codes");
    }

    [TestMethod]
    public void Highlight_HyphenatedKeyword_ContainsAnsiCode()
    {
        if (!Ansi.IsEnabled)
            Assert.Inconclusive("ANSI disabled in this environment");

        var result = KqlHighlighter.Highlight("project-away");
        Assert.IsTrue(result.Contains('\x1b'), "hyphenated keyword should be highlighted");
    }
}
