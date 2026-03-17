#!/usr/bin/env bash
set -euo pipefail

REPO="mauve/maz"
INSTALL_DIR="${MAZ_INSTALL_DIR:-$HOME/.local/bin}"

# Detect OS
case "$(uname -s)" in
  Linux*)  OS="linux" ;;
  Darwin*) OS="osx"   ;;
  *)
    echo "error: unsupported OS: $(uname -s)" >&2
    exit 1
    ;;
esac

# Detect architecture
case "$(uname -m)" in
  x86_64)        ARCH="x64"   ;;
  aarch64|arm64) ARCH="arm64" ;;
  *)
    echo "error: unsupported architecture: $(uname -m)" >&2
    exit 1
    ;;
esac

ASSET="maz-${OS}-${ARCH}"

# Resolve latest release tag
echo "Fetching latest release..."
TAG=$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" \
  | grep '"tag_name"' \
  | sed 's/.*"tag_name": *"\([^"]*\)".*/\1/')

if [ -z "$TAG" ]; then
  echo "error: could not determine latest release" >&2
  exit 1
fi

URL="https://github.com/${REPO}/releases/download/${TAG}/${ASSET}"

echo "Downloading maz ${TAG} (${OS}/${ARCH})..."
mkdir -p "$INSTALL_DIR"
curl -fsSL "$URL" -o "$INSTALL_DIR/maz"
chmod +x "$INSTALL_DIR/maz"

echo "Installed: $INSTALL_DIR/maz"

# Warn if not in PATH
case ":${PATH}:" in
  *":${INSTALL_DIR}:"*) ;;
  *)
    echo ""
    echo "Warning: $INSTALL_DIR is not in your PATH."
    echo "Add this to your shell profile (~/.bashrc, ~/.zshrc, ~/.profile, etc.):"
    echo ""
    echo "  export PATH=\"\$HOME/.local/bin:\$PATH\""
    echo ""
    ;;
esac

# Prompt user to run bootstrap
echo ""
if [ -t 1 ] && command -v tput >/dev/null 2>&1 && [ "$(tput colors 2>/dev/null)" -ge 8 ]; then
  bold=$(tput bold)
  cyan=$(tput setaf 6)
  reset=$(tput sgr0)
  echo "${bold}${cyan}Run 'maz bootstrap' to get started.${reset}"
else
  echo "Run 'maz bootstrap' to get started."
fi
