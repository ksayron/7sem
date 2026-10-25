# PWS — лабораторная работа 1

Все четыре задания: два ASP.NET Core Web API (`WS01-01`, `WS01-02`), по два экземпляра каждого сервиса и два ASP.NET Core шлюза на YARP 2.3.0. Сборка — .NET 10, Linux x64, self-contained single-file. Страница демонстрации встроена в сборку.

Интерфейс использует стандартные поля и кнопки браузера: GET, POST, PUT, DELETE, текстовый результат, JSON и простой журнал запросов.

**Статус проверки (26.10.2026):** Windows-сборка прошла без ошибок и предупреждений. Собраны три Linux self-contained single-file образа и запущены все шесть контейнеров: 15/15 HTTP-проверок, 366 запросов. В каждом образе `/app` содержит только исполняемый `service`, установленного `dotnet` нет. Отчёт — `artifacts/verification.md`, проверка упаковки — `artifacts/docker-packaging-verification.md`. Для Docker-сборки использован локальный NuGet-источник: прямые загрузки внутри Docker в этой среде прерывались с DNS/EOF-ошибками.

## Быстрый запуск

**Без скачивания пакетов и Docker-образов**, с уже восстановленными зависимостями:

```powershell
cd D:\BSTU\7sem\PWS\lab1
.\Start-Local.ps1
```

Это шесть процессов на Windows: две реплики каждого API и два шлюза. Адреса те же — 8101/8102 для шлюзов. Скрипт собирает решение с `--no-restore`; остановка — `./Stop-Local.ps1`, проверка — `./Test-Lab.ps1 -Deployment local`. Другие процессы не останавливаются.

Перед переходом с Windows-процессов к Docker выполните `./Stop-Local.ps1`, чтобы освободить те же шесть портов.

### Запуск из Visual Studio и ошибка 502

Шлюз сам не запускает API. Сообщение `Connection refused (localhost:8112)` означает, что реплика WS01-01-B не запущена на ожидаемом порту. Запуск трёх проектов по одному разу тоже не создаёт четыре реплики API. Запрос Chrome к `/.well-known/appspecific/com.chrome.devtools.json` с ответом 404 к этой ошибке не относится.

Для полной демонстрации остановите отладку и выполните `./Start-Local.ps1`, затем откройте `http://localhost:8101/` или `http://localhost:8102/`. Скрипт запускает все шесть процессов; `launchSettings.json` при этом не используется.

Для отладки только шлюза сначала запустите `./Start-Local.ps1`, затем выберите единственным стартовым проектом `Gateway` с профилем `Gateway`. Его адрес `https://localhost:58037/` использует те же две серверные реплики 8111/8112. Остановка отладки не останавливает процессы скрипта; для них используйте `./Stop-Local.ps1`.

Для ручного запуска без скрипта у API есть отдельные профили: `WS01-01` (8111), `WS01-01-B` (8112), `WS01-02` (8121), `WS01-02-B` (8122). Например, в двух отдельных терминалах запустите `dotnet run --project src/WS01-01 --launch-profile WS01-01` и `dotnet run --project src/WS01-01 --launch-profile WS01-01-B`, затем запустите профиль шлюза `Gateway`. Профиль шлюза `Round Robin` (8102) требует обе реплики WS01-02. Не запускайте один и тот же порт одновременно через скрипт и профиль.

Нужен Docker Desktop с Linux engine. Откройте PowerShell в этой папке:

```powershell
cd D:\BSTU\7sem\PWS\lab1
.\Start-Lab.ps1
```

После сборки откройте [Sticky Session](http://localhost:8101/) и [Round Robin](http://localhost:8102/).

В Docker-сборке нужен доступ к `mcr.microsoft.com` для SDK/runtime-deps и к NuGet для Linux runtime-пакетов. `EOF`/TLS timeout при загрузке SDK — ошибка сетевой загрузки базового образа; строки `WORKDIR` и `COPY` могут быть лишь местом, где Docker обнаружил её. Учебные образы `pws-lab1-*` теперь собираются для каждой реплики, а `pull_policy: never` исключает попытки найти их на Docker Hub.

Повторный запуск уже собранных образов: `./Start-Lab.ps1 -SkipBuild`. Остановка только контейнеров этой лабораторной: `./Stop-Lab.ps1`. Логи: `docker compose logs -f`. Все опубликованные порты привязаны к localhost.

| Задание | Адрес | Размещение состояния | Балансировка |
|---|---|---|---|
| 1 | http://localhost:8111/ и :8112/ | Память WS01-01; cookie — только ID сессии | Прямой доступ |
| 2 | http://localhost:8101/ | Два экземпляра WS01-01 | Sticky Session |
| 3 | http://localhost:8121/ и :8122/ | WS01-02: целиком в cookie клиента | Прямой доступ |
| 4 | http://localhost:8102/ | Два экземпляра WS01-02 | Round Robin |

## Контракт `/stack`

**Принятая трактовка:** базовый RESULT и стек принадлежат отдельному клиенту. Начальная база — 0, начальный стек пуст. POST устанавливает базу; GET вычисляет `база + верхний элемент`, либо `база + 0`, если стек пуст. Вычисленный ответ не записывается обратно в базу. POST/PUT/DELETE тоже возвращают текущее вычисленное значение RESULT и состояние после операции.

Пример в `../PWS-1` использует глобальный RESULT и старый ASP.NET Framework. Здесь сохранена формула, но область хранения RESULT сделана клиентской, чтобы две реплики WS01-02 сохраняли всё состояние при Round Robin без общего хранилища. В исходном задании область хранения RESULT явно не задана.

| Метод | Запрос | Действие |
|---|---|---|
| GET | `/stack` | Прочитать результат; стек и база не меняются |
| POST | `/stack`, `Content-Type: application/json`, тело `{"RESULT":10}` | Установить базу, сохранить стек |
| PUT | `/stack?ADD=5` | PUSH целого числа 5 |
| DELETE | `/stack` | POP верхнего элемента |

Входные числа — Int32. Сумма вычисляется в Int64, поэтому крайние значения двух Int32 не переполняются. В ответе поле `RESULT` остаётся в верхнем регистре. Дополнительные поля `baseValue`, `topStackValue`, `stackSize`, `stack`, `instance`, `storage` нужны для демонстрации. Массив `stack` перечисляет элементы **от дна к верхушке**.

Ошибки: 400 — некорректное число/JSON/cookie; 409 — пустой стек при POP или лимит 64 элементов при PUSH; 413 — JSON больше 4096 байт; 415 — POST без JSON Content-Type; 405 — неподдерживаемый метод. При ошибке текущее состояние не изменяется.

## Как устроено состояние и шлюзы

**WS01-01.** Cookie `WS01.Session` содержит случайный ID из 32 байт. В памяти конкретного процесса по этому ID хранятся база и стек. Сессия истекает через 30 минут бездействия; после перезапуска контейнера память теряется. Изменения одной сессии выполняются под блокировкой, поэтому параллельные PUSH не теряются.

**Sticky Gateway.** YARP выбирает экземпляр Round Robin при первом запросе нового клиента, затем cookie `WS01.Affinity` закрепляет этого клиента за выбранной репликой. Каждый следующий запрос с cookie попадает в ту же реплику. Использован `HashCookie`; cookie привязки не содержит стек. Для некорректной привязки задана политика `Return503Error`, чтобы не скрывать потерю состояния автоматическим переключением. Это учебная система без проверки здоровья и восстановления состояния при отказе реплики.

**WS01-02.** Cookie `WS02.State` содержит `Base64URL(JSON состояния).Base64URL(HMAC-SHA256)`. В JSON находятся и база, и весь стек. Подпись проверяется на каждой реплике; ключ `COOKIE_SIGNING_KEY` должен совпадать. Учебный ключ по умолчанию задан в Compose; при другом использовании замените его через `.env`. Подпись защищает от незаметного редактирования, но не шифрует содержимое. Сервер не хранит состояние клиента.

**Round Robin Gateway.** Привязка к сессии выключена. Последовательные запросы чередуют WS01-02-A и WS01-02-B. Обе реплики восстанавливают одно и то же состояние из переданного cookie.

Все state-cookie: `HttpOnly`, `SameSite=Strict`, `Path=/stack`, без постоянного времени жизни в браузере. Браузер передаёт их автоматически. Ограничение 64 элементов удерживает cookie значительно меньше обычного лимита 4 КБ. Cookie не различают порты: для независимых клиентов или переключения между прямыми WS01-01 и шлюзом используйте приватное окно/отдельный профиль. Cookie на клиенте рассчитан на последовательные операции: параллельные изменения из нескольких вкладок могут перезаписать друг друга последним ответом.

## Демонстрация на защите

1. Откройте `http://localhost:8101/`. Нажмите GET: `RESULT=0`, стек пуст. В журнале виден ответивший экземпляр.
2. POST с базой 10 → `RESULT=10`. PUSH 5 → 15. PUSH 8 → 18. Несколько GET подряд → 18, экземпляр не меняется.
3. POP → 15; ещё POP → 10; POP пустого стека → HTTP 409. База остаётся 10.
4. Откройте этот же адрес в приватном окне. Первый GET → 0: это другая сессия. Новые клиенты распределяются между двумя экземплярами.
5. На `http://localhost:8102/` повторите шаги 1–3. Имена A/B чередуются после каждого запроса, результат сохраняется.
6. В DevTools → Application → Cookies сравните `WS01.Session` + `WS01.Affinity` и `WS02.State`. HttpOnly-cookie нельзя прочитать через `document.cookie`.
7. Покажите `docker compose ps`, `Dockerfile`, single-file файлы и автоматический журнал `artifacts/verification.md`.

Запросы из PowerShell с сохранением cookie:

```powershell
$url = 'http://localhost:8101/stack' # замените на 8102 для Round Robin
Invoke-RestMethod $url -SessionVariable client
Invoke-RestMethod $url -Method Post -WebSession $client -ContentType 'application/json' -Body '{"RESULT":10}'
Invoke-RestMethod "$url`?ADD=5" -Method Put -WebSession $client
Invoke-RestMethod $url -WebSession $client
Invoke-RestMethod $url -Method Delete -WebSession $client
```

## Проверка

```powershell
.\Test-Lab.ps1
```

HTTP-проверки используют только стандартную библиотеку Python 3. Скрипт выбирает Python из Codex runtime, `py -3` либо `python`. Проверяет CRUD на шести адресах, LIFO, неизменность повторного GET, отдельные клиенты, границы чисел, ошибки без изменения состояния, ёмкость, Sticky Session, чередование Round Robin, перенос cookie между репликами, защиту подписи, конкурентные PUSH серверной сессии и маршруты HTTP.

Файлы `artifacts/verification.json` и `artifacts/verification.md` перезаписываются фактическими результатами запуска. Для проверки страницы есть `tests/verify_browser.cjs`: нужен Node.js, Playwright и установленный Microsoft Edge. Он проверяет оба интерфейса при ширине 1440 и 390 пикселей, операции, отображение ошибок, маршруты и ошибки JavaScript; сохраняет PNG.

Локальная проверка уже собранных Release-проектов без скачивания пакетов: `./tests/verify_local.ps1 -Browser`. Она запускает шесть процессов на тех же портах, выполняет проверки и останавливает только свои процессы. Отчёт явно помечается `deployment: local`.

## Linux single-file и резервная сборка

Основной `Dockerfile` публикует проект внутри SDK-контейнера с `--self-contained true`, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`. Сборка проверяет, что в каталоге публикации **ровно один файл**. Затем файл копируется в `runtime-deps` — образ с системными зависимостями, без установленного .NET runtime. Выполнение — `./service`, не `dotnet service.dll`. Native-библиотеки при первом старте распаковываются в `/tmp/dotnet-bundle`.

В `Directory.Build.props` для `PublishSingleFile=true` отключён `StaticWebAssetsEnabled`: страница уже встроена как `EmbeddedResource` в `StackApi`, и приложение не использует статические файловые endpoints. Иначе Web SDK дополнительно копирует `*.staticwebassets.endpoints.json`, даже если список endpoints пуст, и проверка одного файла завершает Docker-сборку с кодом 1 после успешного `dotnet publish`. Обычная сборка для разработки сохраняет стандартные настройки SDK. Перед проверкой Dockerfile выводит содержимое `/publish`, чтобы причина возможного сбоя была видна в логе.

Если реестр `mcr.microsoft.com` недоступен, резервный вариант требует SDK .NET 10 на Windows и доступ к NuGet:

```powershell
.\Publish-Linux.ps1
.\Start-Lab.ps1 -Local
.\Test-Lab.ps1
```

`Dockerfile.local` пакует **те же Linux self-contained single-file** из `artifacts/publish/` в Debian. В Debian есть системные OpenSSL/libstdc++/zlib; .NET не установлен. В этом варианте включён invariant globalization, поскольку сервис работает с целыми числами и не требует ICU. Для перезапуска готовых образов достаточно `Start-Lab.ps1 -SkipBuild`.

`compose.local.yaml` — самостоятельная конфигурация всех шести контейнеров; после публикации допустима команда `docker compose -f compose.local.yaml up -d --build` (в том числе через расширение Docker в VS Code). Без публикации каталог `artifacts/publish` пуст и COPY работать не может. `Start-Lab.ps1 -Local` заранее проверяет наличие и ELF-формат всех трёх файлов и сообщает, какой шаг отсутствует. Он не запускает загрузку NuGet автоматически.

Проверка упаковки:

```powershell
Get-ChildItem artifacts/publish/*/* | Select-Object DirectoryName,Name,Length
docker compose exec ws01-01-a sh -c 'ls -lh /app; command -v dotnet || true'
docker compose exec ws01-02-a sh -c 'ls -lh /app; command -v dotnet || true'
```

## Файлы и сборка для разработки

`src/WS01-01` и `src/WS01-02` — два отдельных Web API проекта. `src/StackApi` — общие операции, два хранилища и встроенная страница. `src/Gateway/Program.cs` — маршруты YARP и выбор политики по переменной окружения. `compose.yaml` — топология из шести контейнеров. `tests/` — проверки на реальном HTTP и в браузере.

```powershell
dotnet restore PWS-Lab1.sln -m:1
dotnet build PWS-Lab1.sln --no-restore -c Release -m:1
dotnet run --project src/WS01-01 --urls http://localhost:8111
```

## Документация

- [Microsoft: single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)
- [Microsoft: session affinity в YARP](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/session-affinity)
- [Microsoft: load balancing в YARP](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/load-balancing)
