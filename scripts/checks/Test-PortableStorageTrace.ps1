# Capture file/process events for an isolated synthetic smoke run. Keep the raw
# trace local: it may contain paths of other processes. Inspect it in WPA using
# the process tree and test timestamps; capture alone is not a no-writes proof.
param(
    [Parameter(Mandatory)][string]$Application,
    [string]$ResultDirectory
)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Windows kernel file tracing requires an Administrator PowerShell. No trace session was started.'
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$executable = [IO.Path]::GetFullPath($Application)
if (!(Test-Path -LiteralPath $executable) -or [IO.Path]::GetFileName($executable) -ne 'FlixyPix.exe') { throw 'Select a built FlixyPix.exe.' }
$applicationRoot = Split-Path -Parent $executable
if ((Test-Path -LiteralPath (Join-Path $applicationRoot '.storage-switch')) -or @(Get-ChildItem -LiteralPath $applicationRoot -Directory -Filter '.storage-switch.completed-*').Count) {
    throw 'Use a fresh extracted application copy with no pending storage recovery.'
}
if (!$ResultDirectory) { $ResultDirectory = Join-Path $projectRoot ('artifacts\storage-traces\' + [Guid]::NewGuid().ToString('N')) }
$resultRoot = [IO.Path]::GetFullPath($ResultDirectory)
if (Test-Path -LiteralPath $resultRoot) { throw 'Use a new trace result directory.' }
New-Item -ItemType Directory -Path $resultRoot | Out-Null
$traceName = 'FlixyPixStorage-' + [Guid]::NewGuid().ToString('N')
$providers = Join-Path $resultRoot 'providers.txt'
@(
    '{EDD08927-9CC4-4E65-B970-C2560FB5C289} 0x1EB0 4',
    '{22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716} 0x30 4'
) | Set-Content -LiteralPath $providers -Encoding ASCII
$trace = Join-Path $resultRoot 'file-access.etl'
$started = $false
$smoke = Join-Path $resultRoot 'smoke'
try {
    # Short SQLite/native-tool bursts exhausted the default 8 KB buffers.
    # Reserve 16 MB, allow growth to 64 MB, and retain the complete sequential log.
    $startOutput = & logman start $traceName -pf $providers -o $trace -bs 256 -nb 64 256 -ets 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Could not start the private trace session: ' + ($startOutput -join ' ')) }
    $started = $true
    $startTime = [DateTime]::UtcNow.ToString('o')
    $code = & (Join-Path $projectRoot 'scripts\tests\Start-HiddenDesktop.ps1') -FilePath $executable -ArgumentList @('--smoke-test', ('"' + $smoke + '"')) -TimeoutSeconds 240
    @{ traceSession=$traceName; application=$executable; startedUtc=$startTime; finishedUtc=[DateTime]::UtcNow.ToString('o'); smokeExitCode=$code; review='pending'; traceQuality='unverified'; trace=$trace; bufferSizeKB=256; minimumBuffers=64; maximumBuffers=256 } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $resultRoot 'capture.json') -Encoding UTF8
    if ($code -ne 0) { throw "Synthetic smoke failed ($code). The trace and test results were retained." }
} finally {
    # Stop only the session created above. Do not cancel a WPR session or another capture.
    if ($started) {
        $stopOutput = & logman stop $traceName -ets 2>&1
        if ($LASTEXITCODE -ne 0) { throw ('The owned trace session could not be stopped: ' + $traceName + '. ' + ($stopOutput -join ' ')) }
    }
}
# Avoid a multi-gigabyte decoded event dump. Keep only the small quality summary;
# raw ETL remains available for the separate process/path review.
$summary = Join-Path $resultRoot 'trace-summary.txt'
$summaryOutput = & tracerpt $trace -o NUL -of CSV -summary $summary -y 2>&1
if ($LASTEXITCODE -ne 0) { throw ('Trace quality could not be checked. Results retained. ' + ($summaryOutput -join ' ')) }
$summaryText = Get-Content -LiteralPath $summary -Raw
$loss = [regex]::Match($summaryText, '(?im)^Total\s+Events\s+Lost\s+(\d+)\s*$')
if (!$loss.Success) { throw 'Trace summary has an unrecognized format. Quality remains unverified; results retained.' }
$lostEvents = [long]$loss.Groups[1].Value
$capturePath = Join-Path $resultRoot 'capture.json'
$capture = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
$capture.traceQuality = if ($lostEvents -eq 0) { 'no-event-loss' } else { 'events-lost' }
$capture | Add-Member -NotePropertyName lostEvents -NotePropertyValue $lostEvents
$capture | ConvertTo-Json | Set-Content -LiteralPath $capturePath -Encoding UTF8
if ($lostEvents -ne 0) { throw "Trace lost $lostEvents events. Results retained; this capture cannot support a complete storage review." }
Write-Output "Captured: $trace"
Write-Output 'Quality: no events lost. File/process attribution still requires review.'
Write-Output 'Review the FlixyPix process tree, including bundled tools, before making claims about external writes.'
