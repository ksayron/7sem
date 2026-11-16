#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Prepare','Build','Start','Status','Stop')][string]$Action)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskRuntime=Join-Path $taskRoot '.artifacts/k8s'
$taskLive=Join-Path $taskRoot '.artifacts/live-release'
$taskContext='docker-desktop'; $taskNamespace='burmalda'
. (Join-Path $PSScriptRoot 'Kubernetes.Demo.ps1')
function Native { & $args[0] $args[1..($args.Count-1)]; if ($LASTEXITCODE) { throw "Failed: $($args[0]) $($args[1])" } }
function Invoke-Kube { Native kubectl --context $taskContext --namespace $taskNamespace @args }
function Stop-Bridge {
    $taskPidFile=Join-Path $taskLive 'bridge.json'
    if (Test-Path $taskPidFile) {
        $taskRecord=Get-Content -LiteralPath $taskPidFile -Raw | ConvertFrom-Json
        $taskProcess=Get-CimInstance Win32_Process -Filter "ProcessId=$($taskRecord.pid)"
        if ($taskProcess -and $taskProcess.CommandLine -like '*tools*ci*release-bridge.py*') { Stop-Process -Id $taskRecord.pid }
        Remove-Item -LiteralPath $taskPidFile
    }
}
Push-Location $taskRoot
try {
    New-Item -ItemType Directory -Force $taskLive,$taskRuntime | Out-Null
    switch ($Action) {
        'Prepare' {
            $null=Invoke-Kube get deployment/catalog deployment/gateway deployment/web
            Initialize-Demo
            $taskKeyPath=Join-Path $taskLive 'bridge-token'
            if (!(Test-Path $taskKeyPath)) { [IO.File]::WriteAllText($taskKeyPath,[Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))) }
            $taskKey=[IO.File]::ReadAllText($taskKeyPath).Trim()
            $taskSecret=@{apiVersion='v1';kind='Secret';metadata=@{name='burmalda-release-bridge';namespace=$taskNamespace};type='Opaque';stringData=@{KEY=$taskKey}}
            $taskSecretFile=Join-Path $taskLive 'bridge-secret.json'
            $taskSecret | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $taskSecretFile
            Invoke-Kube apply -f $taskSecretFile
            $taskSource=Join-Path $taskLive 'source'
            if (!(Test-Path (Join-Path $taskSource '.git'))) {
                $taskRepo=(Native git rev-parse --show-toplevel | Out-String).Trim()
                $taskSha=(Native git rev-parse HEAD | Out-String).Trim()
                Native git clone --shared --no-checkout $taskRepo $taskSource
                Native git -C $taskSource sparse-checkout init --cone
                Native git -C $taskSource sparse-checkout set RIOT/burmalda
                Native git -C $taskSource checkout -b demo/live-release $taskSha
                Native git -C $taskSource remote set-url origin https://github.com/ksayron/7sem.git
                Native git -C $taskSource push -u origin demo/live-release
            }
            Write-Host '[info] Prepared isolated source branch and bridge key. Git/Jenkins credentials remain on Windows.'
        }
        'Build' {
            $taskTag='lr10-demo-'+(Get-Date -Format yyyyMMddHHmmss)
            Build-Demo $taskTag
            Get-DemoImages
            Set-Content -LiteralPath (Join-Path $taskLive 'image-tag') $taskTag
        }
        'Start' {
            if (!(Test-Path (Join-Path $taskLive 'bridge-token')) -or !(Test-Path (Join-Path $taskLive 'image-tag'))) { throw 'Run Prepare and Build first.' }
            Invoke-Kube apply -f k8s/ci-rbac.yaml
            Stop-Bridge
            $taskBridge=Start-Process (Get-Command python).Source -ArgumentList @('-u',(Join-Path $PSScriptRoot 'ci/release-bridge.py')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskLive 'bridge.log') -RedirectStandardError (Join-Path $taskLive 'bridge.err')
            @{pid=$taskBridge.Id} | ConvertTo-Json | Set-Content (Join-Path $taskLive 'bridge.json')
            Start-Sleep -Seconds 2
            $taskHealth=Invoke-RestMethod http://127.0.0.1:18090/health -TimeoutSec 5
            if ($taskHealth.service -ne 'burmalda-release-bridge') { throw 'Bridge startup failed.' }
            Start-DemoInfrastructure ([IO.File]::ReadAllText((Join-Path $taskLive 'image-tag')).Trim())
            Invoke-Kube apply -f k8s/demo/live-access.yaml
            $taskPatch=@{spec=@{template=@{spec=@{containers=@(@{name='demo-control';env=@(
                @{name='Release__Enabled';value='true'},@{name='Release__BridgeUrl';value='http://host.docker.internal:18090'},
                @{name='Release__Key';valueFrom=@{secretKeyRef=@{name='burmalda-release-bridge';key='KEY'}}},
                @{name='Demo__LibraryUrl';value='http://localhost:30530'}
            )})}}}}
            $taskPatch | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $taskLive 'control-patch.json')
            Invoke-Kube patch deployment/demo-control --type strategic --patch-file (Join-Path $taskLive 'control-patch.json')
            Complete-DemoStart
            foreach ($taskName in @('catalog','gateway')) {
                @{spec=@{minReplicas=2}} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $taskLive 'hpa-patch.json')
                Invoke-Kube patch "hpa/$taskName" --type merge --patch-file (Join-Path $taskLive 'hpa-patch.json')
            }
            # Apply traffic-drain hooks once before measuring a release. Keep every running image.
            foreach ($taskName in @('identity','catalog','lending','economy','reputation','notifications','gateway','web')) {
                $taskPatch=@{spec=@{template=@{spec=@{terminationGracePeriodSeconds=45;containers=@(@{name=$taskName;lifecycle=@{preStop=@{exec=@{command=@('/bin/sh','-c','sleep 10')}}}})}}}}
                $taskPatch | ConvertTo-Json -Depth 15 | Set-Content (Join-Path $taskLive 'drain-patch.json')
                Invoke-Kube patch "deployment/$taskName" --type strategic --patch-file (Join-Path $taskLive 'drain-patch.json')
                Invoke-Kube rollout status "deployment/$taskName" --timeout=300s
            }
            & (Join-Path $PSScriptRoot 'Jenkins.ps1') Start -Branch demo/live-release
            if (!$?) { throw 'Cannot configure Jenkins.' }
            Start-DemoAccess
            Write-Host '[info] Library: http://localhost:30530; control: http://localhost:5310; Grafana: http://localhost:3000/d/burmalda-demo'
            Write-Host '[info] Wait for the initial Jenkins build, prepare clients, start the release scenario, then press Publish.'
        }
        'Status' {
            Invoke-Kube get deployments,hpa
            Invoke-RestMethod http://127.0.0.1:18090/health -TimeoutSec 5
            & (Join-Path $PSScriptRoot 'Jenkins.ps1') Status
        }
        'Stop' {
            Stop-Bridge
            & (Join-Path $PSScriptRoot 'Kubernetes.ps1') Stop -Demo
            if (!$?) { throw 'Cannot stop the demo.' }
            & (Join-Path $PSScriptRoot 'Jenkins.ps1') Stop
            if (!$?) { throw 'Cannot stop Jenkins.' }
            Write-Host '[info] Demo and CI stopped. PVC, history and source branch retained.'
        }
    }
} finally { Pop-Location }
