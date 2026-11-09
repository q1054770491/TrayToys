# set-startup-shortcut.ps1
# Create / update the Startup-folder shortcut for TrayToys.
#
# NOT called by build.sh / build.bat any more -- building a new exe must not
# silently repoint what runs at logon. Run it yourself when you actually want
# the new build to auto-start:
#     create-startup-shortcut.bat              (newest version in bin\)
#     create-startup-shortcut.bat D:\x\yy.exe  (explicit exe)
#
# Kept ASCII-only on purpose (avoids PowerShell script-encoding pitfalls).

param(
    # Omit to use the highest version found in <repo>\bin\<Name>_v*.exe
    [string]$ExePath,
    [string]$Name = 'TrayToys'
)

$ErrorActionPreference = 'Stop'

# ---------------- 1. Locate the source tree ----------------
# Works both when run via -File and when dot-sourced from another script.
$root = $null
if ($PSScriptRoot) { $root = $PSScriptRoot }
elseif ($MyInvocation.MyCommand.Path) { $root = Split-Path -Parent $MyInvocation.MyCommand.Path }
else { $root = (Get-Location).Path }

# ---------------- 2. Pick the exe ----------------
if (-not $ExePath) {
    $bin = Join-Path $root 'bin'
    if (-not (Test-Path -LiteralPath $bin)) {
        Write-Output "SKIP: bin folder not found -> $bin"
        exit 1
    }

    $best = $null
    $bestVer = $null
    foreach ($f in (Get-ChildItem -LiteralPath $bin -Filter ($Name + '_v*.exe') -File -ErrorAction SilentlyContinue)) {
        # compare by numeric version, so v2.10 wins over v2.9 (string sort would not)
        if ($f.BaseName -match '_v(\d+(?:\.\d+)*)$') {
            $v = [version]$Matches[1]
            if (($null -eq $bestVer) -or ($v -gt $bestVer)) {
                $best = $f
                $bestVer = $v
            }
        }
    }

    if ($null -eq $best) {
        Write-Output ("SKIP: no {0}_v*.exe in {1}" -f $Name, $bin)
        exit 1
    }
    $ExePath = $best.FullName
    Write-Output ("picked: {0} (v{1})" -f $best.Name, $bestVer)
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Output "SKIP: exe not found -> $ExePath"
    exit 1
}
$exe = (Resolve-Path -LiteralPath $ExePath).Path

# ---------------- 3. Create the shortcut ----------------
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
$lnk.Description = 'TrayToys - tray toolbox (auto start)'
$lnk.IconLocation = "$exe,0"
$lnk.Save()

# read back and report what actually landed on disk
$check = $ws.CreateShortcut($lnkPath)
Write-Output "OK: $lnkPath"
Write-Output "    -> $($check.TargetPath)"
