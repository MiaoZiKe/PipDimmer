# Validate with the same interpreter and entry point used by CI. ASCII for PowerShell 5.1.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AhkExePath,
    [string]$ScriptPath
)
$ErrorActionPreference = 'Stop'
if (-not $ScriptPath) { $ScriptPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'ahk\PipDimmer.ahk' }
$ahk = (Resolve-Path -LiteralPath $AhkExePath -ErrorAction Stop).Path
$scriptFile = (Resolve-Path -LiteralPath $ScriptPath -ErrorAction Stop).Path
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($ahk).FileVersion
if ($version -notmatch '^2\.') { throw "AutoHotkey v2 required; found $version" }

function Invoke-AhkCheck([string]$Arguments, [string]$Label) {
    $process = New-Object Diagnostics.Process
    $process.StartInfo.FileName = $ahk
    $process.StartInfo.Arguments = $Arguments
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.CreateNoWindow = $true
    $process.StartInfo.WindowStyle = 'Hidden'
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $process.StartInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    try {
        if (-not $process.Start()) { throw "Could not start AutoHotkey for $Label" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            throw "$Label timed out after 30 seconds"
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "$Label failed (exit $($process.ExitCode)):`n$output"
        }
        Write-Host "$Label OK (AutoHotkey $version)"
        return $output
    }
    finally { $process.Dispose() }
}

$syntaxOutput = Invoke-AhkCheck ('/ErrorStdOut=UTF-8 /validate "' + $scriptFile + '"') 'Syntax validation'
if ($syntaxOutput.Trim()) { Write-Host $syntaxOutput }
$startupOutput = Invoke-AhkCheck ('/force /ErrorStdOut=UTF-8 "' + $scriptFile + '" --self-test') 'Initialization smoke test'
if ($startupOutput.Trim() -ne 'PipDimmer AHK initialization OK') {
    throw "Initialization success marker missing or unexpected output:`n$startupOutput"
}
