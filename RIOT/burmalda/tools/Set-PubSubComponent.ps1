param(
    [Parameter(Mandatory)][ValidateSet('Coordinator','GameApi','BrokerA','BrokerB','Subscribers','Identity','Gateway','Web')][string]$Component,
    [Parameter(Mandatory)][ValidateSet('Start','Stop','Restart')][string]$Action,
    [switch]$Local
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if(!$Local){
    $taskServices=@{Coordinator='concurrency-lab';GameApi='library-game';BrokerA='library-broker-a';BrokerB='library-broker-b';Subscribers='library-subscribers';Identity='identity';Gateway='gateway';Web='web'}
    Push-Location $taskRoot
    try {
        $taskVerb=switch($Action){Start {'start'} Stop {'stop'} Restart {'restart'}}
        docker compose --profile pubsub $taskVerb $taskServices[$Component]
        if($LASTEXITCODE){throw 'Component container action failed.'}
    } finally {Pop-Location}
    return
}
$taskRuntime = Join-Path $taskRoot '.artifacts/pubsub-runtime'
$taskManifest = Join-Path $taskRuntime 'processes.json'
New-Item -ItemType Directory -Force $taskRuntime | Out-Null
$taskEntries = if (Test-Path $taskManifest) { @(Get-Content $taskManifest -Raw | ConvertFrom-Json) } else { @() }
$taskComponents = @{
    Coordinator = @('src/Services/ConcurrencyLab/Burmalda.ConcurrencyLab.Api',5107)
    GameApi = @('src/Services/LibraryGame/Burmalda.LibraryGame.Api',5108)
    BrokerA = @('src/Services/LibraryGame/Burmalda.LibraryGame.Broker',5109)
    BrokerB = @('src/Services/LibraryGame/Burmalda.LibraryGame.BrokerB',5110)
    Subscribers = @('src/Services/LibraryGame/Burmalda.LibraryGame.Subscribers',5111)
    Identity = @('src/Services/Identity/Burmalda.Identity.Api',5101)
    Gateway = @('src/Gateway/Burmalda.Gateway',5000)
    Web = @('src/Clients/Burmalda.Web',5300)
}
$taskSpec = $taskComponents[$Component]
$taskDirectory = Join-Path $taskRoot $taskSpec[0]
$taskName = Split-Path $taskDirectory -Leaf
$taskDll = Join-Path $taskDirectory "bin/Debug/net9.0/$taskName.dll"
$taskExisting = $taskEntries | Where-Object Component -eq $Component | Select-Object -Last 1
if ($Action -in @('Stop','Restart') -and $taskExisting) {
    $taskProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($taskExisting.Pid)"
    if ($taskProcess -and $taskProcess.CommandLine.Contains($taskExisting.CommandMarker)) {
        Get-CimInstance Win32_Process -Filter "ParentProcessId=$($taskExisting.Pid)" | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        Stop-Process -Id $taskExisting.Pid -ErrorAction SilentlyContinue
        Wait-Process -Id $taskExisting.Pid -Timeout 10 -ErrorAction SilentlyContinue
        Write-Host "[info] Stopped $Component"
    }
    $taskEntries = @($taskEntries | Where-Object Component -ne $Component)
    ConvertTo-Json -InputObject @($taskEntries) -Depth 6 | Set-Content $taskManifest
}
if ($Action -in @('Start','Restart')) {
    if (Get-NetTCPConnection -State Listen -LocalPort $taskSpec[1] -ErrorAction SilentlyContinue) {
        $taskOwner = if ($taskExisting) { Get-CimInstance Win32_Process -Filter "ProcessId=$($taskExisting.Pid)" } else { $null }
        if ($Action -eq 'Start' -and $taskOwner -and $taskOwner.CommandLine.Contains($taskExisting.CommandMarker)) { Write-Host "[info] $Component is already running"; return }
        throw "Port $($taskSpec[1]) is occupied; no unrelated process was stopped."
    }
    if (!(Test-Path $taskDll)) { throw "Build the solution first. Missing $taskName." }
    $taskArgs = @("`"$taskDll`"", '--urls', "http://localhost:$($taskSpec[1])")
    if ($Component -eq 'Web') {
        # The existing Blazor SDK owns development hosting; no second application scaffold.
        $taskArgs = @('run','--no-build','--no-launch-profile','--project',"`"$taskDirectory`"",'--urls','http://localhost:5300')
    }
    if ($Component -in @('BrokerA','BrokerB')) {
        $taskBroker = if ($Component -eq 'BrokerA') { 'A' } else { 'B' }
        $taskArgs += @('--Broker:Id',$taskBroker,'--Broker:Storage',"`"$(Join-Path $taskRuntime "broker-$taskBroker.db")`"")
    }
    $taskEnvironment = @{ASPNETCORE_ENVIRONMENT='Development'}
    $taskProcess = Start-Process dotnet -ArgumentList $taskArgs -WorkingDirectory $taskDirectory -Environment $taskEnvironment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $taskRuntime "$Component.log") -RedirectStandardError (Join-Path $taskRuntime "$Component.err")
    $taskEntries = @($taskEntries | Where-Object Component -ne $Component)
    $taskEntries += [pscustomobject]@{Component=$Component;Pid=$taskProcess.Id;CommandMarker=$(if($Component -eq 'Web'){$taskDirectory}else{$taskDll});Port=$taskSpec[1]}
    ConvertTo-Json -InputObject @($taskEntries) -Depth 6 | Set-Content $taskManifest
    $taskReady = $false
    for ($taskAttempt = 0; $taskAttempt -lt 30 -and !$taskReady; $taskAttempt++) {
        if ($taskProcess.HasExited) { throw "$Component exited during startup. Inspect .artifacts/pubsub-runtime/$Component.err and .log." }
        try {
            $taskCheckPath = if ($Component -eq 'Web') { '/' } else { '/health/ready' }
            Invoke-WebRequest "http://localhost:$($taskSpec[1])$taskCheckPath" -TimeoutSec 1 | Out-Null
            $taskReady = $true
        } catch { Start-Sleep -Milliseconds 300 }
    }
    if (!$taskReady) { throw "$Component did not become ready. Inspect .artifacts/pubsub-runtime/$Component.log." }
    Write-Host "[info] $Component started on http://localhost:$($taskSpec[1])"
}

