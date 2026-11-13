#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Build','Start','Status','Access','Stop')][string]$Action,
    [switch]$Background,
    [switch]$Demo,
    [ValidateRange(1024,65535)][int]$Port = 5300
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskRuntime = Join-Path $taskRoot '.artifacts/k8s'
$taskContext = 'docker-desktop'
$taskNamespace = 'burmalda'
. (Join-Path $PSScriptRoot 'Kubernetes.Demo.ps1')
$taskDemoActive = $Demo -or (Test-Path -LiteralPath (Join-Path $taskRuntime 'demo-enabled'))
New-Item -ItemType Directory -Force $taskRuntime | Out-Null
function Invoke-Kube {
    & kubectl --context $taskContext --namespace $taskNamespace @args
    if ($LASTEXITCODE) { throw "kubectl failed: $($args -join ' ')" }
}
function Test-Cluster {
    & kubectl --context $taskContext --namespace $taskNamespace cluster-info --request-timeout=10s | Out-Host
    if ($LASTEXITCODE) { throw 'Enable the single-node kubeadm cluster in Docker Desktop first. Expected context: docker-desktop.' }
    Invoke-Kube wait node/docker-desktop --for=condition=Ready --timeout=60s
}
function Stop-Access {
    Stop-DemoAccess
    $taskPidFile = Join-Path $taskRuntime 'access.json'
    if (Test-Path -LiteralPath $taskPidFile) {
        $taskRecord = Get-Content -LiteralPath $taskPidFile -Raw | ConvertFrom-Json
        $taskProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($taskRecord.pid)"
        if ($taskProcess -and $taskProcess.CommandLine -match 'port-forward.*service/web' -and $taskProcess.CommandLine -match 'docker-desktop' -and $taskProcess.CommandLine -match 'burmalda') {
            Stop-Process -Id $taskRecord.pid
        }
        Remove-Item -LiteralPath $taskPidFile
    }
}
Push-Location $taskRoot
try {
    if ($Action -eq 'Build') {
        $taskTag = 'lr9-' + (Get-Date -Format 'yyyyMMddHHmmss')
        $taskPreviousTag = $env:BURMALDA_IMAGE_TAG
        $taskPreviousParallel = $env:COMPOSE_PARALLEL_LIMIT
        try {
            $env:BURMALDA_IMAGE_TAG = $taskTag
            $env:COMPOSE_PARALLEL_LIMIT = '1'
            # Compose/BuildKit can build every service concurrently despite COMPOSE_PARALLEL_LIMIT.
            # Publish sequentially to keep this 16 GiB demo host responsive.
            foreach ($taskBuildService in @('postgres','identity','catalog','lending','economy','reputation','notifications','gateway','web')) {
                docker compose build $taskBuildService
                if ($LASTEXITCODE) { throw "Docker image build failed: $taskBuildService." }
            }
            if ($taskDemoActive) { Build-Demo $taskTag; Get-DemoImages }
            $taskImages = @{}
            foreach ($taskService in @('postgres','identity','catalog','lending','economy','reputation','notifications','gateway','web')) {
                $taskImage = "burmalda-${taskService}:$taskTag"
                docker tag $taskImage "burmalda-${taskService}:lr9"
                if ($LASTEXITCODE) { throw "Cannot tag $taskImage" }
                $taskImages[$taskService] = @{image=$taskImage; id=(docker image inspect $taskImage --format '{{.Id}}')}
            }
            if ($taskDemoActive) { $taskImages['demo-control'] = @{image="burmalda-demo-control:$taskTag";id=(docker image inspect "burmalda-demo-control:$taskTag" --format '{{.Id}}')} }
            @{tag=$taskTag; images=$taskImages} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $taskRuntime 'images.json')
        } finally { $env:BURMALDA_IMAGE_TAG=$taskPreviousTag; $env:COMPOSE_PARALLEL_LIMIT=$taskPreviousParallel }
        return
    }
    Test-Cluster
    switch ($Action) {
        'Prepare' {
            Invoke-Kube apply -f k8s/namespace.yaml
            $taskExisting = & kubectl --context $taskContext --namespace $taskNamespace get secret burmalda-secrets --ignore-not-found -o name
            if ($LASTEXITCODE) { throw 'Cannot inspect the local Secret.' }
            $taskSecretFile = Join-Path $taskRuntime 'secret.json'
            if (!$taskExisting) {
                if (!(Test-Path -LiteralPath $taskSecretFile)) {
                    $taskValues = @{}
                    foreach ($taskKey in @('POSTGRES_PASSWORD','IDENTITY_DB_PASSWORD','CATALOG_DB_PASSWORD','LENDING_DB_PASSWORD','ECONOMY_DB_PASSWORD','REPUTATION_DB_PASSWORD','NOTIFICATION_DB_PASSWORD','RABBITMQ_PASSWORD','RABBITMQ_ERLANG_COOKIE','JWT_SIGNING_KEY','ADMIN_PASSWORD')) {
                        $taskValues[$taskKey] = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
                    }
                    @{apiVersion='v1'; kind='Secret'; metadata=@{name='burmalda-secrets';namespace=$taskNamespace};type='Opaque';stringData=$taskValues} |
                        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskSecretFile
                }
                Invoke-Kube apply -f $taskSecretFile
            }
            Write-Host '[info] Secret preserved/generated locally. Values are not printed or committed.'
            if ($taskDemoActive) { Initialize-Demo }
        }
        'Start' {
            $taskSecret = Invoke-Kube get secret burmalda-secrets -o name
            $taskLedger = Join-Path $taskRuntime 'images.json'
            $taskTag = if (Test-Path -LiteralPath $taskLedger) { (Get-Content -LiteralPath $taskLedger -Raw | ConvertFrom-Json).tag } else { 'lr9' }
            foreach ($taskName in @('postgres','identity','catalog','lending','economy','reputation','notifications','gateway','web')) {
                docker image inspect "burmalda-${taskName}:$taskTag" --format '{{.Id}}' | Out-Null
                if ($LASTEXITCODE) { throw "Missing image burmalda-${taskName}:$taskTag. Run -Action Build first." }
            }
            docker compose stop
            if ($LASTEXITCODE) { throw 'Cannot stop the core Compose stack.' }
            foreach ($taskFile in @('configmap.yaml','web-config.yaml','service.yaml')) { Invoke-Kube apply -f "k8s/$taskFile" }
            if ($taskDemoActive) { Start-DemoInfrastructure $taskTag }
            foreach ($taskFile in @('statefulsets.yaml','deployment.yaml')) {
                $taskRendered = Join-Path $taskRuntime $taskFile
                (Get-Content "k8s/$taskFile" -Raw).Replace(':lr9', ":$taskTag") | Set-Content -LiteralPath $taskRendered
                if ($taskDemoActive -and $taskFile -eq 'deployment.yaml') {
                    $taskDemoRendered = Join-Path $taskRuntime 'demo-deployments.json'
                    Write-DemoDeployments $taskRendered $taskDemoRendered
                    $taskRendered = $taskDemoRendered
                }
                Invoke-Kube apply -f $taskRendered
                if ($taskFile -eq 'statefulsets.yaml') {
                    foreach ($taskName in @('postgres','rabbitmq')) { Invoke-Kube rollout status "statefulset/$taskName" --timeout=300s }
                }
            }
            foreach ($taskName in @('identity','catalog','lending','economy','reputation','notifications','gateway','web')) {
                Invoke-Kube rollout status "deployment/$taskName" --timeout=300s
            }
            if ($taskDemoActive) { Complete-DemoStart }
            Write-Host '[info] Ready. Run ./tools/Kubernetes.ps1 -Action Access, then open http://localhost:5300.'
        }
        'Status' {
            Invoke-Kube get nodes
            Invoke-Kube get 'deployments,statefulsets,pods,services,pvc' -o wide
            Invoke-Kube get endpointslices -l kubernetes.io/service-name=catalog
            if ($taskDemoActive) { Invoke-Kube get hpa; Invoke-Kube top pods }
        }
        'Access' {
            if ($Background) {
                Stop-Access
                $taskProcess = Start-Process (Get-Command kubectl).Source -ArgumentList @('--context',$taskContext,'--namespace',$taskNamespace,'port-forward','service/web',"${Port}:8080",'--address','127.0.0.1') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskRuntime 'access.log') -RedirectStandardError (Join-Path $taskRuntime 'access.err')
                @{pid=$taskProcess.Id;port=$Port} | ConvertTo-Json | Set-Content (Join-Path $taskRuntime 'access.json')
                $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
                do {
                    $taskProcess.Refresh()
                    if ($taskProcess.HasExited) { throw 'Port-forward exited. See .artifacts/k8s/access.err.' }
                    $taskListening = Select-String -LiteralPath (Join-Path $taskRuntime 'access.log') -SimpleMatch "Forwarding from 127.0.0.1:$Port" -Quiet
                    if (!$taskListening) { Start-Sleep -Milliseconds 250 }
                } while (!$taskListening -and [DateTimeOffset]::UtcNow -lt $taskDeadline)
                if (!$taskListening) { throw 'Port-forward did not start listening within 20 seconds. See .artifacts/k8s/access.err.' }
                Write-Host "[info] Access process started: http://localhost:$Port. See .artifacts/k8s/access.log."
                if ($taskDemoActive) { Start-DemoAccess }
            } else { if ($taskDemoActive) { Start-DemoAccess }; Invoke-Kube port-forward service/web "${Port}:8080" --address 127.0.0.1 }
        }
        'Stop' {
            Stop-Demo
            Stop-Access
            Invoke-Kube scale deployment --all --replicas=0
            Invoke-Kube scale statefulset --all --replicas=0
            $taskPods = Invoke-Kube get pod -l app.kubernetes.io/part-of=burmalda -o name
            if ($taskPods) { Invoke-Kube wait --for=delete pod -l app.kubernetes.io/part-of=burmalda --timeout=180s }
            Write-Host '[info] Workloads stopped. Namespace, Secrets, Services and PVC retained.'
        }
    }
} finally { Pop-Location }
