@echo off
REM ============================================================
REM  Lumo - install / uninstall
REM
REM  ASCII-only on purpose. cmd.exe reads .bat files using the
REM  system ANSI code page, not UTF-8. Any non-ASCII text here
REM  gets mangled and corrupts line parsing -- you end up with
REM  nonsense like "'EM' is not recognized as a command".
REM  Chinese instructions live in README.md instead.
REM
REM  Usage:
REM    install.bat              install to %LOCALAPPDATA%\Programs\Lumo
REM    install.bat uninstall    remove it
REM    install.bat /silent      no prompts (for automated setup)
REM ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "TARGET=%LOCALAPPDATA%\Programs\Lumo"
set "EXE=%~dp0Lumo.exe"
set "SILENT="

for %%A in (%*) do (
    if /i "%%~A"=="uninstall" goto :uninstall
    if /i "%%~A"=="/silent" set "SILENT=1"
)

if not exist "%EXE%" (
    echo ERROR: Lumo.exe not found next to this script.
    echo        Expected: %EXE%
    echo.
    pause
    exit /b 1
)

REM ---- check architecture ----
if /i not "%PROCESSOR_ARCHITECTURE%"=="AMD64" (
    if /i not "%PROCESSOR_ARCHITEW6432%"=="AMD64" (
        echo ERROR: Lumo.exe is a 64-bit build.
        echo        This looks like 32-bit Windows, which is not supported.
        echo.
        pause
        exit /b 1
    )
)

REM ---- warn if already running ----
tasklist /fi "IMAGENAME eq Lumo.exe" 2>nul | find /i "Lumo.exe" >nul
if not errorlevel 1 (
    echo Stopping the running Lumo.exe...
    taskkill /f /im Lumo.exe >nul 2>&1
    timeout /t 1 /nobreak >nul
)

echo ============================================================
echo   Lumo - install
echo ============================================================
echo.
echo Target: %TARGET%
echo.

if not exist "%TARGET%" mkdir "%TARGET%"
if errorlevel 1 (
    echo ERROR: cannot create %TARGET%
    pause
    exit /b 1
)

copy /y "%EXE%" "%TARGET%\Lumo.exe" >nul
if errorlevel 1 (
    echo ERROR: failed to copy Lumo.exe
    pause
    exit /b 1
)
echo   [OK] Lumo.exe

REM ---- create Start Menu shortcut via PowerShell ----
echo   [..] Creating Start Menu shortcut...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "$sh=New-Object -ComObject WScript.Shell;" ^
    "$l=$sh.CreateShortcut([Environment]::GetFolderPath('Programs')+'\Lumo.lnk');" ^
    "$l.TargetPath='%TARGET%\Lumo.exe';" ^
    "$l.WorkingDirectory='%TARGET%';" ^
    "$l.Description='Lumo Translate';" ^
    "$l.Save()" 2>nul
if errorlevel 1 (
    echo   [WARN] Start Menu shortcut failed. You can run Lumo.exe directly.
) else (
    echo   [OK] Start Menu shortcut
)

echo.
echo ============================================================
echo   Installed.
echo.
echo   Run it:  "%TARGET%\Lumo.exe"
echo   Or use the Start Menu entry "Lumo".
echo.
echo   First launch starts in the tray. Double-click the tray
echo   icon to open the window. Default hotkey: Alt+D
echo ============================================================
echo.

if not defined SILENT pause
exit /b 0


:uninstall
echo ============================================================
echo   Lumo - uninstall
echo ============================================================
echo.
echo This removes the program files. Settings and history in
echo %%APPDATA%%\Lumo are kept. Delete that folder too if you
echo want a clean reset.
echo.

if not defined SILENT (
    set /p OK=Continue? [y/N] 
    if /i not "!OK!"=="y" (
        echo Cancelled.
        exit /b 0
    )
)

taskkill /f /im Lumo.exe >nul 2>&1

if exist "%TARGET%" (
    rmdir /s /q "%TARGET%"
    if exist "%TARGET%" (
        echo   [WARN] could not remove %TARGET%
        echo          close Explorer windows showing that folder and retry
    ) else (
        echo   [OK] Program files removed
    )
)

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "Remove-Item ([Environment]::GetFolderPath('Programs')+'\Lumo.lnk') -ErrorAction SilentlyContinue" 2>nul
echo   [OK] Start Menu shortcut removed

echo.
echo Settings kept at: %%APPDATA%%\Lumo
echo.
if not defined SILENT pause
exit /b 0
