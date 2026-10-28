# Батарея без плагинов — 28 октября 2026

`battery_plus` и его платформенная обёртка удалены из `pubspec.yaml`, `pubspec.lock` и сгенерированной регистрации плагинов. Камера продолжает использовать существующий `image_picker`.

## Реализация

- Dart: `lib/device_services.dart`, канал `by.bstu.lab6/battery`, метод `getBatteryReading`. Ответ `{level: int, state: String}` преобразуется в собственные `BatteryReading` и `BatteryState`.
- Android: `MainActivity.kt` читает последний `ACTION_BATTERY_CHANGED` без постоянного BroadcastReceiver. Процент рассчитывается из уровня и шкалы; состояние — из статуса и подключения питания.
- iOS: `DeviceChannels.swift` читает `UIDevice.batteryLevel` и `batteryState` при включённом мониторинге, затем восстанавливает прежний режим. Неизвестный уровень возвращает `UNAVAILABLE`.
- Экран сохраняет прежние кнопку обновления, процент, русские подписи состояния и сообщения об ошибках.

## Проверки Windows / Android

- `flutter pub get` — успешно; зависимости батареи удалены без добавления новых пакетов.
- `flutter analyze --no-pub` — No issues found.
- `flutter test --no-pub --reporter expanded` — 12 тестов прошли. Проверены контракт батареи, все состояния, границы 0/100, неверные/недоступные уровни и ошибки платформы, существующие формы и навигация.
- `flutter build apk --debug --no-pub` — debug APK успешно собран.
- AVD `coffee_lab`, `emulator-5554`: `integration_test/battery_test.dart` прошёл при заданных через ADB **73%, charging**. Проверены реальный нативный ответ, неизвестный метод и два обновления с точной подписью на экране.
- Повторный нативный тест прошёл с **37%, discharging**, включая точный процент и подпись «разряжается». После тестов `dumpsys battery reset` восстановил системные данные эмулятора.
- `integration_test/lab6_test.dart` — прошёл с реальными Android-каналами, батареей и полным сценарием навигации.

Для воспроизведения проверки батареи в окружении SDK/Gradle из `tools/run-android.ps1`:

```powershell
$batteryAdb = 'D:\Android\sdk-coffee\platform-tools\adb.exe'
try {
    & $batteryAdb -s emulator-5554 shell dumpsys battery set ac 1
    & $batteryAdb -s emulator-5554 shell dumpsys battery set level 73
    & $batteryAdb -s emulator-5554 shell dumpsys battery set status 2
    flutter test integration_test/battery_test.dart -d emulator-5554 --no-pub --dart-define=BATTERY_LEVEL=73 --dart-define=BATTERY_STATE=charging
} finally {
    & $batteryAdb -s emulator-5554 shell dumpsys battery reset
}
```

## Ограничения

iOS-исходники добавлены, но Swift/Xcode-сборка и запуск на iPhone на Windows не проверялись. На Mac используйте `flutter test integration_test/battery_test.dart -d <physical-iphone-id>`; симулятор может не предоставлять уровень заряда.

`tools/check-source.py` обнаруживает только отличие `.idea/caches/deviceStreaming.xml` от старой контрольной суммы. Дополнительное сравнение подтвердило совпадение остальных файлов `lab4-5`, включая исходники. Кеш и исходная лабораторная не редактировались.

## Системные API

- [Android BatteryManager](https://developer.android.com/reference/android/os/BatteryManager)
- [Apple UIDevice: isBatteryMonitoringEnabled](https://developer.apple.com/documentation/uikit/uidevice/isbatterymonitoringenabled)
