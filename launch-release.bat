@echo off
REM Duplimate - Release launcher
REM
REM Publishes a self-contained single-file .exe into .\dist\, then launches
REM it. The file in dist\ is what you'd share with other Windows users —
REM no .NET runtime required on their end.
REM
REM To build without launching, run: build-release.bat

REM === Toggles ============================================================
REM Set to 1 to copy a pre-populated Duplimate.config\ folder into dist\
REM before launching the exe — gives the freshly-published instance some
REM test data (configured backups, destinations, history) so you don't have
REM to re-create them on every Release rebuild. Set to 0 to launch with a
REM virgin config (the exe will create an empty Duplimate.config\ on
REM first run).
set "SEED_TEST_DATA=1"
set "SEED_SOURCE=%~dp0Duplimate.config"
REM ========================================================================

setlocal
cd /d "%~dp0"

set "PROJ=src\Duplimate\Duplimate.csproj"
set "OUT=dist"
set "EXE=%OUT%\Duplimate.exe"

REM Kill any stale instance so "rd /s /q dist" can delete the old exe. If a
REM running Duplimate.exe keeps dist\ locked, the publish appears to
REM succeed but you'd quietly keep executing the old binary.
tasklist /FI "IMAGENAME eq Duplimate.exe" 2>nul | find /I "Duplimate.exe" >nul
if not errorlevel 1 (
    echo [launch-release] Stopping existing Duplimate.exe ...
    taskkill /F /IM Duplimate.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
)

echo [launch-release] Publishing single-file Release to %OUT%\ ...
if exist "%OUT%" rd /s /q "%OUT%"
REM Duplimate multi-targets net10.0-windows10.0.19041.0 (full Win32
REM stack) and net10.0 (cross-platform fallback). dotnet publish requires
REM an explicit -f when more than one TFM is declared, otherwise:
REM   error NETSDK1129: The 'Publish' target is not supported without
REM                     specifying a target framework.
dotnet publish "%PROJ%" -c Release -f net10.0-windows10.0.19041.0 ^
    -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%OUT%"
if errorlevel 1 goto FAIL

if not exist "%EXE%" (
    echo [launch-release] Publish succeeded but exe is missing at "%EXE%".
    goto FAIL
)

echo.
echo [launch-release] Built: %CD%\%EXE%

REM Seed dist\Duplimate.config\ with test data BEFORE launch so the
REM newly-started exe reads a populated config on first init. Copying
REM AFTER launch would race with the app's own first-run config-folder
REM creation, so we do it here. Toggle SEED_TEST_DATA at the top of
REM this file to disable.
if "%SEED_TEST_DATA%"=="1" (
    if exist "%SEED_SOURCE%" (
        echo [launch-release] Seeding %OUT%\Duplimate.config\ from %SEED_SOURCE% ...
        REM /E       - copy subfolders, including empty ones
        REM /I       - assume destination is a folder
        REM /Y       - overwrite existing files without prompting
        REM /Q       - quiet (suppress per-file listing)
        xcopy "%SEED_SOURCE%" "%OUT%\Duplimate.config" /E /I /Y /Q >nul
        if errorlevel 1 echo [launch-release] WARN: seed copy reported errors; continuing.
    ) else (
        echo [launch-release] SEED_TEST_DATA=1 but source folder not found at "%SEED_SOURCE%" — skipping.
    )
)

echo [launch-release] Launching...
start "" "%EXE%"

echo.
echo [launch-release] Distribute dist\Duplimate.exe to share.
endlocal
exit /b 0

:FAIL
echo.
echo [launch-release] FAILED. See output above.
pause
endlocal
exit /b 1
