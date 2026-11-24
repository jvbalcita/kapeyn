# Installs Kapeyn for the current user (no admin needed):
#   - copies Kapeyn.exe and kapeyn.com to %LOCALAPPDATA%\Programs\Kapeyn
#   - adds that folder to your PATH, so "kapeyn" works in new terminal windows
#   - adds a Start menu shortcut
#   - turns on "Start with Windows"
#   - starts it
#
# Uninstall:  .\install.ps1 -Uninstall
param([switch]$Uninstall)
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Kapeyn'
$shortcut   = Join-Path ([Environment]::GetFolderPath('Programs')) 'Kapeyn.lnk'
$runKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Set-UserPath([bool]$include) {
    $envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
    $raw = [string]$envKey.GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    $parts = @($raw -split ';' | Where-Object { $_ -and ($_.TrimEnd('\') -ne $installDir.TrimEnd('\')) })
    if ($include) { $parts += $installDir }
    $envKey.SetValue('Path', ($parts -join ';'), 'ExpandString')
    $envKey.Close()
    # Tell Windows the environment changed, so new terminals see it.
    [Environment]::SetEnvironmentVariable('KAPEYN_INSTALL_REFRESH', '1', 'User')
    [Environment]::SetEnvironmentVariable('KAPEYN_INSTALL_REFRESH', $null, 'User')
}

# Stop any running copy (wherever it was started from) and wait for it to exit.
$old = Get-Process Kapeyn -ErrorAction SilentlyContinue
if ($old) {
    $old | Stop-Process -Force
    $old | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

if ($Uninstall) {
    Remove-ItemProperty -Path $runKey -Name 'Kapeyn' -ErrorAction SilentlyContinue
    Remove-Item $shortcut -Force -ErrorAction SilentlyContinue
    Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
    Set-UserPath $false
    Write-Host "Kapeyn uninstalled. (Settings remain in $env:APPDATA\Kapeyn - delete that folder too if you like.)"
    return
}

# Find the files: next to this script (release zip), in dist\ (built from source), or build them.
$sourceDir = $PSScriptRoot
if (-not (Test-Path (Join-Path $sourceDir 'Kapeyn.exe'))) { $sourceDir = Join-Path $PSScriptRoot 'dist' }
if (-not (Test-Path (Join-Path $sourceDir 'Kapeyn.exe'))) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $sourceDir = Join-Path $PSScriptRoot 'dist'
}

New-Item -ItemType Directory -Force $installDir | Out-Null
foreach ($name in 'Kapeyn.exe', 'kapeyn.com') {
    $src = Join-Path $sourceDir $name
    if (-not (Test-Path $src)) { continue }
    for ($try = 1; ; $try++) {
        try { Copy-Item $src (Join-Path $installDir $name) -Force; break }
        catch { if ($try -ge 10) { throw }; Start-Sleep -Milliseconds 500 }  # file can stay locked briefly after exit
    }
}
$target = Join-Path $installDir 'Kapeyn.exe'

$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = $target
$lnk.WorkingDirectory = $installDir
$lnk.Description = 'Keep your PC awake'
$lnk.Save()

Set-ItemProperty -Path $runKey -Name 'Kapeyn' -Value "`"$target`""
if (Test-Path (Join-Path $installDir 'kapeyn.com')) { Set-UserPath $true }

Start-Process $target
Write-Host "Kapeyn installed to $installDir and started. It also starts automatically when you sign in."
Write-Host "Open a new terminal window and try:  kapeyn status"
