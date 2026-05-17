#!/usr/bin/env bash
# Duplimate - Debug launcher (macOS / Linux)
#
# Builds the Debug configuration of the cross-platform TFM, sets
# DUPLIMATE_DEBUG=1 so the app boots with verbose Serilog logging,
# launches the app, and tails the live app log in this terminal.
#
# Use this while developing on macOS / Linux. The Windows equivalent is
# launch-debug.bat.

set -euo pipefail
cd "$(dirname "$0")"

PROJ="src/Duplimate/Duplimate.csproj"
TFM="net10.0"
BIN_DIR="src/Duplimate/bin/Debug/${TFM}"
EXE="${BIN_DIR}/Duplimate"            # net10.0 produces an extensionless binary
LOG_DIR="${BIN_DIR}/Duplimate.config/logs/app"

# Stop any prior instance so the build doesn't fight a held file lock
# on the DLL. macOS ships pgrep+pkill; Linux usually does too. Fall
# back to ps + grep + kill if neither is available.
if pgrep -x Duplimate >/dev/null 2>&1; then
  echo "[launch-debug] Stopping existing Duplimate ..."
  pkill -x Duplimate || true
  sleep 1
fi

echo "[launch-debug] Building Debug for ${TFM} ..."
dotnet build "$PROJ" -c Debug -f "$TFM"

if [[ ! -x "$EXE" ]]; then
  echo "[launch-debug] Expected binary at '${EXE}' but it's missing or not executable." >&2
  exit 1
fi

echo "[launch-debug] Launching with verbose logging (DUPLIMATE_DEBUG=1) ..."
mkdir -p "$LOG_DIR"
DUPLIMATE_DEBUG=1 "$EXE" &
APP_PID=$!

# Tail the freshest app log in this terminal. If the user Ctrl-Cs the
# tail, that's fine — the app keeps running; they can close the window
# normally.
echo "[launch-debug] Tailing log dir '${LOG_DIR}' (Ctrl-C stops the tail; the app keeps running)."
echo

# Wait briefly for the app to create today's log file before starting
# the tail, otherwise tail -F prints "no such file" and bails.
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if compgen -G "${LOG_DIR}/app-*.log" >/dev/null; then break; fi
  sleep 0.4
done
LATEST="$(ls -1t "${LOG_DIR}"/app-*.log 2>/dev/null | head -n1 || true)"
if [[ -n "$LATEST" ]]; then
  tail -n 50 -F "$LATEST"
else
  echo "[launch-debug] No app log appeared yet — open a new terminal and tail manually if needed."
  wait "$APP_PID"
fi
