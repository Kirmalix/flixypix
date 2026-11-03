param(
    [Parameter(Mandatory, ParameterSetName='Targeted')][string]$Filter,
    [Parameter(Mandatory, ParameterSetName='Full')][switch]$Full,
    [string]$Project = 'tests/FlixyPix.Core.Tests',
    [switch]$KeepTestArtifacts
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workspaceRoot = Join-Path ([IO.Path]::GetTempPath()) 'FlixyPix-test-runs'
if ((Test-Path -LiteralPath $workspaceRoot) -and ((Get-Item -LiteralPath $workspaceRoot).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Test workspace root cannot be a link.' }
New-Item -ItemType Directory -Path $workspaceRoot -Force | Out-Null
. (Join-Path $PSScriptRoot 'TestWorkspace.ps1')
foreach ($old in Get-ChildItem -LiteralPath $workspaceRoot -Directory) {
    try { Remove-OwnedRun $old.FullName } catch { Write-Warning "Test cleanup: $($_.Exception.Message)" }
}
$runId = [Guid]::NewGuid().ToString('N')
$run = Join-Path $workspaceRoot $runId
New-Item -ItemType Directory -Path $run | Out-Null
$lease = [IO.File]::Open((Join-Path $run 'run.lock'), 'CreateNew', 'ReadWrite', 'ReadWrite')
@{ id=$runId; repository=$repository; keep=[bool]$KeepTestArtifacts } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $run 'owner.json') -Encoding UTF8
$priorRoot = $env:FLIXYPIX_TEST_RUN_ROOT
$priorCapture = $env:FLIXYPIX_TEST_CAPTURE
$env:FLIXYPIX_TEST_RUN_ROOT = $run
$env:FLIXYPIX_TEST_CAPTURE = if ($KeepTestArtifacts) { '1' } else { '0' }
Push-Location $repository
try {
    $projects = if ($Full) { @(Get-ChildItem tests -Recurse -Filter '*.csproj' | ForEach-Object FullName) } else { @($Project) }
    $selection = if ($Full) { 'Category!=Slow' } else { "($Filter)&Category!=Slow" }
    foreach ($testProject in $projects) {
        & .tools/dotnet/dotnet.exe test $testProject -c Release --no-restore --filter $selection
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $testProject ($LASTEXITCODE)" }
    }
} finally {
    Pop-Location
    $env:FLIXYPIX_TEST_RUN_ROOT = $priorRoot
    $env:FLIXYPIX_TEST_CAPTURE = $priorCapture
    $lease.Dispose()
    if ($KeepTestArtifacts) { Write-Output "Retained test workspace: $run" }
    else { try { Remove-OwnedRun $run } catch { Write-Warning "Test workspace remains: $run ($($_.Exception.Message))" } }
}
