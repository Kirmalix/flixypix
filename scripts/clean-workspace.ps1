# Removes build, test and tool output that is no longer needed, so local runs do not fill the disk over time.
# build.ps1, tests\Invoke-Tests.ps1 and tests\Start-HiddenDesktop.ps1 run it automatically, at most every few hours.
#
# - artifacts\: release packages under releases\ keep the newest -KeepReleases versions; every other top-level entry
#   goes once it is older than -MaxAgeDays. README.md and entries starting with "." stay.
# - %TEMP%: folders and files of tests and tools (flixypix-*, FlixyPix-ui-*, storage-tests-*) older than
#   -TempAgeDays; finished runs of tests\Invoke-Tests.ps1 through its own ownership check.
#
# Never removed: a running FlixyPix and its folder, portable installations with their library (FlixyPix.exe beside a
# Data folder) under artifacts\, linked entries, and files still in use. Test catalogs are output like any other.
param([int]$MaxAgeDays = 3, [int]$TempAgeDays = 1, [int]$KeepReleases = 2, [double]$EveryHours = 6, [switch]$Force, [switch]$WhatIf)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = Join-Path $repository 'artifacts'
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$stamp = Join-Path $artifacts '.cleanup-stamp'
if (!$Force -and !$WhatIf -and (Test-Path -LiteralPath $stamp) -and ((Get-Date) - (Get-Item -LiteralPath $stamp).LastWriteTime).TotalHours -lt $EveryHours) { return }

$running = @(Get-Process -Name FlixyPix -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Path } catch { $null } } | Where-Object { $_ })
$freed = 0L

function Test-Running([IO.FileSystemInfo]$entry) {
    $root = $entry.FullName.TrimEnd('\') + '\'
    return [bool]($running | Where-Object { $_.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) })
}

function Test-Protected([IO.FileSystemInfo]$entry) {
    if (!$entry.PSIsContainer) { return $false }
    if (Test-Running $entry) { return $true }
    # A portable installation with its library: FlixyPix.exe beside a Data folder. Test catalogs alone do not count.
    $found = Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue -Attributes !ReparsePoint -Filter FlixyPix.exe |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.DirectoryName 'Data') } | Select-Object -First 1
    return [bool]$found
}

function Remove-Entry([IO.FileSystemInfo]$entry, [string]$within, [string]$reason, [switch]$CheckData) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Write-Warning "Linked entry kept: $($entry.FullName)"; return }
    $parent = [IO.Path]::GetDirectoryName($entry.FullName.TrimEnd('\'))
    if (!$parent.StartsWith($within, [StringComparison]::OrdinalIgnoreCase)) { throw "Unexpected cleanup path: $($entry.FullName)" }
    if ($entry.PSIsContainer -and @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force -ErrorAction SilentlyContinue | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { Write-Warning "Entry with links kept: $($entry.FullName)"; return }
    if ($entry.PSIsContainer -and (Test-Running $entry)) { Write-Output "Kept (running): $($entry.Name)"; return }
    if ($CheckData -and (Test-Protected $entry)) { Write-Output "Kept (installation with library data): $($entry.Name)"; return }
    $size = if ($entry.PSIsContainer) { (Get-ChildItem -LiteralPath $entry.FullName -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum } else { $entry.Length }
    if ($WhatIf) { Write-Output ("Would remove ({0}, {1:N0} MB): {2}" -f $reason, ($size / 1MB), $entry.FullName); $script:freed += $size; return }
    try { Remove-Item -LiteralPath $entry.FullName -Recurse -Force; $script:freed += $size }
    catch { Write-Warning "In use, kept: $($entry.FullName)" }
}

# 1. artifacts\
if (Test-Path -LiteralPath $artifacts) {
    $cutoff = (Get-Date).AddDays(-$MaxAgeDays)
    $releases = Join-Path $artifacts 'releases'
    if (Test-Path -LiteralPath $releases) {
        # A release folder, its ZIP, manifest and checksum share one name and go together.
        $groups = Get-ChildItem -LiteralPath $releases -Force | Group-Object { $_.Name -replace '(\.zip\.sha256|\.zip|\.manifest\.json)$', '' }
        $ordered = $groups | Sort-Object { ($_.Group | Measure-Object LastWriteTime -Maximum).Maximum } -Descending
        foreach ($group in ($ordered | Select-Object -Skip $KeepReleases)) {
            if ($group.Group | Where-Object { Test-Protected $_ }) { Write-Output "Kept release with library data or running: $($group.Name)"; continue }
            foreach ($entry in $group.Group) { Remove-Entry $entry $releases 'older release' }
        }
    }
    foreach ($entry in Get-ChildItem -LiteralPath $artifacts -Force) {
        if ($entry.Name -in @('README.md', 'releases') -or $entry.Name.StartsWith('.') -or $entry.LastWriteTime -gt $cutoff) { continue }
        Remove-Entry $entry $artifacts "older than $MaxAgeDays days" -CheckData
    }
}

# 2. Leftovers of tests and tools in %TEMP%
$tempCutoff = (Get-Date).AddDays(-$TempAgeDays)
foreach ($entry in Get-ChildItem -LiteralPath $temp -Force -ErrorAction SilentlyContinue) {
    if ($entry.Name -notmatch '^(flixypix-|FlixyPix-ui-|storage-tests-)' -or $entry.LastWriteTime -gt $tempCutoff) { continue }
    Remove-Entry $entry $temp "test output older than $TempAgeDays days"
}
$runs = Join-Path $temp 'FlixyPix-test-runs'
if (!$WhatIf -and (Test-Path -LiteralPath $runs) -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'tests\TestWorkspace.ps1'))) {
    $workspaceRoot = $runs
    . (Join-Path $PSScriptRoot 'tests\TestWorkspace.ps1')
    foreach ($old in Get-ChildItem -LiteralPath $runs -Directory) { try { Remove-OwnedRun $old.FullName } catch { Write-Warning "Test cleanup: $($_.Exception.Message)" } }
}

if ($WhatIf) { Write-Output ("Would free {0:N0} MB." -f ($freed / 1MB)); return }
if ($freed -gt 0) { Write-Output ("Workspace cleanup freed {0:N0} MB." -f ($freed / 1MB)) }
if (Test-Path -LiteralPath $artifacts) { [IO.File]::WriteAllText($stamp, (Get-Date).ToString('o')) }
