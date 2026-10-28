import 'package:coffee_lab6/device_screen.dart';
import 'package:coffee_lab6/device_services.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('native battery channel and refresh display', (tester) async {
    final services = DeviceServices();
    await expectLater(
      DeviceServices.batteryChannel.invokeMethod<void>('unknownMethod'),
      throwsA(isA<MissingPluginException>()),
    );
    const expectedLevel = int.fromEnvironment(
      'BATTERY_LEVEL',
      defaultValue: -1,
    );
    const expectedState = String.fromEnvironment('BATTERY_STATE');
    final reading = await services.battery();
    expect(reading.level, inInclusiveRange(0, 100));
    if (expectedLevel >= 0) expect(reading.level, expectedLevel);
    if (expectedState.isNotEmpty) expect(reading.state.name, expectedState);
    final stateLabel = switch (reading.state) {
      BatteryState.charging => 'заряжается',
      BatteryState.discharging => 'разряжается',
      BatteryState.full => 'полностью заряжена',
      BatteryState.connectedNotCharging => 'подключена, не заряжается',
      BatteryState.unknown => 'состояние неизвестно',
    };

    await tester.pumpWidget(
      MaterialApp(home: DeviceScreen(services: services)),
    );
    await tester.pumpAndSettle();
    for (var i = 0; i < 2; i++) {
      await tester.tap(find.byKey(const Key('battery-button')));
      await tester.pumpAndSettle();
      expect(find.text('${reading.level}% $stateLabel'), findsOneWidget);
      expect(tester.takeException(), isNull);
    }
  });
}
