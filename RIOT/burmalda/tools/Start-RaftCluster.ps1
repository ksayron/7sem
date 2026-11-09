#requires -Version 7.4
param([switch]$NoBuild,[string]$RuntimeRoot,[int]$BasePort=5121,[int]$PostgresPort=5547,[int]$ReplicaPort=5548,[switch]$Local)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
if(!$Local){
    Push-Location $taskRoot
    try {
        if(!$NoBuild){docker compose build raft-node-1; if($LASTEXITCODE){throw 'Raft image build failed.'}}
        docker compose up -d --no-deps raft-node-1 raft-node-2 raft-node-3 raft-node-4 raft-node-5
        if($LASTEXITCODE){throw 'Raft container startup failed.'}
    } finally {Pop-Location}
    return
}
if(!$NoBuild){dotnet build (Join-Path $taskRoot 'src/Services/ClusterControl/Burmalda.ClusterControl.Node') --nologo -v:q; if($LASTEXITCODE){throw 'Raft build failed.'}}
foreach($taskNode in 1..5){& "$PSScriptRoot/Set-RaftNode.ps1" -Local -NodeId $taskNode -Action Start -RuntimeRoot $RuntimeRoot -BasePort $BasePort -PostgresPort $PostgresPort -ReplicaPort $ReplicaPort}
Write-Host '[info] Five Raft processes started; their SQLite state is preserved.'
