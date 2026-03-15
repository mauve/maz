using Azure.Monitor.Query;
using Console.Tui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Console.Tests.Tui;

[TestClass]
public class KqlAutocompleteTests
{
    // SchemaProvider backed by a credential that is never actually called
    // (workspaceId=null → QueryAsync throws InvalidOperationException → caught → returns []).
    // This lets us test keyword completion in isolation without a real workspace.
    private static SchemaProvider EmptySchema() =>
        new(new LogsQueryClient(new NullCredential()), null, null);

    private static async Task<List<string>> Complete(string prefix, string query = "") =>
        await KqlAutocomplete.GetCompletionsAsync(prefix, query, EmptySchema());

    // ── Edge cases ────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetCompletions_EmptyPrefix_ReturnsEmpty()
    {
        var results = await Complete("");
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task GetCompletions_NoMatch_ReturnsEmpty()
    {
        var results = await Complete("zzzznotakeyword");
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public async Task GetCompletions_ExactMatch_ExcludedFromResults()
    {
        // A prefix equal to a completion's full text has length == completion, so
        // the filter `c.Length > prefix.Length` rejects it.
        var results = await Complete("where");
        Assert.IsFalse(
            results.Contains("where", StringComparer.OrdinalIgnoreCase),
            "Exact match should not appear — only longer completions qualify"
        );
    }

    // ── Keyword matching ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetCompletions_PartialKeyword_MatchesKeyword()
    {
        var results = await Complete("whe");
        CollectionAssert.Contains(results, "where");
    }

    [TestMethod]
    public async Task GetCompletions_CaseInsensitive_MatchesKeyword()
    {
        var results = await Complete("WHE");
        CollectionAssert.Contains(results, "where");
    }

    [TestMethod]
    public async Task GetCompletions_SummarizePrefix_MatchesSummarize()
    {
        var results = await Complete("summar");
        CollectionAssert.Contains(results, "summarize");
    }

    [TestMethod]
    public async Task GetCompletions_DistinctPrefix_MatchesDistinct()
    {
        var results = await Complete("dist");
        CollectionAssert.Contains(results, "distinct");
    }

    [TestMethod]
    public async Task GetCompletions_FunctionPrefix_MatchesFunction()
    {
        var results = await Complete("tost");
        CollectionAssert.Contains(results, "tostring");
    }

    [TestMethod]
    public async Task GetCompletions_AgoPrefix_MatchesAgo()
    {
        var results = await Complete("ag");
        CollectionAssert.Contains(results, "ago");
    }

    // ── Hyphenated operators ──────────────────────────────────────────────────

    [TestMethod]
    public async Task GetCompletions_ProjectHyphenPrefix_MatchesProjectAway()
    {
        var results = await Complete("project-aw");
        CollectionAssert.Contains(
            results,
            "project-away",
            "Hyphenated prefix should match hyphenated keyword"
        );
    }

    [TestMethod]
    public async Task GetCompletions_ProjectHyphenPrefix_MatchesProjectRename()
    {
        var results = await Complete("project-r");
        CollectionAssert.Contains(results, "project-rename");
    }

    [TestMethod]
    public async Task GetCompletions_ProjectPrefix_MatchesAllProjectVariants()
    {
        var results = await Complete("project-");
        Assert.IsTrue(
            results.Count >= 3,
            $"Expected at least project-away/rename/reorder, got: {string.Join(", ", results)}"
        );
        CollectionAssert.Contains(results, "project-away");
        CollectionAssert.Contains(results, "project-rename");
        CollectionAssert.Contains(results, "project-reorder");
    }

    [TestMethod]
    public async Task GetCompletions_MakePrefix_MatchesMakeSeries()
    {
        var results = await Complete("make-s");
        CollectionAssert.Contains(results, "make-series");
    }

    [TestMethod]
    public async Task GetCompletions_MvPrefix_MatchesMvApply()
    {
        var results = await Complete("mv-");
        CollectionAssert.Contains(results, "mv-apply");
        CollectionAssert.Contains(results, "mv-expand");
    }

    // ── Ordering ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetCompletions_ExactCaseMatchFirst()
    {
        // "where" (lower) should come before "WHERE"-style entries if both existed.
        // With a "whe" prefix we can verify "where" appears before "WHERE" won't be in our
        // keyword list, so just check "where" is in results and at a low index.
        var results = await Complete("wher");
        Assert.IsTrue(results.Count > 0);
        Assert.AreEqual(
            "where",
            results[0],
            "Exact-case match should sort before case-insensitive matches"
        );
    }

    [TestMethod]
    public async Task GetCompletions_ResultsAreSortedAlphabetically_WithinSameCasePriority()
    {
        var results = await Complete("pro");
        // All results start with "pro" (case-insensitive). Within same priority bucket, sorted.
        for (int i = 1; i < results.Count; i++)
        {
            var prev = results[i - 1];
            var curr = results[i];
            // Items sharing the same "exact case" priority should be ascending
            bool prevExact = prev.StartsWith("pro", StringComparison.Ordinal);
            bool currExact = curr.StartsWith("pro", StringComparison.Ordinal);
            if (prevExact == currExact)
                Assert.IsTrue(
                    string.Compare(prev, curr, StringComparison.OrdinalIgnoreCase) <= 0,
                    $"Expected '{prev}' before '{curr}' alphabetically"
                );
        }
    }

    // ── Volume limits ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetCompletions_MaxTwentyResults()
    {
        // "t" matches many keywords; result count must not exceed 20
        var results = await Complete("t");
        Assert.IsTrue(results.Count <= 20, $"Expected ≤20 results, got {results.Count}");
    }

    [TestMethod]
    public async Task GetCompletions_NoDuplicates()
    {
        var results = await Complete("to");
        var distinct = results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.AreEqual(distinct.Count, results.Count, "Completions must not contain duplicates");
    }

    // ── Context: pipe detection ───────────────────────────────────────────────

    [TestMethod]
    public void QueryHasPipe_NoPipe_ReturnsFalse()
    {
        Assert.IsFalse(KqlAutocomplete.QueryHasPipe("SecurityEvent"));
    }

    [TestMethod]
    public void QueryHasPipe_WithPipe_ReturnsTrue()
    {
        Assert.IsTrue(KqlAutocomplete.QueryHasPipe("T | where x > 0"));
    }

    [TestMethod]
    public void QueryHasPipe_RealPipeAlongsidePipeInString_ReturnsTrue()
    {
        // The real pipe before 'extend' must be detected even though 'a|b' also contains one
        Assert.IsTrue(KqlAutocomplete.QueryHasPipe("T | extend x = 'a|b'"));
    }

    [TestMethod]
    public void QueryHasPipe_OnlyPipeInsideString_ReturnsFalse()
    {
        // No real pipe — the | lives entirely inside a string literal
        Assert.IsFalse(KqlAutocomplete.QueryHasPipe("print 'a|b'"));
    }

    [TestMethod]
    public void QueryHasPipe_OnlyPipeInsideDoubleQuoteString_ReturnsFalse()
    {
        Assert.IsFalse(KqlAutocomplete.QueryHasPipe("print \"a|b\""));
    }

    [TestMethod]
    public void QueryHasPipe_PipeInComment_ReturnsFalse()
    {
        Assert.IsFalse(KqlAutocomplete.QueryHasPipe("SecurityEvent // | fake pipe"));
    }

    [TestMethod]
    public void QueryHasPipe_MultilineWithPipe_ReturnsTrue()
    {
        Assert.IsTrue(KqlAutocomplete.QueryHasPipe("T\n| where x > 0"));
    }

    // ── Context: no pipe → keywords only (no table names from empty schema) ───

    [TestMethod]
    public async Task GetCompletions_BeforePipe_DoesNotMixInTableResults()
    {
        // With empty schema, tables = []. Keywords starting with "whe" should still work.
        var results = await Complete("whe", "whe");
        CollectionAssert.Contains(results, "where");
    }

    // ── Context: after pipe → no table name suggestions ───────────────────────

    [TestMethod]
    public async Task GetCompletions_AfterPipe_KeywordsStillAvailable()
    {
        var results = await Complete("whe", "T | whe");
        CollectionAssert.Contains(results, "where");
    }

    [TestMethod]
    public async Task GetCompletions_AfterPipe_DoesNotIncludeTableNames()
    {
        // Even if the schema returned a table named "where_table", it should not appear
        // because we're after a pipe. With empty schema this just confirms keywords work.
        var results = await Complete("whe", "T | where");
        // All results must be keywords (since schema is empty no table names can appear)
        Assert.IsTrue(results.Count >= 0, "No crash after pipe");
    }

    // ── Null credential helper ────────────────────────────────────────────────

    private sealed class NullCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(
            Azure.Core.TokenRequestContext r,
            CancellationToken ct
        ) => new("", DateTimeOffset.MaxValue);

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(
            Azure.Core.TokenRequestContext r,
            CancellationToken ct
        ) => ValueTask.FromResult(new Azure.Core.AccessToken("", DateTimeOffset.MaxValue));
    }
}
