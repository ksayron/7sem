#requires -Version 7.4
param([switch]$NoBuild,[int]$PostgresPort=5547,[int]$ReplicaPort=5548)
& "$PSScriptRoot/Start-Lab.ps1" -Full -NoBuild:$NoBuild -PostgresPort $PostgresPort -ReplicaPort $ReplicaPort
if($LASTEXITCODE){throw 'Lab startup failed.'}
Write-Host '[info] Open http://localhost:5300/raft-lab'
