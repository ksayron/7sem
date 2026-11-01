param(
    [string]$DeviceId = '',
    [switch]$Offline,
    [ValidateRange(10, 600)][int]$BootTimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if ($Offline) { $env:SPORTS_LAB7_OFFLINE = '1' }

if (Test-Path 'D:\Android\sdk-coffee') {
    $env:ANDROID_HOME = 'D:\Android\sdk-coffee'
    $env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
    $env:GRADLE_USER_HOME = 'D:\Android\gradle-coffee'
    $env:TEMP = 'D:\Android\temp'
    $env:TMP = $env:TEMP
}

Push-Location $project
try {
    $sdk = $env:ANDROID_HOME
    if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\sdk' }
    $adb = Join-Path $sdk 'platform-tools\adb.exe'
    if (-not (Test-Path $adb)) { throw "ADB не найден: $adb" }
    $devices = @(& $adb devices)
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось получить список устройств ADB.' }
    $connected = @($devices | Select-String '^emulator-\d+\s+device$')
    if (-not $DeviceId -and $connected.Count -eq 0) {
        $emulator = Join-Path $sdk 'emulator\emulator.exe'
        if (Test-Path $emulator) {
            # Keep userdata, bypass snapshots with a stale graphics state.
            Start-Process -FilePath $emulator -ArgumentList @('-avd', 'coffee_lab', '-gpu', 'swiftshader', '-feature', '-Vulkan', '-memory', '1536', '-cores', '2', '-no-snapshot-load', '-no-snapshot-save') -WindowStyle Hidden
        } else {
            flutter emulators --launch coffee_lab
            if ($LASTEXITCODE -ne 0) { throw 'Не удалось запустить coffee_lab. Запустите Android AVD через Device Manager.' }
        }
        Write-Host 'Ожидание подключения Android-эмулятора...'
        $connectionTimer = [Diagnostics.Stopwatch]::StartNew()
        while ($connectionTimer.Elapsed.TotalSeconds -lt $BootTimeoutSeconds) {
            $connected = @(& $adb devices | Select-String '^emulator-\d+\s+device$')
            if ($LASTEXITCODE -eq 0 -and $connected.Count -gt 0) { break }
            Start-Sleep -Seconds 2
        }
    }
    if (-not $DeviceId) {
        if ($connected.Count -eq 0) { throw 'Android-эмулятор не найден. Запустите AVD и повторите команду.' }
        $DeviceId = ($connected[0].Line -split '\s+')[0]
    }

    # ADB can report "device" before Android's package/activity services are ready.
    # Check both new and already running devices before Flutter builds/installs.
    Write-Host "Ожидание загрузки Android и службы установки пакетов ($DeviceId)..."
    $bootTimer = [Diagnostics.Stopwatch]::StartNew()
    $ready = $false
    $lastStatus = 'Нет ответа от Android.'
    while ($bootTimer.Elapsed.TotalSeconds -lt $BootTimeoutSeconds) {
        try {
            $status = @(& $adb -s $DeviceId shell 'getprop sys.boot_completed; cmd package path android; service check activity' 2>&1)
            $statusExitCode = $LASTEXITCODE
            $lastStatus = ($status | ForEach-Object { "$_" }) -join "`n"
            $ready = $statusExitCode -eq 0 -and
                $lastStatus -match '(?m)^1\r?$' -and
                $lastStatus -match '(?m)^package:.+\.apk\r?$' -and
                $lastStatus -match 'Service activity: found'
        } catch {
            $lastStatus = $_.Exception.Message
        }
        if ($ready) { break }
        Start-Sleep -Seconds 2
    }
    if (-not $ready) {
        throw "Android на $DeviceId не готов за $BootTimeoutSeconds с. В Android Studio откройте Device Manager, остановите этот AVD (Stop) и выберите Cold Boot, затем повторите запуск. Последний ответ ADB:`n$lastStatus"
    }
    Write-Host "Android готов ($DeviceId). Запуск Flutter..."
    flutter run -d $DeviceId
    if ($LASTEXITCODE -ne 0) { throw 'Flutter завершился с ошибкой.' }
} finally {
    Pop-Location
}

