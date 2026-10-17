import 'package:coffee_lab6/coffee_app.dart';
import 'package:coffee_lab6/device_services.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();
  testWidgets('real Android channels and complete navigation', (tester) async {
    final services = DeviceServices();
    final manufacturer = await services.manufacturer();
    expect(manufacturer.trim(), isNotEmpty);
    final battery = await services.battery();
    expect(battery.level, inInclusiveRange(0, 100));
    await expectLater(
      DeviceServices.alarmChannel.invokeMethod<bool>('openAlarm', {
        'hour': 24,
        'minute': 0,
        'label': 'Invalid',
      }),
      throwsA(
        isA<PlatformException>().having(
          (e) => e.code,
          'code',
          'INVALID_ARGUMENT',
        ),
      ),
    );
    await expectLater(
      DeviceServices.manufacturerChannel.invokeMethod<void>('unknownMethod'),
      throwsA(isA<MissingPluginException>()),
    );
    await tester.pumpWidget(CoffeeApp(services: services));
    await tester.pumpAndSettle();
    Future<void> press(Finder finder) async {
      if (finder.evaluate().isEmpty) {
        await tester.scrollUntilVisible(
          finder,
          180,
          scrollable: find.byType(Scrollable).first,
        );
      }
      await tester.ensureVisible(finder);
      await tester.pumpAndSettle();
      await tester.tap(finder);
      await tester.pumpAndSettle();
    }

    for (var i = 0; i < 3; i++) {
      if (i > 0) await press(find.byKey(const Key('offer-next')));
      final coffee = [coffees[0], coffees[1], coffees[5]][i];
      await press(find.byKey(Key('offer-order-${coffee.name}')));
      expect(find.text(coffee.description), findsOneWidget);
      await press(find.byTooltip('Back'));
    }
    await press(find.byKey(const Key('product-Espresso')));
    await press(find.byTooltip('Increase quantity'));
    await press(find.byKey(const Key('size-Small')));
    await press(find.byKey(const Key('preview-order')));
    expect(find.textContaining('Total: USD 3.00'), findsOneWidget);
    await press(find.byKey(const Key('preview-back')));
    await press(find.byKey(const Key('add-to-cart')));
    tester
        .state<ScrollableState>(find.byType(Scrollable).first)
        .position
        .jumpTo(0);
    await tester.pumpAndSettle();
    expect(find.textContaining('2 × Espresso · Small'), findsOneWidget);
    await press(find.byKey(const Key('product-Espresso')));
    await press(find.byKey(const Key('preview-order')));
    await press(find.byKey(const Key('return-catalog')));
    expect(find.byType(CoffeeDetailsScreen), findsNothing);
    await press(find.text('Setting'));
    await press(find.byKey(const Key('manufacturer-button')));
    expect(find.text(manufacturer), findsOneWidget);
    await press(find.byKey(const Key('battery-button')));
    expect(find.textContaining('${battery.level}%'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
