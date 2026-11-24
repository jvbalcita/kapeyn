# Builds and runs Kapeyn's Windows tests with the C# compiler that ships with Windows (no SDK needed).
# Exit code 0 = all tests passed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }

$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$exe = Join-Path $obj 'KapeynTests.exe'
$src = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs) + @(Get-ChildItem (Join-Path $root 'tests\windows') -Filter *.cs) |
       ForEach-Object { $_.FullName }
$help = Join-Path $root 'help\help.md'

& $csc /nologo /target:exe /main:Kapeyn.Tests.TestMain /optimize+ `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    "/resource:$help,Kapeyn.help.md" "/out:$exe" @src
if ($LASTEXITCODE -ne 0) { throw "Test build failed." }

& $exe $root
exit $LASTEXITCODE
