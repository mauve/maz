using Console.Cli.Http;
using Console.Rendering;

namespace Console.Tui;

/// <summary>
/// Tree pane for browsing blob storage with virtual folder hierarchy.
/// Supports containers as top-level nodes, lazy-loaded folders, blob selection,
/// and glob/tag filtering.
/// </summary>
internal sealed class BlobTreePane
{
    private readonly BlobRestClient _client;
    private readonly string _account;
    private readonly string? _initialContainer;
    private readonly string? _initialPrefix;

    // ── Tree state ──────────────────────────────────────────────────────

    private readonly List<TreeNode> _roots = [];
    private readonly HashSet<string> _selectedBlobs = new(StringComparer.Ordinal);

    // Pending async loads: key = node path, value = task that populates children
    private readonly Dictionary<string, Task> _pendingLoads = new(StringComparer.Ordinal);

    // ── Visual state ────────────────────────────────────────────────────

    private int _throbberFrame;

    private List<VisualRow> _rows = [];
    private int _selectedIndex;
    private int _scrollOffset;
    private int _lastRenderHeight;

    // ── Counts ──────────────────────────────────────────────────────────

    public int SelectedCount => _selectedBlobs.Count;
    public int TotalBlobCount { get; private set; }
    public int ScannedBlobCount { get; private set; }
    public IReadOnlySet<string> SelectedBlobs => _selectedBlobs;
    public bool IsFilterLoading { get; set; }

    // ── Node types ──────────────────────────────────────────────────────

    private abstract class TreeNode
    {
        public required string Name { get; init; }
        public required string FullPath { get; init; }
    }

    private sealed class ContainerNode : TreeNode
    {
        public bool IsExpanded { get; set; }
        public bool IsLoaded { get; set; }
        public List<TreeNode> Children { get; } = [];
    }

    private sealed class FolderNode : TreeNode
    {
        public required string Container { get; init; }
        public bool IsExpanded { get; set; }
        public bool IsLoaded { get; set; }
        public List<TreeNode> Children { get; } = [];
        public int BlobCount { get; set; }
        public long TotalSize { get; set; }
    }

    private sealed class BlobNode : TreeNode
    {
        public required string Container { get; init; }
        public required BlobItem Blob { get; init; }
    }

    // ── Visual row types ────────────────────────────────────────────────

    private abstract record VisualRow;
    private sealed record ContainerRow(ContainerNode Node, int Depth) : VisualRow;
    private sealed record FolderRow(FolderNode Node, int Depth) : VisualRow;
    private sealed record BlobRow(BlobNode Node, int Depth) : VisualRow;
    private sealed record LoadingRow(int Depth) : VisualRow;

    // ── Constructor ─────────────────────────────────────────────────────

    public BlobTreePane(
        BlobRestClient client,
        string account,
        string? container,
        string? prefix
    )
    {
        _client = client;
        _account = account;
        _initialContainer = container;
        _initialPrefix = prefix;
    }

    /// <summary>Kicks off the initial load (containers or blobs).</summary>
    public void StartInitialLoad(CancellationToken ct)
    {
        if (_initialContainer is null)
        {
            // Account-only: load containers
            _pendingLoads["__containers__"] = LoadContainersAsync(ct);
        }
        else
        {
            // Container specified: create a container node and auto-expand it
            var containerNode = new ContainerNode
            {
                Name = _initialContainer,
                FullPath = _initialContainer,
                IsExpanded = true,
            };
            _roots.Add(containerNode);

            var loadPrefix = _initialPrefix is not null ? _initialPrefix + "/" : null;
            _pendingLoads[containerNode.FullPath] = LoadFolderChildrenAsync(
                containerNode,
                _initialContainer,
                loadPrefix,
                ct
            );
            Rebuild();
        }
    }

    // ── Async loading ───────────────────────────────────────────────────

    private async Task LoadContainersAsync(CancellationToken ct)
    {
        var containers = new List<ContainerNode>();
        await foreach (var item in _client.ListContainersAsync(_account, ct))
        {
            containers.Add(
                new ContainerNode { Name = item.Name, FullPath = item.Name }
            );
        }
        containers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _roots.Clear();
        _roots.AddRange(containers);
    }

    private async Task LoadFolderChildrenAsync(
        ContainerNode container,
        string containerName,
        string? prefix,
        CancellationToken ct
    )
    {
        var children = new List<TreeNode>();
        int blobCount = 0;
        long totalSize = 0;

        await foreach (var item in _client.ListBlobsByHierarchyAsync(_account, containerName, prefix, ct))
        {
            if (item.IsPrefix)
            {
                // Virtual folder — strip trailing slash for display name
                var folderPath = item.Name.TrimEnd('/');
                var displayName = folderPath;
                if (prefix is not null && displayName.StartsWith(prefix, StringComparison.Ordinal))
                    displayName = displayName[prefix.Length..];
                displayName = displayName.TrimEnd('/');

                children.Add(new FolderNode
                {
                    Name = displayName,
                    FullPath = item.Name,
                    Container = containerName,
                });
            }
            else if (item.Blob is not null)
            {
                var displayName = item.Blob.Name;
                if (prefix is not null && displayName.StartsWith(prefix, StringComparison.Ordinal))
                    displayName = displayName[prefix.Length..];

                children.Add(new BlobNode
                {
                    Name = displayName,
                    FullPath = item.Blob.Name,
                    Container = containerName,
                    Blob = item.Blob,
                });
                blobCount++;
                totalSize += item.Blob.Size;
            }
        }

        children.Sort((a, b) =>
        {
            // Folders first, then blobs, both alphabetical
            var aIsFolder = a is FolderNode;
            var bIsFolder = b is FolderNode;
            if (aIsFolder != bIsFolder)
                return aIsFolder ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        container.Children.Clear();
        container.Children.AddRange(children);
        container.IsLoaded = true;
        TotalBlobCount += blobCount;
    }

    private async Task LoadSubfolderChildrenAsync(
        FolderNode folder,
        CancellationToken ct
    )
    {
        var prefix = folder.FullPath;
        if (!prefix.EndsWith('/'))
            prefix += "/";

        var children = new List<TreeNode>();
        int blobCount = 0;
        long totalSize = 0;

        await foreach (var item in _client.ListBlobsByHierarchyAsync(_account, folder.Container, prefix, ct))
        {
            if (item.IsPrefix)
            {
                var folderPath = item.Name.TrimEnd('/');
                var displayName = folderPath;
                if (displayName.StartsWith(prefix, StringComparison.Ordinal))
                    displayName = displayName[prefix.Length..];
                displayName = displayName.TrimEnd('/');

                children.Add(new FolderNode
                {
                    Name = displayName,
                    FullPath = item.Name,
                    Container = folder.Container,
                });
            }
            else if (item.Blob is not null)
            {
                var displayName = item.Blob.Name;
                if (displayName.StartsWith(prefix, StringComparison.Ordinal))
                    displayName = displayName[prefix.Length..];

                children.Add(new BlobNode
                {
                    Name = displayName,
                    FullPath = item.Blob.Name,
                    Container = folder.Container,
                    Blob = item.Blob,
                });
                blobCount++;
                totalSize += item.Blob.Size;
            }
        }

        children.Sort((a, b) =>
        {
            var aIsFolder = a is FolderNode;
            var bIsFolder = b is FolderNode;
            if (aIsFolder != bIsFolder)
                return aIsFolder ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        folder.Children.Clear();
        folder.Children.AddRange(children);
        folder.IsLoaded = true;
        folder.BlobCount = blobCount;
        folder.TotalSize = totalSize;
        TotalBlobCount += blobCount;
    }

    /// <summary>
    /// Drains completed async loads. Returns true if any completed (caller should redraw).
    /// </summary>
    public bool DrainLoads()
    {
        if (_pendingLoads.Count == 0)
            return false;

        var completed = _pendingLoads
            .Where(kv => kv.Value.IsCompleted)
            .Select(kv => kv.Key)
            .ToList();

        if (completed.Count == 0)
            return false;

        foreach (var key in completed)
            _pendingLoads.Remove(key);

        Rebuild();
        return true;
    }

    public bool HasPendingLoads => _pendingLoads.Count > 0;

    // ── Navigation ──────────────────────────────────────────────────────

    public void MoveUp()
    {
        for (int i = _selectedIndex - 1; i >= 0; i--)
            if (IsSelectable(_rows[i]))
            {
                _selectedIndex = i;
                ClampScroll();
                return;
            }
    }

    public void MoveDown()
    {
        for (int i = _selectedIndex + 1; i < _rows.Count; i++)
            if (IsSelectable(_rows[i]))
            {
                _selectedIndex = i;
                ClampScroll();
                return;
            }
    }

    public void PageUp()
    {
        var pageSize = Math.Max(1, _lastRenderHeight - 3);
        for (int n = 0; n < pageSize; n++)
            MoveUp();
    }

    public void PageDown()
    {
        var pageSize = Math.Max(1, _lastRenderHeight - 3);
        for (int n = 0; n < pageSize; n++)
            MoveDown();
    }

    public void ExpandSelected(CancellationToken ct)
    {
        if (_selectedIndex >= _rows.Count)
            return;

        switch (_rows[_selectedIndex])
        {
            case ContainerRow { Node.IsExpanded: false } cr:
                cr.Node.IsExpanded = true;
                if (!cr.Node.IsLoaded && !_pendingLoads.ContainsKey(cr.Node.FullPath))
                    _pendingLoads[cr.Node.FullPath] = LoadFolderChildrenAsync(
                        cr.Node,
                        cr.Node.Name,
                        null,
                        ct
                    );
                Rebuild();
                break;
            case FolderRow { Node.IsExpanded: false } fr:
                fr.Node.IsExpanded = true;
                if (!fr.Node.IsLoaded && !_pendingLoads.ContainsKey(fr.Node.FullPath))
                    _pendingLoads[fr.Node.FullPath] = LoadSubfolderChildrenAsync(fr.Node, ct);
                Rebuild();
                break;
        }
    }

    public void CollapseSelected()
    {
        if (_selectedIndex >= _rows.Count)
            return;

        switch (_rows[_selectedIndex])
        {
            case ContainerRow { Node.IsExpanded: true } cr:
                cr.Node.IsExpanded = false;
                Rebuild();
                break;
            case FolderRow { Node.IsExpanded: true } fr:
                fr.Node.IsExpanded = false;
                Rebuild();
                break;
            case BlobRow:
            case FolderRow { Node.IsExpanded: false }:
                // Move to parent folder/container
                for (int i = _selectedIndex - 1; i >= 0; i--)
                {
                    if (_rows[i] is ContainerRow or FolderRow { Node.IsExpanded: true })
                    {
                        _selectedIndex = i;
                        ClampScroll();
                        return;
                    }
                }
                break;
        }
    }

    public void ToggleOrExpand(CancellationToken ct)
    {
        if (_selectedIndex >= _rows.Count)
            return;

        switch (_rows[_selectedIndex])
        {
            case ContainerRow cr:
                if (cr.Node.IsExpanded)
                    cr.Node.IsExpanded = false;
                else
                {
                    cr.Node.IsExpanded = true;
                    if (!cr.Node.IsLoaded && !_pendingLoads.ContainsKey(cr.Node.FullPath))
                        _pendingLoads[cr.Node.FullPath] = LoadFolderChildrenAsync(
                            cr.Node,
                            cr.Node.Name,
                            null,
                            ct
                        );
                }
                Rebuild();
                break;
            case FolderRow fr:
                if (fr.Node.IsExpanded)
                    fr.Node.IsExpanded = false;
                else
                {
                    fr.Node.IsExpanded = true;
                    if (!fr.Node.IsLoaded && !_pendingLoads.ContainsKey(fr.Node.FullPath))
                        _pendingLoads[fr.Node.FullPath] = LoadSubfolderChildrenAsync(fr.Node, ct);
                }
                Rebuild();
                break;
        }
    }

    // ── Selection ───────────────────────────────────────────────────────

    public void ToggleSelection()
    {
        if (_selectedIndex >= _rows.Count)
            return;

        switch (_rows[_selectedIndex])
        {
            case BlobRow br:
                var key = $"{br.Node.Container}/{br.Node.FullPath}";
                if (!_selectedBlobs.Remove(key))
                    _selectedBlobs.Add(key);
                break;
            case ContainerRow cr when cr.Node.IsLoaded:
                ToggleAllDescendants(cr.Node.Children, cr.Node.Name);
                break;
            case FolderRow fr when fr.Node.IsLoaded:
                ToggleAllDescendants(fr.Node.Children, fr.Node.Container);
                break;
        }
    }

    private void ToggleAllDescendants(List<TreeNode> children, string container)
    {
        var blobs = CollectDescendantBlobs(children, container);
        // If any unselected, select all; otherwise deselect all
        bool anyUnselected = blobs.Any(b => !_selectedBlobs.Contains(b));
        foreach (var b in blobs)
        {
            if (anyUnselected)
                _selectedBlobs.Add(b);
            else
                _selectedBlobs.Remove(b);
        }
    }

    private static List<string> CollectDescendantBlobs(List<TreeNode> children, string container)
    {
        var result = new List<string>();
        foreach (var child in children)
        {
            if (child is BlobNode bn)
                result.Add($"{container}/{bn.FullPath}");
            else if (child is FolderNode fn)
                result.AddRange(CollectDescendantBlobs(fn.Children, container));
        }
        return result;
    }

    public void SelectAll()
    {
        var allBlobs = new List<string>();
        foreach (var row in _rows)
        {
            if (row is BlobRow br)
                allBlobs.Add($"{br.Node.Container}/{br.Node.FullPath}");
        }

        bool anyUnselected = allBlobs.Any(b => !_selectedBlobs.Contains(b));
        foreach (var b in allBlobs)
        {
            if (anyUnselected)
                _selectedBlobs.Add(b);
            else
                _selectedBlobs.Remove(b);
        }
    }

    /// <summary>Get account, container, and blob item for all selected blobs.</summary>
    public List<(string Account, string Container, BlobItem Blob)> GetSelectedBlobInfo()
    {
        var result = new List<(string, string, BlobItem)>();
        void Walk(List<TreeNode> nodes, string container)
        {
            foreach (var node in nodes)
            {
                if (node is BlobNode bn && _selectedBlobs.Contains($"{container}/{bn.FullPath}"))
                    result.Add((_account, container, bn.Blob));
                else if (node is FolderNode fn)
                    Walk(fn.Children, container);
                else if (node is ContainerNode cn)
                    Walk(cn.Children, cn.Name);
            }
        }
        Walk(_roots, _initialContainer ?? "");
        return result;
    }

    /// <summary>Get info about the currently focused blob (for single-item actions).</summary>
    public (string Account, string Container, BlobItem Blob)? GetFocusedBlobInfo()
    {
        if (_selectedIndex >= _rows.Count)
            return null;
        if (_rows[_selectedIndex] is BlobRow br)
            return (_account, br.Node.Container, br.Node.Blob);
        return null;
    }

    // ── Flat-list loading for filter mode ───────────────────────────────

    /// <summary>Replace the tree with a flat list of blobs from a filtered search.</summary>
    public async Task LoadFilteredAsync(
        string? container,
        string? prefix,
        Cli.Commands.Copy.GlobMatcher? glob,
        CancellationToken ct
    )
    {
        _pendingLoads.Clear();
        _roots.Clear();
        TotalBlobCount = 0;
        ScannedBlobCount = 0;

        if (container is null)
        {
            // Account-level: iterate all containers
            await foreach (var c in _client.ListContainersAsync(_account, ct))
            {
                await LoadFilteredContainer(c.Name, null, glob, ct);
            }
        }
        else
        {
            await LoadFilteredContainer(container, prefix, glob, ct);
        }
        Rebuild();
    }

    private async Task LoadFilteredContainer(
        string containerName,
        string? prefix,
        Cli.Commands.Copy.GlobMatcher? glob,
        CancellationToken ct
    )
    {
        var containerNode = new ContainerNode
        {
            Name = containerName,
            FullPath = containerName,
            IsExpanded = true,
            IsLoaded = true,
        };

        // Build a tree from flat blob list
        var folderMap = new Dictionary<string, FolderNode>(StringComparer.Ordinal);

        await foreach (var blob in _client.ListBlobsAsync(_account, containerName, prefix, ct))
        {
            ScannedBlobCount++;

            var relativeName = prefix is not null && blob.Name.StartsWith(prefix, StringComparison.Ordinal)
                ? blob.Name[prefix.Length..]
                : blob.Name;

            if (glob is not null && !glob.IsMatch(relativeName))
                continue;

            var blobNode = new BlobNode
            {
                Name = relativeName.Contains('/')
                    ? relativeName[(relativeName.LastIndexOf('/') + 1)..]
                    : relativeName,
                FullPath = blob.Name,
                Container = containerName,
                Blob = blob,
            };

            // Find or create parent folders
            var segments = relativeName.Split('/');
            if (segments.Length > 1)
            {
                var parentList = containerNode.Children;
                var pathSoFar = prefix ?? "";
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    pathSoFar += segments[i] + "/";
                    if (!folderMap.TryGetValue(pathSoFar, out var folder))
                    {
                        folder = new FolderNode
                        {
                            Name = segments[i],
                            FullPath = pathSoFar,
                            Container = containerName,
                            IsExpanded = true,
                            IsLoaded = true,
                        };
                        folderMap[pathSoFar] = folder;
                        parentList.Add(folder);
                    }
                    parentList = folder.Children;
                    folder.BlobCount++;
                    folder.TotalSize += blob.Size;
                }
                parentList.Add(blobNode);
            }
            else
            {
                containerNode.Children.Add(blobNode);
            }

            TotalBlobCount++;
        }

        if (containerNode.Children.Count > 0)
            _roots.Add(containerNode);
    }

    /// <summary>Replace the tree with blobs found by tag query.</summary>
    public async Task LoadByTagQueryAsync(
        string? container,
        string tagQuery,
        Cli.Commands.Copy.GlobMatcher? glob,
        CancellationToken ct
    )
    {
        _pendingLoads.Clear();
        _roots.Clear();
        TotalBlobCount = 0;
        ScannedBlobCount = 0;

        if (container is null)
            throw new InvalidOperationException("Tag queries require a container.");

        var containerNode = new ContainerNode
        {
            Name = container,
            FullPath = container,
            IsExpanded = true,
            IsLoaded = true,
        };

        await foreach (var tagItem in _client.FindBlobsByTagsAsync(_account, container, tagQuery, ct))
        {
            if (glob is not null && !glob.IsMatch(tagItem.Name))
                continue;

            var displayName = tagItem.Name.Contains('/')
                ? tagItem.Name[(tagItem.Name.LastIndexOf('/') + 1)..]
                : tagItem.Name;

            containerNode.Children.Add(new BlobNode
            {
                Name = displayName,
                FullPath = tagItem.Name,
                Container = container,
                // Tag queries don't return size/date — show placeholder
                Blob = new BlobItem(tagItem.Name, 0, null, null),
            });
            TotalBlobCount++;
        }

        if (containerNode.Children.Count > 0)
            _roots.Add(containerNode);

        Rebuild();
    }

    /// <summary>Reload the tree in hierarchy mode (clearing any filter).</summary>
    public void ReloadHierarchy(CancellationToken ct)
    {
        _roots.Clear();
        _pendingLoads.Clear();
        TotalBlobCount = 0;
        ScannedBlobCount = 0;
        StartInitialLoad(ct);
    }

    /// <summary>Remove blobs from the tree by their full key (container/blobName).</summary>
    public void RemoveBlobs(IReadOnlySet<string> keys)
    {
        void Walk(List<TreeNode> nodes, string container)
        {
            nodes.RemoveAll(n =>
                n is BlobNode bn && keys.Contains($"{container}/{bn.FullPath}"));
            foreach (var node in nodes)
            {
                if (node is FolderNode fn)
                    Walk(fn.Children, container);
                else if (node is ContainerNode cn)
                    Walk(cn.Children, cn.Name);
            }
        }
        Walk(_roots, _initialContainer ?? "");
        foreach (var key in keys)
        {
            _selectedBlobs.Remove(key);
            TotalBlobCount--;
        }
        TotalBlobCount = Math.Max(0, TotalBlobCount);
        Rebuild();
    }

    // ── Visual tree rebuild ─────────────────────────────────────────────

    private void Rebuild()
    {
        _rows = [];
        foreach (var root in _roots)
            FlattenNode(root, 0);
        ClampSelection();
    }

    private void FlattenNode(TreeNode node, int depth)
    {
        switch (node)
        {
            case ContainerNode cn:
                _rows.Add(new ContainerRow(cn, depth));
                if (cn.IsExpanded)
                {
                    if (!cn.IsLoaded && _pendingLoads.ContainsKey(cn.FullPath))
                        _rows.Add(new LoadingRow(depth + 1));
                    else
                        foreach (var child in cn.Children)
                            FlattenNode(child, depth + 1);
                }
                break;
            case FolderNode fn:
                _rows.Add(new FolderRow(fn, depth));
                if (fn.IsExpanded)
                {
                    if (!fn.IsLoaded && _pendingLoads.ContainsKey(fn.FullPath))
                        _rows.Add(new LoadingRow(depth + 1));
                    else
                        foreach (var child in fn.Children)
                            FlattenNode(child, depth + 1);
                }
                break;
            case BlobNode bn:
                _rows.Add(new BlobRow(bn, depth));
                break;
        }
    }

    private static bool IsSelectable(VisualRow row) =>
        row is ContainerRow or FolderRow or BlobRow;

    private void ClampSelection()
    {
        if (_rows.Count == 0)
        {
            _selectedIndex = 0;
            return;
        }
        _selectedIndex = Math.Clamp(_selectedIndex, 0, _rows.Count - 1);
        if (!IsSelectable(_rows[_selectedIndex]))
        {
            for (int i = _selectedIndex + 1; i < _rows.Count; i++)
                if (IsSelectable(_rows[i]))
                {
                    _selectedIndex = i;
                    return;
                }
            for (int i = _selectedIndex - 1; i >= 0; i--)
                if (IsSelectable(_rows[i]))
                {
                    _selectedIndex = i;
                    return;
                }
        }
    }

    private void ClampScroll()
    {
        int rows = Math.Max(1, _lastRenderHeight - 3);
        if (_selectedIndex < _scrollOffset)
            _scrollOffset = _selectedIndex;
        if (_selectedIndex >= _scrollOffset + rows)
            _scrollOffset = _selectedIndex - rows + 1;
        _scrollOffset = Math.Max(0, _scrollOffset);
    }

    // ── Rendering ───────────────────────────────────────────────────────

    public void Render(int top, int left, int width, int height, bool focused)
    {
        _lastRenderHeight = height;
        if (height < 2 || width < 10)
            return;

        int displayRows = height - 1; // reserve 1 for title
        int row = 0;

        // Title bar
        MoveTo(top, left);
        var title = $" {_account}";
        if (_initialContainer is not null)
            title += $"/{_initialContainer}";
        if (_initialPrefix is not null)
            title += $"/{_initialPrefix}";
        WriteCell(Ansi.Bold(title), width);

        // Show throbber when loading (filter or initial)
        if (IsFilterLoading || (_rows.Count == 0 && HasPendingLoads))
        {
            _throbberFrame++;
            var frame = Ansi.ThrobberFrames[_throbberFrame % Ansi.ThrobberFrames.Length];
            var loadMsg = IsFilterLoading ? "Searching…" : "Loading…";
            MoveTo(top + 1, left);
            WriteCell(Ansi.Dim($"  {frame} {loadMsg}"), width);
            row++;
        }
        else
        {
            for (int i = _scrollOffset; i < _rows.Count && row < displayRows; i++, row++)
            {
                MoveTo(top + 1 + row, left);
                RenderRow(_rows[i], i == _selectedIndex && focused, width);
            }
        }

        // Clear remaining rows
        while (row < displayRows)
        {
            MoveTo(top + 1 + row++, left);
            System.Console.Write(new string(' ', width));
        }
    }

    private void RenderRow(VisualRow row, bool selected, int width)
    {
        switch (row)
        {
            case ContainerRow cr:
            {
                var indent = new string(' ', cr.Depth * 2);
                var arrow = cr.Node.IsExpanded ? "▼ " : "▶ ";
                var name = cr.Node.Name + "/";
                var line = $" {indent}{arrow}{name}";

                // Right-side info
                var info = "";
                if (cr.Node.IsLoaded)
                    info = $"  {cr.Node.Children.Count} items";

                var maxLine = width - Ansi.VisibleLength(info);
                if (Ansi.VisibleLength(line) > maxLine)
                    line = line[..Math.Max(0, maxLine)];

                var full = line + new string(' ', Math.Max(0, maxLine - Ansi.VisibleLength(line))) + info;
                WriteCell(
                    selected ? Ansi.Color(full, "\x1b[1;7m") : Ansi.Bold(full),
                    width
                );
                break;
            }
            case FolderRow fr:
            {
                var indent = new string(' ', fr.Depth * 2);
                var arrow = fr.Node.IsExpanded ? "▼ " : "▶ ";
                var name = fr.Node.Name + "/";
                var line = $" {indent}{arrow}{name}";

                var info = "";
                if (fr.Node.IsLoaded && fr.Node.BlobCount > 0)
                    info = $"  {fr.Node.BlobCount} items  {FormatSize(fr.Node.TotalSize)}";

                var maxLine = width - Ansi.VisibleLength(info);
                if (Ansi.VisibleLength(line) > maxLine)
                    line = line[..Math.Max(0, maxLine)];

                var full = line + new string(' ', Math.Max(0, maxLine - Ansi.VisibleLength(line))) + info;
                WriteCell(selected ? Ansi.Color(full, "\x1b[7m") : full, width);
                break;
            }
            case BlobRow br:
            {
                var indent = new string(' ', br.Depth * 2);
                var key = $"{br.Node.Container}/{br.Node.FullPath}";
                var isChecked = _selectedBlobs.Contains(key);
                var checkbox = isChecked ? "[x] " : "[ ] ";
                var name = br.Node.Name;
                var line = $" {indent}{checkbox}{name}";

                // Right-side: size + date
                var sizeStr = FormatSize(br.Node.Blob.Size);
                var dateStr = br.Node.Blob.LastModified?.ToString("yyyy-MM-dd") ?? "";
                var info = $"  {sizeStr,10}  {dateStr}";

                var maxLine = width - Ansi.VisibleLength(info);
                if (Ansi.VisibleLength(line) > maxLine && maxLine > 5)
                    line = line[..Math.Max(0, maxLine)];

                var full = line + new string(' ', Math.Max(0, maxLine - Ansi.VisibleLength(line))) + info;
                if (isChecked)
                    WriteCell(
                        selected ? Ansi.Color(full, "\x1b[1;7m") : Ansi.Bold(full),
                        width
                    );
                else
                    WriteCell(selected ? Ansi.Color(full, "\x1b[7m") : full, width);
                break;
            }
            case LoadingRow lr:
            {
                var indent = new string(' ', lr.Depth * 2);
                WriteCell(Ansi.Dim($" {indent}  ⠋ loading…"), width);
                break;
            }
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "";
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F0} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private static void MoveTo(int row, int col) =>
        System.Console.Write($"\x1b[{row + 1};{col + 1}H");

    private static void WriteCell(string text, int width)
    {
        var vis = Ansi.VisibleLength(text);
        if (vis >= width)
            System.Console.Write(ResultsPane.TruncateAnsi(text, width));
        else
        {
            System.Console.Write(text);
            System.Console.Write(new string(' ', width - vis));
        }
    }
}
