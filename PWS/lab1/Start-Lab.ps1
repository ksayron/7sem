param([switch]$SkipBuild, [switch]$Local)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $composeFile = if ($Local) { 'compose.local.yaml' } else { 'compose.yaml' }
    if ($Local -and -not $SkipBuild) {
        foreach ($project in 'WS01-01','WS01-02','Gateway') {
            $binary = Join-Path $PSScriptRoot "artifacts/publish/$project/$project"
            if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
                throw "Linux binary is missing: $binary. Run ./Publish-Linux.ps1 first (requires NuGet), or ./Start-Local.ps1 for Windows without downloads."
            }
            $stream = [System.IO.File]::OpenRead($binary)
            try {
                $magic = New-Object byte[] 4
                $count = $stream.Read($magic, 0, 4)
                if ($count -ne 4 -or [BitConverter]::ToString($magic) -ne '7F-45-4C-46') {
                    throw "Expected a Linux ELF executable: $binary. Run ./Publish-Linux.ps1 first."
                }
            } finally { $stream.Dispose() }
        }
    }
    if (-not $SkipBuild) {
        docker compose -f $composeFile build
        if ($LASTEXITCODE -ne 0) { throw 'Docker build failed.' }
    }
    docker compose -f $composeFile up -d --no-build --pull never
    if ($LASTEXITCODE -ne 0) { throw 'Docker startup failed.' }
    foreach ($port in 8101,8102,8111,8112,8121,8122) {
        $ready = $false
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try {
                Invoke-RestMethod "http://localhost:$port/health" -TimeoutSec 2 | Out-Null
                $ready = $true
                break
            } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $ready) { throw "Service on port $port did not become ready." }
    }
    Write-Host '[info] All six containers are ready.' -ForegroundColor Green
    Write-Host '[info] Sticky Session: http://localhost:8101/' -ForegroundColor Cyan
    Write-Host '[info] Round Robin:    http://localhost:8102/' -ForegroundColor Cyan
} finally { Pop-Location }
