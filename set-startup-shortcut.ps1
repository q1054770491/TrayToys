# set-startup-shortcut.ps1
# Point the Startup-folder shortcut of TrayToys at the given exe.
# Called automatically by build.sh / build.bat after every successful build,
# so the shortcut always follows the newest version.
# Kept ASCII-only on purpose (avoids PowerShell script-encoding pitfalls).
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$Name = 'TrayToys'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Output "SKIP: exe not found -> $ExePath"
    exit 1
}
$exe = (Resolve-Path -LiteralPath $ExePath).Path

$startup = [Environment]::GetFolderPath('Startup')
if (-not (Test-Path -LiteralPath $startup)) {
    New-Item -ItemType Directory -Path $startup -Force | Out-Null
}
$lnkPath = Join-Path $startup ($Name + '.lnk')

# Remove shortcuts left behind by earlier program names, so the Startup folder
# never ends up with two entries pointing at different versions.
foreach ($old in @('AudioSwitcher.lnk', 'a_few_tool.lnk')) {
    $oldPath = Join-Path $startup $old
    if ((Test-Path -LiteralPath $oldPath) -and ($old -ne ($Name + '.lnk'))) {
        Remove-Item -LiteralPath $oldPath -Force -ErrorAction SilentlyContinue
        Write-Output "removed legacy shortcut: $old"
    }
}

$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut($lnkPath)
$lnk.TargetPath = $exe
$lnk.WorkingDirectory = Split-Path -Path $exe -Parent
$lnk.Description = 'TrayToys - playback/input device switcher (auto start)'
$lnk.IconLocation = "$exe,0"
$lnk.Save()

# read back and report what actually landed on disk
$check = $ws.CreateShortcut($lnkPath)
Write-Output "OK: $lnkPath"
Write-Output "    -> $($check.TargetPath)"
