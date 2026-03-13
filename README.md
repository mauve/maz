# README

Self-contained Azure CLI written in C#.

_Because the official CLI is slow and annoying._

## Dynamic option completions

`[CliOption]` supports dynamic completion providers via `CompletionProviderType`.

Example:

```csharp
[CliOption("--subscription-id", CompletionProviderType = typeof(SubscriptionIdCompletionProvider))]
public partial string? SubscriptionId { get; }
```

Provider contract:

```csharp
public interface ICliCompletionProvider
{
    ValueTask<IEnumerable<string>> GetCompletionsAsync(CompletionContext context);
}
```

## Building

Run the following command to produce a binary for your preferred platform,
in this example we are building for linux-x64.

```sh
dotnet publish --self-contained true -r linux-x64 -p:PublishSingleFile=true
```
