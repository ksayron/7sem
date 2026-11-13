#requires -Version 7.4
param([switch]$Browser)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskFolder=Join-Path $taskRoot 'lab-results/kubernetes-verification'
$taskRuntime=Join-Path $taskRoot '.artifacts/k8s'
$taskPreviousResult=$env:BURMALDA_RESULT_DIR
$taskPreviousMode=$env:BURMALDA_EXPECTED_MODE
$taskReport=@{startedAtUtc=[DateTimeOffset]::UtcNow;success=$false;checks=@()}
$taskImage=$null
$taskMode=$null
function K {
    $taskOutput=& kubectl --context docker-desktop --namespace burmalda @args
    if($LASTEXITCODE){throw "kubectl failed: $($args -join ' ')"}
    $taskOutput
}
function Mark([string]$Name){$taskReport.checks+=$Name;Write-Host "PASS $Name"}
function Snapshot([string]$Name){
    K get 'deployments,statefulsets,pods,services,endpointslices,pvc' -o json | Set-Content (Join-Path $taskFolder "$Name.json")
}
function CatalogPods {
    @((K get pods -l app=catalog -o json | ConvertFrom-Json).items | Where-Object { !$_.metadata.deletionTimestamp })
}
function Wait-Catalog([int]$Count,[string]$ExcludedUid=''){
    $taskDeadline=[DateTimeOffset]::UtcNow.AddMinutes(4)
    do {
        $taskPods=CatalogPods
        $taskReady=@($taskPods | Where-Object {($_.status.conditions | Where-Object type -eq Ready).status -eq 'True'})
        if($taskPods.Count -eq $Count -and $taskReady.Count -eq $Count -and !@($taskPods | Where-Object {$_.metadata.uid -eq $ExcludedUid}).Count){return $taskPods}
        Start-Sleep -Seconds 2
    }while([DateTimeOffset]::UtcNow -lt $taskDeadline)
    throw "Catalog did not reach $Count Ready pods."
}
function Core([string]$Name,[string]$Mode,[switch]$Full,[switch]$Unavailable,[switch]$WithBrowser){
    $env:BURMALDA_RESULT_DIR=Join-Path $taskFolder $Name
    $env:BURMALDA_EXPECTED_MODE=$Mode
    $taskArgs=@('tools/verify-core.cjs')
    if($Unavailable){$taskArgs+='--unavailable'}elseif(!$Full){$taskArgs+='--probe'}
    if($WithBrowser){$taskArgs+='--browser'}
    & node @taskArgs
    if($LASTEXITCODE){throw "Core acceptance failed: $Name"}
}
function Set-Mode([string]$Mode){
    $taskPatch=Join-Path $taskRuntime 'configmap-patch.json'
    @{data=@{APP_MODE=$Mode}} | ConvertTo-Json | Set-Content -LiteralPath $taskPatch
    K patch configmap burmalda-config --type merge --patch-file $taskPatch | Out-Host
}
Push-Location $taskRoot
New-Item -ItemType Directory -Force $taskFolder,$taskRuntime | Out-Null
try {
    $taskNode=(K get nodes -o json | ConvertFrom-Json).items
    if($taskNode.Count -ne 1){throw 'This demonstration expects a single Docker Desktop node.'}
    $taskReport.cluster=@{context='docker-desktop';namespace='burmalda';node=$taskNode[0].metadata.name;version=$taskNode[0].status.nodeInfo.kubeletVersion}
    $taskImage=(K get deployment catalog -o json | ConvertFrom-Json).spec.template.spec.containers[0].image
    $taskMode=(K get configmap burmalda-config -o json | ConvertFrom-Json).data.APP_MODE
    K scale deployment/catalog --replicas=1 | Out-Host
    Wait-Catalog 1 | Out-Null
    Snapshot 'initial'
    Core 'initial-operations' $taskMode -Full
    Mark 'deployment-service-domain-operations-and-configmap'

    K scale deployment/catalog --replicas=3 | Out-Host
    Wait-Catalog 3 | Out-Null
    $taskEndpoints=(K get endpointslices -l kubernetes.io/service-name=catalog -o json | ConvertFrom-Json).items.endpoints
    $taskReadyEndpoints=@($taskEndpoints | Where-Object {$_.conditions.ready -eq $true})
    if($taskReadyEndpoints.Count -ne 3){throw 'Catalog Service does not select three Ready endpoints.'}
    Snapshot 'scaled-three'
    Core 'three-replica-operations' $taskMode -Full
    Mark 'scale-one-to-three-and-competing-domain-operations'

    $taskBefore=(CatalogPods)[0]
    K delete pod $taskBefore.metadata.name --wait=false | Out-Host
    $taskAfter=Wait-Catalog 3 $taskBefore.metadata.uid
    $taskReport.recovery=@{deletedName=$taskBefore.metadata.name;deletedUid=$taskBefore.metadata.uid;readyPods=@($taskAfter | ForEach-Object {@{name=$_.metadata.name;uid=$_.metadata.uid}})}
    Snapshot 'pod-recovered'
    Core 'pod-recovery-operations' $taskMode
    Mark 'deleted-pod-replaced-and-existing-data-readable'

    Set-Mode 'kubernetes-configmap-demo'
    Core 'configmap-before-restart' $taskMode
    K rollout restart deployment/catalog | Out-Host
    K rollout status deployment/catalog --timeout=240s | Out-Host
    Wait-Catalog 3 | Out-Null
    Core 'configmap-after-restart' 'kubernetes-configmap-demo'
    Mark 'configmap-env-keeps-old-value-until-rollout'

    $taskBadImage='burmalda-catalog:lr9-missing-'+[guid]::NewGuid().ToString('N')
    K set image deployment/catalog "catalog=$taskBadImage" | Out-Host
    $taskDeadline=[DateTimeOffset]::UtcNow.AddMinutes(3)
    $taskBroken=$null
    do {
        $taskBroken=@(CatalogPods | Where-Object { $_.spec.containers[0].image -eq $taskBadImage -and $_.status.containerStatuses -and $_.status.containerStatuses[0].state.waiting.reason -in @('ErrImagePull','ImagePullBackOff') }) | Select-Object -First 1
        if(!$taskBroken){Start-Sleep -Seconds 2}
    }while(!$taskBroken -and [DateTimeOffset]::UtcNow -lt $taskDeadline)
    if(!$taskBroken){throw 'The missing-image error was not observed within the deadline.'}
    $taskReport.imageError=@{image=$taskBadImage;pod=$taskBroken.metadata.name;reason=$taskBroken.status.containerStatuses[0].state.waiting.reason}
    K describe pod $taskBroken.metadata.name | Set-Content (Join-Path $taskFolder 'bad-image-describe.txt')
    K get events --field-selector "involvedObject.name=$($taskBroken.metadata.name)" -o json | Set-Content (Join-Path $taskFolder 'bad-image-events.json')
    Snapshot 'bad-image'
    K set image deployment/catalog "catalog=$taskImage" | Out-Host
    K rollout status deployment/catalog --timeout=240s | Out-Host
    Wait-Catalog 3 | Out-Null
    Core 'image-restored-operations' 'kubernetes-configmap-demo'
    Mark 'missing-image-diagnosed-and-correct-image-restored'

    $taskPostgres=(K get pod postgres-0 -o json | ConvertFrom-Json)
    K scale statefulset/postgres --replicas=0 | Out-Host
    K wait --for=delete pod/postgres-0 --timeout=120s | Out-Host
    # Allow readiness checks and EndpointSlices to observe the missing database.
    Start-Sleep -Seconds 20
    Core 'dependency-outage' '' -Unavailable
    $taskCatalog=(CatalogPods)[0].metadata.name
    $taskLive=K exec $taskCatalog '--' curl -s -o /dev/null -w '%{http_code}' http://localhost:8080/health/live
    $taskReady=K exec $taskCatalog '--' curl -s -o /dev/null -w '%{http_code}' http://localhost:8080/health/ready
    if($taskLive -ne '200' -or $taskReady -ne '503'){throw 'Dependency failure should change readiness while keeping liveness healthy.'}
    $taskReport.outage=@{liveness=$taskLive;readiness=$taskReady;oldPostgresUid=$taskPostgres.metadata.uid}
    K scale statefulset/postgres --replicas=1 | Out-Host
    K rollout status statefulset/postgres --timeout=240s | Out-Host
    Wait-Catalog 3 | Out-Null
    $taskRestoredPostgres=K get pod postgres-0 -o json | ConvertFrom-Json
    if($taskRestoredPostgres.metadata.uid -eq $taskPostgres.metadata.uid){throw 'PostgreSQL pod was not recreated.'}
    $taskReport.outage.newPostgresUid=$taskRestoredPostgres.metadata.uid
    Core 'postgres-recreated-operations' 'kubernetes-configmap-demo'
    Mark 'dependency-outage-readiness-recovery-and-pvc-persistence'

    Set-Mode $taskMode
    K scale deployment/catalog --replicas=1 | Out-Host
    K rollout restart deployment/catalog | Out-Host
    K rollout status deployment/catalog --timeout=240s | Out-Host
    Wait-Catalog 1 | Out-Null
    Core 'final-operations' $taskMode -Full -WithBrowser:$Browser
    $taskNames=@((K get 'deployments,statefulsets' -o json | ConvertFrom-Json).items.metadata.name | Sort-Object)
    $taskExpected=@('catalog','economy','gateway','identity','lending','notifications','postgres','rabbitmq','reputation','web' | Sort-Object)
    if(Compare-Object $taskExpected $taskNames){throw 'Unexpected services in the Burmalda namespace.'}
    Snapshot 'final'
    K logs deployment/catalog --tail=60 | Set-Content (Join-Path $taskFolder 'catalog-final.log')
    K get events -o json | Set-Content (Join-Path $taskFolder 'events.json')
    Mark 'scale-back-to-one-browser-and-only-business-components'
    $taskReport.success=$true
} catch {
    $taskReport.failure=$_.Exception.Message
    Write-Error $_ -ErrorAction Continue
} finally {
    # Always return intentional failures to the supported image/configuration and preserve all PVC.
    try {
        K scale statefulset/postgres --replicas=1 | Out-Null
        if($taskImage){K set image deployment/catalog "catalog=$taskImage" | Out-Null}
        if($taskMode){Set-Mode $taskMode;K rollout restart deployment/catalog | Out-Null}
        K scale deployment/catalog --replicas=1 | Out-Null
        K rollout status deployment/catalog --timeout=240s | Out-Null
    } catch { $taskReport.restorationFailure=$_.Exception.Message }
    $taskReport.finishedAtUtc=[DateTimeOffset]::UtcNow
    $taskReport | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $taskFolder 'experiments.json')
    $env:BURMALDA_RESULT_DIR=$taskPreviousResult;$env:BURMALDA_EXPECTED_MODE=$taskPreviousMode
    Pop-Location
}
if(!$taskReport.success){throw 'Kubernetes acceptance failed. See lab-results/kubernetes-verification/experiments.json.'}
