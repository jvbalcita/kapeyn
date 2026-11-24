# Installs Kapeyn for the current user (no admin needed):
#   - copies Kapeyn.exe and kapeyn.com to %LOCALAPPDATA%\Programs\Kapeyn
#   - adds that folder to your PATH, so "kapeyn" works in new terminal windows
#   - adds a Start menu shortcut, starts Kapeyn at sign-in, and starts it now
#
# One-line install (downloads the latest release and checks its SHA-256):
#   irm https://github.com/jvbalcita/kapeyn/releases/latest/download/install.ps1 | iex
# From an unzipped Kapeyn-Windows.zip or this repository:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1
# Uninstall:
#   powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
#   & ([scriptblock]::Create((irm https://github.com/jvbalcita/kapeyn/releases/latest/download/install.ps1))) -Uninstall
param([switch]$Uninstall, [string]$Version = 'latest')

function Install-Kapeyn([bool]$Uninstall, [string]$Version, [string]$ScriptDir) {
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

    # Downloads Kapeyn-Windows.zip from a release, verifies it against SHA256SUMS.txt, and unzips it.
    function Get-Release([string]$Version, [string]$TempDir) {
        $base = if ($Version -eq 'latest') { 'https://github.com/jvbalcita/kapeyn/releases/latest/download' }
                else { "https://github.com/jvbalcita/kapeyn/releases/download/$Version" }
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        $zip = Join-Path $TempDir 'Kapeyn-Windows.zip'
        Write-Host "Downloading Kapeyn ($Version)..."
        Invoke-WebRequest -UseBasicParsing "$base/Kapeyn-Windows.zip" -OutFile $zip

        $sums = $null
        try {
            $response = Invoke-WebRequest -UseBasicParsing "$base/SHA256SUMS.txt"
            $sums = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
        } catch { }
        if ($sums) {
            $line = $sums -split "`n" | Where-Object { $_ -match '\sKapeyn-Windows\.zip\s*$' } | Select-Object -First 1
            if (-not $line) { throw "The release's checksum list doesn't include Kapeyn-Windows.zip. Nothing was installed." }
            $expected = ($line.Trim() -split '\s+')[0].ToLowerInvariant()
            $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($expected -ne $actual) { throw "The download didn't match its published SHA-256 checksum. Nothing was installed." }
            Write-Host "Download verified (SHA-256)."
        } else {
            Write-Warning "This release has no checksum list (SHA256SUMS.txt), so the download couldn't be verified."
        }

        $pkg = Join-Path $TempDir 'pkg'
        Expand-Archive $zip $pkg
        return $pkg
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

    # Find the files: next to this script (release zip), in dist\ (built from source), build them, or download them.
    $temp = $null
    $sourceDir = $null
    if ($ScriptDir) {
        if (Test-Path (Join-Path $ScriptDir 'Kapeyn.exe')) { $sourceDir = $ScriptDir }
        elseif (Test-Path (Join-Path $ScriptDir 'dist\Kapeyn.exe')) { $sourceDir = Join-Path $ScriptDir 'dist' }
        elseif (Test-Path (Join-Path $ScriptDir 'build.ps1')) {
            & (Join-Path $ScriptDir 'build.ps1')
            $sourceDir = Join-Path $ScriptDir 'dist'
        }
    }
    if (-not $sourceDir) {
        $temp = Join-Path ([IO.Path]::GetTempPath()) ('kapeyn-install-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory $temp | Out-Null
        $sourceDir = Get-Release $Version $temp
    }

    try {
        New-Item -ItemType Directory -Force $installDir | Out-Null
        foreach ($name in 'Kapeyn.exe', 'kapeyn.com', 'LICENSE') {
            $src = Join-Path $sourceDir $name
            if (-not (Test-Path $src)) { continue }
            for ($try = 1; ; $try++) {
                try { Copy-Item $src (Join-Path $installDir $name) -Force; break }
                catch { if ($try -ge 10) { throw }; Start-Sleep -Milliseconds 500 }  # file can stay locked briefly after exit
            }
        }
    }
    finally {
        if ($temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $target = Join-Path $installDir 'Kapeyn.exe'

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($shortcut)
    $lnk.TargetPath = $target
    $lnk.WorkingDirectory = $installDir
    $lnk.Description = 'Keep your PC awake'
    $lnk.Save()

    # A brand-new Windows account may not have the Run key yet. Create it only when missing:
    # New-Item -Force on an existing key would wipe other apps' start-at-sign-in entries.
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey | Out-Null }
    Set-ItemProperty -Path $runKey -Name 'Kapeyn' -Value "`"$target`""
    if (Test-Path (Join-Path $installDir 'kapeyn.com')) { Set-UserPath $true }

    Start-Process $target
    $version = (Get-Item $target).VersionInfo.ProductVersion
    Write-Host "Kapeyn $version installed to $installDir and started. It also starts automatically when you sign in."
    Write-Host "Look for the coffee cup in the system tray. Open a new terminal window and try:  kapeyn status"
}

# $PSScriptRoot is empty when this script is piped into Invoke-Expression (the one-line install).
Install-Kapeyn -Uninstall $Uninstall.IsPresent -Version $Version -ScriptDir $PSScriptRoot
