import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';
import 'package:sports_lab7/main.dart' as app;

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('SQLite screens and real path_provider file round trip', (
    tester,
  ) async {
    app.main();
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('players-list')), findsOneWidget);

    await tester.tap(find.byKey(const Key('add-player')));
    await tester.pumpAndSettle();
    final playerName =
        'Тестовый игрок ${DateTime.now().millisecondsSinceEpoch}';
    await tester.enterText(find.byKey(const Key('name-field')), playerName);
    await tester.enterText(find.byType(TextFormField).at(2), 'Вратарь');
    await tester.enterText(find.byType(TextFormField).at(3), 'Неман');
    await tester.tap(find.byKey(const Key('save-player')));
    await tester.pumpAndSettle();
    // The new item may be below the viewport in the lazily built list.
    await tester.enterText(find.byKey(const Key('player-search')), playerName);
    await tester.pumpAndSettle();
    final playerTile = find.descendant(
      of: find.byKey(const Key('players-list')),
      matching: find.text(playerName),
    );
    expect(playerTile, findsOneWidget);
    FocusManager.instance.primaryFocus?.unfocus();
    await tester.pumpAndSettle();

    await tester.tap(find.text('Файлы'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('write-temporary')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('path-temporary')), findsOneWidget);
    await tester.ensureVisible(find.byKey(const Key('read-temporary')));
    await tester.tap(find.byKey(const Key('read-temporary')));
    await tester.pumpAndSettle();
    expect(find.byKey(const Key('content-temporary')), findsOneWidget);

    await tester.tap(find.text('Игроки'));
    await tester.pumpAndSettle();
    await tester.tap(playerTile);
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('delete-player')));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Удалить'));
    await tester.pumpAndSettle();
    expect(playerTile, findsNothing);
  });
}
