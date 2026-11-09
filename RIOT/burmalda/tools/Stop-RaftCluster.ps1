#requires -Version 7.4
param([string]$RuntimeRoot,[switch]$Local)
$ErrorActionPreference='Stop'
if(!$Local){
    Push-Location (Split-Path $PSScriptRoot -Parent)
    try {docker compose stop raft-node-1 raft-node-2 raft-node-3 raft-node-4 raft-node-5; if($LASTEXITCODE){throw 'Raft stop failed.'}}
    finally {Pop-Location}
    return
}
foreach($taskNode in 1..5){& "$PSScriptRoot/Set-RaftNode.ps1" -Local -NodeId $taskNode -Action Stop -RuntimeRoot $RuntimeRoot}
