# Лабораторная № 4–5 — Flutter Widgets

**Для изучения с нуля:** [подробный гайд по вёрстке обоих экранов](docs/flutter-layout-guide-ru.md). В нём разобраны Dart, виджеты, каждый блок приложения, порядок сборки, изменения и ошибки. Отдельный учебный экран — `examples/layout_practice.dart`.

Мобильное Android-приложение с двумя экранами по предоставленному макету:

1. **Каталог:** местоположение, рекламный баннер, поиск, категории, сетка карточек, избранное и нижняя навигация.
2. **Напиток:** изображение на оранжевом фоне, название и рейтинг, опции, количество, три размера, цена и кнопка Add to Cart.

Нажмите Espresso, Order Now или Custom, чтобы открыть второй экран. Стрелка назад возвращает каталог. Размер, количество, флажки, поиск, категории и избранное работают. Add to Cart показывает локальное подтверждение; сервер и оплата для этой лабораторной не используются. Надписи оставлены на английском, как в макете.

## Запуск в Android-эмуляторе

Из папки проекта выполните в PowerShell:

```powershell
.\tools\run-android.ps1
```

На этом компьютере подготовлен AVD **coffee_lab**, его данные размещены в `D:\Android\avd\coffee_lab.avd`. Эмулятор `test_phone_1` сохранён. Для сборки подготовлены SDK `D:\Android\sdk-coffee` и кеш Gradle `D:\Android\gradle-coffee`: это позволяет не занимать почти заполненный диск C. Скрипт задаёт соответствующие переменные только своему процессу.

Если эмулятор уже запущен, можно указать его идентификатор:

```powershell
.\tools\run-android.ps1 -DeviceId emulator-5554
```

В Android Studio откройте **корневую папку `lab4-5`**, а не только `android`. При установленных плагинах Flutter и Dart выберите конфигурацию **main.dart**, Android-устройство и нажмите Run или Debug. В Device Manager можно запустить `coffee_lab`. Конфигурация `.idea/runConfigurations/main_dart.xml` задаёт `ANDROID_HOME` и `ANDROID_SDK_ROOT` равными `D:\Android\sdk-coffee`, `GRADLE_USER_HOME` — `D:\Android\gradle-coffee`, а `TEMP` и `TMP` — `D:\Android\temp` только для запуска приложения. Настройка Gradle user home в IDE сама по себе не задаёт окружение процесса `flutter run`.

Если Android Studio уже открыт после изменения конфигурации, переоткройте проект. В **Run → Edit Configurations → main.dart → Environment variables** должны быть перечисленные выше переменные. На другом компьютере замените эти пути на существующие локальные каталоги или удалите переменные из конфигурации.

Запуск также защищён на уровне проекта, если IDE не загрузила окружение конфигурации: `android/gradlew.bat` использует готовый кеш `D:\Android\gradle-coffee`, когда `GRADLE_USER_HOME` не задан; `android/settings.gradle.kts` выбирает существующий SDK `D:\Android\sdk-coffee` и передаёт Flutter список NDK из этого SDK. Это учитывает перезапись `sdk.dir`, которую выполняет Flutter перед сборкой. Явный `GRADLE_USER_HOME` имеет приоритет. Другой SDK можно выбрать через Gradle-свойство `coffee.androidSdkDir` (например, в `android/gradle.properties`); если подготовленного каталога нет, сохраняется стандартный выбор SDK.

Ошибка `org.gradle.kotlin.kotlin-dsl ... 6.4.2 ... was not found` возникала при запуске с пустым кешем `C:\Users\user\.gradle`. Подготовленный кеш на D: содержит этот плагин. SDK на D: также содержит требуемый NDK `28.2.13676358`. Предупреждение Java `A restricted method in java.lang.System has been called` не препятствует проверенной сборке.

На другом компьютере установите Flutter и Android SDK, создайте Android AVD и выполните `flutter pub get`, затем `flutter run -d <android-device-id>`. `android/local.properties` содержит локальные пути и не должен переноситься между компьютерами.

## Структура и Widgets

- `lib/main.dart` — точка входа.
- `lib/coffee_app.dart` — тема, данные, каталог и экран напитка.
- `assets/images/` — локальные PNG; сеть при показе экранов не нужна.
- `assets/sources/` — исходные изображения с Pinterest.
- `docs/image-sources.md` — ссылки на источники.
- `test/widget_test.dart` — проверка размещения на двух размерах экрана и основных действий.

Использованы `MaterialApp`, `Scaffold`, `SafeArea`, `CustomScrollView`, `SliverList`, `SliverGrid`, `Stack`, `Positioned`, `Row`, `Column`, `Expanded`, `Wrap`, `Image.asset`, `TextField`, `IconButton`, `FilledButton`, `InkWell`, `SingleChildScrollView`. Состояние хранится в `StatefulWidget`, переход сделан через `Navigator` и `MaterialPageRoute`.

Системные панели Android настоящие, поэтому их значки и время отличаются от iPhone на исходном макете. Картинки заменены на похожие изображения кофе с Pinterest; фон удалён обычной обработкой, без генерации.

## Проверка

```powershell
flutter analyze
flutter test
```

Проверки прошли; подробности — в `docs/verification.md`. Снимки реально запущенного Android-приложения — `docs/screenshots/catalog.png` и `docs/screenshots/details.png`. Готовый APK для эмулятора: `build/app/outputs/flutter-apk/app-debug.apk`.

