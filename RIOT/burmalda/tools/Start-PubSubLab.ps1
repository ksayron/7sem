#requires -Version 7.4
param([switch]$NoBuild, [switch]$Local)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if(!$Local){
    Push-Location $taskRoot
    try {
        $taskArgs=@('compose','--profile','pubsub','up','-d',$(if($NoBuild){'--no-build'}else{'--build'}))
        docker @taskArgs
        if($LASTEXITCODE){throw 'Pub/Sub Docker startup failed.'}
        Write-Host '[info] http://localhost:5300/pubsub-lab'
    } finally {Pop-Location}
    return
}
Push-Location $taskRoot
try {
    if (!$NoBuild) { dotnet build Burmalda.slnx -m:1 --nologo; if ($LASTEXITCODE) { throw 'Build failed.' } }
    docker compose up -d --no-deps postgres postgres-replica
    if ($LASTEXITCODE) { throw 'Docker PostgreSQL startup failed.' }
    $taskConfig = docker compose config --format json | ConvertFrom-Json
    $taskAdmin = $taskConfig.services.postgres.environment.POSTGRES_USER
    & "$PSScriptRoot/Start-RaftCluster.ps1" -Local -NoBuild
    & "$PSScriptRoot/Set-PubSubComponent.ps1" -Local -Component Coordinator -Action Start
    $taskWriter = $null
    for ($taskAttempt=0; $taskAttempt -lt 15 -and !$taskWriter; $taskAttempt++) {
        foreach ($taskNode in @('postgres','postgres-replica')) {
            $taskWritable = docker compose exec -T $taskNode psql -U $taskAdmin -d postgres -Atc "SELECT NOT pg_is_in_recovery() AND NOT current_setting('default_transaction_read_only')::boolean" 2>$null
            if ($LASTEXITCODE -eq 0 -and $taskWritable -eq 't') { $taskWriter = $taskNode; break }
        }
        if (!$taskWriter) { Start-Sleep -Seconds 1 }
    }
    if (!$taskWriter) { throw 'No safe writable PostgreSQL node. Inspect LR5 recovery controls; promotion requires a fenced source and replayed WAL.' }
    docker compose exec -T $taskWriter sh /opt/replication/provision-library.sh
    if ($LASTEXITCODE) { throw 'Library database provisioning failed.' }
    foreach ($taskComponent in @('BrokerA','BrokerB','Subscribers','GameApi','Identity','Gateway','Web')) {
        & "$PSScriptRoot/Set-PubSubComponent.ps1" -Local -Component $taskComponent -Action Start
    }
    $taskReady = $false
    for ($taskAttempt=0; $taskAttempt -lt 40 -and !$taskReady; $taskAttempt++) {
        try {
            foreach ($taskPort in @(5107,5108,5109,5110,5111,5101,5000)) { Invoke-WebRequest "http://localhost:$taskPort/health/ready" -TimeoutSec 2 | Out-Null }
            Invoke-WebRequest 'http://localhost:5300/' -TimeoutSec 2 | Out-Null
            $taskHeaders = @{'X-Library-Key'=$(if($env:Library__InternalKey){$env:Library__InternalKey}else{'library-local-only-change-me'})}
            $taskA = Invoke-RestMethod "http://localhost:5109/internal/snapshot/$([guid]::Empty)" -Headers $taskHeaders -TimeoutSec 2
            $taskB = Invoke-RestMethod "http://localhost:5110/internal/snapshot/$([guid]::Empty)" -Headers $taskHeaders -TimeoutSec 2
            $taskReady = $taskA.subscriptions.Count -ge 4 -and $taskB.subscriptions.Count -ge 1
        } catch { }
        if (!$taskReady) { Start-Sleep -Seconds 1 }
    }
    if (!$taskReady) { throw 'Some components are not ready. Inspect .artifacts/pubsub-runtime/*.log and *.err.' }
    Write-Host '[info] Open http://localhost:5300/library-game or /pubsub-lab. Existing Burmalda accounts work.' -ForegroundColor Cyan
} finally { Pop-Location }
