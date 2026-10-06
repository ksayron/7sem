$ErrorActionPreference = 'Stop'
$taskDir = 'D:\BSTU\7sem\UITP'
$taskSupport = Join-Path $taskDir '_служебные_8-9'
New-Item -ItemType Directory -Path $taskSupport -Force | Out-Null
[xml]$taskXml = Get-Content -LiteralPath (Join-Path $taskDir 'Разработка ПО — задания 8-9.xml') -Raw
$taskAssignments = Import-Csv -LiteralPath (Join-Path $taskDir 'проверка-назначений.tsv') -Delimiter "`t"
$taskDates = Import-Csv -LiteralPath (Join-Path $taskDir 'проверка-сроков.tsv') -Delimiter "`t"
$taskLaborNames = @('Руководитель проекта','Программист 1','Программист 2','Программист 3','Бизнес-аналитик','Тест-инженер','Технический лидер','Системный аналитик')
$taskInvariant = [System.Globalization.CultureInfo]::InvariantCulture
function Format-TaskNumber([string]$value) {
 if ([string]::IsNullOrWhiteSpace($value)) { return '0' }
 return ([double]::Parse($value,$taskInvariant)).ToString('0.##',$taskInvariant)
}
$taskLines = [System.Collections.Generic.List[string]]::new()
$taskLines.Add('# Разработка ПО — вариант 1, задания 8 и 9')
$taskLines.Add('')
$taskLines.Add('Итоговый файл: **Разработка ПО — задания 8-9.pod**. Открывать в ProjectLibre. Исходный Проект1.pod не изменён.')
$taskLines.Add('')
$taskLines.Add('Порядок, названия, длительности и предшественники 39 задач приведены к таблице 2.3, предоставленной пользователем. Пять этапов оформлены как суммарные задачи; их длительности вычисляются программой. Связи — окончание–начало, без задержек. Длительность рабочего дня — 8 часов. Начало — 18.09.2026 09:00 (Минск); использован исходный календарь проекта, включая его исключения. Завершение по расчёту — 07.04.2027 17:00.')
$taskLines.Add('')
$taskLines.Add('Добавлены 18 ресурсов: 17 из таблицы 2.6 и системный аналитик из таблицы 2.7. Выполнены 77 назначений на 27 задач. У всех назначений используется таблица норм затрат A. Длительности рабочих задач сохранены при назначении ресурсов: фиксированная длительность, отключено перераспределение общего объёма работы при добавлении исполнителей.')
$taskLines.Add('')
$taskLines.Add('Особенности задания и программы:')
$taskLines.Add('')
$taskLines.Add('- Системный аналитик: по явному указанию пользователя стандартная ставка 11 руб./ч, сверхурочная 14 руб./ч.')
$taskLines.Add('- Бизнес-аналитик: заполнены таблицы A и B одинаковыми заданными ставками 11/14 руб./ч. Отдельных ставок B в задании нет.')
$taskLines.Add('- Интернет: в задании это ресурс типа «Затраты». В ProjectLibre 1.9.8 такого отдельного типа нет, поэтому применён материальный эквивалент: 50 руб./мес.; 0,2 мес. на написание требований = 10 руб., 0,24 мес. на ТЗ = 12 руб. Дополнительные ежемесячные расходы не начислялись.')
$taskLines.Add('- Серверная ОС: ставка за использование 3130 руб., назначение 0,5; ProjectLibre рассчитывает 1565 руб.')
$taskLines.Add('- Все числа затрат выражены в рублях. Символ валюты в интерфейсе ProjectLibre определяется настройками программы и может отображаться как $. Численные значения ставок не переводились в другую валюту.')
$taskLines.Add('')
$taskLines.Add('Общая расчётная стоимость проекта: **56 979,80 руб.** Сверхурочные ставки заданы, сверхурочная работа не назначалась.')
$taskLines.Add('')
$taskLines.Add('Проверено повторное чтение бинарной части POD штатными библиотеками установленного ProjectLibre 1.9.8 и повторный расчёт расписания: все 39 названий и длительностей, предшественники, пять суммарных этапов, 18 ресурсов, ставки A/B, 77 назначений, трудозатраты, затраты каждого назначения и итог проекта. Визуальная проверка интерфейса ProjectLibre не выполнялась.')
$taskLines.Add('')
$taskLines.Add('## Ресурсы')
$taskLines.Add('')
$taskLines.Add('| Ресурс | Тип в ProjectLibre | Таблицы | Стандартная ставка, руб. | Сверхурочная ставка, руб. | За использование, руб. | Единица |')
$taskLines.Add('|---|---|---|---:|---:|---:|---|')
foreach ($r in $taskXml.Project.Resources.Resource) {
 if ($r.UID -eq '0') { continue }
 $taskType = if ($r.Name -in $taskLaborNames) {'Трудовой'} else {'Материальный'}
 $taskGroup = if ($r.Name -eq 'Бизнес-аналитик') {'A, B'} elseif ($r.Name -eq 'Интернет') {'—'} else {'A'}
 $taskUnit = if ($taskType -eq 'Трудовой') {'ч'} else {[string]$r.MaterialLabel}
 $taskLines.Add("| $($r.Name) | $taskType | $taskGroup | $(Format-TaskNumber $r.StandardRate) | $(Format-TaskNumber $r.OvertimeRate) | $(Format-TaskNumber $r.CostPerUse) | $taskUnit |")
}
$taskLines.Add('')
$taskLines.Add('## План задач')
$taskLines.Add('')
$taskLines.Add('| № | Задача | Длительность, рабочие дни | Предшественники | Начало (Минск) | Окончание (Минск) |')
$taskLines.Add('|---:|---|---:|---|---|---|')
foreach ($t in $taskXml.Project.Tasks.Task) {
 $taskAudit = $taskDates | Where-Object {$_.task_id -eq $t.ID} | Select-Object -First 1
 if (-not $taskAudit) { throw "Task audit missing: $($t.ID)" }
 $taskDays = if ($t.Summary -eq '1') { 'суммарная' } else {([double]::Parse($taskAudit.result_duration_hours,$taskInvariant)/8).ToString('0.##',$taskInvariant)}
 $taskPreds = @($t.PredecessorLink | ForEach-Object {$_.PredecessorUID}) -join ', '
 $taskStart = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$taskAudit.start_ms).ToOffset([TimeSpan]::FromHours(3)).ToString('dd.MM.yyyy HH:mm')
 $taskFinish = [DateTimeOffset]::FromUnixTimeMilliseconds([long]$taskAudit.finish_ms).ToOffset([TimeSpan]::FromHours(3)).ToString('dd.MM.yyyy HH:mm')
 $taskLines.Add("| $($t.ID) | $($t.Name) | $taskDays | $taskPreds | $taskStart | $taskFinish |")
}
$taskLines.Add('')
$taskLines.Add('## Назначения')
$taskLines.Add('')
$taskLines.Add('| № задачи | Задача | Ресурс | Единицы | Таблица | Затраты, руб. |')
$taskLines.Add('|---:|---|---|---:|---|---:|')
foreach ($a in ($taskAssignments | Sort-Object {[int]$_.task_id})) {
 $taskUnits = if ($a.resource -in $taskLaborNames) { ([double]::Parse($a.units,$taskInvariant)*100).ToString('0.##',$taskInvariant)+'%' } else { Format-TaskNumber $a.units }
 $taskLines.Add("| $($a.task_id) | $($a.task) | $($a.resource) | $taskUnits | A | $(Format-TaskNumber $a.cost) |")
}
$taskLines.Add('')
$taskLines.Add('## Контрольные суммы')
$taskLines.Add('')
foreach ($taskFile in @('Проект1.pod','Разработка ПО — задания 8-9.pod')) {
 $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskDir $taskFile) -Algorithm SHA256).Hash
 $taskLines.Add("$taskFile — SHA256: $taskHash")
$taskLines.Add('')
}
$taskLines | Set-Content -LiteralPath (Join-Path $taskDir 'Задания 8-9 — пояснения.md') -Encoding utf8
$taskCreatedFiles = @('исходный-проект.xml','Проект1 — задания 8-9.pod','Проект1 — задания 8-9.xml','Разработка ПО — задания 8-9.xml','проверка-назначений.tsv','проверка-сроков.tsv','candidate.pod','candidate2.pod','candidate2.xml','InspectLibra.java','InspectLibra.class','BuildLibra.java','BuildLibra.class','BuildLibra$A.class','BuildLibra$R.class','VerifyLibra.java','VerifyLibra.class','PlanLibra.java','PlanLibra.class','PlanLibra$T.class','libre-api.txt','libre-assignment-detail.txt','libre-assignment-service.txt','libre-assignment.txt','libre-cost-api.txt','libre-file-api.txt','libre-normal-task.txt','libre-resource-pool.txt','libre-task.txt','libre-project.txt','libre-microsoft-importer.txt')
foreach ($taskName in $taskCreatedFiles) {
 $taskSource = [System.IO.Path]::GetFullPath((Join-Path $taskDir $taskName))
 if (-not $taskSource.StartsWith($taskDir + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Move outside workspace refused' }
 if (Test-Path -LiteralPath $taskSource) { Move-Item -LiteralPath $taskSource -Destination (Join-Path $taskSupport $taskName) }
}
'Экспорт XML и промежуточные версии являются служебными. Для работы использовать итоговый POD в родительской папке. Проверка итогового POD выполняется через VerifyLibra (classpath — эта папка и установленный projectlibre-1.9.8.jar). Не использовать промежуточные candidate-файлы и предыдущую копию Проект1 — задания 8-9.' | Set-Content -LiteralPath (Join-Path $taskSupport 'README.txt') -Encoding utf8
Get-ChildItem -LiteralPath $taskDir -File | Select-Object Name,Length | Format-Table -AutoSize
