# ЛР7 + ЛР8: единый Docker-deliverable Burmalda

Одна версия приложения закрывает выбор лидера/консенсус ЛР7 и контейнеризацию ЛР8.
Работы можно защищать отдельно по чек-листам ниже. Предметная область — библиотека,
выдача и возврат экземпляров; сохраняются Saga, RabbitMQ, outbox/inbox и репликация PostgreSQL.

## Запуск

Нужен Docker Desktop в режиме Linux containers и доступ к реестрам образов/NuGet при
первой сборке. Visual Studio, .NET SDK, PowerShell и Node.js на хосте для запуска не нужны.

```powershell
docker compose config --quiet
docker compose up -d --build
docker compose ps -a
# Основное приложение: http://localhost:5300
# ЛР7: http://localhost:5300/raft-lab
# PostgreSQL: http://localhost:5300/replication-lab
# Дополнительные компоненты ЛР6:
docker compose --profile pubsub up -d --build
```

Первая сборка загружает .NET SDK/runtime и восстанавливает зависимости. `up -d` запускает
стек в фоне; готовность проверяется через `docker compose ps` и `/health/ready` API.
У `database-bootstrap` нормальное итоговое состояние — `Exited (0)`.

Необязательный `.env` создаётся из `.env.example`. Начальный администратор:
`admin@burmalda.local` / `ChangeMe123!`. Параметры `ADMIN_EMAIL`, `ADMIN_PASSWORD`,
`JWT_SIGNING_KEY`, `RAFT_INTERNAL_KEY`, `LIBRARY_INTERNAL_KEY` задаются для всего стека.
Изменение паролей PostgreSQL на уже созданных томах само по себе не меняет роли в БД.

```powershell
docker compose logs --tail 100 concurrency-lab raft-node-1
docker compose logs -f catalog lending
./tools/Set-RaftNode.ps1 -NodeId 5 -Action Stop
./tools/Set-RaftNode.ps1 -NodeId 5 -Action Start
docker compose restart catalog
docker compose --profile pubsub down
docker compose up -d --build
```

`down` сохраняет данные. `down -v` удаляет тома и для обычного запуска/демонстрации
не используется. `Stop-Lab.ps1` останавливает контейнеры, `Start-Lab.ps1` запускает
основной стек; `Start-PubSubLab.ps1` добавляет профиль `pubsub`.

## Контейнеры и сеть

| Компоненты | Адрес с хоста | Адрес внутри сети |
|---|---|---|
| nginx / Blazor WASM | `5300` | `web:8080` |
| Gateway | `5000` | `gateway:8080` |
| Identity, Catalog, Lending, Economy, Reputation, Notifications | `5101–5106` | соответствующее имя сервиса, `8080` |
| Лабораторный API | `5107` | `concurrency-lab:8080` |
| Игра / брокеры A, B / подписчики, профиль `pubsub` | `5108–5111` | `library-game`, `library-broker-a`, `library-broker-b`, `library-subscribers`, порт `8080` |
| Пять управляющих узлов | `5121–5125` | `raft-node-1` … `raft-node-5`, порт `8080` |
| PostgreSQL / replica | `5547 / 5548` | `postgres:5432`, `postgres-replica:5432` |
| RabbitMQ / Management | `5672 / 15672` | `rabbitmq:5672` |
| Jaeger / OTLP / метрики | `16686 / 4317,4318 / 8889` | `jaeger`, `otel-collector` |

Хостовые порты публикуются на `127.0.0.1`. Браузер загружает `/appsettings.json` с
`ApiBaseUrl=/`; nginx проксирует `/api` в Gateway. Остальные сервисы используют DNS
Compose. Прямое открытие `/raft-lab`, `/discover` и других маршрутов возвращает SPA.

`docker/Dockerfile` содержит стадии SDK publish, ASP.NET runtime и nginx web. SDK
соответствует `global.json`. Пять узлов получают один образ с разными `Raft__NodeId`.
В финальных образах нет SDK, `.env`, локальной истории и результатов проверок.
Серверный `ENTRYPOINT` запускает apphost напрямую: shell удалял бы переменные
с дефисами в именах, например адрес `postgres-replica` или маршрут `concurrency-lab`.
Брокеры A/B используют один образ и разные ID/тома, как пять участников Raft.
Образ PostgreSQL включает репликационные скрипты, поэтому maintenance не зависит от
путей Windows или примонтированного исходного репозитория.

`database-bootstrap` определяет текущий writable primary и идемпотентно создаёт
недостающие БД/роли. При двух writable узлах либо отсутствии writer запуск подготовки
завершается ошибкой. Зависимости приложений не привязаны к здоровью именно `postgres`:
после promotion writer может находиться в `postgres-replica`. Открытие соединения имеет
ограниченные повторы; SQL-транзакции после неизвестного COMMIT автоматически не повторяются.

## Консенсус, исполнение и сохранность

Raft сохраняет пять участников и кворум 3/5. `Raft:Members` задаёт адреса участников,
`Raft:ListenUrl` — адрес прослушивания. Ключ, cluster ID и ID отправителя проверяются
на внутренних RPC; Gateway их не публикует. В Docker используется отдельный cluster ID
`burmalda-postgres-docker-v1`. Существующие Windows SQLite не импортируются.

Методичка предлагает упрощённый выбор максимального ID. По принятому решению сохранён
существующий Raft: роль лидера определяется term, голосами и актуальностью журнала.
Поэтому Node 5 не обязан становиться лидером. Положительное подтверждение большинства
проверяется через committed log, а не через количество отвечающих HTTP-сервисов.
В разделе сети 3+2 меньшинство не принимает новые решения, даже если старый узел ещё
показывает локальную роль Leader. Авторитетный результат требует свежего кворума.

Каждый участник имеет свой том `raft-node-N` с SQLite, term, голосами, журналом,
результатами команд и snapshot. `raft-execution` содержит общий журнал внешних действий
и блокировку исполнителя одного Docker-хоста. Это отдельное хранилище, не журнал консенсуса.
`lab-journal` хранит сессии панели репликации. Брокеры A/B используют отдельные SQLite-тома.
Существующие тома PostgreSQL, fencing-маркеров и RabbitMQ сохраняют прежние имена.

Только управляющие узлы и лабораторный API получают Docker socket. Это локальный
учебный стенд с правами управления своим Docker-хостом. Цели проверяются по ID,
имени и меткам проекта/сервиса. `/api/raft-lab/processes` сохраняет тело
`{"nodeId":5,"action":"Stop"}` и управляет контейнером, без PowerShell внутри него.
Диагностические действия доступны в Development, как и в исходной лаборатории.

Перед запуском внешнего Docker exec или maintenance сохраняется его устойчивый ID.
Maintenance наследует образ, mounts, окружение и сеть целевого PostgreSQL. При падении
лидера другой участник проверяет состояние действия в Docker Engine, а не чужие PID.
Работающее действие нельзя запустить повторно. Удалённое доказательство или неизвестный
исход блокирует мутации до проверки. Maintenance удаляется только после успешного
завершения, committed `Completed` и `Done` в журнале исполнения, без удаления томов.
Если повторная сборка заменила OCI index только из-за provenance, исполнитель может
использовать другой локальный index лишь при совпадении исходного platform manifest.
Произвольная подмена образом с тем же изменяемым тегом не допускается.

При первом Docker-bootstrap прежняя `.artifacts` читается через read-only mount:
незавершённые журналы, операции/fencing и неостановленные прежние узлы блокируют активацию.
Сначала нужно завершить прежнюю операцию в локальном режиме и штатно остановить его.
Не удаляйте историю или fence-маркеры для обхода проверки.

Смена лидера и переключение PostgreSQL — разные действия. Бизнес-данные хранятся в
PostgreSQL; при потере кворума исправный каталог продолжает обслуживать SQL. Для promotion
нужны подтверждённые fencing и конечный WAL LSN. Асинхронная репликация и размещение
всех контейнеров на одном Docker-хосте остаются ограничениями стенда.

## Проверки

Для инструментов проверки на хосте нужны .NET SDK и Node.js, а для браузерных тестов —
Playwright. Они не требуются пользователю приложения. Изменяющие кластер проверки
выполняются последовательно, без одновременных ручных переключений.

```powershell
dotnet build Burmalda.slnx -m:1
dotnet test tests/Burmalda.Raft.Tests --logger "trx;LogFileName=raft.trx" --results-directory lab-results/docker-verification
dotnet test tests/Burmalda.Domain.Tests --logger "trx;LogFileName=domain.trx" --results-directory lab-results/docker-verification
dotnet test tests/Burmalda.LibraryGame.Tests --logger "trx;LogFileName=library.trx" --results-directory lab-results/docker-verification
node tools/verify-docker-lab.cjs --recreate
./tools/verify-compose-fresh.ps1
node tools/verify-raft-lab.cjs
node tools/verify-application-failover.cjs
node tools/verify-real-saga.cjs
node tools/verify-raft-ui.cjs --controls
docker compose --profile test run --rm load-test
```

Docker verifier создаёт помеченные тестовые аккаунты/книгу/выдачу и сохраняет их GUID
в `lab-results/docker-verification/integration.json` для проверки сохранности. `--recreate`
проверяет пересоздание контейнеров и `down/up`; тома не удаляются. Raft verifier использует
свою SQL fixture и удаляет только её. В локальном режиме ему нужен `BURMALDA_NODE_MODE=Local`.
Проверка `verify-compose-fresh.ps1` использует отдельные новые тома и затем возвращает
основной стек с исходными томами. Имена проверочных томов сохранены в `fresh-volumes.json`;
скрипт их не удаляет. Она тоже останавливает стек, поэтому выполняется отдельно.
Для Playwright можно подготовить локальные зависимости без изменения приложения:

```powershell
npm install --prefix .artifacts/verification-deps playwright
$env:NODE_PATH=(Resolve-Path .artifacts/verification-deps/node_modules).Path
```

Браузерные verifier-ы по умолчанию используют установленный Microsoft Edge.

Нагрузка обращается к внутреннему Catalog API с настоящим JWT: 10 виртуальных
пользователей, 60 секунд. Отдельно проверяется Gateway rate limit. Сводка k6:
`lab-results/docker-verification/load-summary.json`, включая throughput, ошибки,
p50/p95/p99. Пороги: ошибки <1%, p95 <1000 мс. Порог отражает конкретную машину, а не SLA.
Нагрузочный тест оставляет помеченную книгу и аккаунт; GUID книги выводится в журнал.

## Чек-лист защиты ЛР7

1. Показать пять контейнеров, их ID, роли, term, журнал и текущего авторитетного лидера.
2. Выполнить управляющую операцию, показать committed результат и большинство 3/5.
3. Остановить текущего лидера, показать heartbeat timeout, новые выборы и работу 4/5.
4. Остановить второй узел: 3/5 ещё подтверждают решения. При 2/5 новая команда не подтверждается.
5. Восстановить контейнеры, показать догоняющую репликацию и сохранённую историю.
6. Показать разделение 3+2, результаты unit/integration-тестов и логи.
7. Объяснить отличие от max-ID модели, связи с Raft/Paxos/Zab и роль лидера именно в управлении.

## Чек-лист защиты ЛР8

1. Показать исходную архитектуру, Dockerfile, стадии сборки, сеть, порты и переменные окружения.
2. Выполнить `docker compose up -d --build`, показать состав и готовность контейнеров.
3. Через Web создать книгу/экземпляр, выполнить выдачу и возврат.
4. Остановить Catalog, показать понятную ошибку; запустить и повторить запрос.
5. Перезапустить/пересоздать контейнеры, показать сохранённые данные и историю Raft.
6. Показать логи, результаты unit/integration/UI-проверок и нагрузочную сводку.
7. Сравнить Docker-запуск с `Start-Lab.ps1 -Local -Full` и объяснить сохранность данных в томах.

## Ошибки запуска и локальная отладка

Ошибки сборки, загрузки образов, конфигурации и занятые порты видны в выводе Compose;
проверяйте exit code, `docker compose config --quiet`, `ps -a` и `logs`. Неправильные
учётные данные или недоступная БД приводят к ошибке bootstrap/подключения, без сброса томов.
Если Docker socket недоступен, управляющая операция не объявляется успешно выполненной.
После неизвестного результата используйте прежний CommandId и наблюдения панели.

Для отладки остановите контейнеры приложения и выберите `-Local` у стартовых скриптов.
Штатный `Stop-Lab.ps1 -Local` завершает только проверенные локальные процессы. Перед
возвратом в Docker закончите локальные операции и остановите локальный кластер.
Не допускается одновременное управление одной парой PostgreSQL разными кластерами.
