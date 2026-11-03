# Explicit interactive, synthetic-only input acceptance. Run by the tester at a physical keyboard/mouse.
param(
 [Parameter(Mandatory)][string]$FilePath,
 [Parameter(Mandatory)][ValidateSet('plain','encrypted')][string]$Mode,
 [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$program=(Resolve-Path -LiteralPath $FilePath).Path
$target=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $target){throw 'Choose a new output directory. Existing test runs and files are kept.'}
if(-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $program) 'tools/ffmpeg.exe'))){throw 'Use the bundled package with tools/ffmpeg.exe and tools/mpv.exe.'}
if(-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $program) 'tools/mpv.exe'))){throw 'The bundled mpv executable is missing.'}
if($target.Contains('"')){throw 'The output directory must not contain a quote.'}
$launchInfo=New-Object System.Diagnostics.ProcessStartInfo
$launchInfo.FileName=$program
$launchInfo.Arguments='--player-input-test '+$Mode+' "'+$target.TrimEnd([char[]]'\/')+'"'
$launchInfo.WorkingDirectory=Split-Path -Parent $program
$launchInfo.UseShellExecute=$false
$launchInfo.WindowStyle=[System.Diagnostics.ProcessWindowStyle]::Normal
# Scope the repository runtime to this child process; leave the caller's environment unchanged.
$localRuntime=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../.tools/dotnet'))
if(Test-Path -LiteralPath (Join-Path $localRuntime 'dotnet.exe')){
 $launchInfo.EnvironmentVariables['DOTNET_ROOT']=$localRuntime
 $launchInfo.EnvironmentVariables['DOTNET_ROOT_X64']=$localRuntime
}
$process=[System.Diagnostics.Process]::Start($launchInfo)
Write-Output "Input acceptance process $($process.Id). Complete the checks in the visible report window."
Write-Output "Report: $(Join-Path $target 'player-input-result.json')"
