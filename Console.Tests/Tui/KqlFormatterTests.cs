using Console.Tui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests.Tui;

[TestClass]
public class KqlFormatterTests
{
    // ── No-op cases ──────────────────────────────────────────────────────────

    [TestMethod]
    public void Format_SingleClause_Unchanged()
    {
        Assert.AreEqual("SecurityEvent", KqlFormatter.Format("SecurityEvent"));
    }

    [TestMethod]
    public void Format_EmptyInput_ReturnsEmpty()
    {
        Assert.AreEqual("", KqlFormatter.Format(""));
    }

    [TestMethod]
    public void Format_WhitespaceOnly_ReturnsEmpty()
    {
        Assert.AreEqual("", KqlFormatter.Format("   \n  "));
    }

    // ── Pipe splitting ────────────────────────────────────────────────────────

    [TestMethod]
    public void Format_SinglePipe_ProducesIndentedSecondLine()
    {
        var result = KqlFormatter.Format("SecurityEvent | where Level == 1");
        var lines = result.Split('\n');
        Assert.AreEqual(2, lines.Length);
        Assert.AreEqual("SecurityEvent", lines[0]);
        Assert.AreEqual("| where Level == 1", lines[1]);
    }

    [TestMethod]
    public void Format_MultiplePipes_EachOnOwnLine()
    {
        var result = KqlFormatter.Format("T | where x > 0 | summarize count() by y | take 10");
        var lines = result.Split('\n');
        Assert.AreEqual(4, lines.Length);
        Assert.AreEqual("T", lines[0]);
        Assert.AreEqual("| where x > 0", lines[1]);
        Assert.AreEqual("| summarize count() by y", lines[2]);
        Assert.AreEqual("| take 10", lines[3]);
    }

    [TestMethod]
    public void Format_AlreadyFormatted_Idempotent()
    {
        var query = "T\n| where x > 0\n| take 5";
        Assert.AreEqual(query, KqlFormatter.Format(query));
    }

    [TestMethod]
    public void Format_ExtraWhitespaceAroundPipe_Trimmed()
    {
        var result = KqlFormatter.Format("  T  |  where x > 0  ");
        var lines = result.Split('\n');
        Assert.AreEqual("T", lines[0]);
        Assert.AreEqual("| where x > 0", lines[1]);
    }

    // ── Pipe inside literals / comments is not split ──────────────────────────

    [TestMethod]
    public void Format_PipeInsideSingleQuoteString_NotSplit()
    {
        var query = "T | extend x = 'a|b'";
        var result = KqlFormatter.Format(query);
        var lines = result.Split('\n');
        Assert.AreEqual(2, lines.Length, "Only the outer pipe should split");
        Assert.AreEqual("| extend x = 'a|b'", lines[1]);
    }

    [TestMethod]
    public void Format_PipeInsideDoubleQuoteString_NotSplit()
    {
        var query = "T | extend x = \"a|b\"";
        var result = KqlFormatter.Format(query);
        var lines = result.Split('\n');
        Assert.AreEqual(2, lines.Length, "Only the outer pipe should split");
        Assert.AreEqual("| extend x = \"a|b\"", lines[1]);
    }

    [TestMethod]
    public void Format_PipeInsideComment_NotSplit()
    {
        var query = "T | where x > 0 // keep | this together";
        var result = KqlFormatter.Format(query);
        var lines = result.Split('\n');
        Assert.AreEqual(2, lines.Length, "Only the non-comment pipe should split");
        StringAssert.StartsWith(lines[1], "| where x > 0");
        StringAssert.Contains(lines[1], "// keep | this together");
    }

    // ── Content preservation ──────────────────────────────────────────────────

    [TestMethod]
    public void Format_ContentPreserved_NoTokensAdded()
    {
        var query = "SecurityEvent | where EventID == 4624 | project TimeGenerated, Computer";
        var result = KqlFormatter.Format(query);
        // All original tokens must survive the round-trip
        StringAssert.Contains(result, "SecurityEvent");
        StringAssert.Contains(result, "EventID == 4624");
        StringAssert.Contains(result, "TimeGenerated, Computer");
    }
}
