#requires -Version 7.4
param([switch]$NoBuild,[int]$PostgresPort=5547,[int]$ReplicaPort=5548,[switch]$Local)
$taskOptions=@{Local=$Local;Full=$true;NoBuild=$NoBuild}
foreach($taskPort in @('PostgresPort','ReplicaPort')){if($PSBoundParameters.ContainsKey($taskPort)){$taskOptions[$taskPort]=$PSBoundParameters[$taskPort]}}
& "$PSScriptRoot/Start-Lab.ps1" @taskOptions
if($LASTEXITCODE){throw 'Lab startup failed.'}
Write-Host '[info] Open http://localhost:5300/raft-lab'
