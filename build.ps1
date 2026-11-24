# Builds dist\Kapeyn.exe using the C# compiler that ships with Windows (no SDK needed).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw "csc.exe not found. .NET Framework 4.x is required (it ships with Windows 10/11)." }

$src      = Join-Path $root 'src\Kapeyn.cs'
$manifest = Join-Path $root 'src\Kapeyn.manifest'
$obj      = Join-Path $root 'obj'
$dist     = Join-Path $root 'dist'
$exe      = Join-Path $dist 'Kapeyn.exe'
New-Item -ItemType Directory -Force $obj, $dist | Out-Null

$common = @('/nologo', '/target:winexe', '/optimize+', '/platform:anycpu',
            '/reference:System.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
            "/win32manifest:$manifest")

# Pass 1: build a throwaway copy and use it to render the app icon.
$iconTool = Join-Path $obj 'Kapeyn-icon.exe'
$ico      = Join-Path $obj 'Kapeyn.ico'
& $csc @common "/out:$iconTool" $src
if ($LASTEXITCODE -ne 0) { throw "Compile failed." }
Start-Process -FilePath $iconTool -ArgumentList '--export-icon', "`"$ico`"" -Wait -NoNewWindow
if (-not (Test-Path $ico)) { throw "Icon export failed." }

# Stop a running copy of dist\Kapeyn.exe so it can be replaced.
$running = Get-Process Kapeyn -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }
if ($running) { $running | Stop-Process -Force; Start-Sleep -Milliseconds 500 }

# Pass 2: the real build, with the icon embedded.
& $csc @common "/win32icon:$ico" "/out:$exe" $src
if ($LASTEXITCODE -ne 0) { throw "Compile failed." }

Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"
