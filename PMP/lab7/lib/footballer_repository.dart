import 'dart:io';

import 'package:sqflite/sqflite.dart';

import 'footballer.dart';

enum SortField {
  name('Имя', 'name'),
  rating('Рейтинг', 'rating'),
  club('Клуб', 'club'),
  position('Позиция', 'position'),
  discipline('Дисциплина', 'discipline');

  const SortField(this.label, this.column);
  final String label;
  final String column;
}

class SortRule {
  const SortRule(this.field, {this.ascending = true});
  final SortField field;
  final bool ascending;
  String get sql =>
      '${field.column} COLLATE NOCASE ${ascending ? 'ASC' : 'DESC'}';
}

class FootballerRepository {
  FootballerRepository({DatabaseFactory? factory, this.path, this.seed = true})
    : _factory = factory ?? databaseFactory;
  final DatabaseFactory _factory;
  final String? path;
  final bool seed;
  Database? _database;
  Future<Database>? _opening;
  Future<Database> get database async =>
      _database ??= await (_opening ??= _open());
  Future<Database> _open() async {
    final location =
        path ??
        Directory(await _factory.getDatabasesPath()).uri
            .resolve('sports_lab7.db')
            .toFilePath();
    try {
      return await _factory.openDatabase(
        location,
        options: OpenDatabaseOptions(
          version: 1,
          onCreate: (db, version) async {
            await db.execute('''CREATE TABLE footballers (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL CHECK(length(trim(name)) BETWEEN 1 AND 80),
            discipline TEXT NOT NULL CHECK(length(trim(discipline)) BETWEEN 1 AND 80),
            position TEXT NOT NULL CHECK(length(trim(position)) BETWEEN 1 AND 80),
            rating INTEGER NOT NULL CHECK(rating BETWEEN 1 AND 10),
            club TEXT NOT NULL CHECK(length(trim(club)) BETWEEN 1 AND 80)
          )''');
            if (seed) {
              final batch = db.batch();
              for (final player in demoFootballers()) {
                batch.insert('footballers', player.toMap());
              }
              await batch.commit(noResult: true);
            }
          },
        ),
      );
    } catch (_) {
      _opening = null;
      rethrow;
    }
  }

  Future<List<Footballer>> list({
    String query = '',
    String? position,
    List<SortRule> sort = const [SortRule(SortField.name)],
  }) async {
    final db = await database;
    final rows = await db.query(
      'footballers',
      orderBy: [...sort.map((rule) => rule.sql), 'id ASC'].join(', '),
    );
    final needle = query.trim().toLowerCase();
    return rows
        .map(Footballer.fromMap)
        .where(
          (player) =>
              (position == null || player.position == position) &&
              [
                player.name,
                player.discipline,
                player.position,
                player.club,
                player.rating.toString(),
              ].any((value) => value.toLowerCase().contains(needle)),
        )
        .toList();
  }

  Future<Footballer?> get(int id) async {
    final rows = await (await database).query(
      'footballers',
      where: 'id = ?',
      whereArgs: [id],
    );
    return rows.isEmpty ? null : Footballer.fromMap(rows.single);
  }

  Future<int> save(Footballer player) async {
    final db = await database;
    if (player.id == null) return db.insert('footballers', player.toMap());
    final changed = await db.update(
      'footballers',
      player.toMap(),
      where: 'id = ?',
      whereArgs: [player.id],
    );
    if (changed != 1) throw StateError('Игрок уже удалён. Обновите список.');
    return player.id!;
  }

  Future<void> delete(int id) async {
    final count = await (await database).delete(
      'footballers',
      where: 'id = ?',
      whereArgs: [id],
    );
    if (count != 1) throw StateError('Игрок уже удалён. Обновите список.');
  }

  Future<void> close() async {
    await _database?.close();
    _database = null;
    _opening = null;
  }
}
