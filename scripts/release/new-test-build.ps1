# Makes a test build the same way every time and installs it into the working installation.
#
# 1. Only from a committed state: the commit in the version must be the code that was built.
# 2. Version from Git, no file change: <Version of Directory.Build.props>-dev.<commits since the last release tag>,
#    the SDK appends +<commit>. On a release tag the version of Directory.Build.props applies unchanged.
# 3. Building and testing are separate: a test build only builds, packages and installs. Tests have usually run
#    before (scripts\tests\Invoke-Tests.ps1, a smoke run, CI). -RunTests adds the unit tests, -UiSmoke the UI smoke
#    test against the finished package.
# 4. Package in artifacts\releases\FlixyPix-<version>-win-x64 with ZIP, manifest and checksum; clean-workspace.ps1
#    keeps the newest two there.
# 5. Installs into -InstallTo: the program files are replaced, the Data folder (library, settings, logs) stays.
#    Refuses while FlixyPix runs from there. -MoveDataFrom moves the Data folder of an older installation once,
#    when the working installation has none yet.
param(
    [string]$InstallTo = 'C:\Projekte\FlixyPix-App',
    [string]$MoveDataFrom,
    [switch]$NoInstall,
    [switch]$RunTests,
    [switch]$UiSmoke
)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Push-Location $repository
try {
    # 1. Committed state only.
    $dirty = @(git status --porcelain)
    if ($dirty.Count) { throw "Commit or stash the changes first; a test build is made from a commit only:`n$($dirty -join "`n")" }
    $commit = (git rev-parse HEAD).Trim()

    # A running FlixyPix keeps its files; refuse before building rather than after.
    $install = [IO.Path]::GetFullPath($InstallTo)
    $running = @(Get-Process -Name FlixyPix -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Path } catch { $null } } | Where-Object { $_ })
    function Test-RunsFrom([string]$folder) { $root = $folder.TrimEnd('\') + '\'; return [bool]($running | Where-Object { $_.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) }) }
    if (!$NoInstall -and (Test-RunsFrom $install)) { throw "FlixyPix runs from $install. Close it, then start the test build again." }
    if ($MoveDataFrom -and (Test-RunsFrom ([IO.Path]::GetFullPath($MoveDataFrom)))) { throw "FlixyPix runs from $MoveDataFrom. Close it first." }
    if ($MoveDataFrom -and (Test-Path -LiteralPath (Join-Path $install 'Data'))) { throw "The working installation has a Data folder already; nothing would be moved: $(Join-Path $install 'Data')" }

    # 2. Version.
    $props = [xml](Get-Content -LiteralPath 'Directory.Build.props' -Raw)
    $base = @($props.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    $tag = (git describe --tags --abbrev=0 2>$null)
    $count = if ($tag) { [int](git rev-list --count "$($tag.Trim())..HEAD") } else { [int](git rev-list --count HEAD) }
    $version = if ($count -eq 0) { @($props.Project.PropertyGroup.InformationalVersion | Where-Object { $_ })[0] } else { "$base-dev.$count" }
    Write-Host "Test build $version from $($commit.Substring(0, 7))"

    $name = "FlixyPix-$version-win-x64"
    $releases = Join-Path $repository 'artifacts\releases'
    $output = Join-Path $releases "$name\FlixyPix"
    if (Test-Path -LiteralPath (Join-Path $releases $name)) { throw "This build exists already: $(Join-Path $releases $name)" }

    # 3. Tests only on request.
    Write-Host ("Unit tests: $(if ($RunTests) { 'run' } else { 'not run (-RunTests)' }) · UI smoke test: $(if ($UiSmoke) { 'run' } else { 'not run (-UiSmoke)' })")

    # The profile's TEMP may be an 8.3 short path that SQLite cannot open; the run uses an ASCII folder of its own.
    # No compiler server or build node stays behind to lock it.
    $temp = Join-Path $repository "artifacts\tmp-$version"
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    $prior = @{ TEMP = $env:TEMP; TMP = $env:TMP; UseSharedCompilation = $env:UseSharedCompilation; MSBUILDDISABLENODEREUSE = $env:MSBUILDDISABLENODEREUSE }
    $env:TEMP = $temp; $env:TMP = $temp; $env:UseSharedCompilation = 'false'; $env:MSBUILDDISABLENODEREUSE = '1'
    try {
        $buildArguments = @{ OutputDirectory = $output; InformationalVersion = $version }
        if (!$RunTests) { $buildArguments.SkipTests = $true }
        if (!$UiSmoke) { $buildArguments.SkipUiSmoke = $true }
        & (Join-Path $repository 'scripts\build.ps1') @buildArguments
        # 4. Package.
        python scripts\release\package-portable.py --source $output --output (Join-Path $releases "$name.zip") --version $version
        if ($LASTEXITCODE -ne 0) { throw 'Packaging failed.' }
    } finally {
        foreach ($key in $prior.Keys) { Set-Item -Path "env:$key" -Value $prior[$key] -ErrorAction SilentlyContinue; if ($null -eq $prior[$key]) { Remove-Item -Path "env:$key" -ErrorAction SilentlyContinue } }
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
    # Builds rewrite the lock files for the runtime identifier; the committed ones stay authoritative.
    git checkout -- '*packages.lock.json' 2>$null

    # 5. Working installation.
    if ($NoInstall) { Write-Host "Package ready: $output"; return }
    $running = @(Get-Process -Name FlixyPix -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Path } catch { $null } } | Where-Object { $_ })
    if (Test-RunsFrom $install) { throw "FlixyPix was started from $install during the build; the package is ready in $output." }
    New-Item -ItemType Directory -Path $install -Force | Out-Null
    $data = Join-Path $install 'Data'
    if ($MoveDataFrom) {
        $source = Join-Path ([IO.Path]::GetFullPath($MoveDataFrom)) 'Data'
        if (!(Test-Path -LiteralPath $source)) { throw "No Data folder at $source" }
        Move-Item -LiteralPath $source -Destination $data
        Write-Host "Library moved: $source -> $data"
    }
    foreach ($entry in Get-ChildItem -LiteralPath $install -Force) {
        if ($entry.Name -eq 'Data') { continue }
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked entry in the working installation: $($entry.FullName)" }
        Remove-Item -LiteralPath $entry.FullName -Recurse -Force
    }
    Copy-Item -Path (Join-Path $output '*') -Destination $install -Recurse -Force
    [IO.File]::WriteAllText((Join-Path $install 'installed-build.json'), (@{ version = $version; commit = $commit; installedAt = (Get-Date).ToString('o'); package = $name } | ConvertTo-Json))
    Write-Host "Installed $version into $install$(if (Test-Path -LiteralPath $data) { ' (library kept)' } else { ' (no library yet)' })"
} finally { Pop-Location }
