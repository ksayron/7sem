// Real PostgreSQL evidence via gateway. Two independent CLI processes reserve one fixture.
// Run after building and starting the lab: node tools/verify-database-lab.mjs
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
const base = process.env.BURMALDA_LAB_URL ?? 'http://localhost:5000';
const route = '/api/concurrency-lab/database/runs';
const results = [];
async function request(url, body, expected = 200) {
    const response = await fetch(base + url, { method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
    assert.equal(response.status, expected, `${url}: ${await response.clone().text()}`);
    return expected === 200 || expected === 201 ? response.json() : null;
}
const create = (mode, failure = 0) => request(route, { mode, failure }, 201);
function client(id, label) {
    return new Promise((resolve, reject) => {
        const cli = path.resolve('tools/ConcurrencyLab.Client/bin/Debug/net9.0/ConcurrencyLab.Client.dll');
        const child = spawn('dotnet', [cli, 'db-reserve', id, label], { env: { ...process.env, BURMALDA_LAB_URL: base }, windowsHide: true });
        let output = ''; child.stdout.on('data', value => output += value); child.stderr.on('data', value => output += value);
        child.on('error', reject); child.on('close', code => code === 0 ? resolve({ label, pid: child.pid, output }) : reject(new Error(output)));
    });
}
for (let repeat = 0; repeat < 2; repeat++) {
    for (const mode of [0, 1]) {
        const run = await create(mode);
        const processes = await Promise.all([client(run.id, 'Client 1'), client(run.id, 'Client 2')]);
        const snapshot = await request(`${route}/${run.id}`);
        assert.equal(snapshot.after.loans, mode === 0 ? 2 : 1);
        assert.equal(snapshot.status, mode === 0 ? 'Incorrect' : 'Correct');
        if (mode === 1) assert.deepEqual(snapshot.clients.map(x => x.result).sort(), ['FAIL', 'SUCCESS']);
        results.push({ repeat, processes, snapshot });
        await request(`${route}/${run.id}/reserve`, { clientId: 'Client 3' }, 409);
    }
    for (const [mode, failure, status, loans, available] of [[2,0,'Saved',1,false],[2,1,'Partial',1,true],[3,0,'Committed',1,false],[3,1,'RolledBack',0,true],[3,2,'RolledBack',0,true]]) {
        const run = await create(mode, failure);
        await request(`${route}/${run.id}/execute`, {});
        const snapshot = await request(`${route}/${run.id}`);
        assert.equal(snapshot.status, status); assert.equal(snapshot.after.loans, loans); assert.equal(snapshot.after.copyAvailable, available);
        results.push({ repeat, snapshot });
        await request(`${route}/${run.id}/execute`, {}, 409);
    }
    for (const [failure, steps, status] of [[0,3,'Completed'],[3,3,'Compensated'],[4,5,'Compensated']]) {
        const run = await create(4, failure);
        const intermediate = [];
        for (let step = 0; step < steps; step++) intermediate.push(await request(`${route}/${run.id}/step`, {}));
        const snapshot = await request(`${route}/${run.id}`);
        assert.equal(snapshot.status, status);
        assert.deepEqual(snapshot.after, { copyAvailable: failure !== 0, loans: failure === 0 ? 1 : 0, reservedPoints: failure === 0 ? 10 : 0 });
        results.push({ repeat, snapshot, intermediate });
    }
}
await request(route, { mode: 999, failure: 0 }, 400);
await request(route, { mode: 0, failure: 1 }, 400);
await request(`${route}/00000000-0000-0000-0000-000000000001`, undefined, 404);
const invalid = await create(1);
await request(`${route}/${invalid.id}/reserve`, { clientId: '' }, 400);
await request(`${route}/${invalid.id}/unknown`, {}, 404);
await request(`${route}/${invalid.id}/execute`, {}, 409);
const folder = path.resolve('lab-results', `database-${new Date().toISOString().replace(/[:.]/g, '-')}`);
await mkdir(folder, { recursive: true });
await writeFile(path.join(folder, 'evidence.json'), JSON.stringify({ recordedAtUtc: new Date().toISOString(), base, provider: 'PostgreSQL', results }, null, 2));
const time = value => value?.substring(11, 23) ?? '—';
let tables = '# ЛР 4 — результаты реальных опытов\n\nPostgreSQL; запросы через gateway. Конкурентные запросы выполнялись двумя отдельными CLI-процессами. Каждый опыт повторён дважды. Время UTC.\n\n| Механизм | Клиент | Отправка (часы клиента) | Вход | Выход | Результат |\n|---|---|---|---|---|---|\n';
for (const { snapshot: s } of results.filter(x => x.repeat === 0 && x.snapshot.mode <= 1)) for (const c of s.clients) tables += `| ${s.mode === 0 ? 'Без синхронизации' : 'lock'} | ${c.clientId} | ${time(c.sentAtUtc)} | ${time(c.enteredAtUtc)} | ${time(c.exitedAtUtc)} | ${c.result} |\n`;
tables += '\nВход/выход без lock: интервал check → save. Для lock: временные метки внутри критической секции непосредственно перед работой и освобождением.\n\n| Опыт | Общая транзакция | Операция 1 | Операция 2 | Ошибка | Состояние БД |\n|---|---|---|---|---|---|\n';
let number = 0;
for (const { snapshot: s } of results.filter(x => x.repeat === 0 && x.snapshot.mode >= 2 && x.snapshot.mode <= 3)) tables += `| ${++number} | ${s.mode === 3 ? 'Да' : 'Нет'} | Выполнена | ${s.events.some(x => x.phase === 'OPERATION 2') ? 'Выполнена' : 'Не выполнена'} | ${s.failure === 0 ? 'Нет' : s.failure === 1 ? 'После операции 1' : 'Перед COMMIT'} | ${s.status}; выдач ${s.after.loans}; экземпляр ${s.after.copyAvailable ? 'свободен' : 'занят'} |\n`;
tables += '\nПосле ROLLBACK выполненные внутри транзакции операции отменены. Partial: выдача сохранена, экземпляр свободен — состояние некорректно.\n\n| Критерий | Без общей транзакции | С транзакцией |\n|---|---|---|\n| Связанные операции | Два отдельных сохранения | В одном BEGIN |\n| Атомарность | Нет | Да |\n| Частичное изменение | Возможно | Исключено при успешном ROLLBACK |\n| COMMIT | Отдельные autocommit | Явный общий COMMIT |\n| ROLLBACK | Нет общей отмены | Явный общий ROLLBACK |\n| Сохранение при успехе | Обе операции сохраняются | Обе операции сохраняются |\n| Отмена при ошибке | Первая операция остаётся | Изменения отменены |\n| Согласованность | Может нарушаться | Сохранена |\n\n| Часть | Условие | Ожидание | Получено |\n|---|---|---|---|\n| Конкурентный доступ | 2 клиента, Resource = 1, lock | Один SUCCESS, один FAIL | Один SUCCESS, один FAIL |\n| lock | 2 клиента | 1 выдача | 1 выдача |\n| Транзакция | Без ошибки | COMMIT | Committed |\n| Транзакция | Ошибка после операции 1 | ROLLBACK | RolledBack |\n| БД | После COMMIT | Изменения сохранены | 1 выдача, экземпляр занят |\n| БД | После ROLLBACK | Изменения отменены | 0 выдач, экземпляр свободен |\n\nСага — учебная модель на отдельной БД, без транспорта RabbitMQ. Успех: 1 выдача, 10 BD в резерве, книга занята. Ошибки Economy/Lending: обратные действия завершены, 0 выдач, 0 BD, книга свободна.\n';
await writeFile(path.join(folder, 'tables.md'), tables);
console.log(`[info] Verified ${results.length} PostgreSQL experiments; evidence: ${folder}`);
