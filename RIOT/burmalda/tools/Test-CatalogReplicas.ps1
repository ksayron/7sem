#requires -Version 7.4
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskNames=@('BURMALDA_TEST_POSTGRES','BURMALDA_TEST_RABBIT_USER','BURMALDA_TEST_RABBIT_PASSWORD','BURMALDA_TEST_RABBIT_PORT')
$taskPrevious=@{}
foreach($taskName in $taskNames){$taskPrevious[$taskName]=[Environment]::GetEnvironmentVariable($taskName,'Process')}
Push-Location $taskRoot
try {
    $taskConfig=docker compose config --format json | ConvertFrom-Json
    if($LASTEXITCODE){throw 'Cannot read core Compose configuration.'}
    docker compose up -d --no-build --wait postgres rabbitmq
    if($LASTEXITCODE){throw 'Core test dependencies did not become ready.'}
    $taskDatabase=$taskConfig.services.postgres
    $taskRabbit=$taskConfig.services.rabbitmq
    $env:BURMALDA_TEST_POSTGRES="Host=127.0.0.1;Port=$($taskDatabase.ports[0].published);Database=postgres;Username=$($taskDatabase.environment.POSTGRES_USER);Password=$($taskDatabase.environment.POSTGRES_PASSWORD)"
    $env:BURMALDA_TEST_RABBIT_USER=$taskRabbit.environment.RABBITMQ_DEFAULT_USER
    $env:BURMALDA_TEST_RABBIT_PASSWORD=$taskRabbit.environment.RABBITMQ_DEFAULT_PASS
    $env:BURMALDA_TEST_RABBIT_PORT=$taskRabbit.ports[0].published
    dotnet test tests/Burmalda.Catalog.IntegrationTests -m:1 --nologo --logger 'trx;LogFileName=replicas.trx' --results-directory .artifacts/lr9-tests
    if($LASTEXITCODE){throw 'Catalog PostgreSQL/RabbitMQ integration tests failed.'}
} finally {
    foreach($taskName in $taskNames){[Environment]::SetEnvironmentVariable($taskName,$taskPrevious[$taskName],'Process')}
    Pop-Location
}
