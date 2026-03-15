#!/usr/bin/env bash
set -euo pipefail

VERSION="${1:-}"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Usage: $0 <major.minor.patch>  (e.g. 1.2.3)" >&2
  exit 1
fi

TAG="v$VERSION"
git tag -a "$TAG" -m "Release $TAG"
echo "Tagged $TAG locally. Push with: git push origin $TAG"
