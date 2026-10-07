<#
.SYNOPSIS
  Starts one build of 9Transcribe in tray mode, lets it settle, and reports its memory.

.DESCRIPTION
  Used by CI to compare the Portable and Lite builds, and handy on a real machine too:

    pwsh tools/measure-memory.ps1 -Label "Lite" -Path .\9Transcribe.exe

  "Private working set" is the number Task Manager shows in its Memory column. "Private bytes"
  is everything the process has committed, resident or not. A CI runner has no GPU and no
  microphone, so its numbers are only good for comparing builds with each other.
#>
param(
    [Parameter(Mandatory)][string] $Label,
    [Parameter(Mandatory)][string] $Path,
    [int] $Seconds = 20,
    [switch] $Header
)

$ErrorActionPreference = 'Stop'

function Write-Row([string] $row) {
    Write-Output $row
    if ($env:GITHUB_STEP_SUMMARY) {
        Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $row -Encoding utf8
    }
}

if ($Header) {
    Write-Row ''
    Write-Row '### Memory after start-up (tray mode, idle)'
    Write-Row ''
    Write-Row '| Build | File size | Private working set | Private bytes | Working set |'
    Write-Row '|---|---|---|---|---|'
}

# The app is single-instance; a leftover copy would swallow the launch.
Get-Process -Name '9Transcribe' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

$exe = Get-Item -Path $Path
$sizeMb = $exe.Length / 1MB
$process = Start-Process -FilePath $exe.FullName -ArgumentList '--tray' -PassThru
Start-Sleep -Seconds $Seconds
$process.Refresh()

if ($process.HasExited) {
    Write-Row ("| {0} | {1:N1} MB | exited (code {2}) | - | - |" -f $Label, $sizeMb, $process.ExitCode)
    exit 0
}

$privateWorkingSet = $null
try {
    $counter = Get-CimInstance -ClassName Win32_PerfFormattedData_PerfProc_Process |
        Where-Object { $_.IDProcess -eq $process.Id } |
        Select-Object -First 1
    if ($counter) {
        $privateWorkingSet = [double]$counter.WorkingSetPrivate / 1MB
    }
}
catch {
    Write-Warning "Private working set could not be read: $($_.Exception.Message)"
}

$privateBytes = $process.PrivateMemorySize64 / 1MB
$workingSet = $process.WorkingSet64 / 1MB
Stop-Process -Id $process.Id -Force

$pws = if ($null -ne $privateWorkingSet) { '{0:N1} MB' -f $privateWorkingSet } else { 'n/a' }
Write-Row ("| {0} | {1:N1} MB | {2} | {3:N1} MB | {4:N1} MB |" -f $Label, $sizeMb, $pws, $privateBytes, $workingSet)
