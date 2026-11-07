import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:hive/hive.dart';
import 'package:coffee_lab8/coffee_repository.dart';
import 'package:coffee_lab8/models.dart';

void main() {
  late Directory directory;
  CoffeeRepository? repo;
  setUp(() async {
    directory = await Directory.systemTemp.createTemp('coffee_lab8_test_');
    repo = await CoffeeRepository.open(directory);
    await repo!.signIn('emma', 'emma123');
  });
  tearDown(() async {
    await repo?.close();
    await Hive.close();
    await directory.delete(recursive: true);
  });

  CoffeeProduct product({String name = 'Test coffee', int price = 375}) =>
      CoffeeProduct(
        id: 'test-product',
        name: name,
        asset: imagePaths.last,
        priceCents: price,
        category: 2,
        location: 'Minsk',
        description: 'Persisted description',
        rating: 4.7,
        reviewCount: 31,
      );

  test(
    'sign-in rejects invalid credentials and session survives restart',
    () async {
      await repo!.signOut();
      expect(repo!.isSignedIn, isFalse);
      expect(() => repo!.currentUser, throwsStateError);
      for (final credentials in [
        ('emma', 'wrong'),
        ('missing', 'emma123'),
        ('emma', ''),
      ]) {
        await expectLater(
          repo!.signIn(credentials.$1, credentials.$2),
          throwsA(isA<SignInException>()),
        );
        expect(repo!.isSignedIn, isFalse);
      }
      await repo!.signIn('  EMMA  ', 'emma123');
      await repo!.setFavorite('coffee-0', true);
      await repo!.close();
      repo = await CoffeeRepository.open(directory);
      expect(repo!.currentUser.id, 'user-1');
      expect(repo!.isFavorite('coffee-0'), isTrue);
      await repo!.signOut();
      expect(repo!.favorites, isEmpty);
      await expectLater(repo!.setFavorite('coffee-0', false), throwsStateError);
      await expectLater(
        repo!.saveProduct(product(), creating: true),
        throwsStateError,
      );
      await expectLater(repo!.deleteProduct('coffee-0'), throwsStateError);
      await repo!.close();
      repo = await CoffeeRepository.open(directory);
      expect(repo!.isSignedIn, isFalse);
      await repo!.signIn('daniel', 'daniel123');
      expect(repo!.favorites, isEmpty);
      await repo!.signOut();
      await repo!.signIn('emma', 'emma123');
      expect(repo!.isFavorite('coffee-0'), isTrue);
    },
  );

  test('legacy user selection requires login and preserves data', () async {
    await repo!.setFavorite('coffee-0', true);
    await Hive.box<dynamic>('settings').delete('authVersion');
    await repo!.close();
    repo = await CoffeeRepository.open(directory);
    expect(repo!.isSignedIn, isFalse);
    expect(repo!.products.length, 7);
    await repo!.signIn('emma', 'emma123');
    expect(repo!.isFavorite('coffee-0'), isTrue);
  });

  test('seeded models, adapters and all fields survive close/reopen', () async {
    expect(repo!.users.length, 3);
    expect(repo!.products.length, 7);
    await repo!.signIn('admin', 'admin123');
    await repo!.saveProduct(product(), creating: true);
    await repo!.setFavorite('test-product', true);
    await repo!.close();
    repo = null;
    repo = await CoffeeRepository.open(directory);
    final actual = repo!.product('test-product')!;
    expect(actual.name, 'Test coffee');
    expect(actual.priceCents, 375);
    expect(actual.asset, imagePaths.last);
    expect(actual.category, 2);
    expect(actual.location, 'Minsk');
    expect(actual.description, 'Persisted description');
    expect(actual.rating, 4.7);
    expect(actual.reviewCount, 31);
    expect(repo!.currentUser.id, 'admin');
    expect(repo!.isFavorite(actual.id), isTrue);
  });

  test('repository rejects all product mutations for normal users', () async {
    await expectLater(
      repo!.saveProduct(product(), creating: true),
      throwsStateError,
    );
    await expectLater(
      repo!.saveProduct(repo!.products.first, creating: false),
      throwsStateError,
    );
    await expectLater(repo!.deleteProduct('coffee-0'), throwsStateError);
    expect(repo!.products.length, 7);
    // A rejected write must not poison the serialized queue.
    await repo!.signIn('admin', 'admin123');
    await repo!.saveProduct(product(), creating: true);
    await repo!.saveProduct(
      product(name: 'Changed', price: 499),
      creating: false,
    );
    expect(repo!.product('test-product')!.priceCents, 499);
    await repo!.deleteProduct('test-product');
    expect(repo!.product('test-product'), isNull);
  });

  test(
    'favorites are unique, isolated, removable and cleaned on deletion',
    () async {
      await repo!.setFavorite('coffee-0', true);
      await repo!.setFavorite('coffee-0', true);
      expect(repo!.favorites.length, 1);
      await repo!.signIn('daniel', 'daniel123');
      expect(repo!.favorites, isEmpty);
      await repo!.setFavorite('coffee-0', true);
      await repo!.setFavorite('coffee-0', false);
      expect(repo!.favorites, isEmpty);
      await repo!.signIn('admin', 'admin123');
      await repo!.deleteProduct('coffee-0');
      await repo!.signIn('emma', 'emma123');
      expect(repo!.favorites, isEmpty);
      expect(Hive.box<Favorite>('favorites').length, 0);
    },
  );

  test(
    'empty catalog stays empty across restart; orphan favorites are removed',
    () async {
      await repo!.signIn('admin', 'admin123');
      for (final p in repo!.products) {
        await repo!.deleteProduct(p.id);
      }
      await Hive.box<Favorite>('favorites')
          .put('user-1:missing', const Favorite('user-1', 'missing'));
      await repo!.close();
      repo = null;
      repo = await CoffeeRepository.open(directory);
      expect(repo!.products, isEmpty);
      expect(Hive.box<Favorite>('favorites').length, 0);
    },
  );

  test(
    'actual wrong key failure and correct read preserve working data',
    () async {
      final before = repo!.products.map((p) => p.id).toList();
      final result = await repo!.demonstrateKeys();
      expect(result, contains('Wrong key rejected:'));
      expect(result, contains('HiveError'));
      expect(result, contains('Coffee Lab 8: encrypted control record'));
      expect(repo!.products.map((p) => p.id).toList(), before);
      final bytes = await File('${directory.path}/products.hive').readAsBytes();
      expect(String.fromCharCodes(bytes), isNot(contains('Espresso')));
      expect(await File('${directory.path}/hive.key').length(), 32);
    },
  );

  test(
    'compaction reduces demo size and preserves all current fields',
    () async {
      await repo!.setFavorite('coffee-0', true);
      final report = await repo!.demonstrateCompaction();
      expect(report, contains('1 current record preserved.'));
      expect(await repo!.compactBoxes(), contains('products:'));
      await repo!.close();
      repo = null;
      repo = await CoffeeRepository.open(directory);
      expect(repo!.products.length, 7);
      expect(repo!.isFavorite('coffee-0'), isTrue);
      expect(repo!.product('coffee-0')!.description, contains('Espresso'));
    },
  );

  for (final mode in ['missing', 'short', 'wrong']) {
    test('$mode key fails without changing database bytes', () async {
      await repo!.close();
      repo = null;
      final dbFile = File('${directory.path}/products.hive');
      final before = await dbFile.readAsBytes();
      final keyFile = File('${directory.path}/hive.key');
      final correctKey = await keyFile.readAsBytes();
      if (mode == 'missing') {
        await keyFile.delete();
      } else {
        await keyFile.writeAsBytes(
          mode == 'short' ? [1, 2] : Hive.generateSecureKey(),
        );
      }
      await expectLater(CoffeeRepository.open(directory), throwsA(anything));
      expect(await dbFile.readAsBytes(), before);
      if (mode == 'missing') expect(await keyFile.exists(), isFalse);
      await keyFile.writeAsBytes(correctKey);
      repo = await CoffeeRepository.open(directory);
      expect(repo!.products.length, 7);
    });
  }

  test('invalid product cannot be written through the repository', () async {
    await repo!.signIn('admin', 'admin123');
    await expectLater(
      repo!.saveProduct(product(price: 0), creating: true),
      throwsArgumentError,
    );
    expect(repo!.products.length, 7);
  });
}
