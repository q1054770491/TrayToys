@echo off
rem ============================================================
rem  TrayToys - one-click build
rem  Uses the csc.exe that ships with Windows (.NET Framework),
rem  so no Visual Studio / SDK / NuGet is required.
rem
rem  Output: bin\<Name>_v<Version>.exe
rem          Name and Version are parsed from src\AppInfo.cs
rem          (single source of truth; rename/version there only).
rem
rem  Note: each version gets its own exe; older files are kept.
rem        The build does NOT touch the Startup shortcut -- run
rem        create-startup-shortcut.bat when you want auto-start.
rem
rem  Kept ASCII-only on purpose: cmd.exe parses .bat files using
rem  the system ANSI codepage, so UTF-8 Chinese comments can be
rem  split mid-character and end up executed as commands.
rem ============================================================
setlocal enabledelayedexpansion
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

rem ---- read version from src\AppInfo.cs ----
set VER=
for /f "tokens=6" %%a in ('findstr /c:"const string Version" src\AppInfo.cs') do set VER=%%a
set VER=%VER:"=%
set VER=%VER:;=%
if "%VER%"=="" (
  echo ERROR: cannot parse Version from src\AppInfo.cs
  exit /b 1
)

rem ---- read program name from src\AppInfo.cs ----
set NAME=
for /f "tokens=6" %%a in ('findstr /c:"const string Name" src\AppInfo.cs') do set NAME=%%a
set NAME=%NAME:"=%
set NAME=%NAME:;=%
if "%NAME%"=="" (
  echo ERROR: cannot parse Name from src\AppInfo.cs
  exit /b 1
)

if not exist bin mkdir bin

pushd src
"%CSC%" /nologo /target:winexe /optimize+ /out:..\bin\%NAME%_v%VER%.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Runtime.Serialization.dll ^
  Program.cs MainForm.cs ToastForm.cs SettingsForm.cs SessionRow.cs AudioManager.cs AudioSessionManager.cs ^
  NativeMethods.cs Settings.cs HotkeyKeys.cs TrayIconHelper.cs Logger.cs AutoStart.cs ^
  ScreenKeeper.cs ScreenDimmer.cs DimForm.cs FullscreenWatcher.cs SessionResetKeeper.cs DeviceBatteryManager.cs BatteryProtocols.cs BatteryValueTracker.cs BatterySetupForm.cs DeviceSetupSimpleForm.cs ProtocolManageForm.cs AppInfo.cs AssemblyInfo.cs
popd

if exist bin\%NAME%_v%VER%.exe (
  echo Build OK: bin\%NAME%_v%VER%.exe  ^(version %VER%^)
) else (
  echo Build FAILED - see compiler errors above.
  exit /b 1
)
endlocal
