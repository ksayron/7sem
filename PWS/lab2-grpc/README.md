# ПВС: лабораторная работа — gRPC

Четыре приложения используют единый контракт `protos/calculator.proto` и настоящий gRPC через HTTP/2 с бинарными сообщениями Protocol Buffers.

| Приложение | Технология | Назначение / адрес по умолчанию |
| --- | --- | --- |
| `gRPCServer01` | ASP.NET Core, .NET 10 | Сервер, `127.0.0.1:8301` |
| `gRPCClient01` | C#, .NET 10 | Консольный клиент; по умолчанию вызывает Server01 |
| `gRPCServer02` | Node.js, `@grpc/grpc-js` | Сервер, `127.0.0.1:8302` |
| `gRPCClient02` | Go, `google.golang.org/grpc` | Консольный клиент; по умолчанию вызывает Server02 |

Серверы привязаны к loopback. Для локальной лабораторной работы используется HTTP/2 без TLS (h2c), сертификат не требуется. Порт ASP.NET Core настроен только на HTTP/2. Это соответствует [требованиям gRPC .NET для незашифрованного соединения](https://learn.microsoft.com/en-us/aspnet/core/grpc/troubleshoot#call-insecure-grpc-services-with-net-client). Обычная адресная строка браузера не является gRPC-клиентом.

## Требования

- .NET SDK 10.
- Node.js 22 или новее и npm.
- Go 1.25 или новее.
- Интернет для первого восстановления зависимостей NuGet, npm и Go.

PowerShell-команды ниже выполняются из папки лабораторной:

```powershell
cd D:\BSTU\7sem\PWS\lab2-grpc
```

## Полная автоматическая демонстрация

```powershell
.\Test-Lab.ps1
```

Скрипт восстанавливает зависимости, собирает оба C#-проекта и Go-клиент, проверяет синтаксис Node.js и выполняет `go vet`. Затем запускает два собственных сервера и проверяет все сочетания:

1. `gRPCClient01` → `gRPCServer01` — задание 1.
2. `gRPCClient02` → `gRPCServer02` — задание 2.
3. `gRPCClient02` → `gRPCServer01` — задание 3.1.
4. `gRPCClient01` → `gRPCServer02` — задание 3.2.

У каждого клиента 23 проверки результата либо точного gRPC-кода и сообщения ошибки. Ожидаемая итоговая строка:

```text
PASS: 4 combinations, 92/92 live gRPC checks.
```

Скрипт завершает свои серверы в `finally`, в том числе при ошибке. Занятые порты не освобождает принудительно. Для других портов:

```powershell
.\Test-Lab.ps1 -DotnetPort 8311 -NodePort 8312
```

Логи и машинный отчет сохраняются в `artifacts/`:

- `verification.json` — время, успешность и результаты сочетаний;
- `CSharp-to-ASPNET.log`, `Go-to-Node.log`, `Go-to-ASPNET.log`, `CSharp-to-Node.log` — все вызовы клиентов;
- `server01.log`, `server02.log` и соответствующие `*.err.log` — вывод серверов.

Код возврата каждого клиента — `0`, только если прошли все проверки; иначе `1`. Ожидаемые ошибки удаленных процедур считаются успешной проверкой, когда их код и сообщение совпадают с ожидаемыми.

## Ручной запуск: задания 1 и 2

Подготовка:

```powershell
dotnet restore .\PWS-Lab2-gRPC.sln --locked-mode
dotnet build .\PWS-Lab2-gRPC.sln -c Release --no-restore -m:1
npm --prefix .\gRPCServer02 ci --cache .\artifacts\npm-cache --no-audit --no-fund
```

Первый терминал — ASP.NET Core сервер:

```powershell
dotnet run --project .\gRPCServer01 -c Release --no-build -- --port 8301
```

Второй терминал — C#-клиент этого сервера:

```powershell
dotnet run --project .\gRPCClient01 -c Release --no-build -- http://127.0.0.1:8301
```

Третий терминал — Node.js сервер:

```powershell
node .\gRPCServer02\server.js --port 8302
```

Четвертый терминал — Go-клиент этого сервера:

```powershell
cd .\gRPCClient02
go run . -address 127.0.0.1:8302
```

Для остановки вручную запущенного сервера нажмите `Ctrl+C` в его терминале.

## Перекрестные вызовы: задание 3

При работающих обоих серверах, из корня лабораторной:

```powershell
# Go -> ASP.NET Core (задание 3.1)
Push-Location .\gRPCClient02
go run . -address 127.0.0.1:8301
Pop-Location

# C# -> Node.js (задание 3.2)
dotnet run --project .\gRPCClient01 -c Release --no-build -- http://127.0.0.1:8302
```

В каждом случае ожидаются, среди прочего, `SUM(10.5, 2.5) = 13`, `SUB(10, 3) = 7`, `MUL(2.5, 4) = 10`, `DIV(7, 2) = 3.5`, `FACT(5) = 120` и строка `Checks: 23/23 passed, 0 failed.`

## Процедуры и исключения

| RPC | Поля запроса | Тип результата | Вычисление |
| --- | --- | --- | --- |
| `SUM` | `double x`, `double y` | `double` | `x + y` |
| `SUB` | `double x`, `double y` | `double` | `x - y` |
| `MUL` | `double x`, `double y` | `double` | `x * y` |
| `DIV` | `double x`, `double y` | `double` | `x / y` |
| `FACT` | `int32 x` | `int32` | `x!` |

gRPC передает параметры и результаты в сообщениях `BinaryRequest`, `FactorialRequest`, `DoubleReply` и `IntReply`. Полное имя сервиса — `calculator.v1.Calculator`, например путь метода `/calculator.v1.Calculator/SUM`. У `FACT` отдельный целочисленный ответ: результат не подменяется `double`.

| Условие | gRPC-статус | Сообщение |
| --- | --- | --- |
| `DIV`, `y = 0` (включая `-0`) | `INVALID_ARGUMENT` (3) | `Division by zero.` |
| `FACT`, `x < 0` | `INVALID_ARGUMENT` (3) | `Factorial requires x >= 0.` |
| `FACT`, `x > 12` | `OUT_OF_RANGE` (11) | `Factorial exceeds Int32.MaxValue.` |
| `NaN` или бесконечный операнд | `INVALID_ARGUMENT` (3) | `Operands must be finite doubles.` |
| Бесконечный результат арифметики | `OUT_OF_RANGE` (11) | `Result exceeds finite double range.` |

`0! = 1`, `1! = 1`, `12! = 479001600`. Значение `13! = 6227020800` превышает `Int32.MaxValue = 2147483647`. Поэтому сервер отклоняет `x > 12` до умножений; огромный аргумент не запускает долгий цикл. В C# умножение дополнительно выполняется в `checked`.

В ASP.NET Core ошибки реализованы через `throw new RpcException(new Status(...))`. Node.js передает ошибку с `code` и `details` в gRPC callback. В C#-клиенте используется `catch (RpcException)`, в Go — проверка `err` и `status.Convert(err)`: в Go исключений как в C# нет. Такой способ передачи ошибок описан в [документации gRPC](https://grpc.io/docs/guides/error/). Оба клиента продолжают демонстрацию после ожидаемых ошибок. Каждый вызов ограничен тайм-аутом 5 секунд.

## Контракт и генерация

Единственный исходный контракт — `protos/calculator.proto`:

- C#-код генерируется `Grpc.Tools` при сборке серверного и клиентского проектов.
- Node.js загружает этот же файл через `@grpc/proto-loader` относительно `server.js`.
- Сгенерированный Go-код включен в `gRPCClient02/calculatorpb/`, поэтому для обычного запуска `protoc` не нужен. Подход соответствует [официальной инструкции gRPC Go](https://grpc.io/docs/languages/go/basics/).

После изменения `.proto` пересоздайте Go-код, пересоберите C# и повторите проверку:

```powershell
.\Generate-Go.ps1
.\Test-Lab.ps1
```

`Generate-Go.ps1` рассчитан на Windows x64. Он использует `protoc` из пакета `Grpc.Tools`, устанавливает закрепленные версии `protoc-gen-go` и `protoc-gen-go-grpc` в локальную папку `artifacts/tools`. Для запуска клиентов установка этих инструментов не требуется. Версии зависимостей закреплены в `packages.lock.json`, `package-lock.json`, `go.mod` и `go.sum`.
