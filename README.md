# README

Self-contained Azure CLI written in C#.

_Because the official CLI is slow and annoying._

## Building

Run the following command to produce a binary for your preferred platform,
in this example we are building for linux-x64.

```sh
dotnet publish --self-contained true -r linux-x64 -p:PublishSingleFile=true
```