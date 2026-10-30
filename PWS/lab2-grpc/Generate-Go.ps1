# Regenerate checked-in Go stubs from the shared proto. Windows x64.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = $PSScriptRoot
$toolsDir = Join-Path $root 'artifacts/tools'
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

Push-Location $root
$previousGoBin = $env:GOBIN
try {
    dotnet restore gRPCServer01/gRPCServer01.csproj
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    $assets = Get-Content gRPCServer01/obj/project.assets.json -Raw | ConvertFrom-Json
    $protoc = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder 'grpc.tools/2.85.0/tools/windows_x64/protoc.exe'
        if (Test-Path -LiteralPath $candidate) { $protoc = $candidate; break }
    }
    if (-not $protoc) { throw 'Windows x64 protoc not found in the restored Grpc.Tools package.' }

    $env:GOBIN = $toolsDir
    go install google.golang.org/protobuf/cmd/protoc-gen-go@v1.36.12
    if ($LASTEXITCODE -ne 0) { throw 'Installing protoc-gen-go failed.' }
    go install google.golang.org/grpc/cmd/protoc-gen-go-grpc@v1.5.1
    if ($LASTEXITCODE -ne 0) { throw 'Installing protoc-gen-go-grpc failed.' }
    $output = Join-Path $root 'gRPCClient02/calculatorpb'
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & $protoc '-I=protos' "--plugin=protoc-gen-go=$toolsDir/protoc-gen-go.exe" `
        "--plugin=protoc-gen-go-grpc=$toolsDir/protoc-gen-go-grpc.exe" `
        "--go_out=$output" '--go_opt=paths=source_relative' `
        "--go-grpc_out=$output" '--go-grpc_opt=paths=source_relative' 'protos/calculator.proto'
    if ($LASTEXITCODE -ne 0) { throw 'protoc failed.' }
    Write-Host 'Go stubs regenerated from protos/calculator.proto.'
}
finally {
    $env:GOBIN = $previousGoBin
    Pop-Location
}
