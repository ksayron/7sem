#requires -Version 7.4
param(
    [Parameter(Mandatory)][ValidateRange(1,5)][int]$NodeId,
    [Parameter(Mandatory)][ValidateSet('Start','Stop','Restart')][string]$Action,
    [string]$RuntimeRoot,
    [int]$BasePort=5121,
    [int]$PostgresPort=5547,
    [int]$ReplicaPort=5548
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
if (!$RuntimeRoot) { $RuntimeRoot=Join-Path $taskRoot '.artifacts/raft-runtime' }
$taskRuntime=[IO.Path]::GetFullPath($RuntimeRoot)
New-Item -ItemType Directory -Force $taskRuntime | Out-Null
$taskLock=$null
for ($taskAttempt=0;$taskAttempt -lt 50 -and !$taskLock;$taskAttempt++) {
    try { $taskLock=[IO.File]::Open((Join-Path $taskRuntime 'process-management.lock'),'OpenOrCreate','ReadWrite','None') }
    catch [IO.IOException] { Start-Sleep -Milliseconds 100 }
}
if (!$taskLock) { throw 'Another Raft process management operation is running.' }
try {
    $taskManifest=Join-Path $taskRuntime 'processes.json'
    $taskEntries=if(Test-Path $taskManifest){@(Get-Content $taskManifest -Raw | ConvertFrom-Json)}else{@()}
    $taskEntry=$taskEntries | Where-Object NodeId -eq $NodeId | Select-Object -Last 1
    $taskDll=Join-Path $taskRoot 'src/Services/ClusterControl/Burmalda.ClusterControl.Node/bin/Debug/net9.0/Burmalda.ClusterControl.Node.dll'
    $taskSettingsPath=Join-Path $taskRuntime 'settings.json'
    if (Test-Path $taskSettingsPath) {
        $taskSettings=Get-Content $taskSettingsPath -Raw | ConvertFrom-Json
        $BasePort=$taskSettings.BasePort; $PostgresPort=$taskSettings.PostgresPort; $ReplicaPort=$taskSettings.ReplicaPort
    } else { @{BasePort=$BasePort;PostgresPort=$PostgresPort;ReplicaPort=$ReplicaPort} | ConvertTo-Json | Set-Content $taskSettingsPath }
    if ($Action -in @('Stop','Restart') -and $taskEntry) {
        $taskProcess=Get-CimInstance Win32_Process -Filter "ProcessId=$($taskEntry.Pid)"
        if ($taskProcess -and $taskProcess.CommandLine.Contains($taskDll) -and $taskProcess.CommandLine.Contains("--Raft:NodeId $NodeId") -and $taskProcess.CreationDate.ToUniversalTime().Ticks -eq [long]$taskEntry.StartedUtcTicks) {
            # Kill only the participant. Its external Docker commands are intentionally not blindly killed.
            Stop-Process -Id $taskEntry.Pid
            Wait-Process -Id $taskEntry.Pid -Timeout 10 -ErrorAction SilentlyContinue
        }
        $taskEntries=@($taskEntries | Where-Object NodeId -ne $NodeId)
    }
    if ($Action -in @('Start','Restart')) {
        if (!(Test-Path -LiteralPath $taskDll)) { throw 'Build Burmalda.ClusterControl.Node first.' }
        $taskPort=$BasePort+$NodeId-1
        if(Get-NetTCPConnection -State Listen -LocalPort $taskPort -ErrorAction SilentlyContinue) {
            $taskOwner=if($taskEntry){Get-CimInstance Win32_Process -Filter "ProcessId=$($taskEntry.Pid)"}else{$null}
            if($Action -eq 'Start' -and $taskOwner -and $taskOwner.CommandLine.Contains($taskDll) -and $taskOwner.CreationDate.ToUniversalTime().Ticks -eq [long]$taskEntry.StartedUtcTicks) { Write-Host "Node $NodeId already runs."; return }
            throw "Port $taskPort belongs to another process."
        }
        $taskEnv=@{ ASPNETCORE_ENVIRONMENT='Development'; Raft__RuntimeRoot=$taskRuntime; ReplicationLab__ComposeRoot=$taskRoot }
        Push-Location $taskRoot
        try {
            $taskConfig=docker compose config --format json | ConvertFrom-Json
            if($LASTEXITCODE){throw 'Cannot read Docker Compose configuration.'}
            $taskAdmin=$taskConfig.services.postgres.environment.POSTGRES_USER
            $taskPassword=$taskConfig.services.postgres.environment.POSTGRES_PASSWORD
            $taskEnv['ReplicationLab__Nodes__postgres']="Host=127.0.0.1;Port=$PostgresPort;Database=postgres;Username=$taskAdmin;Password=$taskPassword"
            $taskEnv['ReplicationLab__Nodes__postgres-replica']="Host=127.0.0.1;Port=$ReplicaPort;Database=postgres;Username=$taskAdmin;Password=$taskPassword"
            $taskEnv['ReplicationLab__CatalogPassword']=$taskConfig.services.postgres.environment.CATALOG_DB_PASSWORD
        } finally { Pop-Location }
        $taskArgs=@("`"$taskDll`"",'--Raft:NodeId',"$NodeId",'--Raft:BasePort',"$BasePort")
        $taskInput=Join-Path $taskRuntime 'empty-input.txt'
        if(!(Test-Path $taskInput)){[IO.File]::WriteAllText($taskInput,'')}
        $taskProcess=Start-Process dotnet -ArgumentList $taskArgs -Environment $taskEnv -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru -RedirectStandardInput $taskInput -RedirectStandardOutput (Join-Path $taskRuntime "node-$NodeId.log") -RedirectStandardError (Join-Path $taskRuntime "node-$NodeId.err")
        $taskIdentity=Get-CimInstance Win32_Process -Filter "ProcessId=$($taskProcess.Id)"
        $taskEntries=@($taskEntries | Where-Object NodeId -ne $NodeId)
        $taskEntries += [pscustomobject]@{NodeId=$NodeId;Pid=$taskProcess.Id;StartedUtcTicks=$taskIdentity.CreationDate.ToUniversalTime().Ticks;Port=$taskPort}
        $taskReady=$false
        for($taskAttempt=0;$taskAttempt -lt 40 -and !$taskReady;$taskAttempt++) {
            if($taskProcess.HasExited){throw "Node $NodeId exited. Inspect $taskRuntime/node-$NodeId.err"}
            try { Invoke-WebRequest "http://127.0.0.1:$taskPort/health/ready" -TimeoutSec 1 | Out-Null; $taskReady=$true } catch { Start-Sleep -Milliseconds 150 }
        }
        if(!$taskReady){throw "Node $NodeId did not become ready."}
        Write-Host "[info] Raft Node $NodeId at $taskPort"
    }
    ConvertTo-Json -InputObject @($taskEntries) -Depth 6 | Set-Content $taskManifest
} finally { $taskLock.Dispose() }
