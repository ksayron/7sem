#requires -Version 7.4
param([switch]$NoBuild,[string]$RuntimeRoot,[int]$BasePort=5121,[int]$PostgresPort=5547,[int]$ReplicaPort=5548)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
if(!$NoBuild){dotnet build (Join-Path $taskRoot 'src/Services/ClusterControl/Burmalda.ClusterControl.Node') --nologo -v:q; if($LASTEXITCODE){throw 'Raft build failed.'}}
foreach($taskNode in 1..5){& "$PSScriptRoot/Set-RaftNode.ps1" -NodeId $taskNode -Action Start -RuntimeRoot $RuntimeRoot -BasePort $BasePort -PostgresPort $PostgresPort -ReplicaPort $ReplicaPort}
Write-Host '[info] Five Raft processes started; their SQLite state is preserved.'
