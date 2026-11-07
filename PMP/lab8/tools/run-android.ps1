param([string]$DeviceId = '')
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
if (Test-Path 'D:/Android/sdk-coffee') {
    $env:ANDROID_HOME = 'D:/Android/sdk-coffee'
    $env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
    $env:GRADLE_USER_HOME = 'D:/Android/gradle-coffee'
    $env:TEMP = 'D:/Android/temp'
    $env:TMP = $env:TEMP
}
if (Test-Path 'D:/Soft/Android-Studio/jbr') { $env:JAVA_HOME = 'D:/Soft/Android-Studio/jbr' }
Push-Location $project
try {
    $sdk = $env:ANDROID_HOME
    if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android/sdk' }
    $adb = Join-Path $sdk 'platform-tools/adb.exe'
    if (-not (Test-Path $adb)) { throw 'Android platform-tools not found.' }
    $connected = @(& $adb devices | Select-String '^emulator-\d+\s+(device|offline)$')
    if (-not $DeviceId -and $connected.Count -eq 0) {
        flutter emulators --launch coffee_lab
        if ($LASTEXITCODE -ne 0) { throw 'Launch an Android emulator in Device Manager and retry.' }
    }
    for ($attempt = 0; $attempt -lt 90; $attempt++) {
        if (-not $DeviceId) {
            $connected = @(& $adb devices | Select-String '^emulator-\d+\s+device$')
            if ($connected.Count -gt 0) { $DeviceId = ($connected[0].Line -split '\s+')[0] }
        }
        if ($DeviceId) {
            $boot = (& $adb -s $DeviceId shell getprop sys.boot_completed 2>$null) -join ''
            $package = (& $adb -s $DeviceId shell cmd package path android 2>$null) -join ''
            $activity = (& $adb -s $DeviceId shell service check activity 2>$null) -join ''
            if ($boot.Trim() -eq '1' -and $package -match '^package:' -and $activity -match 'found') { break }
        }
        Start-Sleep -Seconds 2
    }
    if ($attempt -eq 90) { throw 'Android package/activity services did not become ready.' }
    flutter pub get
    if ($LASTEXITCODE -ne 0) { throw 'Dependency resolution failed.' }
    flutter run -d $DeviceId
    if ($LASTEXITCODE -ne 0) { throw 'Flutter run failed.' }
} finally { Pop-Location }
