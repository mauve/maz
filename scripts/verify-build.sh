#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

cd "$REPO_ROOT"

dotnet build maz.slnx
dotnet test maz.slnx --no-build
dotnet csharpier check .
