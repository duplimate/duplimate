@echo off
REM Duplimate - Debug launcher
REM
REM Builds the Debug configuration, sets DUPLIMATE_DEBUG=1 so the app boots
REM with verbose Serilog logging, launches the app, and opens a separate
REM PowerShell window that tails the live app log.
REM
REM Use this while developing or when reproducing an issue to report.

setlocal
cd /d "%~dp0"

set "PROJ=src\Duplimate\Duplimate.csproj"
set "EXE=src\Duplimate\bin\Debug\net10.0-windows10.0.19041.0\Duplimate.exe"
set "LOGDIR=src\Duplimate\bin\Debug\net10.0-windows10.0.19041.0\Duplimate.config\logs\app"

REM Kill any stale instance before rebuilding. An older Duplimate.exe
REM holds file locks on the DLL — the build would fail silently (or the new
REM code would sit in the bin folder while the old window stays on screen),
REM so always terminate first.
tasklist /FI "IMAGENAME eq Duplimate.exe" 2>nul | find /I "Duplimate.exe" >nul
if not errorlevel 1 (
    echo [launch-debug] Stopping existing Duplimate.exe ...
    taskkill /F /IM Duplimate.exe >nul 2>&1
    REM Give the OS a moment to release file locks before we rebuild.
    timeout /t 1 /nobreak >nul
)

echo [launch-debug] Building (Debug)...
REM Explicit -f so we don't pay to also build the cross-platform net10.0
REM TFM on every dev rebuild — that TFM only matters when publishing for
REM macOS / Linux. Drop -f if you want to build both.
dotnet build "%PROJ%" -c Debug -f net10.0-windows10.0.19041.0
if errorlevel 1 goto FAIL

if not exist "%EXE%" (
    echo [launch-debug] Expected exe at "%EXE%" but it's missing.
    goto FAIL
)

echo [launch-debug] Launching with verbose logging (DUPLIMATE_DEBUG=1)...
set "DUPLIMATE_DEBUG=1"
start "" "%EXE%"

REM Wait briefly for the app to create today's log file, then tail it
REM in a separate window so you can watch activity live.
timeout /t 3 /nobreak > nul
if not exist "%LOGDIR%" mkdir "%LOGDIR%" > nul 2>&1

echo [launch-debug] Opening log tail window...
REM Force the tail console to UTF-8. Without setting both the console
REM codepage AND -Encoding UTF8 on Get-Content, PowerShell 5.1 on
REM Windows decodes the log as the system ANSI codepage and renders
REM em-dashes etc. as garbled "â€"" sequences.
start "Duplimate - live log" powershell -NoExit -NoProfile -Command ^
    "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8; $OutputEncoding = [System.Text.Encoding]::UTF8; $d='%CD%\%LOGDIR%'; while ($true) { $f = Get-ChildItem -Path $d -Filter 'app-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1; if ($f) { Get-Content -Path $f.FullName -Wait -Tail 50 -Encoding UTF8; break } else { Start-Sleep -Milliseconds 400 } }"

echo.
echo [launch-debug] App running. Close the app to stop. The tail window can be closed any time.
endlocal
exit /b 0

:FAIL
echo.
echo [launch-debug] FAILED. See output above.
pause
endlocal
exit /b 1
