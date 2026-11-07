import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:path_provider/path_provider.dart';
import 'package:coffee_lab8/coffee_app.dart';
import 'package:coffee_lab8/coffee_repository.dart';

void main() {
  final binding = IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  testWidgets('Android: roles, CRUD, favorites, reopen, keys and compaction', (
    tester,
  ) async {
    final support = await getApplicationSupportDirectory();
    final directory = Directory(
      '${support.path}/integration_lab8_${DateTime.now().microsecondsSinceEpoch}',
    );
    var repo = await CoffeeRepository.open(directory);
    try {
      await tester.pumpWidget(CoffeeApp(repository: repo));
      await tester.pumpAndSettle();
      await binding.convertFlutterSurfaceToImage();
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('login-username')), findsOneWidget);

      Future<void> show(Finder finder) async {
        if (finder.evaluate().isEmpty) {
          await tester.scrollUntilVisible(
            finder,
            -180,
            scrollable: find.byType(Scrollable).first,
          );
        }
        await Scrollable.ensureVisible(tester.element(finder), alignment: 0.15);
        await tester.pumpAndSettle();
      }

      Future<void> tap(Finder finder) async {
        await show(finder);
        await tester.tap(finder);
        await tester.pumpAndSettle();
      }

      Future<void> waitFor(bool Function() condition) async {
        for (var i = 0; i < 100 && !condition(); i++) {
          await tester.pump(const Duration(milliseconds: 100));
        }
        expect(condition(), isTrue);
        await tester.pumpAndSettle();
      }

      Future<void> screenshot(String name) async {
        await tester.pumpAndSettle();
        await binding.takeScreenshot(name);
      }

      Future<void> switchUser(String id, String label) async {
        if (repo.isSignedIn) {
          await tap(find.text('Profile'));
          await screenshot('auth-profile');
          await tap(find.byKey(const Key('sign-out')));
          await screenshot('auth-sign-out');
          await tap(find.byKey(const Key('confirm-sign-out')));
          await waitFor(() => !repo.isSignedIn);
        }
        final login = {
          'admin': 'admin',
          'user-1': 'emma',
          'user-2': 'daniel',
        }[id]!;
        await tester.enterText(find.byKey(const Key('login-username')), login);
        await tester.enterText(
          find.byKey(const Key('login-password')),
          '${login}123',
        );
        await tap(find.byKey(const Key('sign-in')));
        await waitFor(() => repo.currentUser.id == id);
      }

      Future<void> enter(String label, String value) async {
        final field = find.byKey(Key('field-$label'));
        await show(field);
        await tester.enterText(field, value);
        await tester.pumpAndSettle();
      }

      await screenshot('auth-login');
      await tester.enterText(find.byKey(const Key('login-username')), 'emma');
      await tester.enterText(find.byKey(const Key('login-password')), 'wrong');
      await tap(find.byKey(const Key('sign-in')));
      await waitFor(
        () => find.byKey(const Key('login-error')).evaluate().isNotEmpty,
      );
      await screenshot('auth-login-error');
      await switchUser('user-1', 'Emma · user');
      expect(find.byKey(const Key('add-product')), findsNothing);
      await screenshot('catalog-user');
      await show(find.byKey(const Key('product-Espresso')));
      await screenshot('catalog-products');
      await switchUser('admin', 'Alex · admin');
      await tap(find.byKey(const Key('add-product')));
      await enter('Name', 'Android test coffee');
      await enter('Price (USD)', '5.25');
      await enter('Location', 'Minsk, BY');
      await enter(
        'Description',
        'Created and read from encrypted Hive on Android.',
      );
      await enter('Rating', '4.8');
      await enter('Review count', '42');
      await show(find.byKey(const Key('field-Name')));
      await screenshot('add-product');
      await tap(find.byKey(const Key('save-product')));
      await waitFor(
        () => repo.products.any((p) => p.name == 'Android test coffee'),
      );
      var product = repo.products.singleWhere(
        (p) => p.name == 'Android test coffee',
      );

      // Search makes the new item visible regardless of its category.
      final search = find.byKey(const Key('coffee-search'));
      await show(search);
      await tester.enterText(search, 'Android');
      await tester.pumpAndSettle();
      await tap(find.byKey(const Key('product-Android test coffee')));
      await show(find.byKey(const Key('product-description')));
      expect(find.text('Minsk, BY'), findsOneWidget);
      expect(find.text('(42 Reviews)'), findsOneWidget);
      await screenshot('details-admin');
      await tap(find.byKey(const Key('edit-product')));
      await enter('Price (USD)', '6.75');
      await enter('Description', 'Edited on Android and persisted in Hive.');
      await screenshot('edit-product');
      await tap(find.byKey(const Key('save-product')));
      await waitFor(() => repo.product(product.id)!.priceCents == 675);
      await tap(find.byTooltip('Back'));

      await switchUser('user-1', 'Emma · user');
      await tap(find.byKey(const Key('product-Android test coffee')));
      expect(find.byKey(const Key('edit-product')), findsNothing);
      await tap(find.byTooltip('Favorite'));
      await waitFor(() => repo.isFavorite(product.id));
      await tap(find.byTooltip('Back'));
      await tap(find.text('Favorites'));
      expect(find.text('Android test coffee'), findsOneWidget);
      await screenshot('favorites');
      await tester.pageBack();
      await tester.pumpAndSettle();
      await switchUser('user-2', 'Daniel · user');
      await tap(find.text('Favorites'));
      expect(
        find.text('No favorites yet. Tap a heart in the catalog.'),
        findsOneWidget,
      );
      await tester.pageBack();
      await tester.pumpAndSettle();
      await expectLater(repo.deleteProduct(product.id), throwsStateError);
      await expectLater(
        repo.saveProduct(product, creating: false),
        throwsStateError,
      );
      await expectLater(
        repo.saveProduct(product, creating: true),
        throwsStateError,
      );

      await switchUser('user-1', 'Emma · user');
      await tester.pumpWidget(const SizedBox.shrink());
      await repo.close();
      repo = await CoffeeRepository.open(directory);
      expect(repo.currentUser.id, 'user-1');
      expect(repo.isFavorite(product.id), isTrue);
      expect(repo.product(product.id)!.priceCents, 675);
      expect(
        repo.product(product.id)!.description,
        'Edited on Android and persisted in Hive.',
      );
      await tester.pumpWidget(CoffeeApp(repository: repo));
      await tester.pumpAndSettle();
      await tap(find.text('Hive'));
      await tap(find.byKey(const Key('demo-keys')));
      String result() => tester
          .widget<SelectableText>(find.byKey(const Key('maintenance-result')))
          .data!;
      await waitFor(() => result().contains('Correct key read:'));
      final keyResult = result();
      await show(find.byKey(const Key('maintenance-result')));
      await screenshot('wrong-key');
      await tap(find.byKey(const Key('compact-boxes')));
      await waitFor(() => result().contains('settings:'));
      final compactResult = result();
      await tap(find.byKey(const Key('demo-compaction')));
      await waitFor(() => result().contains('1 current record preserved.'));
      final demoResult = result();
      await show(find.byKey(const Key('maintenance-result')));
      await screenshot('compaction');
      await tester.pageBack();
      await tester.pumpAndSettle();

      await switchUser('admin', 'Alex · admin');
      await show(search);
      await tester.enterText(search, 'Android');
      await tester.pumpAndSettle();
      await tap(find.byKey(const Key('product-Android test coffee')));
      await tap(find.byKey(const Key('delete-product')));
      await tester.tap(find.text('Delete').last);
      await waitFor(() => repo.product(product.id) == null);
      await repo.signIn('emma', 'emma123');
      expect(repo.favorites, isEmpty);
      await repo.signIn('admin', 'admin123');
      for (final p in repo.products) {
        await repo.deleteProduct(p.id);
      }
      await tester.pumpWidget(const SizedBox.shrink());
      await tester.pumpAndSettle();
      await repo.close();
      repo = await CoffeeRepository.open(directory);
      expect(repo.products, isEmpty);
      binding.reportData = {
        ...?binding.reportData,
        'keyResult': keyResult,
        'compactResult': compactResult,
        'demoResult': demoResult,
        'verified': 'Android UI CRUD, role guards, favorite isolation, persisted reopen, empty catalog restart',
      };
      expect(tester.takeException(), isNull);
    } finally {
      await tester.pumpWidget(const SizedBox.shrink());
      await repo.close();
      await directory.delete(recursive: true);
    }
  });
}
