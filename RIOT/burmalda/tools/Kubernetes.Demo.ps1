# Dot-sourced by Kubernetes.ps1; all Kubernetes operations keep its explicit context/namespace.
function Initialize-Demo {
    $taskExists = Invoke-Kube get secret burmalda-demo-secrets --ignore-not-found -o name
    if (!$taskExists) {
        $taskDemoSecret = Join-Path $taskRuntime 'demo-secret.json'
        if (!(Test-Path -LiteralPath $taskDemoSecret)) {
            $taskValues = @{}
            foreach ($taskKey in @('CONTROL_KEY','FIXTURE_PASSWORD','GRAFANA_PASSWORD')) {
                $taskValues[$taskKey] = [Convert]::ToHexString([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
            }
            @{apiVersion='v1';kind='Secret';metadata=@{name='burmalda-demo-secrets';namespace=$taskNamespace};type='Opaque';stringData=$taskValues} |
                ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskDemoSecret
        }
        Invoke-Kube apply -f $taskDemoSecret
    }
}
function Build-Demo([string]$Tag) {
    docker build -f docker/Dockerfile --target runtime --build-arg PROJECT_PATH=src/Services/DemoControl/Burmalda.DemoControl/Burmalda.DemoControl.csproj --build-arg ASSEMBLY_NAME=Burmalda.DemoControl -t "burmalda-demo-control:$Tag" .
    if ($LASTEXITCODE) { throw 'DemoControl image build failed.' }
    docker tag "burmalda-demo-control:$Tag" burmalda-demo-control:lr9
    if ($LASTEXITCODE) { throw 'DemoControl image tag failed.' }
}
function Get-DemoImages {
    foreach ($taskImage in @('prom/prometheus:v3.5.0','grafana/grafana:12.1.0','registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.17.0','registry.k8s.io/metrics-server/metrics-server:v0.8.0')) {
        docker image inspect $taskImage --format '{{.Id}}' 2>$null | Out-Null
        if ($LASTEXITCODE) {
            docker pull $taskImage
            if ($LASTEXITCODE) { throw "Cannot download $taskImage." }
        }
    }
}
function Start-DemoInfrastructure([string]$Tag) {
    Initialize-Demo
    $taskMetrics = & kubectl --context $taskContext get apiservice v1beta1.metrics.k8s.io --ignore-not-found -o name
    if ($LASTEXITCODE) { throw 'Cannot inspect metrics API.' }
    $taskMetricsDeployment = & kubectl --context $taskContext --namespace kube-system get deployment metrics-server --ignore-not-found -o name
    if ($LASTEXITCODE) { throw 'Cannot inspect metrics-server deployment.' }
    if (!$taskMetrics -or !$taskMetricsDeployment) {
        # The upstream bundle explicitly targets kube-system, unlike our application manifests.
        & kubectl --context $taskContext apply -f k8s/demo/metrics-server.yaml
        if ($LASTEXITCODE) { throw 'Cannot install metrics-server.' }
    }
    foreach ($taskFile in @('rbac.yaml','prometheus-config.yaml','grafana-provisioning.yaml','grafana-dashboards.yaml')) {
        Invoke-Kube apply -f "k8s/demo/$taskFile"
    }
    (Get-Content k8s/demo/workloads.yaml -Raw).Replace('burmalda-demo-control:lr9', "burmalda-demo-control:$Tag") |
        Set-Content (Join-Path $taskRuntime 'demo-workloads.yaml')
    Invoke-Kube apply -f (Join-Path $taskRuntime 'demo-workloads.yaml')
    $taskTelemetry = @{data=@{
        Telemetry__Enabled='true'; Telemetry__TracesEnabled='false'; OTEL_EXPORTER_OTLP_METRICS_PROTOCOL='http/protobuf';
        OTEL_EXPORTER_OTLP_PROTOCOL='http/protobuf'; OTEL_EXPORTER_OTLP_ENDPOINT='http://prometheus:9090/api/v1/otlp';
        OTEL_EXPORTER_OTLP_METRICS_ENDPOINT='http://prometheus:9090/api/v1/otlp/v1/metrics'; OTEL_METRIC_EXPORT_INTERVAL='5000'
    }}
    $taskTelemetry | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $taskRuntime 'demo-telemetry.json')
    Invoke-Kube patch configmap burmalda-config --type merge --patch-file (Join-Path $taskRuntime 'demo-telemetry.json')
}
function Write-DemoDeployments([string]$InputFile,[string]$OutputFile) {
    $taskItems = [System.Collections.Generic.List[object]]::new()
    foreach ($taskDocument in ((Get-Content -LiteralPath $InputFile -Raw) -split '(?m)^---\s*$')) {
        if ([string]::IsNullOrWhiteSpace($taskDocument)) { continue }
        $taskOne = Join-Path $taskRuntime 'demo-one-deployment.yaml'
        Set-Content -LiteralPath $taskOne $taskDocument
        $taskItem = & kubectl --context $taskContext --namespace $taskNamespace create --dry-run=client -f $taskOne -o json | Out-String | ConvertFrom-Json -AsHashtable
        if ($LASTEXITCODE) { throw 'Cannot parse deployment.' }
        $taskItems.Add($taskItem)
    }
    $taskList = @{apiVersion='v1';kind='List';items=$taskItems.ToArray()}
    foreach ($taskDeployment in $taskList.items) {
        $taskName = $taskDeployment.metadata.name
        $taskContainer = $taskDeployment.spec.template.spec.containers[0]
        $taskContainer.env = @($taskContainer.env | Where-Object { $_.name -notin @('POD_UID','POD_NAMESPACE') }) + @(
            @{name='POD_UID';valueFrom=@{fieldRef=@{fieldPath='metadata.uid'}}},
            @{name='POD_NAMESPACE';valueFrom=@{fieldRef=@{fieldPath='metadata.namespace'}}}
        )
        if ($taskName -in @('catalog','gateway')) {
            $taskHpa = Invoke-Kube get hpa $taskName --ignore-not-found -o name
            if ($taskHpa) {
                $taskExisting = Invoke-Kube get deployment $taskName -o json | ConvertFrom-Json -AsHashtable
                $taskDeployment.spec.replicas = $taskExisting.spec.replicas
            }
            $taskContainer.env += @(
                @{name='DemoFaults__Enabled';value='true'}, @{name='DemoFaults__Service';value=$taskName},
                @{name='DemoFaults__ControlUrl';value='http://demo-control:8080'},
                @{name='DemoFaults__Key';valueFrom=@{secretKeyRef=@{name='burmalda-demo-secrets';key='CONTROL_KEY'}}}
            )
        }
    }
    $taskList | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $OutputFile
}
function Complete-DemoStart {
    foreach ($taskName in @('prometheus','grafana','kube-state-metrics','demo-control')) { Invoke-Kube rollout status "deployment/$taskName" --timeout=300s }
    & kubectl --context $taskContext wait apiservice/v1beta1.metrics.k8s.io --for=condition=Available --timeout=120s
    if ($LASTEXITCODE) { throw 'Metrics API did not become available.' }
    Invoke-Kube apply -f k8s/demo/hpa.yaml
    Set-Content (Join-Path $taskRuntime 'demo-enabled') 'true'
    Write-Host '[info] Demo enabled. Access -Demo opens library :5300, control :5310 and Grafana :3000.'
}
function Stop-DemoAccess {
    foreach ($taskName in @('demo-control','grafana')) {
        $taskFile = Join-Path $taskRuntime "access-$taskName.json"
        if (Test-Path -LiteralPath $taskFile) {
            $taskRecord = Get-Content -LiteralPath $taskFile -Raw | ConvertFrom-Json
            $taskProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($taskRecord.pid)"
            if ($taskProcess -and $taskProcess.CommandLine -match "port-forward.*service/$taskName" -and $taskProcess.CommandLine -match 'docker-desktop' -and $taskProcess.CommandLine -match 'burmalda') { Stop-Process -Id $taskRecord.pid }
            Remove-Item -LiteralPath $taskFile
        }
    }
}
function Start-DemoAccess {
    Stop-DemoAccess
    foreach ($taskEndpoint in @(@{name='demo-control';port=5310;target=8080},@{name='grafana';port=3000;target=3000})) {
        $taskName = $taskEndpoint.name
        $taskLog = Join-Path $taskRuntime "access-$taskName.log"
        $taskProcess = Start-Process (Get-Command kubectl).Source -ArgumentList @('--context',$taskContext,'--namespace',$taskNamespace,'port-forward',"service/$taskName","$($taskEndpoint.port):$($taskEndpoint.target)",'--address','127.0.0.1') -WindowStyle Hidden -PassThru -RedirectStandardOutput $taskLog -RedirectStandardError (Join-Path $taskRuntime "access-$taskName.err")
        @{pid=$taskProcess.Id;port=$taskEndpoint.port} | ConvertTo-Json | Set-Content (Join-Path $taskRuntime "access-$taskName.json")
        $taskDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        do {
            $taskProcess.Refresh()
            if ($taskProcess.HasExited) { throw "Port-forward $taskName exited; see .artifacts/k8s/access-$taskName.err." }
            $taskReady = Select-String -LiteralPath $taskLog -SimpleMatch "Forwarding from 127.0.0.1:$($taskEndpoint.port)" -Quiet
            if (!$taskReady) { Start-Sleep -Milliseconds 200 }
        } while (!$taskReady -and [DateTimeOffset]::UtcNow -lt $taskDeadline)
        if (!$taskReady) { throw "Port-forward $taskName did not start." }
        Write-Host "[info] http://localhost:$($taskEndpoint.port)"
    }
}
function Stop-Demo {
    # Removing HPA must precede scaling Deployments down; no controller can bring them back.
    Invoke-Kube delete hpa catalog gateway --ignore-not-found
    Stop-DemoAccess
    $taskMarker = Join-Path $taskRuntime 'demo-enabled'
    if (Test-Path -LiteralPath $taskMarker) { Remove-Item -LiteralPath $taskMarker }
}
