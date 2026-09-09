# Builds PipDimmer.exe with the C# compiler that ships inside Windows.
# No .NET SDK, no Visual Studio, no admin rights required.
#
# NOTE: kept ASCII-only on purpose. Windows PowerShell 5.1 reads .ps1 files as the system
# ANSI codepage unless they carry a UTF-8 BOM, so non-ASCII text here would be mojibake.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw "In-box C# compiler csc.exe not found. Is .NET Framework 4.x installed?"
}

$out      = Join-Path $root 'PipDimmer.exe'
$src      = Join-Path $root 'src\PipDimmer.cs'
$manifest = Join-Path $root 'src\app.manifest'

foreach ($f in @($src, $manifest)) {
    if (-not (Test-Path $f)) { throw "Missing source file: $f" }
}

if (Test-Path $out) { Remove-Item $out -Force }

Write-Host "compiler : $csc"
Write-Host "output   : $out"
Write-Host ""

# /langversion:5 -- this is the pre-Roslyn compiler, so string interpolation, ?., nameof,
#                   expression-bodied members and `out var` are all unavailable. Stating it
#                   explicitly makes any accidental use fail loudly at build time.
# /platform:x64  -- matches 64-bit Chrome and keeps LONG_PTR marshalling unambiguous.
& $csc `
    /nologo `
    /target:winexe `
    /platform:x64 `
    /optimize+ `
    /langversion:5 `
    /warn:4 `
    "/out:$out" `
    "/win32manifest:$manifest" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $src

if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$fi = Get-Item $out
Write-Host ""
Write-Host ("BUILD OK: {0} ({1:N0} bytes)" -f $fi.FullName, $fi.Length) -ForegroundColor Green
