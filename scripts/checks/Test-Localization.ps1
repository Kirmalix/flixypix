param(
    [string]$Application = '',
    [ValidateNotNullOrEmpty()]
    [ValidateSet('de','en','fr','es','it','pt','nl','pl','ja','ru','qps')]
    [string[]]$Languages = @('de','en','fr','es','it','pt','nl','pl','ja','ru','qps'),
    [switch]$FullSmoke
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Application) { $Application = Join-Path $projectRoot 'artifacts\portable-FlixyPix\FlixyPix.exe' }
if (!(Test-Path -LiteralPath $Application)) { throw 'Build the portable app first.' }
$testRoot = Join-Path $projectRoot ('artifacts\localization\run-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
foreach ($language in $Languages) {
    $directory = Join-Path $testRoot $language
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    @{ Language = $(if ($language -eq 'qps') { 'de' } else { $language }) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'appsettings.json') -Encoding UTF8
    $arguments = @('--localization-test', ('"' + $directory + '"'))
    if ($language -eq 'qps') { $arguments = @('--language','qps') + $arguments }
    # Test windows run on an invisible desktop of their own and never appear on the screen.
    $exitCode = & (Join-Path $PSScriptRoot '..\tests\Start-HiddenDesktop.ps1') -FilePath $Application -ArgumentList $arguments -TimeoutSeconds 120
    if ($exitCode -eq -1) { throw "Localization test timed out: $language" }
    $result = Join-Path $directory 'result.txt'
    if ($exitCode -ne 0 -or !(Test-Path -LiteralPath $result) -or !((Get-Content -LiteralPath $result -Raw) -like 'PASS:*')) { throw "Localization test failed: $language ($directory)" }
    Write-Output "PASS $language"
    if ($FullSmoke) {
        $smoke = Join-Path $directory 'full-smoke'
        $exitCode = & (Join-Path $PSScriptRoot '..\tests\Start-HiddenDesktop.ps1') -FilePath $Application -ArgumentList @('--language',$language,'--smoke-test',('"'+$smoke+'"')) -TimeoutSeconds 180
        if ($exitCode -eq -1) { throw "Full smoke timed out: $language" }
        $smokeResult = Join-Path $smoke 'result.txt'
        if ($exitCode -ne 0 -or !(Test-Path -LiteralPath $smokeResult) -or !((Get-Content -LiteralPath $smokeResult -Raw) -like 'PASS:*')) { throw "Full smoke failed: $language ($smoke)" }
        Write-Output "PASS full-smoke $language"
    }
}
# A new process reads the setting actually written by the settings window in the previous process.
$restart = Join-Path $testRoot 'restart-en'
New-Item -ItemType Directory -Path $restart -Force | Out-Null
Copy-Item -LiteralPath (Join-Path (Join-Path $testRoot $Languages[0]) 'saved-settings.json') -Destination (Join-Path $restart 'appsettings.json')
$exitCode = & (Join-Path $PSScriptRoot '..\tests\Start-HiddenDesktop.ps1') -FilePath $Application -ArgumentList @('--localization-test', ('"' + $restart + '"')) -TimeoutSeconds 120
if ($exitCode -eq -1) { throw 'Language restart test timed out.' }
if ($exitCode -ne 0 -or (Get-Content -LiteralPath (Join-Path $restart 'language.txt') -TotalCount 1) -ne 'en') { throw 'Saved language was not applied after restart.' }
Write-Output "PASS restart-en; screenshots: $testRoot"
