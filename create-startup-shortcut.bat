@echo off
rem ============================================================
rem  TrayToys - create / update the Startup-folder shortcut
rem
rem  This is deliberately independent from the build: build.bat and
rem  build.sh no longer touch the Startup folder. Run this script
rem  whenever you actually want a build to start with Windows.
rem
rem  Usage:
rem    create-startup-shortcut.bat                 use the highest
rem                                                version found in bin\
rem    create-startup-shortcut.bat D:\path\x.exe   use that exe
rem
rem  To disable auto-start: delete TrayToys.lnk from
rem    %APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup
rem
rem  Kept ASCII-only on purpose: cmd.exe parses .bat files using the
rem  system ANSI codepage, so UTF-8 Chinese would be split mid-character.
rem ============================================================
setlocal

set "PS1=%~dp0set-startup-shortcut.ps1"
if not exist "%PS1%" (
  echo ERROR: set-startup-shortcut.ps1 not found in %~dp0
  if "%~1"=="" pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

if not "%RC%"=="0" echo FAILED ^(exit code %RC%^)
rem keep the window open when double-clicked (no args); stay quiet in CLI use
if "%~1"=="" pause
exit /b %RC%
