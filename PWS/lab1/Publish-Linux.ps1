$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    foreach ($project in 'WS01-01','WS01-02','Gateway') {
        dotnet publish "src/$project/$project.csproj" -c Release -r linux-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None -p:DebugSymbols=false -p:EnableCompressionInSingleFile=true `
            -m:1 -o "artifacts/publish/$project"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project." }
        $files = @(Get-ChildItem "artifacts/publish/$project" -File)
        if ($files.Count -ne 1 -or $files[0].Name -ne $project) {
            throw "Expected exactly one executable for $project."
        }
        Write-Host "[info] $project Linux single-file ready ($($files[0].Length) bytes)." -ForegroundColor Green
    }
} finally { Pop-Location }
