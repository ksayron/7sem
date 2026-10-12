param([int]$PostgresPort = 5432, [switch]$Full, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskLogs = Join-Path $taskRoot '.artifacts/lab-runtime'
New-Item -ItemType Directory -Force $taskLogs | Out-Null
Push-Location $taskRoot
try {
    $env:POSTGRES_PORT = "$PostgresPort"
    docker compose up -d --wait postgres rabbitmq
    if ($LASTEXITCODE) { throw 'Docker startup failed. Choose a free -PostgresPort.' }
    # This is idempotent and also provisions the lab database on an existing volume.
    docker exec burmalda-postgres sh /docker-entrypoint-initdb.d/10-init-databases.sh | Out-Null
    if ($LASTEXITCODE) { throw 'Database provisioning failed.' }
    if (!$NoBuild) {
        dotnet build Burmalda.slnx -m:1 --nologo
        if ($LASTEXITCODE) { throw 'Build failed.' }
    }
    $taskProjects = @('src/Services/ConcurrencyLab/Burmalda.ConcurrencyLab.Api', 'src/Gateway/Burmalda.Gateway', 'src/Clients/Burmalda.Web')
    if ($Full) { $taskProjects += @('src/Services/Identity/Burmalda.Identity.Api', 'src/Services/Catalog/Burmalda.Catalog.Api', 'src/Services/Lending/Burmalda.Lending.Api', 'src/Services/Economy/Burmalda.Economy.Api', 'src/Services/Reputation/Burmalda.Reputation.Api', 'src/Services/Notifications/Burmalda.Notifications.Api') }
    $taskProcesses = @()
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
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
            if ($taskConnection) { $env:ConnectionStrings__Database = $taskConnection -replace 'Port=\d+', "Port=$PostgresPort" }
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
    Write-Host '[info] Open http://localhost:5300/concurrency-lab' -ForegroundColor Cyan
} finally { $env:ConnectionStrings__Database = $null; Pop-Location }
