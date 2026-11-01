# Лабораторная №7 — SQFLite, File System, Isolates

Самостоятельное Flutter-приложение `by.bstu.sports_lab7`. Сущность **Footballer** взята из `../lab2/lib/sportsmen.dart` и `athlete.dart`: имя, дисциплина, рейтинг, позиция. Добавлен клуб и ключ SQLite. Интерфейс сохраняет тёмный фон, оранжевый акцент, карточки и нижнюю навигацию из `lab6`; на экране теперь спортивный состав.

## Запуск

```powershell
flutter pub get
flutter run -d <android-or-ios-device>
```

На этом компьютере подготовлены Android SDK `D:\Android\sdk-coffee` и Gradle `D:\Android\gradle-coffee`; для эмулятора можно использовать `.\tools\run-android.ps1`. На macOS iOS запускается через Xcode или `flutter run -d <ios-device>`; iOS-сборка на Windows невозможна.

В приложении подключены только Flutter и обязательные по заданию `sqflite` и `path_provider`. JSON, файлы, пути и изоляты обрабатываются стандартными библиотеками Dart. Серверов, облачных сервисов и дополнительных UI-библиотек нет. Зависимости внутри самих плагинов устанавливаются автоматически; инструменты проверки находятся отдельно в `tools/tests`.

При запуске из Android Studio Windows-обёртка Gradle также выбирает подготовленный кеш `D:\Android\gradle-coffee`, если он существует и `GRADLE_USER_HOME` явно не задан. Это предотвращает повторное скачивание уже имеющегося Kotlin-плагина в другой кеш. Для запуска без обращения Gradle к сети используйте `.\tools\run-android.ps1 -Offline`.

## Показ на защите

1. Откройте «Игроки»: список `ListView.builder(itemExtent: 104)` содержит образцы только при первом создании БД. Выберите игрока для просмотра всех полей.
2. «Добавить игрока» → заполните поля → «Сохранить». В карточке «Изменить» меняет запись, «Удалить» спрашивает подтверждение. Перезапустите приложение: записи остаются в SQLite. Для поля рейтинга действует диапазон 1–10.
3. Поиск работает по имени, клубу, позиции, дисциплине и рейтингу, включая кириллицу без учёта регистра. Чипы фильтруют позицию. В «Сортировка» добавьте второе поле и переключите стрелкой порядок каждого поля. При равенстве действует `id`.
4. На вкладке «Файлы» выберите игрока и нажмите «Записать во все доступные». В каждой директории записывается отдельный JSON с отличающимися именем, клубом и рейтингом. Нажмите «Найти файлы» и «Прочитать файл»: появятся данные сущности, полный путь, размер, дата изменения, источник и имя изолята. Файлы находятся в подкаталогах `sports_lab7/<тип>/footballer.json`. Если пути нескольких API совпадают, подкаталоги предотвращают перезапись.
5. Для недоступной директории экран называет неподдерживаемую платформу. Android: `Application Library` недоступна. iOS: три типа `External ...` недоступны. Если ОС не вернула путь или запись запрещена, отображается конкретная ошибка. `Downloads` берётся из `path_provider`, на Android это внешний каталог приложения типа Downloads, а не общедоступная папка Downloads.

| API path_provider | Android | iOS |
|---|---|---|
| Temporary | Да | Да |
| Application Support | Да | Да |
| Application Library | Нет | Да |
| Application Documents | Да | Да |
| Application Cache | Да | Да |
| External Storage | Да | Нет |
| External Cache Directories | Да | Нет |
| External Storage Directories (`documents`) | Да | Нет |
| Downloads | Да | Да |

`path_provider` вызывается в основном изоляте; абсолютные пути и сериализуемая `Footballer` передаются в `Isolate.run`. Запись, поиск файлов и чтение JSON выполняются рабочими изолятами `lab7-file-writer`, `lab7-file-scanner`, `lab7-file-reader`. SQLite-операции плагин выполняет в своём фоновом потоке. Файлы учебного примера живут в контейнере приложения; временные и кэш-директории ОС может очищать.

## Проверки

```powershell
flutter analyze
cd tools/tests
flutter pub get
flutter test
cd ../..
flutter build apk --debug
flutter test integration_test/lab7_test.dart -d <device-id>
```

Для Android-плагина закреплён `path_provider_android` 2.2.23: эта версия поддерживает Downloads и не требует JNI. Хостовые тесты вынесены в `tools/tests`, чтобы SQLite FFI не входил в Android APK. Тест SQLite проверяет CRUD, несколько полей сортировки, кириллический поиск и повторное открытие БД. Тест файлов проверяет различие данных и путей, чтение в изоляте и платформенные ошибки. Исходники: `lib/footballer.dart`, `lib/footballer_repository.dart`, `lib/file_service.dart`, `lib/sports_app.dart`, `lib/file_screen.dart`.

Документация: [path_provider](https://pub.dev/packages/path_provider), [sqflite](https://pub.dev/packages/sqflite), [Isolate.run](https://api.dart.dev/dart-isolate/Isolate/run.html).




## Результат проверки в этом окружении

3 ноября 2026: `flutter analyze` прошёл без замечаний; три хостовых теста (`tools/tests`) прошли. Обычный debug APK для Android x64 собран с подготовленным кешем Gradle как в offline-режиме, так и обычной командой `flutter build apk --debug --no-pub --target-platform android-x64`. На Android подтверждены создание записи SQLite и поиск через экран приложения. Полный интеграционный сценарий файлов не завершён: эмулятор отключился от ADB. iOS-сборка требует macOS/Xcode и здесь не выполнялась.
