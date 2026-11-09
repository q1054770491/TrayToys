#!/bin/bash
# TrayToys 一键构建（Git Bash 版）
# 产物：bin/TrayToys_v<版本号>.exe —— 程序名和版本号都自动从 src/AppInfo.cs 读取
# 说明：每个版本生成独立 exe，不覆盖旧版本文件。
set -e
# 禁用 MSYS 参数路径转换（否则 /nologo 之类会被当成路径）
export MSYS_NO_PATHCONV=1
export MSYS2_ARG_CONV_EXCL='*'

cd "$(dirname "$0")"

# 从 AppInfo.cs 读取版本号（单一来源）
VER=$(sed -n 's/.*Version = "\([0-9][0-9.]*\)".*/\1/p' src/AppInfo.cs | head -1)
if [ -z "$VER" ]; then
  echo "错误：未能从 src/AppInfo.cs 解析出版本号，请检查 AppInfo.cs 的 Version 字段。"
  exit 1
fi
# 程序名同样从 AppInfo.cs 读取（改名只需要改 AppInfo 一处）
NAME=$(sed -n 's/.*Name = "\(.*\)".*/\1/p' src/AppInfo.cs | head -1)
if [ -z "$NAME" ]; then
  echo "错误：未能从 src/AppInfo.cs 解析出程序名 Name。"
  exit 1
fi
OUT="${NAME}_v${VER}.exe"

CSC="/c/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe"
if [ ! -f "$CSC" ]; then CSC="/c/Windows/Microsoft.NET/Framework/v4.0.30319/csc.exe"; fi
mkdir -p bin
cd src
# 用 - 前缀选项（csc 同样支持），彻底规避路径转换
"$CSC" -nologo -target:winexe -optimize+ \
  -out:"../bin/${OUT}" \
  -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Core.dll -r:System.Runtime.Serialization.dll \
  Program.cs MainForm.cs ToastForm.cs SettingsForm.cs SessionRow.cs AudioManager.cs AudioSessionManager.cs \
  NativeMethods.cs Settings.cs HotkeyKeys.cs TrayIconHelper.cs Logger.cs \
  ScreenKeeper.cs ScreenDimmer.cs DimForm.cs FullscreenWatcher.cs SessionResetKeeper.cs DeviceBatteryManager.cs BatteryProtocols.cs BatteryValueTracker.cs BatterySetupForm.cs DeviceSetupSimpleForm.cs ProtocolManageForm.cs AppInfo.cs AssemblyInfo.cs
cd ..
echo "OK -> bin/${OUT}  (版本 ${VER})"

# 让"启动"文件夹里的快捷方式始终指向刚构建出来的这个版本。
# 失败只提示、不影响构建结果（例如脚本执行策略被限制的机器）。
if [ -f "set-startup-shortcut.ps1" ]; then
  WINEXE=$(cygpath -w "$(pwd)/bin/${OUT}" 2>/dev/null || echo "$(pwd)/bin/${OUT}")
  if powershell -NoProfile -ExecutionPolicy Bypass -File "set-startup-shortcut.ps1" -ExePath "$WINEXE" -Name "$NAME" 2>&1 | tail -2; then
    echo "启动项已指向 ${OUT}"
  else
    echo "提示：启动项快捷方式更新失败（不影响构建；可手动运行 set-startup-shortcut.ps1）"
  fi
fi
