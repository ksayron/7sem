# iOS: производитель и будильник — 27 октября 2026

В `ios/` добавлен стандартный Swift-target из установленного Flutter 3.47.2. Он использует SceneDelegate и регистрирует плагины и собственные каналы в `AppDelegate.didInitializeImplicitFlutterEngine`. Android-проект не заменяется. 28 октября батарея переведена на собственный канал без `battery_plus`: `DeviceChannels.handleBattery` читает `UIDevice.current` и возвращает процент и состояние. Подробности и результаты: [battery-verification.md](battery-verification.md).

## Контракт каналов

| Канал / метод | Android | iOS |
|---|---|---|
| `by.bstu.lab6/device` / `getManufacturer` | `Build.MANUFACTURER` | Строка `Apple`: производитель оборудования iOS. `UIDevice.model` возвращает семейство модели и не является производителем. |
| `by.bstu.lab6/alarm` / `openAlarm` | Открывает системные часы через Intent | Устанавливает одноразовый будильник Coffee Lab 6 через AlarmKit на iOS 26+ |
| `by.bstu.lab6/battery` / `getBatteryReading` | Читает `ACTION_BATTERY_CHANGED`: уровень, шкалу, статус | Включает мониторинг `UIDevice`, читает `batteryLevel`/`batteryState`, восстанавливает прежний режим мониторинга |

В обоих случаях аргументы будильника: `{hour: int, minute: int, label: String}`, ответ — `true` либо ошибка платформы. Swift проверяет наличие аргументов, целые значения часов/минут, диапазоны 0–23 / 0–59 и длину подписи до 80 UTF-16 единиц. Логические значения вместо чисел отклоняются. Неизвестный метод получает `FlutterMethodNotImplemented`.

На Android `true` подтверждает отправку Intent, на iOS — успешное завершение `AlarmManager.schedule`. Dart проверяет этот ответ; `false` или `null` не считаются успехом.

## Будильник iOS

1. `DeviceChannels.handleAlarm` проверяет входные данные и версию iOS.
2. В задаче на MainActor запрашивается `AlarmManager.shared.requestAuthorization()`.
3. Только при `.authorized` вычисляется ближайшее будущее наступление выбранного времени в локальном календаре. Если время уже прошло, выбирается следующий день.
4. Создаются `AlarmAttributes<CoffeeAlarmMetadata>` и конфигурация `.alarm(schedule: .fixed(date), ...)` со стандартным звуком. Для пустой подписи используется Coffee time.
5. `AlarmManager.schedule` устанавливает будильник с новым UUID. Ответ во Flutter возвращается после завершения этого вызова.
6. `StopCoffeeAlarmIntent` обрабатывает остановку этого будильника системной кнопкой.

Это будильник нашего приложения, а не запись в приложении Apple Clock. Обратный отсчёт, повтор и snooze не включены, поэтому отдельное расширение Live Activity не используется. Каждое успешное нажатие создаёт отдельный одноразовый будильник.

На iOS 26.1+ используется актуальный инициализатор `AlarmPresentation.Alert`, где Stop предоставляет система. Для iOS 26.0 предусмотрена старая сигнатура с `stopButton`. В `Info.plist` добавлен `NSAlarmKitUsageDescription`. Камера также имеет описания доступа; iOS-иконки сгенерированы из выбранного изображения приложения.

| Ошибка | Значение |
|---|---|
| `INVALID_ARGUMENT` | Некорректные аргументы |
| `UNSUPPORTED_IOS_VERSION` | iOS ниже 26; обычное уведомление вместо будильника не создаётся |
| `ALARM_PERMISSION` | Пользователь не разрешил AlarmKit |
| `ALARM_SCHEDULE_FAILED` | Системный API не смог установить будильник |
| `ALARM_NOT_ACCEPTED` | Dart не получил явное подтверждение `true` |

## Сборка и проверка на Mac

Требуются Mac, Flutter, CocoaPods и **Xcode 26.1 или новее с iOS SDK 26.1+**. Добавлен стандартный Flutter Podfile для установки нативных зависимостей. Deployment target приложения — iOS 15; функция будильника доступна только на iOS 26+. Для установки на iPhone в Xcode необходимо выбрать собственную Signing Team. Bundle ID создан как `by.bstu.coffeeLab6`; при необходимости замените его на доступный вашему аккаунту.

Из корня `lab6`:

```sh
flutter pub get
flutter build ios --simulator --debug
open ios/Runner.xcworkspace
```

Выберите iOS Simulator и выполните Product → Test для `RunnerTests`. Для проверки каналов:

```sh
flutter devices
flutter test integration_test/ios_channels_test.dart -d <ios-device-id>
flutter test integration_test/battery_test.dart -d <physical-iphone-id>
flutter run -d <ios-device-id>
```

Интеграционный тест проверяет реальный ответ Apple, неизвестные методы и отказ на некорректные аргументы. Он намеренно не создаёт будильник и не вызывает запрос разрешения. Автоматическая проверка успеха в Flutter использует подставленный сервис и не доказывает работу AlarmKit.

Для чтения батареи используйте физический iPhone: симулятор может возвращать неизвестный уровень −1, и тогда канал корректно отвечает `UNAVAILABLE`. Проверьте процент и состояния с зарядкой/без зарядки через «Обновить заряд».

Ручной сценарий на iPhone с iOS 26+: открыть Setting, получить производителя, ввести время на несколько минут вперёд, установить будильник, разрешить AlarmKit, проверить сообщение, заблокировать устройство и дождаться системного сигнала. Проверить остановку. Отдельно проверить отказ в разрешении и старую iOS. Не оставляйте тестовые будильники без остановки.

## Граница проверки

Работа выполняется на Windows: Xcode, iOS SDK и iPhone здесь недоступны. Нативная iOS-компиляция, XCTest, iOS-интеграционный тест, разрешения и реальное срабатывание AlarmKit **не проверены**. Наличие исходников и проверки Dart не заменяют эти проверки. Локальные результаты перечислены в `ios-verification.md`.

## Документация Apple и Flutter

- [AlarmKit: установка и разрешения](https://developer.apple.com/documentation/alarmkit/scheduling-an-alarm-with-alarmkit)
- [AlarmManager.schedule](https://developer.apple.com/documentation/alarmkit/alarmmanager/schedule(id:configuration:))
- [Alert: актуальная сигнатура с iOS 26.1](https://developer.apple.com/documentation/alarmkit/alarmpresentation/alert-swift.struct/init(title:secondarybutton:secondarybuttonbehavior:))
- [Alert: сигнатура для iOS 26.0](https://developer.apple.com/documentation/alarmkit/alarmpresentation/alert-swift.struct/init(title:stopbutton:secondarybutton:secondarybuttonbehavior:))
- [Flutter: SceneDelegate и регистрация каналов](https://docs.flutter.dev/release/breaking-changes/uiscenedelegate)
