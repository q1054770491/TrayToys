@echo off
rem ============================================================
rem  TrayToys 一键构建脚本（使用 Windows 内置 .NET Framework 编译器，无需装 VS/SDK）
rem  产物: bin\TrayToys_v<版本号>.exe（程序名与版本号都自动读自 src\AppInfo.cs）
rem  说明: 每个版本生成独立 exe，不覆盖旧版本文件。
rem ============================================================
setlocal enabledelayedexpansion
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe

rem ---- 从 src\AppInfo.cs 解析版本号 ----
set VER=
for /f "tokens=6" %%a in ('findstr /c:"const string Version" src\AppInfo.cs') do set VER=%%a
set VER=%VER:"=%
set VER=%VER:;=%
if "%VER%"=="" (
  echo 错误: 未能从 src\AppInfo.cs 解析出版本号，请检查 AppInfo.cs 的 Version 字段。
  exit /b 1
)

rem ---- 从 src\AppInfo.cs 解析程序名 ----
set NAME=
for /f "tokens=6" %%a in ('findstr /c:"const string Name" src\AppInfo.cs') do set NAME=%%a
set NAME=%NAME:"=%
set NAME=%NAME:;=%
if "%NAME%"=="" (
  echo 错误: 未能从 src\AppInfo.cs 解析出程序名。
  exit /b 1
)

if not exist bin mkdir bin

pushd src
"%CSC%" /nologo /target:winexe /optimize+ /out:..\bin\%NAME%_v%VER%.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Runtime.Serialization.dll ^
  Program.cs MainForm.cs ToastForm.cs SettingsForm.cs SessionRow.cs AudioManager.cs AudioSessionManager.cs ^
  NativeMethods.cs Settings.cs HotkeyKeys.cs TrayIconHelper.cs Logger.cs ^
  ScreenKeeper.cs ScreenDimmer.cs DimForm.cs FullscreenWatcher.cs SessionResetKeeper.cs DeviceBatteryManager.cs BatteryProtocols.cs BatteryValueTracker.cs BatterySetupForm.cs DeviceSetupSimpleForm.cs ProtocolManageForm.cs AppInfo.cs AssemblyInfo.cs
popd

if exist bin\%NAME%_v%VER%.exe (
  echo 构建成功: bin\%NAME%_v%VER%.exe  ^(版本 %VER%^)
  rem ---- 让"启动"文件夹里的快捷方式始终指向刚构建出来的这个版本 ----
  if exist "set-startup-shortcut.ps1" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "set-startup-shortcut.ps1" -ExePath "%~dp0bin\%NAME%_v%VER%.exe" -Name "%NAME%"
  )
) else (
  echo 构建失败，请检查上面的编译错误。
)
endlocal
