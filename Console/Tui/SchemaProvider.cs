using System.Collections.Concurrent;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;

namespace Console.Tui;

/// <summary>
/// Provides Log Analytics workspace schema for tab completion, with session-level caching.
/// All failures return empty lists so the TUI remains usable even without schema access.
/// </summary>
internal sealed class SchemaProvider(LogsQueryClient client, string? workspaceId, string? resourceId)
{
    private List<string>? _tablesCache;
    private readonly ConcurrentDictionary<string, List<string>> _columnsCache = new();

    public async Task<IReadOnlyList<string>> GetTablesAsync(CancellationToken ct = default)
    {
        if (_tablesCache is not null)
            return _tablesCache;

        // Fast path: Usage table lists every ingested DataType (= table name).
        // Falls back to union * for workspaces that have no recent billable data.
        var tables = await TryGetTablesViaUsageAsync(ct);
        if (tables.Count == 0)
            tables = await TryGetTablesViaUnionAsync(ct);

        // Always cache — even an empty result — so repeated Tab presses don't re-run queries.
        _tablesCache = tables;
        return _tablesCache;
    }

    private async Task<List<string>> TryGetTablesViaUsageAsync(CancellationToken ct)
    {
        try
        {
            var result = await QueryAsync(
                "Usage | where TimeGenerated > ago(30d) | summarize by DataType | sort by DataType asc | limit 300",
                TimeSpan.FromSeconds(8),
                ct);
            var tables = new List<string>();
            int colIndex = 0;
            for (int i = 0; i < result.Table.Columns.Count; i++)
                if (result.Table.Columns[i].Name == "DataType") { colIndex = i; break; }
            foreach (var row in result.Table.Rows)
                if (row[colIndex] is string name && !string.IsNullOrEmpty(name))
                    tables.Add(name);
            return tables;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<string>> TryGetTablesViaUnionAsync(CancellationToken ct)
    {
        try
        {
            var result = await QueryAsync(
                "union * | distinct $table | sort by $table asc | limit 300",
                TimeSpan.FromSeconds(15),
                ct);
            var tables = new List<string>();
            foreach (var row in result.Table.Rows)
                if (row[0] is string name && !string.IsNullOrEmpty(name))
                    tables.Add(name);
            return tables;
        }
        catch
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> GetColumnsAsync(string tableName, CancellationToken ct = default)
    {
        if (_columnsCache.TryGetValue(tableName, out var cached))
            return cached;

        try
        {
            var result = await QueryAsync($"{tableName} | getschema", TimeSpan.FromSeconds(8), ct);
            var columns = new List<string>();
            int colIndex = -1;
            for (int i = 0; i < result.Table.Columns.Count; i++)
                if (result.Table.Columns[i].Name == "ColumnName") { colIndex = i; break; }
            if (colIndex >= 0)
                foreach (var row in result.Table.Rows)
                    if (row[colIndex] is string col && !string.IsNullOrEmpty(col))
                        columns.Add(col);
            _columnsCache[tableName] = columns;
            return columns;
        }
        catch
        {
            return [];
        }
    }

    private async Task<LogsQueryResult> QueryAsync(string kql, TimeSpan timeout, CancellationToken ct)
    {
        var opts = new LogsQueryOptions { ServerTimeout = timeout };
        if (workspaceId is not null)
            return await client.QueryWorkspaceAsync(workspaceId, kql, QueryTimeRange.All, opts, ct);
        if (resourceId is not null)
            return await client.QueryResourceAsync(new ResourceIdentifier(resourceId), kql, QueryTimeRange.All, opts, ct);
        throw new InvalidOperationException("No workspace or resource ID configured.");
    }
}
