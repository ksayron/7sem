#requires -Version 7.4
param([string]$RuntimeRoot)
$ErrorActionPreference='Stop'
foreach($taskNode in 1..5){& "$PSScriptRoot/Set-RaftNode.ps1" -NodeId $taskNode -Action Stop -RuntimeRoot $RuntimeRoot}
