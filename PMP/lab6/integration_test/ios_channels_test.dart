import 'dart:io';

import 'package:coffee_lab6/device_services.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:integration_test/integration_test.dart';

void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets(
    'real iOS manufacturer and alarm validation without creating an alarm',
    (tester) async {
      expect(await DeviceServices().manufacturer(), 'Apple');
      for (final channel in [
        DeviceServices.manufacturerChannel,
        DeviceServices.alarmChannel,
        DeviceServices.batteryChannel,
      ]) {
        await expectLater(
          channel.invokeMethod<void>('unknownMethod'),
          throwsA(isA<MissingPluginException>()),
        );
      }
      for (final arguments in [
        {'hour': 24, 'minute': 0, 'label': 'Invalid'},
        {'hour': true, 'minute': 42, 'label': 'Invalid'},
        {'hour': 9, 'minute': 42, 'label': 'x' * 81},
      ]) {
        await expectLater(
          DeviceServices.alarmChannel.invokeMethod<bool>(
            'openAlarm',
            arguments,
          ),
          throwsA(
            isA<PlatformException>().having(
              (e) => e.code,
              'code',
              'INVALID_ARGUMENT',
            ),
          ),
        );
      }
    },
    skip: !Platform.isIOS,
  );
}
