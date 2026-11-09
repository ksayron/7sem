param([switch]$Local)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path $PSScriptRoot -Parent
if(!$Local){
    Push-Location $taskRoot
    try {docker compose --profile pubsub stop; if($LASTEXITCODE){throw 'Docker lab stop failed.'}}
    finally {Pop-Location}
    return
}
$taskManifest = Join-Path $taskRoot '.artifacts/lab-runtime/processes.json'
& "$PSScriptRoot/Stop-RaftCluster.ps1" -Local
if (!(Test-Path $taskManifest)) { Write-Host '[info] No managed lab processes.'; return }
foreach ($taskEntry in (Get-Content $taskManifest -Raw | ConvertFrom-Json)) {
    $taskProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($taskEntry.Pid)"
    # Verify the command line to avoid terminating an unrelated process after PID reuse.
    if ($taskProcess -and $taskProcess.CommandLine -like "*$($taskEntry.Project)*") {
        Get-CimInstance Win32_Process -Filter "ParentProcessId=$($taskEntry.Pid)" | ForEach-Object { Stop-Process -Id $_.ProcessId -ErrorAction SilentlyContinue }
        Stop-Process -Id $taskEntry.Pid -ErrorAction SilentlyContinue
        Write-Host "[info] Stopped $($taskEntry.Name)" -ForegroundColor Cyan
    }
}
