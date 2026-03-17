namespace Console.Cli.Shared;

/// <summary>Identifies where a resolved value originated.</summary>
public enum ValueSource
{
    /// <summary>Value was specified explicitly on the command line.</summary>
    Cli,

    /// <summary>Value came from an environment variable.</summary>
    Environment,

    /// <summary>Value came from the maz configuration file.</summary>
    Config,
}
