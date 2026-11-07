import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:coffee_lab8/auth_screens.dart';
import 'package:coffee_lab8/coffee_app.dart';
import 'package:coffee_lab8/coffee_repository.dart';

void main() {
  late Directory directory;
  late CoffeeRepository repo;
  setUp(() async {
    directory = await Directory.systemTemp.createTemp('coffee_lab8_auth_');
    repo = await CoffeeRepository.open(directory);
  });
  tearDown(() async {
    await repo.close();
    await directory.delete(recursive: true);
  });

  Future<void> tap(WidgetTester tester, Finder finder) async {
    if (finder.evaluate().isEmpty) {
      await tester.scrollUntilVisible(
        finder,
        200,
        scrollable: find.byType(Scrollable).first,
      );
    }
    await tester.ensureVisible(finder);
    await tester.pumpAndSettle();
    await tester.runAsync(() => tester.tap(finder));
    await tester.pumpAndSettle();
  }

  Future<void> submit(WidgetTester tester) async {
    final button = find.byKey(const Key('sign-in'));
    await tester.ensureVisible(button);
    await tester.pumpAndSettle();
    await tester.runAsync(() async {
      await tester.tap(button);
      await Future<void>.delayed(const Duration(milliseconds: 100));
    });
    await tester.pumpAndSettle();
  }

  testWidgets(
    'login validates, reveals password and rejects invalid credentials',
    (tester) async {
      await tester.pumpWidget(CoffeeApp(repository: repo));
      await submit(tester);
      expect(find.text('Enter your username.'), findsOneWidget);
      expect(find.text('Enter your password.'), findsOneWidget);
      await tester.enterText(find.byKey(const Key('login-username')), 'emma');
      await tester.enterText(find.byKey(const Key('login-password')), 'wrong');
      await tap(tester, find.byTooltip('Show password'));
      expect(
        tester
            .widget<TextFormField>(find.byKey(const Key('login-password')))
            .controller!
            .text,
        'wrong',
      );
      expect(find.byTooltip('Hide password'), findsOneWidget);
      await submit(tester);
      expect(find.text('Incorrect username or password.'), findsOneWidget);
      expect(repo.isSignedIn, isFalse);
      expect(find.byType(CatalogScreen), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    },
  );

  testWidgets('profile cancels logout and clears all previous routes', (
    tester,
  ) async {
    await tester.pumpWidget(CoffeeApp(repository: repo));
    await tester.enterText(find.byKey(const Key('login-username')), 'admin');
    await tester.enterText(find.byKey(const Key('login-password')), 'admin123');
    await submit(tester);
    expect(find.byKey(const Key('add-product')), findsOneWidget);
    await tap(tester, find.text('Profile'));
    expect(find.text('Alex'), findsOneWidget);
    expect(find.text('Role: admin'), findsOneWidget);
    await tap(tester, find.byKey(const Key('sign-out')));
    await tap(tester, find.byKey(const Key('cancel-sign-out')));
    expect(repo.isSignedIn, isTrue);
    expect(find.byType(ProfileScreen), findsOneWidget);
    await tap(tester, find.byKey(const Key('sign-out')));
    await tester.tap(find.byKey(const Key('confirm-sign-out')));
    await tester.pump(const Duration(milliseconds: 300));
    await tester.runAsync(() async {
      await Future<void>.delayed(const Duration(milliseconds: 100));
    });
    await tester.pumpAndSettle();
    expect(find.byType(LoginScreen), findsOneWidget);
    expect(
      Navigator.of(tester.element(find.byType(LoginScreen))).canPop(),
      isFalse,
    );
    expect(find.byType(ProfileScreen), findsNothing);
    expect(find.byType(CatalogScreen), findsNothing);
    expect(
      tester
          .widget<TextFormField>(find.byKey(const Key('login-password')))
          .controller!
          .text,
      isEmpty,
    );
    expect(find.byKey(const Key('add-product')), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox.shrink());
  });

  for (final size in [const Size(375, 667), const Size(667, 375)]) {
    testWidgets('auth layout with keyboard and large text at $size', (
      tester,
    ) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      tester.view.viewInsets = const FakeViewPadding(bottom: 180);
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetViewInsets);
      await tester.pumpWidget(CoffeeApp(repository: repo));
      final context = tester.element(find.byType(LoginScreen));
      final dispatcher = tester.binding.platformDispatcher;
      dispatcher.textScaleFactorTestValue = 2;
      dispatcher.accessibilityFeaturesTestValue =
          const FakeAccessibilityFeatures(disableAnimations: true);
      addTearDown(dispatcher.clearTextScaleFactorTestValue);
      addTearDown(dispatcher.clearAccessibilityFeaturesTestValue);
      await tester.pumpAndSettle();
      expect(MediaQuery.textScalerOf(context).scale(16), 32);
      await tester.enterText(find.byKey(const Key('login-username')), 'emma');
      await tester.enterText(find.byKey(const Key('login-password')), 'wrong');
      await submit(tester);
      expect(find.byKey(const Key('login-error')), findsOneWidget);
      tester.view.resetViewInsets();
      await tester.pumpWidget(const SizedBox.shrink());
      await tester.runAsync(() => repo.signIn('emma', 'emma123'));
      await tester.pumpWidget(
        MaterialApp(
          theme: ThemeData.dark(),
          home: MediaQuery(
            data: MediaQueryData(
              size: size,
              textScaler: const TextScaler.linear(2),
            ),
            child: ProfileScreen(repository: repo),
          ),
        ),
      );
      await tester.pumpAndSettle();
      await tap(tester, find.byKey(const Key('sign-out')));
      expect(find.text('Sign out?'), findsOneWidget);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox.shrink());
    });
  }
}
