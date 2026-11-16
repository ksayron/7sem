#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Prepare','Build','Start','Status','Run','Stop')][string]$Action,
    [ValidatePattern('^[a-zA-Z0-9/_.-]+$')][string]$Branch = 'lab10/cicd'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskSecrets = Join-Path $taskRoot '.artifacts/ci/secrets'
$taskCompose = Join-Path $taskRoot 'docker/jenkins/compose.yaml'
$taskUrl = 'http://127.0.0.1:8081'
function Native {
    & $args[0] $args[1..($args.Count-1)]
    if ($LASTEXITCODE) { throw "Command failed: $($args[0]) $($args[1])" }
}
function K { Native kubectl --context docker-desktop -n burmalda @args }
function Api([string]$Path, [string]$Method='GET', $Body=$null) {
    $taskPassword = [IO.File]::ReadAllText((Join-Path $taskSecrets 'admin-password')).Trim()
    $taskHeaders = @{ Authorization='Basic '+[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('admin:'+$taskPassword)) }
    $taskSession = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
    if ($Method -ne 'GET') {
        $taskCrumb = Invoke-RestMethod "$taskUrl/crumbIssuer/api/json" -Headers $taskHeaders -WebSession $taskSession
        $taskHeaders[$taskCrumb.crumbRequestField] = $taskCrumb.crumb
    }
    Invoke-RestMethod "$taskUrl/$Path" -Method $Method -Headers $taskHeaders -WebSession $taskSession -Body $Body
}
Push-Location $taskRoot
try {
    switch ($Action) {
        'Prepare' {
            New-Item -ItemType Directory -Force $taskSecrets | Out-Null
            if (!(Test-Path (Join-Path $taskSecrets 'admin-password'))) {
                [IO.File]::WriteAllText((Join-Path $taskSecrets 'admin-password'), [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)))
            }
            & (Join-Path $PSScriptRoot 'Kubernetes.ps1') Prepare
            if (!$?) { throw 'Cannot prepare Kubernetes namespace/Secret.' }
            foreach ($taskFile in @('configmap.yaml','web-config.yaml','service.yaml','statefulsets.yaml','ci-rbac.yaml')) { K apply -f "k8s/$taskFile" }
            $taskDeployments = (K get deployments -o json | ConvertFrom-Json).items.metadata.name
            if ('catalog' -notin $taskDeployments) { K apply -f k8s/deployment.yaml }
            foreach ($taskService in @('postgres','rabbitmq')) {
                K scale "statefulset/$taskService" --replicas=1
                K rollout status "statefulset/$taskService" --timeout=300s
            }
            foreach ($taskService in @('identity','catalog','lending','economy','reputation','notifications','gateway','web')) {
                K scale "deployment/$taskService" --replicas=1
                K rollout status "deployment/$taskService" --timeout=300s
            }
            $taskConfig = Native kubectl --context docker-desktop config view --raw --minify -o json | ConvertFrom-Json
            $taskToken = (K create token burmalda-ci --duration=168h | Out-String).Trim()
            $taskKubeconfig = @{
                apiVersion='v1'; kind='Config'; 'current-context'='docker-desktop'
                clusters=@(@{name='docker-desktop'; cluster=@{server=$taskConfig.clusters[0].cluster.server; 'certificate-authority-data'=$taskConfig.clusters[0].cluster.'certificate-authority-data'}})
                contexts=@(@{name='docker-desktop'; context=@{cluster='docker-desktop'; user='burmalda-ci'; namespace='burmalda'}})
                users=@(@{name='burmalda-ci'; user=@{token=$taskToken}})
            }
            [IO.File]::WriteAllText((Join-Path $taskSecrets 'kubeconfig'), ($taskKubeconfig | ConvertTo-Json -Depth 8))
            Write-Host '[info] Prepared. Kubernetes token expires in approximately seven days; Prepare renews it. Credentials remain in .artifacts/ci/secrets.'
        }
        'Build' {
            Native python (Join-Path $PSScriptRoot 'ci/download-plugins.py')
            Native docker compose -f $taskCompose build jenkins
            Native docker compose -f $taskCompose build agent
        }
        'Start' {
            if (!(Test-Path (Join-Path $taskSecrets 'kubeconfig'))) { throw 'Run Prepare first.' }
            $env:BURMALDA_CI_BRANCH = $Branch
            Native docker compose -f $taskCompose up -d jenkins
            $taskReady = $false
            for ($taskAttempt=0; $taskAttempt -lt 120; $taskAttempt++) {
                try { $null = Api 'api/json'; $taskReady=$true; break } catch { Start-Sleep -Seconds 2 }
            }
            if (!$taskReady) { throw 'Jenkins startup timed out. Inspect docker compose logs jenkins.' }
            $taskScript = "println(jenkins.model.Jenkins.get().getComputer('burmalda-linux').getJnlpMac())"
            $taskAgentSecret = (Api 'scriptText' 'POST' @{script=$taskScript} | Out-String).Trim()
            if ($taskAgentSecret -notmatch '^[0-9a-f]+$') { throw 'Cannot obtain agent secret.' }
            [IO.File]::WriteAllText((Join-Path $taskSecrets 'agent-secret'), $taskAgentSecret)
            Native docker compose -f $taskCompose up -d agent
            $taskOnline = $false
            for ($taskAttempt=0; $taskAttempt -lt 60; $taskAttempt++) {
                $taskAgent = Api 'computer/burmalda-linux/api/json'
                if (!$taskAgent.offline) { $taskOnline=$true; break }
                Start-Sleep -Seconds 2
            }
            if (!$taskOnline) { throw 'Jenkins agent did not connect.' }
            Write-Host "[info] Jenkins: $taskUrl/job/burmalda-lab10/; login admin; password file .artifacts/ci/secrets/admin-password. Branch: $Branch"
        }
        'Status' {
            Native docker compose -f $taskCompose ps
            Api 'job/burmalda-lab10/api/json?tree=name,lastBuild[number,result,building,url],lastSuccessfulBuild[number,url]' | ConvertTo-Json -Depth 5
        }
        'Run' { $null = Api 'job/burmalda-lab10/build' 'POST'; Write-Host '[info] Build queued.' }
        'Stop' { Native docker compose -f $taskCompose stop }
    }
} finally { Pop-Location }
