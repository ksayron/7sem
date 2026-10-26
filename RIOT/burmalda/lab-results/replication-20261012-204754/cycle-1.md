# ЛР №5 — выполненный цикл 1

ID: `b9b0f882-843c-418a-aaa0-669b9f2540d1`. Время фиксации UTC: 2026-10-12T20:47:55.1188351+00:00.

Реплицируются доступные экземпляры трёх книг. Изменения в этом цикле относятся к book-1.

## Репликация

| № | Операция | Primary до | Primary после | Replica до | Replica после | Репликация | Результат |
|---|---|---|---|---|---|---|---|
| 1 | Client 1 Update | 10 | 8 | 10 | 8 | SUCCESS | SUCCESS |
| 2 | Client 1 Update | 8 | 7 | 8 | 7 | SUCCESS | SUCCESS |
| 3 | Client 1 Update | 7 | 8 | 7 | 8 | SUCCESS | SUCCESS |
| 6 | Client 2 Update | 8 | 8 | 8 | 7 | FAIL | SUCCESS |
| 8 | Client 1 Update | 7 | 8 | 7 | 8 | SUCCESS | SUCCESS |

Во время отказа Primary операция Update успешно записана в Replica, но передача в отключённый Primary имеет статус FAIL. Успех запроса и успех репликации учитываются отдельно.

## Отказ и запросы

| № | Клиент | Операция | Primary | Replica | Результат | Использованный экземпляр |
|---|---|---|---|---|---|---|
| 1 | Client 1 | Update | AVAILABLE | AVAILABLE | SUCCESS | Primary |
| 2 | Client 1 | Update | AVAILABLE | AVAILABLE | SUCCESS | Primary |
| 3 | Client 1 | Update | AVAILABLE | AVAILABLE | SUCCESS | Primary |
| 4 | Client 1 | Read | UNAVAILABLE | AVAILABLE | SUCCESS | Replica |
| 5 | Client 2 | Read | UNAVAILABLE | AVAILABLE | SUCCESS | Replica |
| 6 | Client 2 | Update | UNAVAILABLE | AVAILABLE | SUCCESS | Replica |
| 7 | Client 1 | Read | UNAVAILABLE | AVAILABLE | SUCCESS | Replica |
| 8 | Client 1 | Update | AVAILABLE | AVAILABLE | SUCCESS | Primary |

## Восстановление

| Этап | Primary book-1 | Replica book-1 | Источник | Копии согласованы |
|---|---|---|---|---|
| Начальное состояние | 10 | 10 | Primary | True |
| Изменение и репликация | 8 | 8 | Primary | True |
| Изменение и репликация | 7 | 7 | Primary | True |
| Изменение и репликация | 8 | 8 | Primary | True |
| Primary FAILURE | 8 | 8 | Primary | False |
| Failover: Read Client 1 | 8 | 8 | Replica | False |
| Update Client 2 через Replica | 8 | 7 | Replica | False |
| Primary RECOVERY до синхронизации | 8 | 7 | Replica | False |
| Synchronization Replica → Primary | 7 | 7 | Primary | True |
| Контрольная операция Primary → Replica | 8 | 8 | Primary | True |

Во время отказа Client 1 и Client 2 читали Replica; Client 2 уменьшил book-1 с 8 до 7. Primary сохранил 8. Актуальная копия — Replica. Recovery копирует её полное состояние вместе с версией в Primary, затем возвращает обработку в Primary. Контрольное изменение увеличивает значение до 8 и передаётся в Replica. book-2 = 6, book-3 = 4 в обеих копиях. Полные события и состояния трёх записей сохранены в соседнем JSON.

