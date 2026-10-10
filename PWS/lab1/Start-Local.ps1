# Starts the six Windows hosts using existing NuGet assets, with no restore/download.
$ErrorActionPreference = 'Stop'
$stateFile = Join-Path $PSScriptRoot 'artifacts/local-processes.json'
if (Test-Path $stateFile) { & (Join-Path $PSScriptRoot 'Stop-Local.ps1') }
$hosts = @(
    @{ Project='WS01-01'; Port=8111; Extra='--INSTANCE_NAME WS01-01-A' },
    @{ Project='WS01-01'; Port=8112; Extra='--INSTANCE_NAME WS01-01-B' },
    @{ Project='WS01-02'; Port=8121; Extra='--INSTANCE_NAME WS01-02-A' },
    @{ Project='WS01-02'; Port=8122; Extra='--INSTANCE_NAME WS01-02-B' },
    @{ Project='Gateway'; Port=8101; Extra='--GATEWAY_MODE sticky --BACKEND_A http://localhost:8111/ --BACKEND_B http://localhost:8112/' },
    @{ Project='Gateway'; Port=8102; Extra='--GATEWAY_MODE round-robin --BACKEND_A http://localhost:8121/ --BACKEND_B http://localhost:8122/' }
)
$listeners = [System.Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners()
foreach ($hostConfig in $hosts) {
    if ($listeners.Port -contains $hostConfig.Port) { throw "Port $($hostConfig.Port) is occupied; its process will not be replaced." }
}
Push-Location $PSScriptRoot
try {
    dotnet build PWS-Lab1.sln --no-restore -c Release -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Offline build failed. NuGet assets must already exist; no restore was attempted.' }
    New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts') -Force | Out-Null
    $owned = @()
    foreach ($hostConfig in $hosts) {
        $port = $hostConfig.Port
        $dll = Join-Path $PSScriptRoot "src/$($hostConfig.Project)/bin/Release/net10.0/$($hostConfig.Project).dll"
        $arguments = '"' + $dll + '" --urls http://localhost:' + $port + ' ' + $hostConfig.Extra
        $process = Start-Process dotnet -ArgumentList $arguments -WorkingDirectory $PSScriptRoot -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $PSScriptRoot "artifacts/local-$port.stdout.log") `
            -RedirectStandardError (Join-Path $PSScriptRoot "artifacts/local-$port.stderr.log") -PassThru
        $owned += @{ Id=$process.Id; StartTicks=$process.StartTime.ToUniversalTime().Ticks; Port=$port }
        ConvertTo-Json -InputObject $owned | Set-Content -LiteralPath $stateFile -Encoding UTF8
    }
    foreach ($port in $hosts.Port) {
        $ready = $false
        for ($attempt=0; $attempt -lt 30; $attempt++) {
            try {
                Invoke-RestMethod "http://localhost:$port/health" -TimeoutSec 2 | Out-Null
                $ready=$true; break
            } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) { throw "Service on port $port is not ready; see artifacts/local-$port.stderr.log." }
    }
    Write-Host '[info] Six Windows hosts ready. No packages were downloaded.' -ForegroundColor Green
    Write-Host '[info] Sticky Session: http://localhost:8101/' -ForegroundColor Cyan
    Write-Host '[info] Round Robin:    http://localhost:8102/' -ForegroundColor Cyan
    Write-Host '[info] Stop: ./Stop-Local.ps1' -ForegroundColor Cyan
} catch {
    if (Test-Path $stateFile) { & (Join-Path $PSScriptRoot 'Stop-Local.ps1') }
    throw
} finally { Pop-Location }
