import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:sqflite_common_ffi/sqflite_ffi.dart';
import 'package:sports_lab7/file_service.dart';
import 'package:sports_lab7/footballer.dart';
import 'package:sports_lab7/footballer_repository.dart';

void main() {
  sqfliteFfiInit();
  test(
    'SQLite CRUD, multi-field sort and Cyrillic search survive reopen',
    () async {
      final root = await Directory.systemTemp.createTemp('lab7-db-');
      final path = '${root.path}/players.db';
      final repo = FootballerRepository(
        factory: databaseFactoryFfi,
        path: path,
        seed: false,
      );
      try {
        final a = await repo.save(
          Footballer(
            name: 'Иван',
            position: 'Вратарь',
            rating: 7,
            club: 'БАТЭ',
          ),
        );
        final b = await repo.save(
          Footballer(
            name: 'Алексей',
            position: 'Вратарь',
            rating: 9,
            club: 'БАТЭ',
          ),
        );
        final c = await repo.save(
          Footballer(
            name: 'Борис',
            position: 'Защитник',
            rating: 9,
            club: 'Неман',
          ),
        );
        expect((await repo.list(query: 'АЛЕКС')).single.id, b);
        expect((await repo.list(position: 'Вратарь')).length, 2);
        expect(
          (await repo.list(
            sort: const [
              SortRule(SortField.rating, ascending: false),
              SortRule(SortField.name),
            ],
          )).map((p) => p.id),
          [b, c, a],
        );
        await repo.save(
          Footballer(
            id: a,
            name: 'Иван',
            position: 'Нападающий',
            rating: 10,
            club: 'БАТЭ',
          ),
        );
        expect((await repo.get(a))!.rating, 10);
        await repo.delete(c);
        await repo.close();
        final reopened = FootballerRepository(
          factory: databaseFactoryFfi,
          path: path,
        );
        expect((await reopened.list()).length, 2);
        expect((await reopened.get(a))!.position, 'Нападающий');
        await reopened.close();
      } finally {
        await repo.close();
        await root.delete(recursive: true);
      }
    },
  );

  test(
    'directory copies have distinct paths/data; reading occurs in isolate',
    () async {
      final root = await Directory.systemTemp.createTemp('lab7-files-');
      final service = FileService(
        platform: 'Android',
        resolver: (location) async => [root.path],
      );
      try {
        final dirs = await service.discover();
        expect(dirs.length, 9);
        expect(
          dirs.singleWhere((d) => d.location == StorageLocation.library).error,
          contains('Android'),
        );
        final player = Footballer(
          name: 'Иван',
          position: 'Вратарь',
          rating: 7,
          club: 'БАТЭ',
        );
        final temp = await service.write(dirs.first, player);
        final support = await service.write(dirs[1], player);
        expect(temp.worker, 'lab7-file-writer');
        expect(temp.entries.single.path, isNot(support.entries.single.path));
        final first = await service.read(temp.entries.single.path);
        final second = await service.read(support.entries.single.path);
        expect(first.worker, 'lab7-file-reader');
        expect(first.player.name, isNot(second.player.name));
        expect(
          (await service.scan(dirs.first)).single.path,
          temp.entries.single.path,
        );
        expect(first.source, 'Temporary');
      } finally {
        await root.delete(recursive: true);
      }
    },
  );

  test('iOS unsupported directories identify iOS', () async {
    final service = FileService(
      platform: 'iOS',
      resolver: (_) async => ['/sandbox'],
    );
    final dirs = await service.discover();
    for (final kind in [
      StorageLocation.external,
      StorageLocation.externalCache,
      StorageLocation.externalDirectories,
    ]) {
      expect(
        dirs.singleWhere((d) => d.location == kind).error,
        contains('iOS'),
      );
    }
    expect(
      dirs.singleWhere((d) => d.location == StorageLocation.library).error,
      isNull,
    );
  });
}
