#!/usr/bin/env bash
# sync-addon.sh — copy the canonical bridge addon into a live s&box project.
#
# Phase 0.2 deliverable. The canonical source lives at:
#   <repo>/sbox-bridge-addon/Editor/MyEditorMenu.cs
#
# The live copy lives at:
#   <sbox-project>/Libraries/claudebridge/Editor/MyEditorMenu.cs
#
# Usage:
#   ./scripts/sync-addon.sh /path/to/sbox-project/Libraries/claudebridge
#   SBOX_PROJECT_LIB=/path/to/...claudebridge ./scripts/sync-addon.sh
#
# Exit codes:
#   0  copy succeeded (or no-op when SHA256 already matches)
#   1  argument missing / target invalid
#   2  canonical file missing
#   3  copy failed

set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
REPO_ROOT="$( cd "$SCRIPT_DIR/../.." && pwd )"
CANONICAL="$REPO_ROOT/sbox-bridge-addon/Editor/MyEditorMenu.cs"

if [[ ! -f "$CANONICAL" ]]; then
    echo "Canonical addon source not found: $CANONICAL" >&2
    exit 2
fi

TARGET="${1:-${SBOX_PROJECT_LIB:-}}"
FORCE="${SBOX_SYNC_FORCE:-0}"

if [[ -z "$TARGET" ]]; then
    cat >&2 <<EOF

Usage: sync-addon.sh <path-to-claudebridge-library>

  Or set SBOX_PROJECT_LIB to the target directory.

Example targets:
  ~/sbox-projects/mygame/Libraries/claudebridge
  /mnt/d/sbox-projects/mygame/Libraries/claudebridge

EOF
    exit 1
fi

TARGET_EDITOR="$TARGET/Editor"
TARGET_FILE="$TARGET_EDITOR/MyEditorMenu.cs"

if [[ ! -d "$TARGET_EDITOR" ]]; then
    if [[ "$FORCE" != "1" ]]; then
        echo "Target Editor/ directory does not exist: $TARGET_EDITOR" >&2
        echo "Set SBOX_SYNC_FORCE=1 to create it (only if you're sure)." >&2
        exit 1
    fi
    mkdir -p "$TARGET_EDITOR"
fi

# SHA256 — prefer shasum (cross-platform) over sha256sum (linux only).
hash_file() {
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$1" | awk '{print toupper($1)}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print toupper($1)}'
    else
        echo "neither sha256sum nor shasum available" >&2
        exit 3
    fi
}

CANONICAL_HASH="$(hash_file "$CANONICAL")"
if [[ -f "$TARGET_FILE" ]]; then
    TARGET_HASH="$(hash_file "$TARGET_FILE")"
else
    TARGET_HASH="(absent)"
fi

echo ""
echo "Canonical: $CANONICAL"
echo "  SHA256:  $CANONICAL_HASH"
echo "Target:    $TARGET_FILE"
echo "  SHA256:  $TARGET_HASH"
echo ""

if [[ "$CANONICAL_HASH" == "$TARGET_HASH" ]]; then
    echo "Already in sync. No copy needed."
    exit 0
fi

cp "$CANONICAL" "$TARGET_FILE"

NEW_HASH="$(hash_file "$TARGET_FILE")"
if [[ "$NEW_HASH" != "$CANONICAL_HASH" ]]; then
    echo "Post-copy hash mismatch (corruption?): expected $CANONICAL_HASH got $NEW_HASH" >&2
    exit 3
fi

echo "Synced. Restart s&box for the addon hotload to pick up changes."
exit 0
