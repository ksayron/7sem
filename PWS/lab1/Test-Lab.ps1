param([ValidateSet('docker','local')][string]$Deployment='docker')
$ErrorActionPreference = 'Stop'
$bundledPython = Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
if (Test-Path $bundledPython) {
    & $bundledPython (Join-Path $PSScriptRoot 'tests/verify_http.py') --deployment $Deployment
} elseif (Get-Command py -ErrorAction SilentlyContinue) {
    py -3 (Join-Path $PSScriptRoot 'tests/verify_http.py') --deployment $Deployment
} else {
    python (Join-Path $PSScriptRoot 'tests/verify_http.py') --deployment $Deployment
}
if ($LASTEXITCODE -ne 0) { throw 'HTTP verification failed; see artifacts/verification.json.' }
