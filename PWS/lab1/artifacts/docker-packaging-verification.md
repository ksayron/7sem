# Проверка single-file упаковки и Docker

Дата: 26.10.2026, Europe/Minsk.

Исходная ошибка воспроизведена в Linux SDK 10.0.403 с включённым `StaticWebAssetsEnabled`: публикация WS01-01 создаёт исполняемый файл около 50 МиБ и `WS01-01.staticwebassets.endpoints.json` размером 53 байта. `dotnet publish` успешен, но последующая проверка количества файлов возвращает 1. Лог: `docker-publish-before.log`.

Исправление в `Directory.Build.props` отключает генерацию static web assets только при single-file публикации. Страница остаётся встроенным ресурсом `StackApi`; файловые static endpoints не используются. Проверка строго одного файла сохранена.

После исправления:

- WS01-01, WS01-02 и Gateway опубликованы в Linux x64 self-contained single-file; каждый каталог публикации содержит ровно один исполняемый файл.
- Собраны образы `pws-lab1-ws01-01:local`, `pws-lab1-ws01-02:local`, `pws-lab1-gateway:local` на исходном `runtime-deps:10.0`.
- Запущены все шесть контейнеров исходного `compose.yaml`.
- В каждом из трёх типов образов проверены один файл `/app/service`, право исполнения и отсутствие команды `dotnet`.
- HTTP-проверки реальных контейнеров: 15/15, 366 запросов. Проверены CRUD, LIFO, изоляция клиентов, валидация, конкурентные PUSH, Sticky Session, Round Robin, перенос cookie и защита подписи, встроенные страницы и маршруты. Полный журнал: `verification.json`, краткий отчёт: `verification.md`.
- Windows Release-сборка: 0 ошибок, 0 предупреждений.

Docker публикует порты только на IPv4 loopback. HTTP-проверки используют `127.0.0.1`, чтобы не зависеть от медленного разрешения `localhost` в текущей среде; проверки поведения API сохранены.

## Источник пакетов при проверке

Прямой restore внутри Docker прерывался с DNS, EOF и connection-refused ошибками. Официальные NuGet-пакеты runtime 10.0.14 и ILLink загружены через Windows; YARP 2.3.0 и System.IO.Hashing 8.0.0 взяты из существующего NuGet-кеша. Пакеты сохранены в игнорируемом Git каталоге `offline-nuget/` рядом с этим файлом.

`Dockerfile.offline-verification` повторяет основной Dockerfile, добавляя только источник NuGet через read-only build mount и отключая сетевой NuGet audit для этой проверки. Основной Dockerfile сохраняет обычное восстановление NuGet. Изменений настроек Docker daemon и постоянных DNS-исключений нет.

Повторная сборка с уже подготовленным источником, из корня лабораторной:

```powershell
foreach ($project in 'WS01-01','WS01-02','Gateway') {
    $imageTag = 'pws-lab1-' + $project.ToLowerInvariant() + ':local'
    docker build --file artifacts/Dockerfile.offline-verification --build-context nuget=./artifacts/offline-nuget --build-arg "PROJECT=$project" --tag $imageTag .
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
docker compose -f compose.yaml up -d --no-build --pull never
./Test-Lab.ps1 -Deployment docker
```

Логи успешной публикации: `docker-publish-WS01-01.log`, `docker-publish-WS01-02.log`, `docker-publish-Gateway.log`.
