# Optional native-host verification using an existing Release build.
param([switch]$Browser)
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path $PSScriptRoot -Parent
$owned = @()
$hosts = @(
    @{ Project='WS01-01'; Port=8111; Extra='--INSTANCE_NAME WS01-01-A' },
    @{ Project='WS01-01'; Port=8112; Extra='--INSTANCE_NAME WS01-01-B' },
    @{ Project='WS01-02'; Port=8121; Extra='--INSTANCE_NAME WS01-02-A' },
    @{ Project='WS01-02'; Port=8122; Extra='--INSTANCE_NAME WS01-02-B' },
    @{ Project='Gateway'; Port=8101; Extra='--GATEWAY_MODE sticky --BACKEND_A http://localhost:8111/ --BACKEND_B http://localhost:8112/' },
    @{ Project='Gateway'; Port=8102; Extra='--GATEWAY_MODE round-robin --BACKEND_A http://localhost:8121/ --BACKEND_B http://localhost:8122/' }
)
try {
    foreach ($hostConfig in $hosts) {
        $port = $hostConfig.Port
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Port $port is occupied; refusing to replace its owner."
        }
        $dll = Join-Path $labRoot "src/$($hostConfig.Project)/bin/Release/net10.0/$($hostConfig.Project).dll"
        $arguments = '"' + $dll + '" --urls http://localhost:' + $port + ' ' + $hostConfig.Extra
        $owned += Start-Process dotnet -ArgumentList $arguments -WorkingDirectory $labRoot -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $labRoot "artifacts/local-$port.stdout.log") `
            -RedirectStandardError (Join-Path $labRoot "artifacts/local-$port.stderr.log") -PassThru
    }
    foreach ($port in $hosts.Port) {
        $ready = $false
        for ($i=0; $i -lt 30; $i++) {
            try { Invoke-RestMethod "http://localhost:$port/health" -TimeoutSec 2 | Out-Null; $ready=$true; break }
            catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) { throw "Port $port is not ready." }
    }
    & (Join-Path $labRoot 'Test-Lab.ps1') -Deployment local
    if ($Browser) {
        $nodePackages = Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules'
        if (Test-Path $nodePackages) { $env:NODE_PATH = $nodePackages }
        node (Join-Path $PSScriptRoot 'verify_browser.cjs')
        if ($LASTEXITCODE -ne 0) { throw 'Browser verification failed.' }
    }
} finally {
    foreach ($process in $owned) {
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
    }
}
