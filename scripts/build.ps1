# Tests marked Category=Slow (large catalogs, time budgets) run only with -FullTests.
# -InformationalVersion overrides the version shown in the application and readmes (test builds, see
# scripts\release\new-test-build.ps1); without it the value of Directory.Build.props applies.
param([switch]$BuildOnly, [switch]$KeepTestArtifacts, [switch]$SkipTests, [switch]$FullTests, [switch]$SkipUiSmoke, [switch]$NoMediaTools, [string]$OutputDirectory, [string]$InformationalVersion)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { throw 'Run scripts\bootstrap.ps1 first.' }
$env:DOTNET_ROOT = Split-Path $dotnet
$priorCapture = $env:FLIXYPIX_TEST_CAPTURE
$env:FLIXYPIX_TEST_CAPTURE = if ($KeepTestArtifacts) { "1" } else { "0" }
Push-Location $projectRoot
try {
    # Old build, test and tool output would otherwise fill the disk over time.
    try { & (Join-Path $PSScriptRoot 'clean-workspace.ps1') | ForEach-Object { Write-Host $_ } } catch { Write-Warning "Workspace cleanup: $($_.Exception.Message)" }
    $versionArguments = if ($InformationalVersion) { @("-p:InformationalVersion=$InformationalVersion") } else { @() }
    & $dotnet build FlixyPix.slnx --configuration Release --nologo @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if ($BuildOnly) { return }
    if (!$SkipTests) {
        $filter = if ($FullTests) { @() } else { @('--filter', 'Category!=Slow') }
        foreach ($project in (Get-ChildItem -Path tests -Filter '*.csproj' -Recurse)) {
            & $dotnet test $project.FullName --configuration Release --no-build --nologo @filter
            if ($LASTEXITCODE -ne 0) { throw "Tests failed: $($project.BaseName)" }
        }
        python -m unittest discover -s scripts\tests -p 'test_*.py'
        if ($LASTEXITCODE -ne 0) { throw 'Static extraction tests failed.' }
    }
    # Checks of the internal documentation (branding, feature ledger, settings of the original) run where those
    # files exist; the public repository does not contain them.
    if (Test-Path -LiteralPath 'scripts\checks\check-branding.py') {
        python scripts\checks\check-branding.py
        if ($LASTEXITCODE -ne 0) { throw 'Unclassified previous product identity found.' }
        python scripts\checks\check-feature-coverage.py
        if ($LASTEXITCODE -ne 0) { throw 'Incomplete or invalid feature ledger.' }
        python scripts\checks\settings-coverage.py
        if ($LASTEXITCODE -ne 0) { throw 'Unclassified or inconsistent setting of the original.' }
    }
    python scripts\checks\i18n.py --complete de,fr,es,it,pt,nl,pl,ja,ru
    if ($LASTEXITCODE -ne 0) { throw 'Translation files or untranslated texts need attention (CONTRIBUTING.md, Translations).' }
    $portable = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts\portable-FlixyPix' }
    if (Test-Path -LiteralPath $portable) { throw 'Publish directory already exists. Preserve its data and move it aside before creating a fresh release.' }
    & $dotnet publish src\FlixyPix.Desktop\FlixyPix.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false --output $portable --nologo @versionArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    if (!$NoMediaTools) {
        $tools = Join-Path $portable 'tools'
        New-Item -ItemType Directory -Path $tools -Force | Out-Null
        $ffmpeg = Get-ChildItem -LiteralPath '.tools\media\ffmpeg' -Recurse -File -Filter ffmpeg.exe | Select-Object -First 1
        if (!$ffmpeg) { throw 'Media tools absent. Run bootstrap.ps1 or select -NoMediaTools.' }
        Copy-Item -LiteralPath $ffmpeg.FullName -Destination $tools -Force
        Copy-Item -LiteralPath (Join-Path $ffmpeg.DirectoryName 'ffprobe.exe') -Destination $tools -Force
        # mpv needs its libraries beside it; the FFmpeg programs are self-contained and do not use them.
        Copy-Item -LiteralPath '.tools\media\mpv\mpv.exe' -Destination $tools -Force
        Copy-Item -Path '.tools\media\mpv\*.dll' -Destination $tools -Force
        $notices = Join-Path $tools 'notices'
        New-Item -ItemType Directory -Path $notices -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $ffmpeg.Directory.Parent.FullName 'LICENSE') -Destination (Join-Path $notices 'FFmpeg-LICENSE.txt') -Force
        Copy-Item -LiteralPath (Join-Path $ffmpeg.Directory.Parent.FullName 'README.txt') -Destination (Join-Path $notices 'FFmpeg-build.txt') -Force
        Copy-Item -LiteralPath '.tools\media\mpv-license\LICENSE.GPL' -Destination (Join-Path $notices 'mpv-LICENSE.GPL') -Force
        Copy-Item -LiteralPath 'docs\release\media-tools-lock.json' -Destination $notices -Force
    }
    # Takeover of original catalogs: only the exporter. SQL Server Compact 4.0 is not shipped; the exporter loads the
    # copy in the original's program folder or a system-wide installation (THIRD-PARTY-NOTICES.md).
    $legacy = Join-Path $portable 'legacy-import'
    if (Test-Path -LiteralPath $legacy) { Remove-Item -LiteralPath $legacy -Recurse -Force }
    New-Item -ItemType Directory -Path $legacy -Force | Out-Null
    $exporterBuild = 'src\FlixyPix.LegacyExporter\bin\Release\net48'
    foreach ($file in @('FlixyPix.LegacyExporter.exe','FlixyPix.LegacyExporter.exe.config')) { Copy-Item -LiteralPath (Join-Path $exporterBuild $file) -Destination $legacy -Force }
    Copy-Item -LiteralPath 'src\FlixyPix.LegacyExporter\LICENSE' -Destination (Join-Path $legacy 'LICENSE-MIT.txt') -Force
    Copy-Item -LiteralPath 'LICENSE' -Destination $portable -Force
    Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.md' -Destination $portable -Force
    Copy-Item -LiteralPath 'docs\release\portable-readme.en.md' -Destination (Join-Path $portable 'README.md') -Force
    Copy-Item -LiteralPath 'docs\release\portable-readme.md' -Destination (Join-Path $portable 'README.de.md') -Force
    # The readmes name the version actually built.
    $shownVersion = if ($InformationalVersion) { $InformationalVersion } else { @(([xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)).Project.PropertyGroup.InformationalVersion | Where-Object { $_ })[0] }
    foreach ($readme in @('README.md', 'README.de.md')) {
        $path = Join-Path $portable $readme
        $lines = [IO.File]::ReadAllLines($path)
        $lines[0] = $lines[0] -replace '^# FlixyPix \S+', "# FlixyPix $shownVersion"
        [IO.File]::WriteAllLines($path, $lines, [Text.UTF8Encoding]::new($false))
    }
    python scripts\release\collect-notices.py --publish-directory $portable --output (Join-Path $portable 'tools\notices\dependencies')
    if ($LASTEXITCODE -ne 0) { throw 'Dependency notice collection failed.' }
    [IO.File]::WriteAllText((Join-Path $portable 'portable.flag'), '')
    $releaseFiles = @(Get-ChildItem -LiteralPath $portable -Recurse -File | ForEach-Object { $_.FullName.Substring(([IO.Path]::GetFullPath($portable).TrimEnd('\') + '\').Length).Replace('\','/') })
    [IO.File]::WriteAllText((Join-Path $portable 'release-files.json'), (ConvertTo-Json -InputObject $releaseFiles))
    if (!$SkipUiSmoke) {
        $smoke = Join-Path ([IO.Path]::GetTempPath()) ('FlixyPix-ui-' + [Guid]::NewGuid().ToString('N'))
        $application = Join-Path $portable 'FlixyPix.exe'
        # On an invisible desktop of its own: no window appears on the screen and none takes the focus.
        try {
        $exitCode = & (Join-Path $PSScriptRoot 'tests\Start-HiddenDesktop.ps1') -FilePath $application -ArgumentList @('--smoke-test', ('"' + $smoke + '"')) -TimeoutSeconds 600
        if ($exitCode -eq -1) { throw 'UI smoke test timed out.' }
        if ($exitCode -ne 0) { throw "UI smoke test failed ($exitCode)." }
        } finally {
            if (!$KeepTestArtifacts -and (Test-Path -LiteralPath $smoke)) {
                $resolvedSmoke = [IO.Path]::GetFullPath($smoke)
                if ([IO.Path]::GetDirectoryName($resolvedSmoke).TrimEnd('\') -ne [IO.Path]::GetTempPath().TrimEnd('\')) { throw 'Unexpected UI test cleanup path.' }
                $links = @(Get-Item -LiteralPath $resolvedSmoke; Get-ChildItem -LiteralPath $resolvedSmoke -Recurse -Force) | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }
                if ($links) { Write-Warning "Linked UI test workspace retained: $resolvedSmoke" }
                else { Remove-Item -LiteralPath $resolvedSmoke -Recurse -Force }
            }
        }

    }
    Write-Output "Ready: $portable\FlixyPix.exe"
} finally { $env:FLIXYPIX_TEST_CAPTURE = $priorCapture; Pop-Location }
