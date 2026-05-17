#!/usr/bin/env bash
# Duplimate - Release launcher (macOS / Linux)
#
# Publishes a self-contained single-file binary for the host platform
# into ./dist/, then launches it. The file in dist/ is what you'd
# share with another macOS / Linux user — no .NET runtime required
# on their end.
#
# To build for a specific RID instead of host-detected, pass it:
#   ./launch-release.sh osx-arm64
#   ./launch-release.sh linux-x64

set -euo pipefail
cd "$(dirname "$0")"

PROJ="src/Duplimate/Duplimate.csproj"
OUT="dist"

# RID detection: prefer the explicit arg, else map host uname tuples.
HOST_OS="$(uname -s)"
HOST_ARCH="$(uname -m)"
case "$HOST_OS" in
  Darwin) HOST_OS_TAG="osx" ;;
  Linux)  HOST_OS_TAG="linux" ;;
  *) echo "[launch-release] Unsupported host: $HOST_OS" >&2; exit 1 ;;
esac
case "$HOST_ARCH" in
  x86_64|amd64)  HOST_ARCH_TAG="x64" ;;
  arm64|aarch64) HOST_ARCH_TAG="arm64" ;;
  *) echo "[launch-release] Unsupported arch: $HOST_ARCH" >&2; exit 1 ;;
esac
RID="${1:-${HOST_OS_TAG}-${HOST_ARCH_TAG}}"

EXE="${OUT}/Duplimate"

# Stop any prior instance so the publish can overwrite the binary.
if pgrep -x Duplimate >/dev/null 2>&1; then
  echo "[launch-release] Stopping existing Duplimate ..."
  pkill -x Duplimate || true
  sleep 1
fi

echo "[launch-release] Publishing self-contained Release for ${RID} -> ${OUT}/ ..."
rm -rf "$OUT"
dotnet publish "$PROJ" -c Release -f net10.0 -r "$RID" --self-contained \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o "$OUT"

if [[ ! -f "$EXE" ]]; then
  echo "[launch-release] Publish succeeded but binary is missing at '${EXE}'." >&2
  exit 1
fi

# Single-file binaries from dotnet publish for macOS/Linux are
# normally already chmod +x, but make double-sure — older RIDs
# occasionally arrived 0644.
chmod +x "$EXE"

echo
echo "[launch-release] Built: $(pwd)/${EXE}"
echo "[launch-release] Launching..."
# `open` on macOS detaches from the parent shell; on Linux we just
# fork + disown so closing this terminal doesn't kill the GUI.
if [[ "$HOST_OS" == "Darwin" ]]; then
  open "$EXE" || "./$EXE" &
else
  "./$EXE" &
fi
disown 2>/dev/null || true

echo
echo "[launch-release] Distribute ${OUT}/Duplimate to share."
