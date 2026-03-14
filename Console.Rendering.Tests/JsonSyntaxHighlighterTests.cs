using Console.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.RegularExpressions;

namespace Console.Rendering.Tests;

[TestClass]
public class JsonSyntaxHighlighterTests
{
    // Strip ANSI escape sequences so we can compare content
    private static string StripAnsi(string s) =>
        Regex.Replace(s, @"\x1b\[[0-9;]*m", "");

    [TestMethod]
    public void Colorize_PreservesJsonContent_Object()
    {
        var json = "{\"name\":\"test\",\"count\":42}";
        var colorized = JsonSyntaxHighlighter.Colorize(json);
        var stripped = StripAnsi(colorized);
        Assert.AreEqual(json, stripped, "Colorizer must not alter the JSON content");
    }

    [TestMethod]
    public void Colorize_PreservesJsonContent_Indented()
    {
        var json = "{\n  \"name\": \"hello\",\n  \"active\": true,\n  \"score\": 3.14,\n  \"nothing\": null\n}";
        var colorized = JsonSyntaxHighlighter.Colorize(json);
        var stripped = StripAnsi(colorized);
        Assert.AreEqual(json, stripped, "Colorizer must not alter the JSON content");
    }

    [TestMethod]
    public void Colorize_PreservesJsonContent_Array()
    {
        var json = "[1,2,3]";
        var colorized = JsonSyntaxHighlighter.Colorize(json);
        var stripped = StripAnsi(colorized);
        Assert.AreEqual(json, stripped);
    }

    [TestMethod]
    public void Colorize_PreservesJsonContent_NestedObjects()
    {
        var json = "{\"outer\":{\"inner\":\"value\"}}";
        var colorized = JsonSyntaxHighlighter.Colorize(json);
        var stripped = StripAnsi(colorized);
        Assert.AreEqual(json, stripped);
    }

    [TestMethod]
    public void Colorize_PreservesJsonContent_StringWithEscapes()
    {
        var json = "{\"msg\":\"hello \\\"world\\\"\"}";
        var colorized = JsonSyntaxHighlighter.Colorize(json);
        var stripped = StripAnsi(colorized);
        Assert.AreEqual(json, stripped, "Escaped quotes inside strings must be preserved");
    }

    [TestMethod]
    public void Colorize_WhenAnsiEnabled_AddsEscapeCodes()
    {
        // Only verify when ANSI is actually enabled (interactive terminal)
        if (!Ansi.IsEnabled)
            Assert.Inconclusive("ANSI is disabled in this environment");

        var json = "{\"key\":\"value\"}";
        var colorized = JsonSyntaxHighlighter.Colorize(json);
        Assert.IsTrue(colorized.Contains('\x1b'), "Should contain ANSI escape codes");
    }
}
