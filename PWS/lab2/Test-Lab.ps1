[CmdletBinding()]
param([ValidateRange(1024, 65535)][int]$Port = 8200)

$ErrorActionPreference = 'Stop'
$labRoot = $PSScriptRoot
$artifactDir = Join-Path $labRoot 'artifacts'
New-Item -ItemType Directory -Path $artifactDir -Force | Out-Null

foreach ($command in @('dotnet', 'go')) {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "$command is required and must be on PATH."
    }
}

& dotnet build (Join-Path $labRoot 'PWS-Lab2.sln') -c Release -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Server build failed.' }

# Do not stop or reuse another application's listener.
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
try { $probe.Start() }
catch { throw "Port $Port is occupied. Stop your server or choose -Port 8201." }
finally { $probe.Stop() }

$serverDll = Join-Path $labRoot 'JSONRPCServer\bin\Release\net10.0\JSONRPCServer.dll'
$baseUrl = "http://127.0.0.1:$Port"
$previousUrl = $env:JSONRPC_URL
$server = $null
try {
    $server = Start-Process -FilePath (Get-Command dotnet).Source `
        -ArgumentList ('"{0}" --urls {1}' -f $serverDll, $baseUrl) `
        -WorkingDirectory (Join-Path $labRoot 'JSONRPCServer') `
        -RedirectStandardOutput (Join-Path $artifactDir 'server.stdout.log') `
        -RedirectStandardError (Join-Path $artifactDir 'server.stderr.log') `
        -WindowStyle Hidden -PassThru

    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        $server.Refresh()
        if ($server.HasExited) { throw 'Server exited. See artifacts/server.stderr.log.' }
        try {
            $health = Invoke-RestMethod -Uri "$baseUrl/" -TimeoutSec 1
            if ($health.service -eq 'JSONRPCServer') { $ready = $true; break }
        } catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $ready) { throw 'Server did not become ready in 20 seconds.' }

    $env:JSONRPC_URL = "$baseUrl/rpc"
    Push-Location (Join-Path $labRoot 'client')
    try {
        $testOutput = @(& go test -count=1 -json . 2>&1)
        $testExit = $LASTEXITCODE
        $testOutput | Set-Content -LiteralPath (Join-Path $artifactDir 'go-test.jsonl') -Encoding UTF8
        if ($testExit -ne 0) {
            $testOutput | Write-Host
            throw 'Go HTTP integration tests failed.'
        }

        $events = @($testOutput | ForEach-Object { $_.ToString() | ConvertFrom-Json })
        $passed = @($events | Where-Object { $_.Action -eq 'pass' -and $_.Test })
        $leafTests = @($passed | Where-Object {
            $parentName = $_.Test + '/'
            -not @($passed | Where-Object { $_.Test.StartsWith($parentName) }).Count
        })
        $skipped = @($events | Where-Object { $_.Action -eq 'skip' })
        if ($leafTests.Count -eq 0 -or $skipped.Count -ne 0) {
            throw 'Expected live integration checks without skipped tests.'
        }
        Write-Host ("HTTP integration checks: {0} passed, 0 failed, 0 skipped." -f $leafTests.Count)

        & go vet .
        if ($LASTEXITCODE -ne 0) { throw 'go vet failed.' }
        & go build -o (Join-Path $artifactDir 'JSONRPCClient.exe') .
        if ($LASTEXITCODE -ne 0) { throw 'Go client build failed.' }

        & (Join-Path $artifactDir 'JSONRPCClient.exe') -url "$baseUrl/rpc" |
            Tee-Object -FilePath (Join-Path $artifactDir 'demo.log')
        if ($LASTEXITCODE -ne 0) { throw 'Go demonstration failed.' }

        [ordered]@{
            verifiedAt = [DateTimeOffset]::UtcNow.ToString('o')
            endpoint = "$baseUrl/rpc"
            passed = $leafTests.Count
            failed = 0
            skipped = 0
            serverBuild = 'passed'
            goVet = 'passed'
            clientBuild = 'passed'
            goDemo = 'passed'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $artifactDir 'verification.json') -Encoding UTF8
    } finally { Pop-Location }
} finally {
    $env:JSONRPC_URL = $previousUrl
    if ($null -ne $server) {
        $server.Refresh()
        if (-not $server.HasExited) {
            Stop-Process -Id $server.Id -ErrorAction SilentlyContinue
            $server.WaitForExit()
        }
        $server.Dispose()
    }
}

Write-Host "Verified. Logs and compiled Go client: $artifactDir"
