# Проверка PWS, лабораторная 1

Время: 2026-10-26T17:49:10.550898+03:00

Развёртывание: docker.

Проверки: 15/15. HTTP-запросов к /stack: 366.

| Проверка | Результат |
|---|---|
| CRUD, LIFO, repeated GET, client isolation :8111 | PASS |
| CRUD, LIFO, repeated GET, client isolation :8112 | PASS |
| CRUD, LIFO, repeated GET, client isolation :8121 | PASS |
| CRUD, LIFO, repeated GET, client isolation :8122 | PASS |
| CRUD, LIFO, repeated GET, client isolation :8101 | PASS |
| CRUD, LIFO, repeated GET, client isolation :8102 | PASS |
| Input validation and integer boundaries :8111 | PASS |
| Capacity and failed mutation atomicity :8111 | PASS |
| Input validation and integer boundaries :8121 | PASS |
| Capacity and failed mutation atomicity :8121 | PASS |
| Sticky Session: same replica per client, both replicas used | PASS |
| Round Robin: alternating replicas preserve state | PASS |
| Client cookie: encoded state, cross-replica use, tamper rejection | PASS |
| Server session: concurrent PUSH without lost updates | PASS |
| Demo pages and HTTP routing | PASS |

Полный журнал HTTP: `verification.json`. Запросы направлены в реально запущенные контейнеры Docker.
