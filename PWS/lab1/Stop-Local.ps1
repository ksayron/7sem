$ErrorActionPreference = 'Stop'
$stateFile = Join-Path $PSScriptRoot 'artifacts/local-processes.json'
if (-not (Test-Path $stateFile)) {
    Write-Host '[info] No managed local hosts.' -ForegroundColor Cyan
    return
}
$owned = @(Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json)
foreach ($entry in $owned) {
    $process = Get-Process -Id $entry.Id -ErrorAction SilentlyContinue
    # A reused PID must never cause an unrelated process to be stopped.
    if ($null -ne $process -and $process.StartTime.ToUniversalTime().Ticks -eq [long]$entry.StartTicks) {
        Stop-Process -Id $process.Id -ErrorAction Stop
        if (-not $process.WaitForExit(5000)) {
            throw "Managed process $($process.Id) did not exit in time; keeping its state file."
        }
    }
}
Remove-Item -LiteralPath $stateFile -Force
Write-Host '[info] Managed local hosts stopped.' -ForegroundColor Green
