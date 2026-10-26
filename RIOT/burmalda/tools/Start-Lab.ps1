param([int]$PostgresPort = 5547, [int]$ReplicaPort = 5548, [switch]$Full, [switch]$NoBuild, [switch]$ReplicationOnly)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskLogs = Join-Path $taskRoot '.artifacts/lab-runtime'
$taskEnvironmentNames = @('ASPNETCORE_ENVIRONMENT','POSTGRES_PORT','POSTGRES_REPLICA_PORT','ConnectionStrings__Database',
    'ReplicationLab__JournalRoot','ReplicationLab__ComposeRoot','ReplicationLab__Nodes__postgres','ReplicationLab__Nodes__postgres-replica','ReplicationLab__CatalogPassword')
$taskPreviousEnvironment = @{}
foreach ($taskKey in $taskEnvironmentNames) { $taskPreviousEnvironment[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey, 'Process') }
New-Item -ItemType Directory -Force $taskLogs | Out-Null
Push-Location $taskRoot
try {
    if ($ReplicationOnly -and $Full) { throw 'Choose -ReplicationOnly or -Full.' }
    if ($PostgresPort -eq $ReplicaPort) { throw 'Primary and replica need different host ports.' }
    $env:POSTGRES_PORT = "$PostgresPort"
    $env:POSTGRES_REPLICA_PORT = "$ReplicaPort"
    # --no-deps also starts a surviving promoted node when the old primary is fenced.
    docker compose up -d --no-deps postgres postgres-replica
    if ($LASTEXITCODE) { throw 'Docker startup failed.' }
    $taskCompose = docker compose config --format json | ConvertFrom-Json
    $taskAdmin = $taskCompose.services.postgres.environment.POSTGRES_USER
    $taskPassword = $taskCompose.services.postgres.environment.POSTGRES_PASSWORD
    $taskWriter = $null
    for ($taskAttempt = 0; $taskAttempt -lt 45 -and !$taskWriter; $taskAttempt++) {
        foreach ($taskNode in @('postgres', 'postgres-replica')) {
            $taskRole = docker compose exec -T $taskNode psql -U $taskAdmin -d postgres -Atc "SELECT NOT pg_is_in_recovery() AND NOT current_setting('default_transaction_read_only')::boolean" 2>$null
            if ($LASTEXITCODE -eq 0 -and $taskRole -eq 't') { $taskWriter = $taskNode; break }
        }
        if (!$taskWriter) { Start-Sleep -Seconds 1 }
    }
    if ($taskWriter) {
        if ($taskWriter -eq 'postgres') { docker compose exec -T postgres sh /docker-entrypoint-initdb.d/10-init-databases.sh | Out-Null }
        else { docker compose exec -T postgres-replica sh /opt/replication/provision.sh | Out-Null }
        if ($LASTEXITCODE) { throw 'Database/replication provisioning failed.' }
        Write-Host "[info] PostgreSQL writable node: $taskWriter" -ForegroundColor Cyan
        foreach ($taskNode in @('postgres', 'postgres-replica')) {
            docker compose exec -T $taskWriter test -f "/control/$taskNode.fenced" 2>$null
            if ($LASTEXITCODE -eq 0) { Write-Warning "$taskNode is fenced; use recovery controls."; continue }
            $taskReady = $false
            for ($taskAttempt = 0; $taskAttempt -lt 45 -and !$taskReady; $taskAttempt++) {
                docker compose exec -T $taskNode pg_isready -U $taskAdmin -d postgres 2>$null | Out-Null
                $taskReady = $LASTEXITCODE -eq 0
                if (!$taskReady) { Start-Sleep -Seconds 1 }
            }
            if (!$taskReady) { throw "$taskNode did not become ready; inspect docker compose logs $taskNode." }
        }
    } else { Write-Warning 'No writable node. Start the lab and use recovery controls; fenced volumes are preserved.' }
    if (!$ReplicationOnly) { docker compose up -d --wait rabbitmq; if ($LASTEXITCODE) { throw 'RabbitMQ startup failed.' } }
    if (!$NoBuild) {
        dotnet build Burmalda.slnx -m:1 -p:UseSharedCompilation=false --nologo
        if ($LASTEXITCODE) { throw 'Build failed.' }
    }
    $taskProjects = @('src/Services/ConcurrencyLab/Burmalda.ConcurrencyLab.Api', 'src/Gateway/Burmalda.Gateway', 'src/Clients/Burmalda.Web')
    if ($Full) { $taskProjects += @('src/Services/Identity/Burmalda.Identity.Api', 'src/Services/Catalog/Burmalda.Catalog.Api', 'src/Services/Lending/Burmalda.Lending.Api', 'src/Services/Economy/Burmalda.Economy.Api', 'src/Services/Reputation/Burmalda.Reputation.Api', 'src/Services/Notifications/Burmalda.Notifications.Api') }
    $taskProcesses = @()
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ReplicationLab__JournalRoot = Join-Path $taskRoot '.artifacts/streaming-replication'
    $env:ReplicationLab__ComposeRoot = $taskRoot
    $env:ReplicationLab__Nodes__postgres = "Host=127.0.0.1;Port=$PostgresPort;Database=postgres;Username=$taskAdmin;Password=$taskPassword"
    Set-Item 'Env:ReplicationLab__Nodes__postgres-replica' "Host=127.0.0.1;Port=$ReplicaPort;Database=postgres;Username=$taskAdmin;Password=$taskPassword"
    $env:ReplicationLab__CatalogPassword = $taskCompose.services.postgres.environment.CATALOG_DB_PASSWORD
    foreach ($taskProject in $taskProjects) {
        $taskDirectory = Join-Path $taskRoot $taskProject
        $taskName = Split-Path $taskDirectory -Leaf
        $taskSettings = Get-Content (Join-Path $taskDirectory 'Properties/launchSettings.json') -Raw | ConvertFrom-Json
        $taskUrl = ($taskSettings.profiles.PSObject.Properties | Select-Object -First 1).Value.applicationUrl
        $taskPort = ([uri]($taskUrl.Split(';')[0])).Port
        if (Get-NetTCPConnection -State Listen -LocalPort $taskPort -ErrorAction SilentlyContinue) { throw "Port $taskPort is occupied. Stop the previous lab before starting another." }
        $taskConfig = Join-Path $taskDirectory 'appsettings.json'
        $env:ConnectionStrings__Database = $null
        if (Test-Path $taskConfig) {
            $taskConnection = (Get-Content $taskConfig -Raw | ConvertFrom-Json).ConnectionStrings.Database
            if ($taskConnection) {
                $env:ConnectionStrings__Database = ($taskConnection -replace 'Host=[^;]+;', "Host=127.0.0.1:$PostgresPort,127.0.0.1:$ReplicaPort;" -replace 'Port=\d+;', '')
            }
        }
        if ($taskName -eq 'Burmalda.Web') {
            $taskProcess = Start-Process dotnet -ArgumentList @('run', '--no-build', '--no-restore', '--project', "`"$taskDirectory`"") -WorkingDirectory $taskRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskLogs "$taskName.log") -RedirectStandardError (Join-Path $taskLogs "$taskName.err")
        } else {
            $taskDll = Join-Path $taskDirectory "bin/Debug/net9.0/$taskName.dll"
            $taskProcess = Start-Process dotnet -ArgumentList @("`"$taskDll`"", '--urls', $taskUrl) -WorkingDirectory $taskDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskLogs "$taskName.log") -RedirectStandardError (Join-Path $taskLogs "$taskName.err")
        }
        $taskProcesses += @{ Pid = $taskProcess.Id; Project = $taskDirectory; Name = $taskName }
        $taskProcesses | ConvertTo-Json -AsArray | Set-Content (Join-Path $taskLogs 'processes.json')
        Write-Host "[info] $taskName → $taskUrl; log: $taskLogs/$taskName.log" -ForegroundColor Cyan
    }
    Write-Host $(if ($ReplicationOnly) { '[info] Open http://localhost:5300/replication-lab' } else { '[info] Open http://localhost:5300/concurrency-lab or /replication-lab' }) -ForegroundColor Cyan
} finally {
    foreach ($taskKey in $taskEnvironmentNames) { [Environment]::SetEnvironmentVariable($taskKey, $taskPreviousEnvironment[$taskKey], 'Process') }
    Pop-Location
}
