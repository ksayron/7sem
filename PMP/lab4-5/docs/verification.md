# Проверка — 5 октября 2026 года

- Flutter 3.47.2 / Dart 3.13.2.
- `flutter analyze` — ошибок и замечаний нет.
- `flutter test` — 2 теста прошли: размещение на 320×640 и 393×873; поиск, избранное, размер, количество, опции и подтверждение корзины.
- `flutter build apk --debug` — успешно.
- `tools/run-android.ps1 -DeviceId emulator-5554` — приложение установлено и запущено.
- Android AVD `coffee_lab`, x86_64, Android 17 / API 37, 1080×2400, density 420.
- На устройстве открыты каталог и карточка Espresso; визуально проверены изображения, порядок блоков, цвета, нижнее меню и цена.
- На устройстве увеличено количество до 2, выбран Small: цена стала $3; Add to Cart показала `2 × Espresso added to cart`. XML подтверждения сохранён в `screenshots/cart-ui.xml`.
- Итоговые PNG: `screenshots/catalog.png` и `screenshots/details.png`.

APK в `build/app/outputs/flutter-apk/app-debug.apk`. APK установлен и проверен на Android-эмуляторе x86_64. Проверка на физическом телефоне не выполнялась.

## Проверка исправления запуска — 13 октября 2026 года

- Установленный Flutter 3.47.2 / Dart 3.13.2; JDK из Android Studio — OpenJDK 25.0.3.
- В `.idea/runConfigurations/main_dart.xml` добавлено окружение с SDK `D:\Android\sdk-coffee`, кешем Gradle `D:\Android\gradle-coffee` и временным каталогом `D:\Android\temp`. `android/local.properties` указывает на этот SDK.
- Запуск `flutter --no-color run --machine --track-widget-creation --device-id=emulator-5554 --dart-define=flutter.inspector.structuredErrors=true lib\main.dart` с этим окружением: `assembleDebug` прошёл, APK установлен, получено событие `app.started` и адрес Dart VM Service.
- Снимок запущенного каталога: `screenshots/launch-2026-10-13.png`.
- Запуск проверен через Flutter CLI с окружением сохранённой IDE-конфигурации. Кнопки Run/Debug в Android Studio вручную не проверялись. Тесты виджетов и `flutter analyze` повторно не запускались: код приложения не менялся.

### Дополнительное исправление после повторного сбоя в IDE

- Повторный лог IDE показал использование кеша C: и ошибку DNS `plugins.gradle.org`: сохранённое окружение запуска не применилось. Flutter также снова записал C: SDK в `local.properties`.
- В `android/gradlew.bat` добавлен выбор существующего кеша D:, если `GRADLE_USER_HOME` не задан. В `android/settings.gradle.kts` добавлен выбор подготовленного SDK и согласование свойств `flutter.androidSdkRoot` / `flutter.installedNdkVersions` с его установленными NDK.
- `gradlew.bat :app:assembleDebug --offline --console=plain -Ptarget-platform=android-x64 -Ptarget=lib/main.dart -Pdart-obfuscation=false -Ptrack-widget-creation=true -Ptree-shake-icons=false` — успешно, без переменных `GRADLE_USER_HOME`, `ANDROID_HOME` и `ANDROID_SDK_ROOT`.
- Точная команда IDE из повторного лога, включая `--start-paused` и `--devtools-server-address=http://127.0.0.1:9100`, запущена через CLI без переменных SDK, Gradle и Java: APK собран, установлен; получены `app.started` и адрес Dart VM Service. Начальная пауза снята через VM Service для проверки каталога. Снимок `screenshots/launch-2026-10-13.png` обновлён.
- Нажатие Run/Debug в самой Android Studio вручную не проверялось. Полная APK-сборка для всех архитектур и физический телефон в этой проверке не использовались.

