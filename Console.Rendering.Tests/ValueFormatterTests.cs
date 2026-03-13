using Console.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Rendering.Tests;

[TestClass]
public class ValueFormatterTests
{
    private static readonly ValueFormatterOptions Opts = new();

    [TestMethod]
    public void Format_Null_ReturnsDimMagenta()
    {
        var result = ValueFormatter.Format(null, Opts);
        Assert.AreEqual("null", result.Text);
        Assert.IsNotNull(result.AnsiCode);
        StringAssert.Contains(result.AnsiCode, "35"); // magenta
    }

    [TestMethod]
    public void Format_True_ReturnsGreenCheck()
    {
        var result = ValueFormatter.Format(true, Opts);
        Assert.AreEqual("✓", result.Text);
        Assert.IsNotNull(result.AnsiCode);
        StringAssert.Contains(result.AnsiCode, "32"); // green
    }

    [TestMethod]
    public void Format_False_ReturnsRedCross()
    {
        var result = ValueFormatter.Format(false, Opts);
        Assert.AreEqual("✗", result.Text);
        Assert.IsNotNull(result.AnsiCode);
        StringAssert.Contains(result.AnsiCode, "31"); // red
    }

    [TestMethod]
    public void Format_Integer_ReturnsRightAligned()
    {
        var result = ValueFormatter.Format(42, Opts);
        Assert.AreEqual("42", result.Text);
        Assert.AreEqual(TextAlignment.Right, result.Alignment);
    }

    [TestMethod]
    public void Format_Succeeded_ReturnsGreenCheckWithText()
    {
        var result = ValueFormatter.Format("Succeeded", Opts);
        Assert.AreEqual("✓ Succeeded", result.Text);
        Assert.IsNotNull(result.AnsiCode);
        StringAssert.Contains(result.AnsiCode, "32"); // green
    }

    [TestMethod]
    public void Format_Failed_ReturnsRedCrossWithText()
    {
        var result = ValueFormatter.Format("Failed", Opts);
        Assert.AreEqual("✗ Failed", result.Text);
        Assert.IsNotNull(result.AnsiCode);
        StringAssert.Contains(result.AnsiCode, "31"); // red
    }

    [TestMethod]
    public void Format_ResourceId_StripsSubscriptionAndFormatsRg()
    {
        var id =
            "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/myRG/providers/foo";
        var result = ValueFormatter.Format(id, Opts);
        Assert.AreEqual("rg:myRG/providers/foo", result.Text);
    }

    [TestMethod]
    public void Truncate_LongerThanMax_MidTruncates()
    {
        var result = ValueFormatter.Truncate("hello world", 5);
        Assert.AreEqual("he…ld", result);
    }

    [TestMethod]
    public void Truncate_ShorterThanMax_ReturnsUnchanged()
    {
        var result = ValueFormatter.Truncate("hi", 10);
        Assert.AreEqual("hi", result);
    }

    [TestMethod]
    public void Truncate_MaxWidthOne_ReturnsEllipsis()
    {
        var result = ValueFormatter.Truncate("hello", 1);
        Assert.AreEqual("…", result);
    }
}
