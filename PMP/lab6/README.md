# Лабораторная № 6 — Navigation. MethodChannel

Самостоятельное продолжение `../lab4-5`: магазин кофе с передачей товара между экранами, возвратом заказа и возможностями Android/iOS. Исходная лабораторная сохранена отдельно. Вариант **8**: производитель устройства и запуск приложения часов с временем будильника.

## Запуск

Откройте **корень `lab6`** в Android Studio, выберите Flutter-конфигурацию `main.dart` и Android-устройство. Через PowerShell из этой папки:

```powershell
flutter pub get
.\tools\run-android.ps1
```

Скрипт использует подготовленные SDK `D:\Android\sdk-coffee`, Gradle `D:\Android\gradle-coffee`, временный каталог `D:\Android\temp` и AVD `coffee_lab`. Они задаются только для запуска. На другом компьютере используйте свои SDK/AVD и `flutter run -d <device-id>`. Сохранены исправления запуска из предыдущей лабораторной: Gradle выбирает подготовленный SDK и кеш на D:, если они существуют.

При недоступных репозиториях и уже заполненном кеше используйте `tools/run-android.ps1 -Offline`. Для сборки/интеграционных тестов в той же PowerShell-сессии можно задать `$env:COFFEE_LAB6_OFFLINE='1'`. Обычный запуск разрешает загрузку зависимостей. Тестовый Android-плагин Flutter согласован с AGP 9.1.0 / Kotlin Gradle Plugin 2.4.0 приложения только в конфигурации `lab6`; общий Flutter SDK и кешированные исходники плагинов не редактируются.

В `android/gradle.properties` отключён только incremental-кеш Kotlin: исходники плагинов находятся на C:, проект — на D:, и кеширование относительных путей между дисками вызывало `this and base files have different roots`. Это не меняет функции приложения.

Для сборки одновременно с AVD ограничены Gradle worker-потоки (2), heap Gradle (1536 МБ) и отдельные процессы компиляции Kotlin (`in-process`). Это уменьшает расход памяти; системные настройки памяти не меняются.

Если AVD ещё не запущен, скрипт запускает `coffee_lab` с программным рендерингом SwiftShader, отключённым Vulkan, двумя ядрами и 1536 МБ RAM, без загрузки/сохранения snapshots. Эти параметры относятся только к запуску; userdata и постоянная конфигурация AVD сохраняются. Это позволяет избежать чёрного кадра и ошибок Vulkan ColorBuffer в этой среде. Уже запущенный эмулятор используется как есть.

Перед запуском Flutter скрипт ожидает завершения загрузки Android (`sys.boot_completed=1`), ответа менеджера пакетов (`cmd package path android`) и службы `activity`. Проверка выполняется и для уже подключённого устройства: статус `device` в ADB сам по себе не гарантирует готовность установки APK. По умолчанию ожидание занимает до 180 секунд; для медленного AVD используйте `.\tools\run-android.ps1 -BootTimeoutSeconds 300`. Ошибка `Can't find service: package` означает, что служба пакетов Android недоступна. Если ожидание завершается ошибкой, в Android Studio → Device Manager остановите проблемный AVD (Stop), затем выберите Cold Boot и повторите команду. Cold Boot загружает Android без восстановления snapshot и сохраняет установленные приложения и данные.

Приложение называется **Coffee Lab 6**, его Android ID — `by.bstu.coffee_lab6`. Предыдущий **Coffee Shop** с ID `by.bstu.coffee_widgets` можно оставить установленным.

## Сценарий защиты — все пункты задания

| Пункт | Что показать | Где реализовано |
|---|---|---|
| 1. Минимум три перехода с данными | Открыть Espresso из карточки; затем Vanilla Latte и Iced Americano из предложений. Названия, изображения, описание и Catalog price соответствуют выбранному товару | `Coffee`, `CoffeeDetailsArguments`, `_openCoffee`, `CoffeeDetailsScreen` |
| 2. Три метода Navigator | Карточка → `push`; Order Now и Preview order → `pushNamed`; в предварительном заказе Return to catalog → `popUntil` | `lib/coffee_app.dart` |
| 3. Передача через pop | Изменить количество, размер и добавки; нажать Add to Cart. В каталоге появится блок с результатом и итогом | `CoffeeOrder`, `Navigator.pop(context, _order())`, ожидание `await` в каталоге |
| 4. PageViewer | Перелистать баннер жестом и стрелками; индикатор меняется от 1 / 3 до 3 / 3; каждое предложение открывает свой товар | Flutter-виджет называется **PageView**, класс `CoffeeOffers` |
| 5. Производитель, вариант 8 | Setting → Получить производителя. Показать реальное значение устройства | `by.bstu.lab6/device`, метод `getManufacturer`: Android `Build.MANUFACTURER`, iOS `Apple` |
| 6. Будильник, вариант 8 | В Setting ввести часы, минуты, подпись; нажать Открыть часы. Проверить переданные параметры в системном приложении | `by.bstu.lab6/alarm`, метод `openAlarm`, `AlarmClock.ACTION_SET_ALARM` |
| 7. Батарея Android/iOS | Нажать Обновить заряд; показать процент и состояние; объяснить таблицу ниже | Собственный `by.bstu.lab6/battery`, нативные Kotlin/Swift-обработчики без плагина батареи |
| 8. Камера | Сделать фото → снять → подтвердить → увидеть фотографию в приложении. При отмене предыдущее фото остаётся | `image_picker`, `ImageSource.camera`, `Image.file` |
| 9. Иконка | Показать Coffee Lab 6 в списке приложений: чашка кофе вместо Flutter | `assets/icon`, Android mipmap и adaptive icon |

Настройка напитка сохранена из предыдущей работы. Цена Catalog price — переданная цена каталога; итог рассчитывается по прежней формуле со скидкой: `(цена каталога − 1 − 0.5 для Small) × количество`. Medium и Large используют прежнюю одинаковую цену. Добавки в этой учебной модели не имеют отдельной стоимости.

### Навигация и возврат данных

```text
Каталог ── push / pushNamed(CoffeeDetailsArguments) ──► Детализация
        ◄──────────── pop(CoffeeOrder) ────────────── Add to Cart
Детализация ── pushNamed(CoffeeOrder) ──────────────► Preview order
Детализация ◄────────── pop() ─────────────────────── Back to customizations
Каталог ◄────────────── popUntil(isFirst) ────────── Return to catalog
```

Передаётся объект товара, а не только название и не фиктивный Espresso для всех карточек. Именованные маршруты проверяют тип аргумента. `CoffeeOrder` содержит товар, количество, размер и неизменяемый список добавок. Обычная стрелка назад и системный Back возвращают `null`, поэтому отмена не добавляет заказ. `popUntil` закрывает и предварительный просмотр, и детализацию; уже добавленный заказ сохраняется в состоянии каталога. Сервер, оплата и постоянное хранение корзины не входят в работу.

Для iOS добавлен target в `ios/`: производитель Apple и установка будильника приложения через AlarmKit на iOS 26+. На старых iOS отображается ошибка версии. Android открывает системные часы. Требования, различия и непроверенные сценарии: [docs/ios.md](docs/ios.md).

## Как работают каналы

| Канал | Dart → Android | Android → Dart |
|---|---|---|
| `by.bstu.lab6/device` | `getManufacturer`, без аргументов | Строка `Build.MANUFACTURER` |
| `by.bstu.lab6/alarm` | `openAlarm`, `{hour: int, minute: int, label: String}` | `true`, если intent передан приложению часов; иначе PlatformException |
| `by.bstu.lab6/battery` | `getBatteryReading`, без аргументов | `{level: int (0–100), state: String}`; недоступная батарея → `UNAVAILABLE` |

Формы проверяют часы 0–23, минуты 0–59; подпись ограничена 80 символами. Kotlin и Swift повторяют проверку входных данных. В manifest добавлено `com.android.alarm.permission.SET_ALARM`. Передаются `EXTRA_HOUR`, `EXTRA_MINUTES`, `EXTRA_MESSAGE`, `EXTRA_SKIP_UI=false`. Установка и поведение будильника принадлежат приложению часов: успешный канал подтверждает его открытие, а не гарантирует срабатывание будильника. Некоторые приложения часов могут сразу создать будильник и показать его; проверьте их экран. Нет приложения часов → `NO_ALARM_APP`; ошибка доступа → `ALARM_PERMISSION`; некорректные параметры → `INVALID_ARGUMENT`. Неизвестные методы возвращают `notImplemented`.

## Android и iOS: батарея без дополнительных плагинов

`DeviceServices.battery()` вызывает `getBatteryReading` собственного `MethodChannel('by.bstu.lab6/battery')`. Android и iOS реализуют один контракт: процент и состояние возвращаются вместе. `battery_plus` удалён из зависимостей; `BatteryState` — собственный enum в `lib/device_services.dart`. Существующий `image_picker` используется только для камеры.

| Различие | Android | iOS |
|---|---|---|
| Нативная реализация | Kotlin, `MainActivity.kt` | Swift, `DeviceChannels.swift` |
| Получение процента | `EXTRA_LEVEL × 100 / EXTRA_SCALE` из `ACTION_BATTERY_CHANGED` | `UIDevice.current.batteryLevel × 100`, округление |
| Формат нативного уровня | Целое число процентов | Дробь 0…1, преобразуемая в проценты |
| Подготовка | Чтение последнего системного sticky Intent через `registerReceiver(null, ...)` | Включение `isBatteryMonitoringEnabled`; после чтения восстанавливается прежнее значение |
| Состояние | `EXTRA_STATUS`, для подключённой без зарядки — `EXTRA_PLUGGED` | `UIDevice.BatteryState` |
| Недоступный уровень | Нет батареи/данных, неверная шкала → `UNAVAILABLE` | Уровень вне 0…1 (в том числе −1 на симуляторе) → `UNAVAILABLE` |

Состояния контракта: `charging`, `discharging`, `full`, `connectedNotCharging`, `unknown`. Неизвестное состояние при известном уровне показывает процент и подпись «состояние неизвестно». Dart дополнительно проверяет диапазон процента. Каждое нажатие «Обновить заряд» читает новые данные ОС; постоянной подписки нет. Дополнительные разрешения для батареи не нужны. Подробнее о платформенном target: [iOS: код и проверка](docs/ios.md). Нативная iOS-сборка и запуск на Windows не выполнялись. Проверки новой реализации: [docs/battery-verification.md](docs/battery-verification.md).

## Камера и восстановление

На Android `image_picker` открывает встроенную камеру. После подтверждения возвращается `XFile`, который отображается через `Image.file`. Отмена не стирает предыдущее фото. Ошибки отображаются текстом на экране. При старте вызывается `retrieveLostData()`; если Android уничтожил процесс во время съёмки и плагин восстановил результат, приложение открывает экран устройства с восстановленным фото. Фотографии остаются во временном кеше: постоянное хранение и экспорт не предусмотрены.

## Проверки и структура

```powershell
flutter analyze
flutter test
flutter test integration_test/lab6_test.dart -d emulator-5554
flutter build apk --debug
python .\tools\check-source.py
```

Для Android-команд задайте окружение SDK/Gradle из `tools/run-android.ps1`, если IDE не применяет его автоматически.

- `lib/coffee_app.dart` — каталог, данные, детализация, предложения и предварительный заказ.
- `lib/device_services.dart` — собственные каналы, батарея и камера; отдельная граница для тестирования.
- `lib/device_screen.dart` — формы, результаты, сообщения об ошибках и фото.
- `lib/coffee_theme.dart` — сохранённые цвета приложения.
- `android/app/src/main/kotlin/by/bstu/coffee_lab6/MainActivity.kt` — Kotlin-обработчики; пакет класса `by.bstu.coffee_lab6` соответствует application ID.
- `test/` — проверки навигации и форм на двух размерах, контрактов каналов и ошибок.
- `integration_test/` — навигация и реальные Android-каналы.
- `assets/icon/` — редактируемый SVG и PNG-ресурсы новой иконки.

Итоговые результаты и снимки: [docs/verification.md](docs/verification.md). Источники фотографий кофе: [docs/image-sources.md](docs/image-sources.md).

## Документация

- [Flutter: Navigator и передача данных](https://docs.flutter.dev/cookbook/navigation)
- [Flutter: platform channels и пример батареи Android/iOS](https://docs.flutter.dev/platform-integration/platform-channels)
- [PageView](https://api.flutter.dev/flutter/widgets/PageView-class.html)
- [Android Build.MANUFACTURER](https://developer.android.com/reference/android/os/Build#MANUFACTURER)
- [Android AlarmClock](https://developer.android.com/reference/android/provider/AlarmClock#ACTION_SET_ALARM)
- [Android BatteryManager: уровень, шкала и состояние](https://developer.android.com/reference/android/os/BatteryManager)
- [Apple UIDevice: включение чтения батареи](https://developer.apple.com/documentation/uikit/uidevice/isbatterymonitoringenabled)
- [image_picker: камера, отмена и retrieveLostData](https://pub.dev/packages/image_picker)
- [flutter_launcher_icons](https://pub.dev/packages/flutter_launcher_icons)
