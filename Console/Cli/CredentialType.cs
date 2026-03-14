using System.ComponentModel;

namespace Console.Cli;

public enum CredentialType
{
    [Description("cli")]
    Cli,

    [Description("dev")]
    Dev,

    [Description("ps")]
    PowerShell,

    [Description("env")]
    Env,

    [Description("mi")]
    ManagedIdentity,

    [Description("browser")]
    Browser,

    [Description("vs")]
    VisualStudio,

    [Description("shared")]
    SharedTokenCache,

    [Description("devicecode")]
    DeviceCode,

    [Description("wid")]
    WorkloadIdentity,
}
