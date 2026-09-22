# PMP Lab 3 — Mixins, Comparable/Iterator/Iterable, JSON, Future/Stream

Иерархия из лабораторной №2 (тематика **Спорт**): `Person` → `Athlete` → `Footballer` / `BasketballPlayer` / `Swimmer` + новый класс `Team`.

Запуск: `dart run bin/main.dart`, проверка стиля: `dart analyze`.

## Соответствие пунктам задания

| Пункт | Где реализовано |
|---|---|
| 1. Миксины | `lib/mixins.dart`: `Trainable`, `Awardable` (без ограничений), `Endurance` (`on Athlete`), `Showman` (`on Athlete`, переопределяет `perform()` с вызовом `super.perform()` — линейализация); применены в `lib/sportsmen.dart` |
| 2. Comparable / Iterator / Iterable | `Athlete implements Comparable<Athlete>` (compareTo по рейтингу → `roster.sort()`); `TeamIterator implements Iterator<Athlete>` (moveNext/current); `Team implements Iterable<Athlete>` — основные методы реализованы (iterator, length, isEmpty, isNotEmpty, first, last, single, elementAt, contains), остальные члены интерфейса — заглушки `throw UnimplementedError()` |
| 3. Сериализация JSON | `Athlete.toMap/toJson/fromJson` (`lib/athlete.dart`) и `Team.toJson/fromJson` с вложенными объектами (`lib/team.dart`), `dart:convert` |
| 4. Асинхронный метод | `fetchAthlete`, `fetchRating` (`async` + `Future.delayed`) в `lib/async_utils.dart` |
| 5. Future<T> | получение результата (`await`), цепочка `then → then → whenComplete`, обработка ошибок (`try-catch` и `catchError`), параллельность (`Future.wait`) — `futureDemo()` в `bin/main.dart` |
| 6. Stream<T> | single-subscription stream (`async*`-генератор `trainingLoad`: подписка, отмена `cancel()`, повторная подписка → `StateError`), broadcast stream (`StreamController.broadcast`: два подписчика, отписка одного), ошибка в потоке (`onError`/`onDone`), преобразования `map → where → take` + `await for` — `streamDemo()` в `bin/main.dart` |
