#!/bin/bash
# One-line install for macOS:
#   curl -fsSL https://github.com/jvbalcita/kapeyn/releases/latest/download/install-mac.sh | bash
#
# Downloads Kapeyn's source for a release, checks its SHA-256, builds it on this Mac and installs it
# (via mac/install.sh). Because it's built here, macOS shows no "unverified developer" warning.
# Needs Apple's free Command Line Tools; this script offers to install them if they're missing.
# Pick a version with KAPEYN_VERSION=v2.3.0 (default: the release this script came from, or the latest).
set -euo pipefail

REPO="jvbalcita/kapeyn"
VERSION="${KAPEYN_VERSION:-__VERSION__}"   # the release workflow fills this in for install-mac.sh

if [ "$VERSION" = "__VER""SION__" ]; then   # (split so the release workflow's fill-in leaves this check alone)
    # Running straight from the repository: use the latest release.
    VERSION=$(curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO/releases/latest" | sed 's#.*/tag/##')
fi

if ! xcode-select -p >/dev/null 2>&1 || ! command -v swiftc >/dev/null 2>&1; then
    echo "Kapeyn is built on your Mac, which needs Apple's free Command Line Tools."
    echo "A window will open to install them (about 5 minutes). When it's done, run the same command again."
    xcode-select --install 2>/dev/null || true
    exit 1
fi

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

echo "Downloading Kapeyn $VERSION..."
curl -fsSL "https://github.com/$REPO/archive/refs/tags/$VERSION.tar.gz" -o "$TMP/source.tar.gz"

# Verify against the release's checksum list when it has one.
if curl -fsSL "https://github.com/$REPO/releases/download/$VERSION/SHA256SUMS.txt" -o "$TMP/SHA256SUMS.txt" 2>/dev/null; then
    expected=$(awk '$2 == "source.tar.gz" { print $1 }' "$TMP/SHA256SUMS.txt")
    actual=$(shasum -a 256 "$TMP/source.tar.gz" | awk '{ print $1 }')
    if [ -z "$expected" ] || [ "$expected" != "$actual" ]; then
        echo "The download didn't match its published SHA-256 checksum. Nothing was installed." >&2
        exit 1
    fi
    echo "Download verified (SHA-256)."
else
    echo "Note: this release has no checksum list, so the download couldn't be verified."
fi

tar -xzf "$TMP/source.tar.gz" -C "$TMP"
SRC=$(find "$TMP" -maxdepth 1 -type d -name 'kapeyn-*' | head -1)
bash "$SRC/mac/install.sh"
