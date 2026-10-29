param([switch]$SkipMedia)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolRoot = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null

function FetchVerified($entry, [string]$destination) {
    $algorithm = if ($entry.sha512) { 'SHA512' } else { 'SHA256' }
    $expected = if ($entry.sha512) { $entry.sha512 } else { $entry.sha256 }
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm $algorithm).Hash.ToLowerInvariant() -eq $expected) { return }
        throw "Cached download differs from lock: $destination"
    }
    # Downloads land beside the target first, so an interrupted run never leaves a cached half file.
    $partial = "$destination.part"
    Invoke-WebRequest -UseBasicParsing $entry.url -OutFile $partial
    if ((Get-FileHash -LiteralPath $partial -Algorithm $algorithm).Hash.ToLowerInvariant() -ne $expected) { Remove-Item -LiteralPath $partial; throw "Download checksum mismatch: $destination" }
    Move-Item -LiteralPath $partial -Destination $destination -Force
}

$lock = Get-Content -LiteralPath (Join-Path $projectRoot 'docs\release\toolchain-lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$sdk = $lock | Where-Object name -eq 'dotnet-sdk'
$dotnet = Join-Path $toolRoot 'dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) {
    $archive = Join-Path $toolRoot 'dotnet-sdk.zip'; FetchVerified $sdk $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $toolRoot 'dotnet')
}
if ((& $dotnet --version) -ne $sdk.version) { throw 'Installed local SDK differs from toolchain lock.' }
$env:DOTNET_ROOT = Split-Path $dotnet
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

if (!$SkipMedia) {
    $mediaRoot = Join-Path $toolRoot 'media'
    New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null
    $mediaLock = Get-Content -LiteralPath (Join-Path $projectRoot 'docs\release\media-tools-lock.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($entry in $mediaLock) {
        $destination = Join-Path $mediaRoot $entry.name
        # A single file such as a license text is stored as is.
        if ($entry.file) {
            New-Item -ItemType Directory -Path $destination -Force | Out-Null
            FetchVerified $entry (Join-Path $destination $entry.file)
            continue
        }
        $archive = Join-Path $mediaRoot ($entry.name + '.zip'); FetchVerified $entry $archive
        if (!(Test-Path -LiteralPath $destination)) {
            Expand-Archive -LiteralPath $archive -DestinationPath $destination
            # The mpv release archive holds one inner archive with the program files.
            $content = @(Get-ChildItem -LiteralPath $destination)
            if ($content.Count -eq 1 -and $content[0].Extension -eq '.zip') {
                Expand-Archive -LiteralPath $content[0].FullName -DestinationPath $destination
                Remove-Item -LiteralPath $content[0].FullName
            }
        }
    }
}
Write-Output "DOTNET=$dotnet"
Write-Output 'Local toolchain ready. No system PATH or Windows feature was changed.'
