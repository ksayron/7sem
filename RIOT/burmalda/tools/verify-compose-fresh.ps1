# Verifies first startup using separate named volumes, then restores the normal stack.
# No original or verification volume is deleted by this script.
param()
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Push-Location $taskRoot
$taskOverride=Join-Path $taskRoot '.artifacts/fresh-volumes.yml'
$taskResult=Join-Path $taskRoot 'lab-results/docker-verification/fresh-volumes.json'
$taskEvidence=@{startedAtUtc=[DateTimeOffset]::UtcNow; success=$false; restored=$false}
$taskPreviousResult=$env:BURMALDA_RESULT_DIR
try {
    New-Item -ItemType Directory -Force (Split-Path $taskOverride),(Split-Path $taskResult) | Out-Null
    $taskConfig=docker compose -f docker-compose.legacy.yml --profile pubsub config --format json | ConvertFrom-Json
    if($LASTEXITCODE){throw 'Invalid Compose configuration.'}
    $taskSuffix=[guid]::NewGuid().ToString('N').Substring(0,12)
    $taskLines=@('volumes:')
    $taskEvidence.volumes=@()
    foreach($taskVolume in $taskConfig.volumes.PSObject.Properties.Name){
        $taskName="burmalda_verify_${taskSuffix}_$taskVolume"
        $taskLines+=@("  ${taskVolume}:","    name: $taskName")
        $taskEvidence.volumes+=$taskName
    }
    $taskLines | Set-Content -LiteralPath $taskOverride
    docker compose -f docker-compose.legacy.yml --profile pubsub down
    if($LASTEXITCODE){throw 'Cannot stop normal stack.'}
    docker compose -f docker-compose.legacy.yml -f $taskOverride --profile pubsub up -d --no-build
    if($LASTEXITCODE){throw 'Fresh stack startup failed.'}
    $taskReady=$false
    for($taskAttempt=0;$taskAttempt -lt 90 -and !$taskReady;$taskAttempt++){
        try {
            $taskStatus=Invoke-RestMethod 'http://localhost:5300/api/raft-lab/status' -TimeoutSec 10
            $taskReady=$taskStatus.state.initialized -and @($taskStatus.nodes | Where-Object available).Count -eq 5
        } catch {}
        if(!$taskReady){Start-Sleep -Seconds 2}
    }
    if(!$taskReady){throw 'Fresh cluster readiness deadline exceeded.'}
    $env:BURMALDA_RESULT_DIR='lab-results/docker-verification/fresh'
    node tools/verify-docker-lab.cjs
    if($LASTEXITCODE){throw 'Fresh stack acceptance failed.'}
    $taskEvidence.success=$true
} finally {
    $env:BURMALDA_RESULT_DIR=$taskPreviousResult
    if(Test-Path -LiteralPath $taskOverride){
        docker compose -f docker-compose.legacy.yml -f $taskOverride --profile pubsub down
        if($LASTEXITCODE){throw 'Cannot stop verification stack; original volumes remain intact.'}
    }
    docker compose -f docker-compose.legacy.yml up -d --no-build
    $taskEvidence.restored=$LASTEXITCODE -eq 0
    $taskEvidence.finishedAtUtc=[DateTimeOffset]::UtcNow
    $taskEvidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskResult
    Pop-Location
}
