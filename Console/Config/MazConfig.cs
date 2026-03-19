using System.Collections.Frozen;

namespace Console.Config;

/// <summary>
/// Represents the maz user configuration file and provides the singleton <see cref="Current"/> instance.
/// </summary>
public sealed class MazConfig
{
    /// <summary>The active configuration, populated by <see cref="Initialize"/>.</summary>
    public static MazConfig Current { get; private set; } = new();

    /// <summary>The resolved path to the config file (null if not found or bypassed).</summary>
    public static string? FilePath { get; private set; }

    // [suggestions]

    /// <summary>Only these subscriptions appear in --subscription-id completions. Empty = all.</summary>
    public IReadOnlyList<string> AllowedSubscriptions { get; init; } = [];

    /// <summary>Only these resource groups appear in --resource-group completions. Empty = all.</summary>
    public IReadOnlyList<string> AllowedResourceGroups { get; init; } = [];

    /// <summary>These resource IDs are never returned in any suggestion.</summary>
    public IReadOnlyList<string> DeniedResourceIds { get; init; } = [];

    // [disallow]

    /// <summary>These subscriptions are rejected even if explicitly specified on the CLI.</summary>
    public IReadOnlyList<string> DisallowedSubscriptions { get; init; } = [];

    /// <summary>These resource groups are rejected even if explicitly specified on the CLI.</summary>
    public IReadOnlyList<string> DisallowedResourceGroups { get; init; } = [];

    /// <summary>These resource IDs are rejected even if explicitly specified on the CLI.</summary>
    public IReadOnlyList<string> DisallowedResourceIds { get; init; } = [];

    // [global] — raw string values, keyed by option name without --

    /// <summary>Default values for global options, keyed by option name (without --).</summary>
    public IReadOnlyDictionary<string, string> GlobalDefaults { get; init; } =
        FrozenDictionary<string, string>.Empty;

    // [global] — typed convenience properties

    /// <summary>
    /// Default subscription ID from <c>[global] defaultSubscriptionId</c> (preferred)
    /// or backward-compatible <c>[global] subscription-id</c>.
    /// </summary>
    public string? DefaultSubscriptionId =>
        GlobalDefaults.TryGetValue("defaultSubscriptionId", out var v) ? v
        : GlobalDefaults.TryGetValue("subscription-id", out var v2) ? v2
        : null;

    /// <summary>
    /// Default resource group from <c>[global] defaultResourceGroup</c> (preferred)
    /// or backward-compatible <c>[global] resource-group</c>.
    /// </summary>
    public string? DefaultResourceGroup =>
        GlobalDefaults.TryGetValue("defaultResourceGroup", out var v) ? v
        : GlobalDefaults.TryGetValue("resource-group", out var v2) ? v2
        : null;

    /// <summary>
    /// Maximum bytes of request/response body to include in verbose output.
    /// From <c>[global] verbose-body-limit</c>. Default 8192.
    /// </summary>
    public int VerboseBodyLimit =>
        GlobalDefaults.TryGetValue("verbose-body-limit", out var v) && int.TryParse(v, out var n)
            ? n
            : 8192;

    /// <summary>
    /// Timestamp format for verbose output: <c>relative</c> (default) or <c>absolute</c>.
    /// From <c>[global] verbose-timestamp</c>.
    /// </summary>
    public string VerboseTimestamp =>
        GlobalDefaults.TryGetValue("verbose-timestamp", out var v) ? v : "relative";

    // [cmd.X] — command path → option name → value

    /// <summary>Per-command default values. Key is the full command path (e.g. "storage account list").</summary>
    public IReadOnlyDictionary<
        string,
        IReadOnlyDictionary<string, string>
    > CommandDefaults { get; init; } =
        FrozenDictionary<string, IReadOnlyDictionary<string, string>>.Empty;

    // [resolution.*] — CFG1: per-subscription scoping filters

    /// <summary>
    /// Per-subscription resource resolution filters (CFG1).
    /// When non-empty, subscription discovery is scoped to these subscriptions and optionally their resource groups.
    /// </summary>
    public IReadOnlyList<ResolutionFilterEntry> ResolutionFilter { get; init; } = [];

    /// <summary>
    /// Initializes <see cref="Current"/> from the config file.
    /// Call once at startup before building the command tree.
    /// Respects <c>MAZ_IGNORE_CONFIG_FILE=1</c> and <c>MAZ_CONFIG_PATH</c>.
    /// </summary>
    public static void Initialize()
    {
        var ignore = Environment.GetEnvironmentVariable("MAZ_IGNORE_CONFIG_FILE");
        if (ignore == "1" || string.Equals(ignore, "true", StringComparison.OrdinalIgnoreCase))
        {
            Current = new MazConfig();
            return;
        }

        var path = ResolveConfigPath();
        FilePath = path;

        if (!File.Exists(path))
        {
            Current = new MazConfig();
            return;
        }

        var sections = IniParser.Parse(File.ReadAllText(path));
        Current = FromSections(sections);
    }

    /// <summary>
    /// Resolves the path to the config file, honouring <c>MAZ_CONFIG_PATH</c> and platform defaults.
    /// </summary>
    public static string ResolveConfigPath()
    {
        var envPath = Environment.GetEnvironmentVariable("MAZ_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
            return envPath;

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "maz", "user-config.ini");
        }

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configBase = !string.IsNullOrWhiteSpace(xdgConfig)
            ? xdgConfig
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config"
            );

        return Path.Combine(configBase, "maz", "user-config.ini");
    }

    private static MazConfig FromSections(Dictionary<string, Dictionary<string, string>> sections)
    {
        static IReadOnlyList<string> ParseList(
            Dictionary<string, Dictionary<string, string>> s,
            string section,
            string key
        )
        {
            if (!s.TryGetValue(section, out var sec))
                return [];
            if (!sec.TryGetValue(key, out var val))
                return [];
            return val.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        var cmdDefaults = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase
        );

        var resolutionFilter = new List<ResolutionFilterEntry>();

        foreach (var (sectionName, sectionData) in sections)
        {
            if (sectionName.StartsWith("cmd.", StringComparison.OrdinalIgnoreCase))
            {
                var cmdPath = sectionName[4..].Trim();
                cmdDefaults[cmdPath] = sectionData.ToFrozenDictionary(
                    StringComparer.OrdinalIgnoreCase
                );
                continue;
            }

            if (sectionName.StartsWith("resolution.", StringComparison.OrdinalIgnoreCase))
            {
                var subId = sectionName[11..].Trim();
                if (string.IsNullOrWhiteSpace(subId))
                    continue;

                IReadOnlyList<string> rgs = [];
                if (sectionData.TryGetValue("resource-groups", out var rgVal))
                    rgs = rgVal.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

                resolutionFilter.Add(new ResolutionFilterEntry(subId, rgs));
            }
        }

        IReadOnlyDictionary<string, string> globalDefaults = sections.TryGetValue(
            "global",
            out var globalSec
        )
            ? globalSec.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase)
            : FrozenDictionary<string, string>.Empty;

        return new MazConfig
        {
            AllowedSubscriptions = ParseList(sections, "suggestions", "allowed-subscriptions"),
            AllowedResourceGroups = ParseList(sections, "suggestions", "allowed-resource-groups"),
            DeniedResourceIds = ParseList(sections, "suggestions", "denied-resource-ids"),
            DisallowedSubscriptions = ParseList(sections, "disallow", "subscriptions"),
            DisallowedResourceGroups = ParseList(sections, "disallow", "resource-groups"),
            DisallowedResourceIds = ParseList(sections, "disallow", "resource-ids"),
            GlobalDefaults = globalDefaults,
            CommandDefaults = cmdDefaults.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            ResolutionFilter = resolutionFilter,
        };
    }
}
