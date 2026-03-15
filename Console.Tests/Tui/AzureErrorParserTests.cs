using Console.Tui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests.Tui;

[TestClass]
public class AzureErrorParserTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Wraps a JSON body in the "Content: {body} Headers: ..." envelope that the Azure SDK produces.</summary>
    private static string Sdk(string leadText, string jsonBody, string statusCode = "400 (Bad Request)")
        => $"{leadText} Status: {statusCode} ErrorCode: BadArgumentError " +
           $"Content: {jsonBody} " +
           $"Headers: Date: Sun, 15 Mar 2026 12:00:00 GMT Connection: keep-alive";

    private static string Sdk(string jsonBody) => Sdk("The request had some invalid properties", jsonBody);

    // ── Real SDK format (Content: on its own line) ────────────────────────────

    /// <summary>
    /// The real RequestFailedException.Message puts "Content:" on its own line followed by
    /// the JSON body on the next line — NOT "Content: {json}" on one line.
    /// This is the format AzureErrorParser must handle.
    /// </summary>
    [TestMethod]
    public void Parse_RealSdkFormat_ContentOnOwnLine_ParsesCorrectly()
    {
        // Reproduce the exact multiline format produced by Azure SDK RequestFailedException
        var raw =
            "The request had some invalid properties\n" +
            "Status: 400 (Bad Request)\n" +
            "ErrorCode: BadArgumentError\n" +
            "Content:\n" +
            "{\"error\":{\"code\":\"BadArgumentError\",\"message\":\"The request had some invalid properties\"," +
            "\"innererror\":{\"code\":\"SyntaxError\",\"message\":\"A recognition error occurred in the query.\"," +
            "\"innererror\":{\"code\":\"SYN0002\",\"message\":\"Query could not be parsed at '|' on line [2,20]\"," +
            "\"line\":2,\"pos\":20,\"token\":\"|\"}}}}";

        var query = "SecurityEvent\n| where TimeGenerated > ago(1h) | badtoken";
        var result = AzureErrorParser.Parse(raw, query);

        Assert.AreEqual("SYN0002: Query could not be parsed at '|' on line [2,20]", result.DisplayMessage);
        Assert.AreEqual(2, result.LineNumber);
        Assert.AreEqual("| where TimeGenerated > ago(1h) | badtoken", result.QueryLine);
        Assert.AreEqual(19, result.Column);
    }

    [TestMethod]
    public void Parse_RealSdkFormat_WithNewlinesInsideJsonStringValues_ParsesCorrectly()
    {
        // SDK also embeds literal \n inside JSON string values (doubly-invalid)
        var raw =
            "The request had some invalid properties\n" +
            "Status: 400 (Bad Request)\n" +
            "Content:\n" +
            "{\"error\":{\"message\":\"The request had some invalid\n  properties\"," +
            "\"code\":\"BadArgumentError\"," +
            "\"innererror\":{\"code\":\"SYN0002\"," +
            "\"message\":\"Query could not be parsed at '|' on line [2,20]\"," +
            "\"line\":2,\"pos\":20,\"token\":\"|\"}}}\n" +
            "Headers:\nDate: Sun, 15 Mar 2026 13:00:00 GMT";

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("SYN0002: Query could not be parsed at '|' on line [2,20]", result.DisplayMessage);
        Assert.AreEqual(2, result.LineNumber);
    }

    [TestMethod]
    public void Parse_RealSdkFormat_DisplayMessage_ContainsNoNewlines()
    {
        var raw =
            "Some error\nStatus: 400\nContent:\n{\"error\":{\"code\":\"E1\",\"message\":\"msg\"}}\nHeaders:\nx: y";

        var result = AzureErrorParser.Parse(raw);

        Assert.IsFalse(result.DisplayMessage.Contains('\n'), "No newlines in display message");
        Assert.IsFalse(result.DisplayMessage.Contains('\r'), "No carriage returns in display message");
    }

    // ── SYN errors — doubly-nested innererror with position ───────────────────

    [TestMethod]
    public void Parse_SynError_DoublyNested_ExtractsInnermostCodeAndMessage()
    {
        var raw = Sdk("""
            {"error":{"code":"BadArgumentError","message":"The request had some invalid properties",
            "innererror":{"code":"SyntaxError","message":"A recognition error occurred in the query.",
            "innererror":{"code":"SYN0002","message":"Query could not be parsed at '|' on line [2,20]",
            "line":2,"pos":20,"token":"|"}}}}
            """);

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("SYN0002: Query could not be parsed at '|' on line [2,20]", result.DisplayMessage);
        Assert.AreEqual(2, result.LineNumber);
        Assert.IsNull(result.QueryLine, "No query text supplied");
        Assert.IsNull(result.Column,    "No query text supplied");
    }

    [TestMethod]
    public void Parse_SynError_DoublyNested_WithQueryText_ExtractsLineAndColumn()
    {
        var raw = Sdk("""
            {"error":{"code":"BadArgumentError","message":"...",
            "innererror":{"code":"SyntaxError","message":"...",
            "innererror":{"code":"SYN0002","message":"Query could not be parsed at '|' on line [2,20]",
            "line":2,"pos":20,"token":"|"}}}}
            """);

        var query = "SecurityEvent\n| where TimeGenerated > ago(1h) | summarize count() by Computer\n| take 10";
        var result = AzureErrorParser.Parse(raw, query);

        Assert.AreEqual(2, result.LineNumber);
        Assert.AreEqual("| where TimeGenerated > ago(1h) | summarize count() by Computer", result.QueryLine);
        Assert.AreEqual(19, result.Column, "pos=20 (1-based) → column 19 (0-based)");
    }

    [TestMethod]
    public void Parse_SynError_WithEmbeddedNewlinesInJson_ParsesSuccessfully()
    {
        // Azure SDK embeds literal \n inside JSON string values — this is the real-world bug
        var raw = "The request had some invalid properties Status: 400 (Bad Request) " +
                  "Content: {\"error\":{\"message\":\"The request had some invalid\n  properties\"," +
                  "\"code\":\"BadArgumentError\",\"innererror\":{\"code\":\"SyntaxError\"," +
                  "\"message\":\"A recognition error occurred\n  in the query.\"," +
                  "\"innererror\":{\"code\":\"SYN0002\"," +
                  "\"message\":\"Query could not be parsed at '|' on line [2,20]\"," +
                  "\"line\":2,\"pos\":20,\"token\":\"|\"}}}} " +
                  "Headers: Date: Sun, 15 Mar 2026 13:00:00 GMT";

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("SYN0002: Query could not be parsed at '|' on line [2,20]", result.DisplayMessage);
        Assert.AreEqual(2, result.LineNumber);
    }

    [TestMethod]
    public void Parse_SynError_DisplayMessage_ContainsNoRawJson()
    {
        var raw = Sdk("""
            {"error":{"code":"BadArgumentError","message":"...",
            "innererror":{"code":"SYN0002","message":"Syntax error near '('","line":3,"pos":5}}}
            """);

        var result = AzureErrorParser.Parse(raw);

        StringAssert.Contains(result.DisplayMessage, "SYN0002");
        StringAssert.Contains(result.DisplayMessage, "Syntax error near '('");
        Assert.IsFalse(result.DisplayMessage.Contains('{'), "No raw JSON braces in display message");
        Assert.IsFalse(result.DisplayMessage.Contains("BadArgumentError"), "Outer code suppressed by inner");
    }

    // ── SEM errors — single innererror level with position ────────────────────

    [TestMethod]
    public void Parse_SemError_SingleInnererror_ExtractsPositionFromInnererror()
    {
        var raw = Sdk("""
            {"error":{"code":"BadArgumentError",
            "message":"Failed to resolve scalar expression named 'foo'",
            "innererror":{"code":"SEM0003",
            "message":"Failed to resolve scalar expression named 'foo'",
            "line":1,"pos":14}}}
            """);

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("SEM0003: Failed to resolve scalar expression named 'foo'", result.DisplayMessage);
        Assert.AreEqual(1, result.LineNumber);
    }

    [TestMethod]
    public void Parse_SemError_WithQueryText_ExtractsColumnFromPos()
    {
        var raw = Sdk("""
            {"error":{"code":"BadArgumentError","message":"...",
            "innererror":{"code":"SEM0003","message":"Unknown column 'foo'","line":1,"pos":14}}}
            """);

        var query = "SecurityEvent | where foo > 0";
        var result = AzureErrorParser.Parse(raw, query);

        Assert.AreEqual("SecurityEvent | where foo > 0", result.QueryLine);
        Assert.AreEqual(13, result.Column, "pos=14 (1-based) → 13 (0-based)");
    }

    // ── LIM errors — no innererror, no position ───────────────────────────────

    [TestMethod]
    public void Parse_LimitError_NoInnererror_ShowsCodeAndMessage()
    {
        var raw = Sdk(
            "Query result exceeds the maximum allowed size",
            "{\"error\":{\"code\":\"LIM0001\",\"message\":\"Query result exceeds the maximum allowed size (67108864 bytes).\"}}",
            "400 (Bad Request)");

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("LIM0001: Query result exceeds the maximum allowed size (67108864 bytes).", result.DisplayMessage);
        Assert.IsNull(result.LineNumber, "LIM errors have no position");
        Assert.IsNull(result.QueryLine);
    }

    // ── Other common Kusto errors ─────────────────────────────────────────────

    [TestMethod]
    public void Parse_WorkspaceNotFound_ExtractsMessage()
    {
        var raw = Sdk(
            "Workspace not found",
            "{\"error\":{\"code\":\"WorkspaceNotFoundError\",\"message\":\"The workspace with ID 'aaa-bbb' was not found.\"}}",
            "404 (Not Found)");

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("WorkspaceNotFoundError: The workspace with ID 'aaa-bbb' was not found.", result.DisplayMessage);
        Assert.IsNull(result.LineNumber);
    }

    [TestMethod]
    public void Parse_Throttled_ExtractsMessage()
    {
        var raw = Sdk(
            "Too many requests",
            "{\"error\":{\"code\":\"TooManyRequests\",\"message\":\"The request is throttled. Please wait before retrying.\"}}",
            "429 (Too Many Requests)");

        var result = AzureErrorParser.Parse(raw);

        StringAssert.Contains(result.DisplayMessage, "TooManyRequests");
        StringAssert.Contains(result.DisplayMessage, "throttled");
    }

    [TestMethod]
    public void Parse_ForbiddenAccess_ExtractsMessage()
    {
        var raw = Sdk(
            "Authorization failed",
            "{\"error\":{\"code\":\"AuthorizationFailedError\",\"message\":\"The client does not have authorization to perform action.\"}}",
            "403 (Forbidden)");

        var result = AzureErrorParser.Parse(raw);

        StringAssert.Contains(result.DisplayMessage, "AuthorizationFailedError");
    }

    // ── Root-level JSON (no "error" wrapper) ─────────────────────────────────

    [TestMethod]
    public void Parse_RootLevelCodeMessage_NoErrorWrapper_Extracted()
    {
        var raw = "Operation failed Status: 400 (Bad Request) " +
                  "Content: {\"code\":\"InvalidQuery\",\"message\":\"The query is not valid.\"} " +
                  "Headers: x: y";

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("InvalidQuery: The query is not valid.", result.DisplayMessage);
    }

    // ── Query line extraction edge cases ─────────────────────────────────────

    [TestMethod]
    public void Parse_WithQueryText_LineOneError_ExtractsFirstLine()
    {
        var raw = Sdk("""
            {"error":{"innererror":{"code":"SYN0001","message":"bad token","line":1,"pos":3}}}
            """);

        var result = AzureErrorParser.Parse(raw, "abc\n| take 10");

        Assert.AreEqual("abc", result.QueryLine);
        Assert.AreEqual(2, result.Column, "pos=3 (1-based) → 2 (0-based)");
    }

    [TestMethod]
    public void Parse_ColumnBeyondLineLength_ClampedToLineLength()
    {
        var raw = Sdk("""
            {"error":{"innererror":{"code":"SYN0001","message":"msg","line":1,"pos":999}}}
            """);

        var result = AzureErrorParser.Parse(raw, "short");

        Assert.AreEqual("short", result.QueryLine);
        Assert.AreEqual(5, result.Column, "Clamped to line length 5");
    }

    [TestMethod]
    public void Parse_LineNumberBeyondQueryLines_QueryLineIsNull()
    {
        var raw = Sdk("""
            {"error":{"innererror":{"code":"SYN0001","message":"msg","line":99,"pos":1}}}
            """);

        var result = AzureErrorParser.Parse(raw, "only one line");

        Assert.AreEqual(99, result.LineNumber, "LineNumber still populated");
        Assert.IsNull(result.QueryLine, "Line index out of range");
    }

    [TestMethod]
    public void Parse_NullQueryText_PositionFieldsAreNull()
    {
        var raw = Sdk("""
            {"error":{"innererror":{"code":"SYN0002","message":"error","line":2,"pos":5}}}
            """);

        var result = AzureErrorParser.Parse(raw, queryText: null);

        Assert.AreEqual(2, result.LineNumber, "LineNumber extracted from JSON");
        Assert.IsNull(result.QueryLine);
        Assert.IsNull(result.Column);
    }

    // ── Fallback — no Content: JSON ───────────────────────────────────────────

    [TestMethod]
    public void Parse_NoContentJson_TextBeforeStatus_Used()
    {
        var result = AzureErrorParser.Parse("Connection refused Status: 503 Headers: x: y");

        Assert.AreEqual("Connection refused", result.DisplayMessage);
        Assert.IsNull(result.LineNumber);
    }

    [TestMethod]
    public void Parse_PlainMessage_NoNoise_ReturnsTrimmed()
    {
        var result = AzureErrorParser.Parse("  Query cancelled.  ");

        Assert.AreEqual("Query cancelled.", result.DisplayMessage);
    }

    [TestMethod]
    public void Parse_FallbackWithEmbeddedNewlines_CollapsedToSpaces()
    {
        var result = AzureErrorParser.Parse("Line one\nLine two\nLine three Status: 400");

        Assert.IsFalse(result.DisplayMessage.Contains('\n'), "Newlines collapsed");
        StringAssert.Contains(result.DisplayMessage, "Line one");
    }

    [TestMethod]
    public void Parse_EmptyMessage_ReturnsEmptyDisplayMessage()
    {
        var result = AzureErrorParser.Parse("");

        Assert.AreEqual("", result.DisplayMessage);
    }

    // ── Display message format ────────────────────────────────────────────────

    [TestMethod]
    public void Parse_CodeAndMessage_FormattedAsCodeColonMessage()
    {
        var raw = Sdk("{\"error\":{\"code\":\"SEM0003\",\"message\":\"Unknown column 'x'\"}}");

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("SEM0003: Unknown column 'x'", result.DisplayMessage);
    }

    [TestMethod]
    public void Parse_MessageWithoutCode_DisplayMessageHasNoColon()
    {
        var raw = Sdk("{\"error\":{\"message\":\"Something went wrong\"}}");

        var result = AzureErrorParser.Parse(raw);

        Assert.AreEqual("Something went wrong", result.DisplayMessage);
    }

    [TestMethod]
    public void Parse_DisplayMessage_NeverContainsHttpHeaders()
    {
        var raw = Sdk("""{"error":{"code":"LIM0001","message":"Result too large."}}""");

        var result = AzureErrorParser.Parse(raw);

        Assert.IsFalse(result.DisplayMessage.Contains("Headers:"), "HTTP headers must not appear");
        Assert.IsFalse(result.DisplayMessage.Contains("Date:"),    "HTTP date must not appear");
        Assert.IsFalse(result.DisplayMessage.Contains("Content:"), "Content marker must not appear");
    }

    [TestMethod]
    public void Parse_DisplayMessage_NeverContainsCorrelationId()
    {
        var raw = Sdk("""
            {"error":{"code":"SEM0003","message":"Unknown column",
            "correlationId":"d6563826-d091-4cbc-9a19-bdd196024a9c"}}
            """);

        var result = AzureErrorParser.Parse(raw);

        Assert.IsFalse(result.DisplayMessage.Contains("correlationId"), "Correlation ID must be stripped");
        Assert.IsFalse(result.DisplayMessage.Contains("d6563826"),      "Correlation ID value must be stripped");
    }
}
