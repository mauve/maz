namespace Console.Cli.Commands.Copy;

/// <summary>Direction of a transfer operation.</summary>
public enum TransferDirection
{
    Upload,
    Download,
    ServerSideCopy,
    ClientSideCopy,
}

/// <summary>Status of an individual transfer.</summary>
public enum TransferStatus
{
    Queued,
    InProgress,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>A single item to transfer.</summary>
public sealed record TransferItem(
    string SourcePath,
    string DestPath,
    long Size,
    DateTimeOffset? LastModified,
    TransferDirection Direction,
    string? SourceAccountName = null,
    string? SourceContainerName = null,
    string? ContentType = null,
    string? SourceGroup = null
);

/// <summary>Progress event emitted by the transfer engine.</summary>
public sealed record TransferProgressEvent(
    int TransferIndex,
    long BytesTransferred,
    long TotalBytes,
    TransferStatus Status,
    string? Error
);
