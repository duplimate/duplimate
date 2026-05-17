#!/usr/bin/env bash
# Downloads the latest duplicacy CLI for the host platform from
# gilbertchen/duplicacy releases to the given out-path. Invoked by
# FetchDuplicacy.targets when building on macOS or Linux.
#
# Self-contained: assumes only `bash`, `curl`, and `jq` — all standard
# on macOS (jq via Homebrew or Xcode CLT, curl is built-in) and Linux.
# Re-runs are no-ops — the MSBuild target skips us when the file
# exists.

set -euo pipefail

OUT_PATH="${1:?usage: fetch-duplicacy.sh <out-path>}"

# Detect host OS + arch. Duplicacy upstream uses:
#   linux  : x64, arm64, i386
#   osx    : x64, arm64, i386
#   win    : x64, arm64, i386
case "$(uname -s)" in
  Darwin*)  OS_TAG="osx"   ;;
  Linux*)   OS_TAG="linux" ;;
  *)
    echo "[fetch-duplicacy] Unsupported host: $(uname -s)" >&2
    exit 1
    ;;
esac

case "$(uname -m)" in
  x86_64|amd64)  ARCH_TAG="x64"   ;;
  arm64|aarch64) ARCH_TAG="arm64" ;;
  i?86)          ARCH_TAG="i386"  ;;
  *)
    echo "[fetch-duplicacy] Unrecognised CPU $(uname -m); falling back to x64" >&2
    ARCH_TAG="x64"
    ;;
esac

ASSET_PREFIX="duplicacy_${OS_TAG}_${ARCH_TAG}_"

if ! command -v jq >/dev/null 2>&1; then
  echo "[fetch-duplicacy] 'jq' is required but not on PATH." >&2
  echo "[fetch-duplicacy]   macOS: brew install jq" >&2
  echo "[fetch-duplicacy]   Linux: sudo apt install jq    (or your distro's equivalent)" >&2
  exit 1
fi

echo "[fetch-duplicacy] Querying GitHub for latest release matching ${ASSET_PREFIX}*..."
RELEASE_JSON="$(curl -fsSL \
  -H 'User-Agent: Duplimate-build' \
  -H 'Accept: application/vnd.github+json' \
  --connect-timeout 30 --max-time 60 \
  https://api.github.com/repos/gilbertchen/duplicacy/releases/latest)"

ASSET_URL="$(echo "$RELEASE_JSON" | jq -r --arg prefix "$ASSET_PREFIX" \
  '.assets[] | select(.name | startswith($prefix)) | .browser_download_url' \
  | head -n1)"
ASSET_NAME="$(echo "$RELEASE_JSON" | jq -r --arg prefix "$ASSET_PREFIX" \
  '.assets[] | select(.name | startswith($prefix)) | .name' \
  | head -n1)"
ASSET_SIZE="$(echo "$RELEASE_JSON" | jq -r --arg prefix "$ASSET_PREFIX" \
  '.assets[] | select(.name | startswith($prefix)) | .size' \
  | head -n1)"

if [[ -z "${ASSET_URL:-}" || "$ASSET_URL" == "null" ]]; then
  echo "[fetch-duplicacy] No asset matching ${ASSET_PREFIX}* in the latest release." >&2
  echo "[fetch-duplicacy] Available assets:" >&2
  echo "$RELEASE_JSON" | jq -r '.assets[].name' >&2
  exit 1
fi

OUT_DIR="$(dirname "$OUT_PATH")"
mkdir -p "$OUT_DIR"

if [[ -n "${ASSET_SIZE:-}" && "$ASSET_SIZE" != "null" ]]; then
  printf "[fetch-duplicacy] Downloading %s (%.1f MB)\n" \
    "$ASSET_NAME" "$(awk "BEGIN { printf \"%.1f\", $ASSET_SIZE / 1024 / 1024 }")"
fi

# Atomic write: download to a sibling .tmp, chmod +x, then mv into place.
TMP_PATH="${OUT_PATH}.tmp"
trap 'rm -f "$TMP_PATH"' EXIT
curl -fsSL --connect-timeout 30 --max-time 600 -o "$TMP_PATH" "$ASSET_URL"
chmod 0755 "$TMP_PATH"
mv "$TMP_PATH" "$OUT_PATH"
trap - EXIT

SIZE="$(wc -c < "$OUT_PATH" | tr -d ' ')"
printf "[fetch-duplicacy] Wrote %s (%.1f MB)\n" "$OUT_PATH" \
  "$(awk "BEGIN { printf \"%.1f\", $SIZE / 1024 / 1024 }")"
