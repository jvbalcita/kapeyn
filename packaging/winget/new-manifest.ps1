# Writes the winget manifests for a published Kapeyn release, in winget-pkgs' folder layout:
#   packaging\winget\manifests\k\Kapeyn\Kapeyn\<version>\
# Usage:  powershell -ExecutionPolicy Bypass -File packaging\winget\new-manifest.ps1 -Version 2.3.1
# Then:   winget validate --manifest packaging\winget\manifests\k\Kapeyn\Kapeyn\2.3.1
# The installer is the release's Kapeyn-Windows.zip; its SHA-256 comes from the release's SHA256SUMS.txt.
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$Version = $Version.TrimStart('v')
$tag = "v$Version"
$id = 'Kapeyn.Kapeyn'
$base = "https://github.com/jvbalcita/kapeyn/releases/download/$tag"

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$sumsResponse = Invoke-WebRequest -UseBasicParsing "$base/SHA256SUMS.txt"
$sums = if ($sumsResponse.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($sumsResponse.Content) } else { [string]$sumsResponse.Content }
$line = $sums -split "`n" | Where-Object { $_ -match '\sKapeyn-Windows\.zip\s*$' } | Select-Object -First 1
if (-not $line) { throw "SHA256SUMS.txt for $tag has no entry for Kapeyn-Windows.zip." }
$sha = ($line.Trim() -split '\s+')[0].ToUpperInvariant()

$release = Invoke-RestMethod -UseBasicParsing "https://api.github.com/repos/jvbalcita/kapeyn/releases/tags/$tag" -Headers @{ 'User-Agent' = 'kapeyn-winget' }
$date = ([datetime]$release.published_at).ToString('yyyy-MM-dd')

$dir = Join-Path $PSScriptRoot "manifests\k\Kapeyn\Kapeyn\$Version"
New-Item -ItemType Directory -Force $dir | Out-Null
$schema = '1.9.0'

$files = @{
"$id.yaml" = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $schema
"@
"$id.installer.yaml" = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
MinimumOSVersion: 10.0.17763.0
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: kapeyn-cli.exe
  PortableCommandAlias: kapeyn
Commands:
- kapeyn
ReleaseDate: $date
Installers:
- Architecture: neutral
  InstallerUrl: $base/Kapeyn-Windows.zip
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: $schema
"@
"$id.locale.en-US.yaml" = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.$schema.schema.json
PackageIdentifier: $id
PackageVersion: $Version
PackageLocale: en-US
Publisher: Kapeyn
PublisherUrl: https://github.com/jvbalcita/kapeyn
PublisherSupportUrl: https://github.com/jvbalcita/kapeyn/issues
Author: Kapeyn contributors
PackageName: Kapeyn
PackageUrl: https://github.com/jvbalcita/kapeyn
License: MIT
LicenseUrl: https://github.com/jvbalcita/kapeyn/blob/main/LICENSE
ShortDescription: Keep your computer awake, intelligently.
Description: |-
  Kapeyn is a tiny tray app that stops sleep, screen lock and chat apps' "Away" status, only when you need it.
  It keeps the PC awake on calls, during downloads, while chosen apps are open or during work hours, includes a
  Lock doctor that checks what could lock your PC, and adds the kapeyn command (kapeyn on 2h, kapeyn run <command>).
Moniker: kapeyn
Tags:
- awake
- caffeine
- keep-awake
- productivity
- screen-lock
- sleep
- tray
ReleaseNotesUrl: https://github.com/jvbalcita/kapeyn/releases/tag/$tag
InstallationNotes: |-
  Run "kapeyn on" to start Kapeyn (look for the coffee cup in the system tray).
  To start it automatically, right-click the cup > Settings > Start with Windows. Help is in the same menu.
Documentations:
- DocumentLabel: Guide
  DocumentUrl: https://github.com/jvbalcita/kapeyn#readme
ManifestType: defaultLocale
ManifestVersion: $schema
"@
}

foreach ($name in $files.Keys) {
    # UTF-8 without BOM, LF line endings (as winget-pkgs expects)
    [IO.File]::WriteAllText((Join-Path $dir $name), ($files[$name] -replace "`r`n", "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
}
Write-Host "Wrote winget manifests for Kapeyn $Version (Kapeyn-Windows.zip SHA-256 $sha) to $dir"
