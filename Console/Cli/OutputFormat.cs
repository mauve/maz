using System.ComponentModel;

namespace Console.Cli;

public enum OutputFormat
{
    [Description("json")]
    Json,

    [Description("jsonl")]
    JsonL,

    [Description("json-pretty")]
    JsonPretty,

    [Description("column")]
    Column,

    [Description("text")]
    Text,
}
