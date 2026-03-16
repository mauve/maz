using Console.Tui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests.Tui;

/// <summary>
/// Tests for <see cref="EditorPane"/> focusing on word-extraction logic used by tab-completion.
/// Console I/O (Render, GetCursorScreenPosition) is not exercised here.
/// </summary>
[TestClass]
public class EditorPaneTests
{
    // Helper: create an editor pre-loaded with text, with the cursor placed after the
    // last character of <paramref name="line"/> (simulating the user having typed it).
    private static EditorPane EditorWith(string line)
    {
        var pane = new EditorPane(line);
        // EditorPane places the cursor at the end of the initial query by default
        return pane;
    }

    // ── Basic word extraction ─────────────────────────────────────────────────

    [TestMethod]
    public void GetWordAtCursor_SimpleWord_ReturnsWholeWord()
    {
        var pane = EditorWith("SecurityEvent");
        Assert.AreEqual("SecurityEvent", pane.GetWordAtCursor());
    }

    [TestMethod]
    public void GetWordAtCursor_EmptyLine_ReturnsEmpty()
    {
        var pane = EditorWith("");
        Assert.AreEqual("", pane.GetWordAtCursor());
    }

    [TestMethod]
    public void GetWordAtCursor_AfterSpace_ReturnsWordAfterSpace()
    {
        // "T | whe" → cursor is after "whe", word should be "whe"
        var pane = EditorWith("T | whe");
        Assert.AreEqual("whe", pane.GetWordAtCursor());
    }

    [TestMethod]
    public void GetWordAtCursor_AfterPipeAndSpace_ReturnsKeywordPrefix()
    {
        var pane = EditorWith("SecurityEvent | where");
        Assert.AreEqual("where", pane.GetWordAtCursor());
    }

    // ── Underscore handling ───────────────────────────────────────────────────

    [TestMethod]
    public void GetWordAtCursor_WithUnderscore_IncludesUnderscore()
    {
        var pane = EditorWith("device_logs");
        Assert.AreEqual("device_logs", pane.GetWordAtCursor());
    }

    [TestMethod]
    public void GetWordAtCursor_PartialWithUnderscore_ReturnsPartial()
    {
        var pane = EditorWith("T | project device_lo");
        Assert.AreEqual("device_lo", pane.GetWordAtCursor());
    }

    // ── Hyphen handling (the bug fix) ─────────────────────────────────────────

    [TestMethod]
    public void GetWordAtCursor_HyphenatedKeyword_IncludesHyphen()
    {
        // "| project-away" → cursor at end → should return "project-away", not just "away"
        var pane = EditorWith("T | project-away");
        Assert.AreEqual(
            "project-away",
            pane.GetWordAtCursor(),
            "Hyphen must be included so 'project-away' is matched as one token"
        );
    }

    [TestMethod]
    public void GetWordAtCursor_PartialHyphenatedKeyword_ReturnsFullPartial()
    {
        // "| project-aw" → should return "project-aw" not just "aw"
        var pane = EditorWith("T | project-aw");
        Assert.AreEqual(
            "project-aw",
            pane.GetWordAtCursor(),
            "Partial hyphenated token must include everything up to the cursor"
        );
    }

    [TestMethod]
    public void GetWordAtCursor_MakeSeries_IncludesHyphen()
    {
        var pane = EditorWith("T | make-series");
        Assert.AreEqual("make-series", pane.GetWordAtCursor());
    }

    [TestMethod]
    public void GetWordAtCursor_MvExpand_IncludesHyphen()
    {
        var pane = EditorWith("T | mv-expand");
        Assert.AreEqual("mv-expand", pane.GetWordAtCursor());
    }

    // ── Cursor in middle of word ──────────────────────────────────────────────

    [TestMethod]
    public void GetWordAtCursor_MultilineQuery_UsesCurrentLine()
    {
        // Initial query spans two lines; cursor ends up on last line
        var pane = EditorWith("SecurityEvent\n| where");
        Assert.AreEqual(
            "where",
            pane.GetWordAtCursor(),
            "Should extract word from the current (last) line"
        );
    }

    // ── Text state after edits ────────────────────────────────────────────────

    [TestMethod]
    public void GetText_InitialQuery_RoundTrips()
    {
        var query = "SecurityEvent\n| where EventID == 4624\n| take 10";
        var pane = new EditorPane(query);
        Assert.AreEqual(query, pane.GetText());
    }

    [TestMethod]
    public void FormatQuery_SinglePipe_FormatsCorrectly()
    {
        var pane = new EditorPane("T | where x > 0 | take 5");
        pane.FormatQuery();
        Assert.AreEqual("T\n| where x > 0\n| take 5", pane.GetText());
    }

    // ── Autocomplete state management ─────────────────────────────────────────

    [TestMethod]
    public void ShowAutocomplete_ThenDismiss_ReturnsTrueOnce()
    {
        var pane = EditorWith("whe");
        Assert.IsFalse(pane.IsAutocompleteVisible);

        pane.ShowAutocomplete([new CompletionItem("where"), new CompletionItem("while")]);
        Assert.IsTrue(pane.IsAutocompleteVisible);

        Assert.IsTrue(pane.DismissAutocomplete(), "First dismiss should return true");
        Assert.IsFalse(pane.IsAutocompleteVisible);
        Assert.IsFalse(
            pane.DismissAutocomplete(),
            "Second dismiss should return false (already hidden)"
        );
    }

    [TestMethod]
    public void AutocompleteAccept_ReplacesWordAtCursor()
    {
        var pane = EditorWith("T | whe");
        pane.ShowAutocomplete([new CompletionItem("where")]);
        pane.AutocompleteAccept();

        Assert.AreEqual(
            "T | where",
            pane.GetText(),
            "Accepted completion should replace the partial word"
        );
        Assert.IsFalse(pane.IsAutocompleteVisible);
    }

    [TestMethod]
    public void AutocompleteAccept_HyphenatedKeyword_ReplacesCorrectly()
    {
        var pane = EditorWith("T | project-aw");
        pane.ShowAutocomplete([new CompletionItem("project-away")]);
        pane.AutocompleteAccept();

        Assert.AreEqual(
            "T | project-away",
            pane.GetText(),
            "Hyphenated completion must replace the full hyphenated prefix"
        );
    }

    [TestMethod]
    public void HandleKey_TypingChar_DismissesAutocomplete()
    {
        var pane = EditorWith("whe");
        pane.ShowAutocomplete([new CompletionItem("where")]);
        Assert.IsTrue(pane.IsAutocompleteVisible);

        pane.HandleKey(new ConsoleKeyInfo('r', ConsoleKey.R, false, false, false));
        Assert.IsFalse(
            pane.IsAutocompleteVisible,
            "Typing a character should dismiss the autocomplete popup"
        );
    }
}
