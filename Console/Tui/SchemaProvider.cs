using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Console.Cli.Http;

namespace Console.Tui;

/// <summary>
/// Provides Log Analytics workspace schema for tab completion, with session-level caching.
/// All failures return empty lists so the TUI remains usable even without schema access.
/// </summary>
internal sealed class SchemaProvider(
    LogsQueryClient client,
    string? workspaceId,
    string? resourceId,
    TokenCredential? credential = null,
    string? workspaceArmId = null
)
{
    private List<string>? _tablesCache;
    private readonly ConcurrentDictionary<string, List<ColumnInfo>> _columnsCache = new();

    public IReadOnlyList<string> GetCachedTables() => (IReadOnlyList<string>?)_tablesCache ?? [];

    public IReadOnlyList<ColumnInfo> GetCachedColumns(string tableName) =>
        _columnsCache.TryGetValue(tableName, out var cols) ? cols : [];

    public async Task<IReadOnlyList<string>> GetTablesAsync(CancellationToken ct = default)
    {
        if (_tablesCache is not null)
            return _tablesCache;

        // Preferred: ARM tables API lists every table regardless of plan (including DCE/DCR
        // custom tables on Basic/Auxiliary plans that never appear in the Usage table).
        // Falls back to KQL discovery when no ARM path is available (GUID-only workspace).
        var tables = await TryGetTablesViaArmAsync(ct);
        if (tables.Count == 0)
            tables = await TryGetTablesViaUsageAsync(ct);
        if (tables.Count == 0)
            tables = await TryGetTablesViaUnionAsync(ct);

        // Always cache — even an empty result — so repeated Tab presses don't re-run queries.
        _tablesCache = tables;
        return _tablesCache;
    }

    private async Task<List<string>> TryGetTablesViaArmAsync(CancellationToken ct)
    {
        if (credential is null || workspaceArmId is null)
            return [];
        try
        {
            var restClient = new AzureRestClient(credential, Cli.Shared.DiagnosticLog.Null);
            var json = await restClient.SendAsync(
                HttpMethod.Get,
                $"{workspaceArmId}/tables",
                "2023-09-01",
                null,
                ct
            );
            var tables = new List<string>();
            if (json?["value"] is JsonArray arr)
                foreach (var item in arr)
                {
                    var name = item?["name"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(name))
                        tables.Add(name);
                }
            tables.Sort(StringComparer.OrdinalIgnoreCase);
            return tables;
        }
        catch
        {
            return [];
        }
    }

    private async Task<List<string>> TryGetTablesViaUsageAsync(CancellationToken ct)
    {
        try
        {
            var result = await QueryAsync(
                "Usage | where TimeGenerated > ago(30d) | summarize by DataType | sort by DataType asc | limit 300",
                TimeSpan.FromSeconds(8),
                ct
            );
            var tables = new List<string>();
            int colIndex = 0;
            for (int i = 0; i < result.Table.Columns.Count; i++)
                if (result.Table.Columns[i].Name == "DataType")
                {
                    colIndex = i;
                    break;
                }
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
                ct
            );
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

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        string tableName,
        CancellationToken ct = default
    )
    {
        if (_columnsCache.TryGetValue(tableName, out var cached))
            return cached;

        try
        {
            var result = await QueryAsync($"{tableName} | getschema", TimeSpan.FromSeconds(8), ct);
            var columns = new List<ColumnInfo>();
            int colIndex = -1;
            int typeIndex = -1;
            for (int i = 0; i < result.Table.Columns.Count; i++)
            {
                if (result.Table.Columns[i].Name == "ColumnName")
                    colIndex = i;
                if (result.Table.Columns[i].Name == "ColumnType")
                    typeIndex = i;
            }
            if (colIndex >= 0)
                foreach (var row in result.Table.Rows)
                    if (row[colIndex] is string col && !string.IsNullOrEmpty(col))
                    {
                        var type = typeIndex >= 0 && row[typeIndex] is string t ? t : "";
                        columns.Add(new ColumnInfo(col, type));
                    }
            _columnsCache[tableName] = columns;
            return columns;
        }
        catch
        {
            return [];
        }
    }

    private async Task<LogsQueryResult> QueryAsync(
        string kql,
        TimeSpan timeout,
        CancellationToken ct
    )
    {
        var opts = new LogsQueryOptions { ServerTimeout = timeout };
        if (workspaceId is not null)
            return await client.QueryWorkspaceAsync(workspaceId, kql, QueryTimeRange.All, opts, ct);
        if (resourceId is not null)
            return await client.QueryResourceAsync(
                new ResourceIdentifier(resourceId),
                kql,
                QueryTimeRange.All,
                opts,
                ct
            );
        throw new InvalidOperationException("No workspace or resource ID configured.");
    }
}
