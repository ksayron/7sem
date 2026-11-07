import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';
import 'package:hive/hive.dart';

import 'models.dart';

class SignInException implements Exception {
  const SignInException();

  @override
  String toString() => 'Incorrect username or password.';
}


const _demoAccounts = <String, ({String id, String hash})>{
  'admin': (
    id: 'admin',
    hash: '2351fe1437c5b36782cb70b4d3393caa3194b35f67793a902e47dff2a6b8655a',
  ),
  'emma': (
    id: 'user-1',
    hash: 'de2d885b4190cda02ec0c758dad468ae62a7361683dbe0a9484679663ac65e8f',
  ),
  'daniel': (
    id: 'user-2',
    hash: '6486eea024a624d29281df716dc5b24be6927f864bb12f6af3b8ba2f9404031f',
  ),
};

class CoffeeRepository extends ChangeNotifier {
  CoffeeRepository._(this.directory, this._key);
  final Directory directory;
  final List<int> _key;
  late final Box<AppUser> _users;
  late final Box<CoffeeProduct> _products;
  late final Box<Favorite> _favorites;
  late final Box<dynamic> _settings;
  Future<void> _pending = Future.value();

  static Future<CoffeeRepository> open(Directory directory) async {
    await directory.create(recursive: true);
    final keyFile = File('${directory.path}/hive.key');
    final existingData = directory.listSync().any(
      (f) => f.path.endsWith('.hive'),
    );
    if (!await keyFile.exists()) {
      if (existingData) {
        throw StateError(
          'Encryption key is missing. Existing data was preserved.',
        );
      }
      await keyFile.writeAsBytes(Hive.generateSecureKey(), flush: true);
    }
    final key = await keyFile.readAsBytes();
    if (key.length != 32) {
      throw StateError('Invalid encryption key file: expected 32 bytes.');
    }
    Hive.init(directory.path);
    if (!Hive.isAdapterRegistered(0)) Hive.registerAdapter(AppUserAdapter());
    if (!Hive.isAdapterRegistered(1)) {Hive.registerAdapter(CoffeeProductAdapter());}
    if (!Hive.isAdapterRegistered(2)) Hive.registerAdapter(FavoriteAdapter());
    final repo = CoffeeRepository._(directory, key);
    try {
      final cipher = HiveAesCipher(key);
      repo._users = await _openEncryptedBox<AppUser>(
        'users',
        encryptionCipher: cipher,
        crashRecovery: false,
      );
      repo._products = await _openEncryptedBox<CoffeeProduct>(
        'products',
        encryptionCipher: cipher,
        crashRecovery: false,
      );
      repo._favorites = await _openEncryptedBox<Favorite>(
        'favorites',
        encryptionCipher: cipher,
        crashRecovery: false,
      );
      repo._settings = await _openEncryptedBox(
        'settings',
        encryptionCipher: cipher,
        crashRecovery: false,
      );
      if (repo._settings.get('seeded') != true) {
        for (final user in const [
          AppUser('admin', 'Alex', 'admin'),
          AppUser('user-1', 'Emma', 'user'),
          AppUser('user-2', 'Daniel', 'user'),
        ]) {
          await repo._users.put(user.id, user);
        }
        for (final product in seedProducts()) {
          await repo._products.put(product.id, product);
        }
        await repo._settings.put('seeded', true);
      }
      if (repo._settings.get('authVersion') != 1) {
        await repo._settings.delete('activeUserId');
        await repo._settings.put('authVersion', 1);
      }
      final activeId = repo._settings.get('activeUserId');
      if (activeId != null && !repo._users.containsKey(activeId)) {
        await repo._settings.delete('activeUserId');
      }
      await repo._removeOrphans();
      return repo;
    } catch (_) {
      await Hive.close();
      rethrow;
    }
  }

  List<AppUser> get users => _users.values.toList();
  AppUser? get sessionUser {
    final id = _settings.get('activeUserId');
    return id is String ? _users.get(id) : null;
  }

  bool get isSignedIn => sessionUser != null;
  AppUser get currentUser =>
      sessionUser ?? (throw StateError('Sign in to continue.'));
  List<CoffeeProduct> get products => _products.values.toList();
  CoffeeProduct? product(String id) => _products.get(id);
  bool isFavorite(String id) =>
      isSignedIn && _favorites.containsKey('${currentUser.id}:$id');
  List<CoffeeProduct> get favorites =>
      products.where((p) => isFavorite(p.id)).toList();

  Future<T> _write<T>(Future<T> Function() operation) {
    final result = _pending.then((_) => operation());
    _pending = result.then<void>((_) {}, onError: (Object _, StackTrace _) {});
    return result.then((value) {
      notifyListeners();
      return value;
    });
  }

  Future<void> signIn(String username, String password) => _write(() async {
    final login = username.trim().toLowerCase();
    final account = _demoAccounts[login];
    final digest = sha256.convert(utf8.encode('coffee-lab8:$login:$password'));
    if (account == null ||
        digest.toString() != account.hash ||
        !_users.containsKey(account.id)) {
      throw const SignInException();
    }
    await _settings.put('activeUserId', account.id);
  });

  Future<void> signOut() => _write(() => _settings.delete('activeUserId'));

  void _requireAdmin() {
    if (!currentUser.isAdmin) {
      throw StateError('Only an admin can change products.');
    }
  }

  Future<void> saveProduct(CoffeeProduct value, {required bool creating}) =>
      _write(() async {
        _requireAdmin();
        value.validate();
        if (creating == _products.containsKey(value.id)) {
          throw StateError(
            creating
                ? 'Product ID already exists.'
                : 'Product no longer exists.',
          );
        }
        await _products.put(value.id, value);
      });

  Future<void> deleteProduct(String id) => _write(() async {
    _requireAdmin();
    await _products.delete(id);
    await _removeOrphans();
  });

  Future<void> setFavorite(String productId, bool value) => _write(() async {
    if (!_products.containsKey(productId)) {
      throw StateError('Product no longer exists.');
    }
    final favorite = Favorite(currentUser.id, productId);
    if (value) {
      await _favorites.put(favorite.key, favorite);
    } else {
      await _favorites.delete(favorite.key);
    }
  });

  Future<void> _removeOrphans() async {
    final keys = _favorites
        .toMap()
        .entries
        .where(
          (e) =>
              !_products.containsKey(e.value.productId) ||
              !_users.containsKey(e.value.userId),
        )
        .map((e) => e.key)
        .toList();
    await _favorites.deleteAll(keys);
  }

  Future<String> demonstrateKeys() => _write(() async {
    const name = 'key_demo';
    final control = 'Coffee Lab 8: encrypted control record';
    final box = await _openEncryptedBox<String>(
      name,
      encryptionCipher: HiveAesCipher(_key),
      crashRecovery: false,
    );
    await box.put('control', control);
    await box.close();
    final secondKey = Hive.generateSecureKey();
    String failure;
    try {
      final wrong = await _openEncryptedBox<String>(
        name,
        encryptionCipher: HiveAesCipher(secondKey),
        crashRecovery: false,
      );
      try {
        final result = wrong.get('control');
        throw StateError('Unexpected read with wrong key: $result');
      } finally {
        await wrong.close();
      }
    } on HiveError catch (error) {
      failure = '$error';
    }
    final correct = await _openEncryptedBox<String>(
      name,
      encryptionCipher: HiveAesCipher(_key),
      crashRecovery: false,
    );
    try {
      if (correct.get('control') != control) {
        throw StateError('Control record mismatch.');
      }
      return 'Wrong key rejected:\n$failure\n\nCorrect key read:\n${correct.get('control')}';
    } finally {
      await correct.close();
    }
  });

  Future<String> compactBoxes() => _write(() async {
    final report = <String>[];
    for (final box in <Box>[_users, _products, _favorites, _settings]) {
      await box.flush();
      final file = File(box.path!);
      final before = await file.length();
      await box.compact();
      await box.flush();
      report.add(
        '${box.name}: $before → ${await file.length()} bytes (${box.length} records)',
      );
    }
    return report.join('\n');
  });

  Future<String> demonstrateCompaction() => _write(() async {
    final demo = await _openEncryptedBox<String>(
      'compact_demo',
      encryptionCipher: HiveAesCipher(_key),
      crashRecovery: false,
      compactionStrategy: (_, _) => false,
    );
    try {
      await demo.clear();
      for (var i = 0; i < 100; i++) {
        await demo.put('updated', 'Revision $i ${'coffee ' * 100}');
        await demo.put('deleted-$i', 'Temporary record $i');
      }
      await demo.deleteAll(List.generate(100, (i) => 'deleted-$i'));
      final expected = demo.get('updated');
      await demo.flush();
      final before = await File(demo.path!).length();
      await demo.compact();
      await demo.flush();
      final after = await File(demo.path!).length();
      if (demo.length != 1 ||
          demo.get('updated') != expected ||
          after >= before) {
        throw StateError('Compaction verification failed.');
      }
      return 'Demo box: $before → $after bytes\n100 revisions, 100 deletions.\n1 current record preserved.';
    } finally {
      await demo.close();
    }
  });

  Future<void> close() async {
    await _pending;
    await Hive.close();
    dispose();
  }
}

Future<Box<T>> _openEncryptedBox<T>(
  String name, {
  required HiveCipher encryptionCipher,
  bool crashRecovery = false,
  CompactionStrategy? compactionStrategy,
}) async {
  Future<Box<T>> open() => compactionStrategy == null
      ? Hive.openBox<T>(
          name,
          encryptionCipher: encryptionCipher,
          crashRecovery: crashRecovery,
        )
      : Hive.openBox<T>(
          name,
          encryptionCipher: encryptionCipher,
          crashRecovery: crashRecovery,
          compactionStrategy: compactionStrategy,
        );
  final results = await Future.wait([open(), open()]);
  return results.first;
}
