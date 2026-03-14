using System.ComponentModel;

namespace Console.Cli;

public enum OutputFormat
{
    [Description("json")]
    Json,

    [Description("table")]
    Table,

    [Description("column")]
    Column,
}
