# Builds Kapeyn with the C# compiler that ships with Windows (no SDK needed):
#   dist\Kapeyn.exe  - the tray app
#   dist\kapeyn.com  - the "kapeyn" command-line tool (same code, console build)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe not found. .NET Framework 4.x is required (it ships with Windows 10/11)." }

$src      = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$manifest = Join-Path $root 'src\Kapeyn.manifest'
$help     = Join-Path $root 'help\help.md'   # the in-app guide, shared with the Mac app
$obj      = Join-Path $root 'obj'
$dist     = Join-Path $root 'dist'
$exe      = Join-Path $dist 'Kapeyn.exe'
$com      = Join-Path $dist 'kapeyn.com'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$common = @('/nologo', '/optimize+', '/platform:anycpu',
            '/reference:System.dll', '/reference:System.Core.dll',
            '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
            "/win32manifest:$manifest", "/resource:$help,Kapeyn.help.md")

function Invoke-Csc([string[]]$extra) {
    & $csc @common @extra @src
    if ($LASTEXITCODE -ne 0) { throw "Compile failed." }
}

# Pass 1: build a throwaway copy and use it to render the app icon.
$iconTool = Join-Path $obj 'Kapeyn-icon.exe'
$ico      = Join-Path $obj 'Kapeyn.ico'
Invoke-Csc @('/target:winexe', "/out:$iconTool")
Start-Process -FilePath $iconTool -ArgumentList '--export-icon', "`"$ico`"" -Wait -NoNewWindow
if (-not (Test-Path $ico)) { throw "Icon export failed." }
Remove-Item $iconTool -Force -ErrorAction SilentlyContinue

# Stop a running copy from dist so it can be replaced.
$running = Get-Process Kapeyn -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { $running | Stop-Process -Force; $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue }

# Pass 2: the real builds, with the icon embedded.
Invoke-Csc @('/target:winexe', "/win32icon:$ico", "/out:$exe")
Invoke-Csc @('/target:exe', '/define:CLI', "/win32icon:$ico", "/out:$com")

# The same command-line tool with an .exe name, for package managers that don't accept .com (winget).
$cli = Join-Path $dist 'kapeyn-cli.exe'
Copy-Item $com $cli -Force

foreach ($f in $exe, $com, $cli) { Write-Host "Built $f ($([math]::Round((Get-Item $f).Length / 1KB)) KB)" }
