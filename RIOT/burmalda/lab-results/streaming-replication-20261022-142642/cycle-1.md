# LR5: PostgreSQL streaming replication

Session: 2e4732bb-6ac5-4abb-b3e6-d02c9f3af09f

| Stage | Writer | Nodes |
|---|---|---|
| Начальные данные Catalog | postgres | postgres: Primary AVAILABLE, WAL 0/729E858, replay 0/7261058; postgres-replica: Standby AVAILABLE, WAL , replay 0/729E858 |
| Client 1: Available=False | postgres | postgres: Primary AVAILABLE, WAL 0/729EA58, replay 0/7261058; postgres-replica: Standby AVAILABLE, WAL , replay 0/729EA58 |
| Client 2: Available=True | postgres | postgres: Primary AVAILABLE, WAL 0/729EC58, replay 0/7261058; postgres-replica: Standby AVAILABLE, WAL , replay 0/729EC58 |
| Client 1: Available=False | postgres | postgres: Primary AVAILABLE, WAL 0/729EE58, replay 0/7261058; postgres-replica: Standby AVAILABLE, WAL , replay 0/729EE58 |
| Контейнер postgres остановлен |  | postgres: Unknown UNAVAILABLE, WAL 0/729EE58, replay 0/7261058; postgres-replica: Standby AVAILABLE, WAL , replay 0/729EED0 |
| Failover: standby повышен до primary | postgres-replica | postgres: Unknown UNAVAILABLE, WAL 0/729EE58, replay 0/7261058; postgres-replica: Primary AVAILABLE, WAL 0/72A11A0, replay 0/729EED0 |
| Client 2: Available=True | postgres-replica | postgres: Unknown UNAVAILABLE, WAL 0/729EE58, replay 0/7261058; postgres-replica: Primary AVAILABLE, WAL 0/72A6538, replay 0/729EED0 |
| Восстановление и streaming replication | postgres-replica | postgres: Standby AVAILABLE, WAL , replay 0/72A65E8; postgres-replica: Primary AVAILABLE, WAL 0/72A65E8, replay 0/729EED0 |
| Client 1: Available=False | postgres-replica | postgres: Standby AVAILABLE, WAL , replay 0/72A67E8; postgres-replica: Primary AVAILABLE, WAL 0/72A67E8, replay 0/729EED0 |
| Client 2: Available=True | postgres-replica | postgres: Standby AVAILABLE, WAL , replay 0/72A69E8; postgres-replica: Primary AVAILABLE, WAL 0/72A69E8, replay 0/729EED0 |
| Цикл завершён; исходная доступность восстановлена | postgres-replica | postgres: Standby AVAILABLE, WAL , replay 0/72A69E8; postgres-replica: Primary AVAILABLE, WAL 0/72A69E8, replay 0/729EED0 |

| Client | Operation | Copy | Node | Committed | Replay | Result |
|---|---|---|---|---|---|---|
| Client 1 | Update | 2605a106-83ee-42cc-8459-6f850256e46e | postgres | True | REPLAYED | SUCCESS |
| Client 2 | Update | 2605a106-83ee-42cc-8459-6f850256e46e | postgres | True | REPLAYED | SUCCESS |
| Client 1 | Update | 2605a106-83ee-42cc-8459-6f850256e46e | postgres | True | REPLAYED | SUCCESS |
| Client 1 | Read | 2605a106-83ee-42cc-8459-6f850256e46e | postgres-replica | False | NOT_REQUIRED | SUCCESS |
| Client 2 | Update | 2605a106-83ee-42cc-8459-6f850256e46e | postgres-replica | True | PENDING | SUCCESS |
| Client 1 | Update | 2605a106-83ee-42cc-8459-6f850256e46e | postgres-replica | True | REPLAYED | SUCCESS |
| Client 2 | Update | 2605a106-83ee-42cc-8459-6f850256e46e | postgres-replica | True | REPLAYED | SUCCESS |

Measured values and full database observations are in the adjacent JSON. Promotion changes roles; recovery rejoins the former primary as a standby.
