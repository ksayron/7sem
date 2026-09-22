# PMP Lab 2 — Основы языка Dart

Тематика: **Спорт** (футбол/баскетбол/плавание).

Запуск: `dart run bin/main.dart`, проверка стиля: `dart analyze`.

## Соответствие пунктам задания

| Пункт | Где реализовано |
|---|---|
| Иерархия классов | `Person` → `Athlete` → `Footballer` / `BasketballPlayer` / `Swimmer` |
| Явный интерфейс | `abstract interface class Match` в `lib/match.dart`, реализован в `FootballMatch` |
| Неявный интерфейс | класс `Person` в `lib/person.dart`, реализован в `Referee implements Person` |
| Абстрактный класс + переопределение | `abstract class SportsClub` в `lib/sports_club.dart`; наследники `FootballClub`, `BasketballClub` переопределяют `trainAthlete()` и `describe()` |
| Конструкторы | обычный `Athlete(...)`, именованные `Athlete.fromMap`, `Footballer.goalkeeper`, `SportsClub.founded` |
| Геттеры/сеттеры | `Athlete.rating` (сеттер с валидацией), `Athlete.isPro` (вычисляемый геттер) |
| Статические поле/функция | `SportsClub.totalClubs`, `SportsClub.formatTitle` |
| Параметры функций | именованные (`position:`, `rating:`), по умолчанию (`discipline = 'футбол'`), необязательные позиционные (`[discipline]`), типа функция (callback `onProgress` в `Athlete.train`, `action` в `forEachAthlete`) |
| List / Map / Set | `bin/main.dart`, разделы 6: `List<Athlete>`, `Map<String,int> medals`, `Set<String>` (union/intersection) |
| continue / break | `Athlete.train` (continue) и цикл по профи-атлетам в `main.dart` (break) |
| try-catch-finally | `lib/person.dart` — `InvalidRatingException`; в `main.dart` два блока try (on-секции + общий catch + finally) |
