import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:coffee_lab8/coffee_app.dart';
import 'package:coffee_lab8/coffee_repository.dart';
import 'package:coffee_lab8/management_screens.dart';

void main() {
  late Directory directory;
  late CoffeeRepository repo;
  setUp(() async {
    directory = await Directory.systemTemp.createTemp('coffee_lab8_widget_');
    repo = await CoffeeRepository.open(directory);
    await repo.signIn('emma', 'emma123');
  });
  tearDown(() async {
    await repo.close();
    await directory.delete(recursive: true);
  });

  for (final size in [const Size(320, 640), const Size(393, 873)]) {
    testWidgets('catalog, details and favorites at $size', (tester) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      await tester.pumpWidget(CoffeeApp(repository: repo));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('add-product')), findsNothing);
      await tester.scrollUntilVisible(
        find.byKey(const Key('product-Espresso')),
        200,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.ensureVisible(find.byKey(const Key('product-Espresso')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('product-Espresso')));
      await tester.pumpAndSettle();
      expect(find.text('(200 Reviews)'), findsOneWidget);
      expect(find.byKey(const Key('product-location')), findsOneWidget);
      expect(find.byKey(const Key('product-description')), findsOneWidget);
      await tester.runAsync(() async {
        await tester.tap(find.byTooltip('Favorite'));
        await Future<void>.delayed(const Duration(milliseconds: 100));
      });
      await tester.pumpAndSettle();
      expect(repo.isFavorite('coffee-0'), isTrue);
      await tester.tap(find.byTooltip('Back'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Favorites'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('favorite-coffee-0')), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }

  testWidgets('admin form validates and saves with keyboard at narrow width', (
    tester,
  ) async {
    await tester.runAsync(() => repo.signIn('admin', 'admin123'));
    tester.view.physicalSize = const Size(320, 640);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await tester.pumpWidget(CoffeeApp(repository: repo));
    await tester.tap(find.byKey(const Key('add-product')));
    await tester.pumpAndSettle();
    expect(find.byType(ProductFormScreen), findsOneWidget);
    final save = find.byKey(const Key('save-product'));
    await tester.ensureVisible(save);
    await tester.pumpAndSettle();
    await tester.tap(save);
    await tester.pumpAndSettle();
    final formState = tester.state<FormState>(find.byType(Form));
    expect(formState.validate(), isFalse);
    for (final entry in {
      'Name': 'New coffee',
      'Price (USD)': '3,75',
      'Location': 'Minsk',
      'Description': 'A stored description',
    }.entries) {
      final field = find.byKey(Key('field-${entry.key}'));
      await tester.ensureVisible(field);
      await tester.enterText(field, entry.value);
    }
    tester.view.viewInsets = const FakeViewPadding(bottom: 220);
    await tester.pumpAndSettle();
    await tester.ensureVisible(save);
    await tester.runAsync(() async {
      await tester.tap(save);
      await Future<void>.delayed(const Duration(milliseconds: 100));
    });
    tester.view.resetViewInsets();
    await tester.pumpAndSettle();
    expect(
      repo.products.where((p) => p.name == 'New coffee').single.priceCents,
      375,
    );
    expect(find.byType(ProductFormScreen), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
  });
}
