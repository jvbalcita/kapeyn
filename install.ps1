# Installs Kapeyn for the current user (no admin needed):
#   - copies Kapeyn.exe to %LOCALAPPDATA%\Programs\Kapeyn
#   - adds a Start menu shortcut
#   - turns on "Start with Windows"
#   - starts it
#
# Uninstall:  .\install.ps1 -Uninstall
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Kapeyn'
$target     = Join-Path $installDir 'Kapeyn.exe'
$shortcut   = Join-Path ([Environment]::GetFolderPath('Programs')) 'Kapeyn.lnk'
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

# Stop any running copy (wherever it was started from).
$old = Get-Process Kapeyn -ErrorAction SilentlyContinue
if ($old) {
    $old | Stop-Process -Force
    $old | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

if ($Uninstall) {
    Remove-ItemProperty -Path $runKey -Name 'Kapeyn' -ErrorAction SilentlyContinue
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Kapeyn uninstalled. (Settings remain in $env:APPDATA\Kapeyn - delete that folder too if you like.)"
    return
}

$source = Join-Path $PSScriptRoot 'dist\Kapeyn.exe'
if (-not (Test-Path $source)) { $source = Join-Path $PSScriptRoot 'Kapeyn.exe' }
if (-not (Test-Path $source)) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $source = Join-Path $PSScriptRoot 'dist\Kapeyn.exe'
}

New-Item -ItemType Directory -Force $installDir | Out-Null
for ($try = 1; ; $try++) {
    try { Copy-Item $source $target -Force; break }
    catch { if ($try -ge 10) { throw }; Start-Sleep -Milliseconds 500 }  # file can stay locked briefly after exit
}

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $target
$lnk.WorkingDirectory = $installDir
$lnk.Description = 'Keep your PC awake'
$lnk.Save()

Set-ItemProperty -Path $runKey -Name 'Kapeyn' -Value "`"$target`""

Start-Process $target
Write-Host "Kapeyn installed to $target and started. It will also start automatically when you sign in."
