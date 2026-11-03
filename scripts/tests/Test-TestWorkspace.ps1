$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'TestWorkspace.ps1')
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$workspaceRoot=Join-Path ([IO.Path]::GetTempPath()) ('FlixyPix-cleanup-check-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $workspaceRoot | Out-Null
function New-Probe([bool]$keep) {
 $id=[Guid]::NewGuid().ToString('N');$path=Join-Path $workspaceRoot $id
 New-Item -ItemType Directory -Path $path | Out-Null
 @{id=$id;repository=$repository;keep=$keep}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $path 'owner.json')
 [IO.File]::WriteAllText((Join-Path $path 'run.lock'),'')
 return $path
}
$active=$null
try {
 $normal=New-Probe $false;Remove-OwnedRun $normal
 if(Test-Path -LiteralPath $normal){throw 'Completed run was retained.'}
 $kept=New-Probe $true;Remove-OwnedRun $kept
 if(!(Test-Path -LiteralPath $kept)){throw 'Kept run was removed.'}
 $busy=New-Probe $false;$active=[IO.File]::Open((Join-Path $busy 'run.lock'),'Open','Read','ReadWrite');Remove-OwnedRun $busy
 if(!(Test-Path -LiteralPath $busy)){throw 'Active run was removed.'}
 $rejected=$false;try{Remove-OwnedRun $workspaceRoot}catch{$rejected=$true}
 if(!$rejected){throw 'Outside cleanup path was accepted.'}
 Write-Output 'PASS: owned cleanup, explicit retention, active lease and outside-root guard.'
} finally {
 if($active){$active.Dispose()}
 foreach($directory in Get-ChildItem -LiteralPath $workspaceRoot -Directory){
  $owner=Join-Path $directory.FullName 'owner.json';$value=Get-Content -LiteralPath $owner -Raw|ConvertFrom-Json;$value.keep=$false;$value|ConvertTo-Json|Set-Content -LiteralPath $owner
  Remove-OwnedRun $directory.FullName
 }
 [IO.Directory]::Delete($workspaceRoot)
}
