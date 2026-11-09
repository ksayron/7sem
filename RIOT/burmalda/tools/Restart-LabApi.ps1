param([switch]$Local)
# Restart only the managed LR5 coordinator; leave both databases and other services running.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if(!$Local){
    Push-Location $taskRoot
    try {docker compose restart concurrency-lab; if($LASTEXITCODE){throw 'Laboratory API restart failed.'}}
    finally {Pop-Location}
    return
}
$taskLogs = Join-Path $taskRoot '.artifacts/lab-runtime'
$taskManifest = Join-Path $taskLogs 'processes.json'
$taskEntries = @(Get-Content $taskManifest -Raw | ConvertFrom-Json)
$taskEntry = $taskEntries | Where-Object Name -eq 'Burmalda.ConcurrencyLab.Api'
if (!$taskEntry) { throw 'Start the managed lab first.' }
$taskProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($taskEntry.Pid)"
if ($taskProcess -and $taskProcess.CommandLine -notlike "*$($taskEntry.Project)*") { throw 'PID belongs to another process; refusing to stop it.' }
if ($taskProcess) { Stop-Process -Id $taskEntry.Pid -Force; Wait-Process -Id $taskEntry.Pid -ErrorAction SilentlyContinue }
$taskKeys = @('ASPNETCORE_ENVIRONMENT','ReplicationLab__JournalRoot','ReplicationLab__ComposeRoot','ReplicationLab__Nodes__postgres','ReplicationLab__Nodes__postgres-replica','ReplicationLab__CatalogPassword')
$taskPrevious = @{}
foreach ($taskKey in $taskKeys) { $taskPrevious[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey,'Process') }
Push-Location $taskRoot
try {
    $taskCompose = docker compose config --format json | ConvertFrom-Json
    $taskAdmin = $taskCompose.services.postgres.environment.POSTGRES_USER
    $taskPassword = $taskCompose.services.postgres.environment.POSTGRES_PASSWORD
    $taskPort = $taskCompose.services.postgres.ports[0].published
    $taskReplicaPort = $taskCompose.services.'postgres-replica'.ports[0].published
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ReplicationLab__JournalRoot = Join-Path $taskRoot '.artifacts/streaming-replication-raft'
    $env:ReplicationLab__ComposeRoot = $taskRoot
    $env:ReplicationLab__Nodes__postgres = "Host=127.0.0.1;Port=$taskPort;Database=postgres;Username=$taskAdmin;Password=$taskPassword"
    Set-Item 'Env:ReplicationLab__Nodes__postgres-replica' "Host=127.0.0.1;Port=$taskReplicaPort;Database=postgres;Username=$taskAdmin;Password=$taskPassword"
    $env:ReplicationLab__CatalogPassword = $taskCompose.services.postgres.environment.CATALOG_DB_PASSWORD
    $taskDll = Join-Path $taskEntry.Project 'bin/Debug/net9.0/Burmalda.ConcurrencyLab.Api.dll'
    $taskNew = Start-Process dotnet -ArgumentList @("`"$taskDll`"", '--urls', 'http://localhost:5107') -WorkingDirectory $taskEntry.Project -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskLogs 'Burmalda.ConcurrencyLab.Api.log') -RedirectStandardError (Join-Path $taskLogs 'Burmalda.ConcurrencyLab.Api.err')
    $taskEntry.Pid = $taskNew.Id
    $taskEntries | ConvertTo-Json -AsArray | Set-Content $taskManifest
    for ($taskTry=0; $taskTry -lt 30; $taskTry++) {
        try { Invoke-RestMethod http://localhost:5107/health/ready | Out-Null; Write-Host '[info] LR5 API restarted; persistent journal retained.'; return } catch { Start-Sleep -Milliseconds 300 }
    }
    throw 'Restarted API did not become ready.'
} finally {
    foreach ($taskKey in $taskKeys) { [Environment]::SetEnvironmentVariable($taskKey,$taskPrevious[$taskKey],'Process') }
    Pop-Location
}
