# Build both clients, start both servers, verify all four combinations, clean up.
[CmdletBinding()]
param(
    [ValidateRange(1, 65535)][int]$DotnetPort = 8301,
    [ValidateRange(1, 65535)][int]$NodePort = 8302
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null
$servers = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$report = [ordered]@{
    startedAt = [DateTimeOffset]::Now.ToString('o')
    completedAt = $null
    success = $false
    combinations = @()
    error = $null
}

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE." }
}

function Assert-FreePort([int]$Port) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() }
    catch { throw "Port $Port is unavailable. Stop its owner or choose different test ports." }
    finally { $listener.Stop() }
}

function Wait-Server([System.Diagnostics.Process]$Process, [int]$Port) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Process.HasExited) { throw "Server on port $Port exited; see artifacts/server*.err.log." }
        $probe = [System.Net.Sockets.TcpClient]::new()
        try { $probe.Connect('127.0.0.1', $Port); return }
        catch { Start-Sleep -Milliseconds 150 }
        finally { $probe.Dispose() }
    }
    throw "Server on port $Port did not start within 20 seconds."
}

function Test-Pair([string]$Name, [string]$Executable, [string[]]$Arguments) {
    Write-Host "Checking $Name"
    $output = & $Executable @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output | Set-Content -Encoding UTF8 (Join-Path $artifacts "$Name.log")
    $output | Out-Host
    $passed = $exitCode -eq 0 -and ($output -match '^Checks: 23/23 passed, 0 failed\.$').Count -eq 1
    $report.combinations += [ordered]@{ pair = $Name; passed = $passed; exitCode = $exitCode; checks = 23 }
    if (-not $passed) { throw "$Name failed; see artifacts/$Name.log." }
}

Push-Location $root
try {
    if ($DotnetPort -eq $NodePort) { throw 'The two server ports must differ.' }
    Assert-FreePort $DotnetPort
    Assert-FreePort $NodePort
    Invoke-Checked 'dotnet' @('restore', 'PWS-Lab2-gRPC.sln', '--locked-mode')
    Invoke-Checked 'dotnet' @('build', 'PWS-Lab2-gRPC.sln', '-c', 'Release', '--no-restore', '-m:1')
    Push-Location (Join-Path $root 'gRPCServer02')
    try {
        Invoke-Checked 'npm.cmd' @('ci', '--cache', (Join-Path $artifacts 'npm-cache'), '--no-audit', '--no-fund')
        Invoke-Checked 'npm.cmd' @('run', 'check')
    }
    finally { Pop-Location }
    Push-Location (Join-Path $root 'gRPCClient02')
    try {
        Invoke-Checked 'go' @('mod', 'download')
        Invoke-Checked 'go' @('mod', 'verify')
        Invoke-Checked 'go' @('vet', './...')
        Invoke-Checked 'go' @('build', '-mod=readonly', '-o', (Join-Path $artifacts 'gRPCClient02.exe'), '.')
    }
    finally { Pop-Location }

    $server01Dll = Join-Path $root 'gRPCServer01/bin/Release/net10.0/gRPCServer01.dll'
    $server01 = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @("`"$server01Dll`"", '--port', $DotnetPort) `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $artifacts 'server01.log') `
        -RedirectStandardError (Join-Path $artifacts 'server01.err.log')
    $servers.Add($server01)
    $server02Js = Join-Path $root 'gRPCServer02/server.js'
    $server02 = Start-Process -FilePath (Get-Command node).Source -ArgumentList @("`"$server02Js`"", '--port', $NodePort) `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $artifacts 'server02.log') `
        -RedirectStandardError (Join-Path $artifacts 'server02.err.log')
    $servers.Add($server02)
    Wait-Server $server01 $DotnetPort
    Wait-Server $server02 $NodePort

    $client01 = Join-Path $root 'gRPCClient01/bin/Release/net10.0/gRPCClient01.dll'
    $client02 = Join-Path $artifacts 'gRPCClient02.exe'
    Test-Pair 'CSharp-to-ASPNET' 'dotnet' @($client01, "http://127.0.0.1:$DotnetPort")
    Test-Pair 'Go-to-Node' $client02 @('-address', "127.0.0.1:$NodePort")
    Test-Pair 'Go-to-ASPNET' $client02 @('-address', "127.0.0.1:$DotnetPort")
    Test-Pair 'CSharp-to-Node' 'dotnet' @($client01, "http://127.0.0.1:$NodePort")
    $report.success = $true
    Write-Host 'PASS: 4 combinations, 92/92 live gRPC checks.'
}
catch {
    $report.error = $_.Exception.Message
    throw
}
finally {
    foreach ($server in $servers) {
        if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
        $server.Dispose()
    }
    $report.completedAt = [DateTimeOffset]::Now.ToString('o')
    $report | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $artifacts 'verification.json')
    Pop-Location
}
