using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace Console.Cli.Commands.Copy;

/// <summary>
/// Non-interactive output: drains progress events and writes one NDJSON line
/// per completed or failed transfer.
/// </summary>
public static class CopyNdjsonOutput
{
    public static async Task DrainAsync(
        ChannelReader<TransferProgressEvent> progress,
        IReadOnlyList<TransferItem> items,
        Stopwatch elapsed,
        CancellationToken ct
    )
    {
        await foreach (var evt in progress.ReadAllAsync(ct))
        {
            if (evt.Status is not (TransferStatus.Completed or TransferStatus.Failed))
                continue;

            var item = items[evt.TransferIndex];
            using var stream = System.Console.OpenStandardOutput();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true });

            if (evt.Status == TransferStatus.Completed)
            {
                writer.WriteStartObject();
                writer.WriteString("status", "ok");
                writer.WriteString("src", item.SourcePath);
                writer.WriteString("dst", item.DestPath);
                writer.WriteNumber("bytes", evt.TotalBytes);
                writer.WriteNumber("duration_ms", elapsed.ElapsedMilliseconds);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject();
                writer.WriteString("status", "error");
                writer.WriteString("src", item.SourcePath);
                writer.WriteString("dst", item.DestPath);
                writer.WriteString("error", evt.Error ?? "Unknown error");
                writer.WriteEndObject();
            }

            writer.Flush();
            System.Console.WriteLine(); // newline after each JSON object
        }
    }
}
