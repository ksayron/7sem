$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    docker compose -f compose.yaml down
    if ($LASTEXITCODE -ne 0) { throw 'Docker shutdown failed.' }
    Write-Host '[info] PWS lab 1 containers stopped.' -ForegroundColor Green
} finally { Pop-Location }
