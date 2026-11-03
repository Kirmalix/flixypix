function Remove-OwnedRun([string]$path) {
    $resolved = [IO.Path]::GetFullPath($path)
    if ([IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($workspaceRoot)) { throw 'Test cleanup escaped its workspace.' }
    if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Test cleanup refuses a linked run directory.' }
    $links = @(Get-ChildItem -LiteralPath $resolved -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count) { Write-Warning "Linked test workspace retained: $resolved"; return }
    $manifestPath = Join-Path $resolved 'owner.json'
    if (!(Test-Path -LiteralPath $manifestPath)) { return }
    $owner = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($owner.repository -ne $repository -or $owner.id -ne (Split-Path $resolved -Leaf) -or $owner.keep) { return }
    # An exclusive open fails while the runner or a participating test process owns the lease.
    try { $lease = [IO.File]::Open((Join-Path $resolved 'run.lock'), 'Open', 'ReadWrite', 'None') } catch [IO.IOException] { return }
    $lease.Dispose()
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
