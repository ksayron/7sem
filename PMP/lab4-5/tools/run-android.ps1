param([string]$DeviceId = '')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot

# Настройка только текущего процесса. Системные переменные не изменяются.
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
    $connected = @(& $adb devices | Select-String '^emulator-\d+\s+device$')
    if (-not $DeviceId -and $connected.Count -eq 0) {
        flutter emulators --launch coffee_lab
        if ($LASTEXITCODE -ne 0) { throw 'Не удалось запустить coffee_lab. Запустите Android AVD через Device Manager.' }
        for ($attempt = 0; $attempt -lt 90; $attempt++) {
            $connected = @(& $adb devices | Select-String '^emulator-\d+\s+device$')
            if ($connected.Count -gt 0) { break }
            Start-Sleep -Seconds 2
        }
    }
    if (-not $DeviceId) {
        if ($connected.Count -eq 0) { throw 'Android-эмулятор не найден. Запустите AVD и повторите команду.' }
        $DeviceId = ($connected[0].Line -split '\s+')[0]
    }
    flutter run -d $DeviceId
    if ($LASTEXITCODE -ne 0) { throw 'Flutter завершился с ошибкой.' }
} finally {
    Pop-Location
}
