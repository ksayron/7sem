# ЛР №5. Настоящая потоковая репликация PostgreSQL

Лаборатория использует два сервера PostgreSQL 17 в Docker Compose. Изменения передаются средствами PostgreSQL через WAL streaming replication. Книги, экземпляры и их версии читаются из существующей `catalog_db`; приложение больше не создаёт три фиксированные записи и не копирует JSON-файлы.

## Запуск и данные

```powershell
./tools/Start-Lab.ps1 -ReplicationOnly
# С бизнес-сервисами, чтобы добавлять книги через интерфейс приложения:
./tools/Start-Lab.ps1 -Full
# После завершения демонстрации:
./tools/Stop-Lab.ps1
```

Открыть [ЛР №5](http://localhost:5300/replication-lab). Скрипт запускает два PostgreSQL, API 5107, Gateway 5000 и Blazor 5300. `-Full` также запускает RabbitMQ и бизнес-сервисы. Можно добавить `-NoBuild` после сборки. Перед повторным запуском остановите управляемые процессы.

| Постоянный ID узла | Контейнер | Порт хоста | Порт PostgreSQL | Начальная роль |
|---|---|---:|---:|---|
| postgres | burmalda-postgres | 5547 | 5432 | Primary |
| postgres-replica | burmalda-postgres-replica | 5548 | 5432 | Standby |

Роли меняются после failover; ID контейнеров сохраняются. Порты задаются `-PostgresPort` и `-ReplicaPort`, либо переменными Compose `POSTGRES_PORT` / `POSTGRES_REPLICA_PORT`. SQL-доступ опубликован только на loopback. Пароли находятся в локальной конфигурации и `.env`; они не входят в отчёты API.

Исходный volume `postgres-data` сохраняется. Пустой `postgres-replica-data` создаётся через `pg_basebackup -R -X stream` с physical replication slot. Все семь логических баз существующего PostgreSQL копируются как один физический кластер: Identity, Catalog, Lending, Economy, Reputation, Notifications и ConcurrencyLab. Пользователи сервисов по-прежнему имеют отдельные базы.

На существующем volume подготовка ролей/`pg_hba.conf` выполняется идемпотентно скриптом запуска. При самостоятельном первом запуске через `docker compose up` старого volume выполните provisioning на фактическом writable узле; предпочтителен `Start-Lab.ps1`, который определяет его роль. Новые volumes инициализируются автоматически.

«Новый опыт» создаёт журнал, а не копию данных. Все опыты используют общий кластер. В пустом Catalog нужно добавить книгу и экземпляр через приложение; лаборатория не подставляет образцы. Зарезервированные и выданные экземпляры защищены от изменений. Доступность выбранного экземпляра меняется `Available ↔ Unavailable`; транзакция увеличивает `Version` и пересчитывает `book_search_projection`.

## Передача WAL, отказ и восстановление

```text
Blazor / C# CLI → Gateway :5000 → Development Lab API :5107
                                  │ SQL + фиксированные команды Docker
                        ┌─────────┴──────────┐
                        │                    │
                  postgres :5547 ── WAL ──→ postgres-replica :5548
                    Primary                   Standby (read-only)
                         после promotion направление меняется
```

1. Создать опыт и выбрать реальный экземпляр Catalog. На странице видны обе роли, доступность контейнеров, WAL/replay LSN, streaming status, lag в байтах и UTC последнего наблюдения.
2. Выполнить несколько изменений через Client 1/Client 2. Подтверждённый COMMIT и результат replay учитываются отдельно: `Committed=true` может сопровождаться `PENDING` при отставшем или остановленном standby.
3. «Приостановить WAL replay» вызывает `pg_wal_replay_pause()`. Передача WAL продолжается; приостановлено именно воспроизведение. После изменения сравнить реальные данные узлов. «Дождаться WAL replay» возобновляет replay и ждёт воспроизведения измеренной границы WAL.
4. «Остановить» на writable узле устанавливает persistent startup fence, переводит исходный сервер в read-only, завершает оставшиеся клиентские соединения, фиксирует WAL-границу и выполняет настоящий `docker compose stop`.
5. Следующее чтение/изменение проверяет, что прежний primary остановлен, а standby воспроизвёл необходимый WAL. Затем `pg_promote()` делает standby writable. При отставании, pause или отсутствии подтверждённого fencing запрос отклоняется. Восстановите исходный узел, если безопасное переключение невозможно.
6. Во время отказа читать/менять данные нового primary. «Восстановить» старый узел запускает `pg_rewind` в отдельном maintenance-контейнере с его volume, создаёт standby-конфигурацию и запускает его вслед за новым primary. После catch-up роли остаются поменянными. Возврата writer в исходный контейнер нет.
7. Повторить цикл: роли поменяются ещё раз. Автоматический цикл использует выбранный Copy ID, сохраняет измеренные промежуточные состояния и восстанавливает исходную доступность с проверками версий. Если внешний клиент изменит этот экземпляр, цикл завершится конфликтом, не перезаписывая чужое изменение.

Старый primary не может стартовать независимо через обычный Docker restart: entrypoint проверяет `/control/<node>.fenced` в общем постоянном volume. При отмене failover до promotion допускается возврат исходного сервера; после promotion допустим только rejoin. Maintenance-контейнеры имеют фиксированные имена, чтобы повторный запрос не запускал два rewind одного volume.

Операции кластера сериализуются для всех опытов. Долгие действия возвращают HTTP 202; страницы и CLI опрашивают их состояние. Два окна могут подключиться к одному ID и видеть общий журнал. Краткие опросы состояния не должны приводить к отказу пользовательских действий.

## HTTP и C# CLI

Базовый путь: `/api/concurrency-lab/replication/runs` (только Development).

| Метод / суффикс | Вход | Назначение |
|---|---|---|
| POST / | `{}` | Новый журнал, без сброса Catalog |
| GET /{id} | — | Журнал и реальные наблюдения двух узлов |
| POST /{id}/read | `{clientId}` | Чтение через текущий writer, при необходимости проверенный failover |
| POST /{id}/update | `{clientId,copyId,available,expectedVersion}` | Транзакционное изменение экземпляра |
| POST /{id}/instances/{node} | `{available}` | HTTP 202: настоящий stop или восстановление |
| POST /{id}/replication | `{enabled}` | Pause/resume WAL replay standby |
| POST /{id}/synchronize | `{}` | HTTP 202: дождаться replay границы WAL |
| POST /{id}/cycle | `{copyId}` | HTTP 202: полный цикл с измеренными значениями |

`node` принимает только `postgres` / `postgres-replica`. Обязательные `available`, `expectedVersion` и управляющие флаги должны присутствовать в JSON. HTTP 400 означает неправильный ввод, 404 — отсутствующий ресурс, 409 — конфликт версии/безопасности/занятого кластера, 503 — недоступность. Транспортная ошибка COMMIT может означать неизвестный результат: нужно перечитать версию, автоматического повтора записи нет.

```powershell
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-create
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-read <run-id> "Client 1"
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-update <run-id> <copy-id> false <version> "Client 2"
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-instance <run-id> postgres off
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-read <run-id> "Client 1"
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-instance <run-id> postgres on
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-sync <run-id>
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-report <run-id>
dotnet run --project tools/ConcurrencyLab.Client --no-build -- rep-cycle <copy-id> 2
```

Используйте действительные GUID, роль writer и версию из ответа, а не буквальные `<...>`. Дополнительные команды: `rep-pause <run-id>` / `rep-resume <run-id>`. `rep-cycle` создаёт новые журналы и сохраняет JSON + Markdown в `lab-results/streaming-replication-<UTC>/`. Базовый URL CLI задаётся `BURMALDA_LAB_URL`, по умолчанию API 5107.

Журнал и переходы полномочий сохраняются в `.artifacts/streaming-replication/`. Они переживают перезапуск API; незавершённое действие отмечается `Interrupted`. `tools/Restart-LabApi.ps1` перезапускает только управляемый API и сохраняет базы и журналы. JSON в этом каталоге является журналом управления, не механизмом репликации книг. Не запускайте два координатора для одного кластера.

## Проверка и доказательства

```powershell
dotnet build Burmalda.slnx --no-restore -m:1 -p:UseSharedCompilation=false
dotnet test tests/Burmalda.Domain.Tests/Burmalda.Domain.Tests.csproj --no-build --no-restore
./tools/Start-Lab.ps1 -Full -NoBuild
node tools/verify-replication-lab.cjs
# После HTTP-проверок; нужен доступный пакет playwright и Edge:
node tools/verify-replication-ui.cjs
# Независимая проверка чистых volumes; временный Compose-проект удаляется:
./tools/verify-replication-bootstrap.ps1
```

В текущем Windows-окружении Playwright доступен в bundled runtime. Если обычный Node не находит пакет, перед UI-проверкой задайте `$env:NODE_PATH = "$env:USERPROFILE/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules"`.

Unit-тесты проверяют роли, журнал, общий gate, оптимистическую конкуренцию, отдельные COMMIT/replay, неизвестный COMMIT без повтора, numeric LSN, запрет stale/unfenced promotion и восстановление после ошибок. Они используют тестовый адаптер; доказательство настоящей репликации дают отдельные Docker-проверки.

HTTP/Docker-верификатор создаёт временные Catalog-строки напрямую SQL, видит их через API и оба узла, проверяет read-only standby, конфликт версий, защиту Reserved/OnLoan, pause/catch-up, настоящий stop/promotion, два цикла с обращением ролей, pg_rewind, переподключение Catalog, неуспешное восстановление и restart API, отказ обоих серверов. В конце удаляет только свои GUID-строки и сравнивает исходные количества книг/экземпляров. Не запускайте HTTP и UI сценарии одновременно: они изменяют один реальный кластер.

Текущие доказательства находятся в [streaming-replication-verification](../lab-results/streaming-replication-verification/): [HTTP/Docker JSON](../lab-results/streaming-replication-verification/http-verification.json), [UI JSON](../lab-results/streaming-replication-verification/ui-verification.json), [тесты TRX](../lab-results/streaming-replication-verification/streaming-replication.trx), [desktop](../lab-results/streaming-replication-verification/cycle-desktop.png), [mobile](../lab-results/streaming-replication-verification/cycle-mobile.png).

Проверено 22 октября 2026: сборка без ошибок и предупреждений, 40 .NET-тестов, 12 HTTP/Docker-проверок, 6 браузерных сценариев, отдельный полный цикл C# CLI с JSON/Markdown, чистая инициализация двух новых volumes и проверка отображения persistent fence. Итоговая сводка: [verification-summary.json](../lab-results/streaming-replication-verification/verification-summary.json). После удаления SQL-фикстуры в обоих узлах осталось 9 существующих книг и 14 экземпляров.

Старые `lab-results/replication-*` от 12 октября относятся к прежней файловой модели и сохранены как исторические материалы. Их значения и результаты не доказывают работу текущей PostgreSQL-реализации.

## Практические границы

Репликация асинхронная. Произвольный аварийный отказ не гарантирует отсутствие потери последних транзакций; без проверяемой границы WAL координатор не выполняет автоматический promotion. На одном компьютере остаётся общий отказ Docker/диска/хоста. Остановка primary временно влияет на все базы сервисов; новые подключения выбирают writable сервер через Npgsql multiple hosts, но выполнявшиеся транзакции не повторяются вслепую.

`pg_rewind` требует `wal_log_hints` или checksums и подходящую историю WAL. Настройки hints включены до экспериментов. Если rewind не удался, целевой volume сохраняется, fence остаётся, ошибка видна в журнале; автоматического удаления данных и повторного `initdb` нет. При утраченной истории WAL требуется отдельное восстановление из проверенного источника. `Stop-Lab.ps1` останавливает приложения, но не удаляет Docker volumes.

Документация PostgreSQL: [Streaming replication](https://www.postgresql.org/docs/17/warm-standby.html), [Failover и fencing](https://www.postgresql.org/docs/17/warm-standby-failover.html), [pg_rewind](https://www.postgresql.org/docs/17/app-pgrewind.html). Подключения сервисов: [Npgsql multiple hosts](https://github.com/npgsql/doc/blob/main/conceptual/Npgsql/failover-and-load-balancing.md).
