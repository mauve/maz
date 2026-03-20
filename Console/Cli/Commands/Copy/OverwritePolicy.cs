using System.ComponentModel;

namespace Console.Cli.Commands.Copy;

/// <summary>Policy for handling existing destination blobs/files.</summary>
public enum OverwritePolicy
{
    /// <summary>Skip if destination exists (default).</summary>
    [Description("skip")]
    Skip,

    /// <summary>Always overwrite.</summary>
    [Description("overwrite")]
    Overwrite,

    /// <summary>Overwrite only if source is newer.</summary>
    [Description("newer")]
    Newer,

    /// <summary>Prompt the user for each conflict (interactive only).</summary>
    [Description("confirm")]
    Confirm,
}
