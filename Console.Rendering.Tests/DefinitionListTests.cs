using Console.Rendering;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Rendering.Tests;

[TestClass]
public class DefinitionListTests
{
    private static string Render(
        IReadOnlyList<(string, string)> entries,
        int width = 80,
        int indent = 2
    )
    {
        using var writer = new StringWriter();
        DefinitionList.Write(writer, entries, indent: indent, width: width);
        return writer.ToString();
    }

    [TestMethod]
    public void BasicAlignment_ColonAfterLabel_ValuesAligned()
    {
        var entries = new[] { ("Name", "Alice"), ("Occupation", "Engineer") };
        var output = Render(entries);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual(2, lines.Length);
        // Colon immediately follows each label (no padding before colon)
        Assert.IsTrue(
            lines[0].Contains("Name: "),
            $"Colon should follow label directly: {lines[0]}"
        );
        Assert.IsTrue(
            lines[1].Contains("Occupation: "),
            $"Colon should follow label directly: {lines[1]}"
        );
        // Values are column-aligned (padding is after ": ", not before ":")
        var valueCol0 = lines[0].IndexOf("Alice");
        var valueCol1 = lines[1].IndexOf("Engineer");
        Assert.AreEqual(valueCol0, valueCol1, "Values should start at the same column");
    }

    [TestMethod]
    public void LongValue_WrapsAtWordBoundary()
    {
        var entries = new[] { ("Key", "one two three four five six seven eight nine ten") };
        // width=20: indent(2) + label(3) + ": "(2) = valueStart=7, valueWidth=13
        var output = Render(entries, width: 20);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.IsTrue(lines.Length > 1, "Long value should wrap to multiple lines");
        // No line should contain a word broken in the middle (only space-based breaks)
        foreach (var line in lines)
            Assert.IsFalse(line.TrimEnd().EndsWith('-'), "Should not break mid-word");
    }

    [TestMethod]
    public void ContinuationLines_IndentedToValueColumn()
    {
        var entries = new[] { ("Key", "one two three four five six seven eight nine ten") };
        // indent=2, label="Key"(3), valueStart = 2+3+2 = 7
        var output = Render(entries, width: 20);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.IsTrue(lines.Length > 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var leadingSpaces = lines[i].Length - lines[i].TrimStart().Length;
            Assert.AreEqual(
                7,
                leadingSpaces,
                $"Continuation line {i} should have 7 leading spaces"
            );
        }
    }

    [TestMethod]
    public void AnsiInLabel_DoesNotCountEscapeCodes()
    {
        // Bold adds escape codes; visible label width should still be 4 ("Name"),
        // so value column = indent(2) + labelWidth(4) + ": "(2) = 8, valueWidth = 20-8 = 12.
        var boldLabel = Ansi.Bold("Name");
        var entries = new[] { (boldLabel, "one two three four five") };
        var output = Render(entries, width: 20);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.IsTrue(
            lines.Length > 1,
            "Value should wrap when ANSI label length is computed correctly"
        );
        // Continuation lines should be indented to valueStart=8
        for (var i = 1; i < lines.Length; i++)
        {
            var leading = lines[i].Length - lines[i].TrimStart().Length;
            Assert.AreEqual(8, leading, $"Continuation line {i} should have 8 leading spaces");
        }
    }

    [TestMethod]
    public void AnsiInValue_WrapsOnVisibleWidth()
    {
        // Yellow wraps text in ANSI codes; visible length of "hello" is 5
        var yellowWord = Ansi.Yellow("hello");
        var entries = new[] { ("Key", $"{yellowWord} world extra words here") };
        // width=20: valueStart=7, valueWidth=13
        // "hello"(visible 5) + " world"(6) = 11 ≤ 13, fits; "extra"(5) pushes to 17 > 13
        var output = Render(entries, width: 20);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.IsTrue(lines.Length >= 2, "Should wrap despite ANSI codes in value");
        Assert.IsTrue(lines[0].Contains("world"), "First line should contain 'world' which fits");
    }

    [TestMethod]
    public void SingleWordLongerThanWidth_DoesNotBreak()
    {
        var entries = new[] { ("K", "superlongwordthatexceedsthecolumnwidth") };
        // width=10: valueStart=5, valueWidth=5 — word is 38 chars, overflows intact
        var output = Render(entries, width: 10);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual(1, lines.Length, "Overflow word should emit as single line, not be broken");
        Assert.IsTrue(lines[0].Contains("superlongwordthatexceedsthecolumnwidth"));
    }

    [TestMethod]
    public void EmptyEntries_WritesNothing()
    {
        var output = Render([]);
        Assert.AreEqual("", output);
    }

    [TestMethod]
    public void ValueExactlyConsoleWidth_SingleLine()
    {
        // indent=2, label="K"(1), valueStart=5, valueWidth=15 with width=20
        var exactValue = new string('x', 15);
        var entries = new[] { ("K", exactValue) };
        var output = Render(entries, width: 20);
        var lines = output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.AreEqual(1, lines.Length, "Value exactly fitting the column should not wrap");
        Assert.IsTrue(lines[0].EndsWith(exactValue));
    }
}
