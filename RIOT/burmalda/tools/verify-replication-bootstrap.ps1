# A fresh, isolated Compose project verifies initialization without touching existing volumes.
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskProject = "burmalda-lr5-bootstrap-$PID"
$taskOverride = Join-Path $taskRoot ".artifacts/$taskProject.yml"
$taskResult = Join-Path $taskRoot 'lab-results/streaming-replication-verification/bootstrap-verification.json'
New-Item -ItemType Directory -Force (Split-Path $taskOverride),(Split-Path $taskResult) | Out-Null
@"
services:
  postgres:
    container_name: $taskProject-postgres
    ports: !reset []
  postgres-replica:
    container_name: $taskProject-replica
    ports: !reset []
networks:
  burmalda:
    name: $taskProject
"@ | Set-Content -LiteralPath $taskOverride -Encoding utf8
$taskArgs = @('compose','-p',$taskProject,'-f',(Join-Path $taskRoot 'docker-compose.yml'),'-f',$taskOverride)
$taskPassed = $false
$taskIsolationVerified = $false
Push-Location $taskRoot
try {
    $taskConfig = & docker @taskArgs config --format json | ConvertFrom-Json
    if ($LASTEXITCODE) { throw 'Bootstrap Compose configuration failed.' }
    foreach ($taskVolume in $taskConfig.volumes.PSObject.Properties) {
        if ($taskVolume.Value.external -or $taskVolume.Value.name -notlike "$taskProject`_*") { throw 'Refusing a bootstrap project using non-isolated volumes.' }
    }
    if ($taskConfig.networks.burmalda.name -ne $taskProject) { throw 'Refusing a bootstrap project using a shared network.' }
    $taskIsolationVerified = $true
    & docker @taskArgs up -d --wait postgres postgres-replica
    if ($LASTEXITCODE) { throw 'Fresh primary/standby initialization failed.' }
    $taskAdmin = $taskConfig.services.postgres.environment.POSTGRES_USER
    $taskRole = & docker @taskArgs exec -T postgres-replica psql -U $taskAdmin -d postgres -Atc 'SELECT pg_is_in_recovery()'
    if ($LASTEXITCODE -or $taskRole -ne 't') { throw 'Fresh replica is not in recovery.' }
    & docker @taskArgs exec -T postgres psql -U $taskAdmin -d catalog_db -v ON_ERROR_STOP=1 -c 'CREATE TABLE lr5_bootstrap_probe(id int PRIMARY KEY); INSERT INTO lr5_bootstrap_probe VALUES (1);'
    if ($LASTEXITCODE) { throw 'Fresh source SQL write failed.' }
    $taskCount = $null
    for ($taskTry=0; $taskTry -lt 30 -and $taskCount -ne '1'; $taskTry++) {
        $taskCount = & docker @taskArgs exec -T postgres-replica psql -U $taskAdmin -d catalog_db -Atc 'SELECT count(*) FROM lr5_bootstrap_probe' 2>$null
        if ($taskCount -ne '1') { Start-Sleep -Milliseconds 300 }
    }
    if ($taskCount -ne '1') { throw 'Fresh standby did not replay the SQL probe.' }
    $taskStreaming = & docker @taskArgs exec -T postgres psql -U $taskAdmin -d postgres -Atc "SELECT count(*) FROM pg_stat_replication WHERE state='streaming'"
    if ($taskStreaming -ne '1') { throw 'Fresh source has no streaming WAL sender.' }
    $taskPassed = $true
    Write-Host '[info] Fresh-volume base backup, replication role, WAL sender and replay verified.'
} finally {
    @{ verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); passed = $taskPassed; project = $taskProject;
       checks = @('isolated-new-volumes','init-databases-and-replication-role','pg-basebackup-bootstrap','standby-recovery-mode','real-sql-wal-replay') } |
       ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskResult -Encoding utf8
    # All resolved volume names were checked before startup; only this temporary project is removed.
    if ($taskIsolationVerified) { & docker @taskArgs down -v --remove-orphans }
    Pop-Location
}
