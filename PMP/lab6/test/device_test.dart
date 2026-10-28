import 'package:coffee_lab6/coffee_app.dart';
import 'package:coffee_lab6/device_screen.dart';
import 'package:coffee_lab6/device_services.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:image_picker/image_picker.dart';

import 'navigation_test.dart' show press;

class FakeDevices extends DeviceServices {
  int? hour;
  int? minute;
  String? label;
  bool fail = false;
  bool ios = false;
  String alarmErrorCode = 'NO_ALARM_APP';
  bool cameraFail = false;
  XFile? photo;
  @override
  bool get usesAlarmKit => ios;
  @override
  Future<String> manufacturer() async => 'Test manufacturer';
  @override
  Future<BatteryReading> battery() async =>
      const BatteryReading(73, BatteryState.charging);
  @override
  Future<void> openAlarm(int h, int m, String l) async {
    if (fail) {
      throw PlatformException(
        code: alarmErrorCode,
        message: 'Будильник недоступен',
      );
    }
    hour = h;
    minute = m;
    label = l;
  }

  @override
  Future<XFile?> takePhoto() async {
    if (cameraFail) {
      throw PlatformException(
        code: 'camera_access_denied',
        message: 'Камера недоступна',
      );
    }
    return photo;
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('battery channel reads percentage and every native state', () async {
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    addTearDown(
      () => messenger.setMockMethodCallHandler(
        DeviceServices.batteryChannel,
        null,
      ),
    );
    for (final state in BatteryState.values) {
      for (final level in [0, 73, 100]) {
        messenger.setMockMethodCallHandler(DeviceServices.batteryChannel, (
          call,
        ) async {
          expect(call.method, 'getBatteryReading');
          expect(call.arguments, isNull);
          return {'level': level, 'state': state.name};
        });
        final reading = await DeviceServices().battery();
        expect(reading.level, level);
        expect(reading.state, state);
      }
    }
    for (final state in [null, 'futureState']) {
      messenger.setMockMethodCallHandler(
        DeviceServices.batteryChannel,
        (_) async => {'level': 42, 'state': state},
      );
      expect((await DeviceServices().battery()).state, BatteryState.unknown);
    }
  });

  test(
    'battery rejects unavailable levels and propagates platform errors',
    () async {
      final messenger =
          TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
      addTearDown(
        () => messenger.setMockMethodCallHandler(
          DeviceServices.batteryChannel,
          null,
        ),
      );
      for (final response in [
        null,
        <String, Object?>{},
        ...[
          -1,
          101,
          73.5,
          '73',
          null,
        ].map((level) => {'level': level, 'state': 'charging'}),
      ]) {
        messenger.setMockMethodCallHandler(
          DeviceServices.batteryChannel,
          (_) async => response,
        );
        await expectLater(
          DeviceServices().battery(),
          throwsA(
            isA<PlatformException>().having(
              (e) => e.code,
              'code',
              'UNAVAILABLE',
            ),
          ),
        );
      }
      messenger.setMockMethodCallHandler(DeviceServices.batteryChannel, (
        _,
      ) async {
        throw PlatformException(code: 'UNAVAILABLE', message: 'No battery');
      });
      await expectLater(
        DeviceServices().battery(),
        throwsA(
          isA<PlatformException>().having(
            (e) => e.message,
            'message',
            'No battery',
          ),
        ),
      );
      messenger.setMockMethodCallHandler(DeviceServices.batteryChannel, null);
      await expectLater(
        DeviceServices().battery(),
        throwsA(isA<MissingPluginException>()),
      );
    },
  );

  testWidgets(
    'recovered photo opens device route and camera errors are visible',
    (tester) async {
      final fake = FakeDevices()..cameraFail = true;
      await tester.pumpWidget(
        CoffeeApp(
          services: fake,
          recoveredPhoto: XFile('assets/icon/coffee_icon.png'),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.byType(DeviceScreen), findsOneWidget);
      await tester.scrollUntilVisible(
        find.byKey(const Key('camera-photo')),
        240,
        scrollable: find.byType(Scrollable).first,
      );
      expect(find.byKey(const Key('camera-photo')), findsOneWidget);
      expect(find.textContaining('Фото восстановлено'), findsOneWidget);
      await press(tester, find.byKey(const Key('camera-button')));
      expect(find.textContaining('camera_access_denied'), findsOneWidget);
      expect(find.byKey(const Key('camera-photo')), findsOneWidget);
      expect(tester.takeException(), isNull);
    },
  );

  test('custom channel names, method names and alarm payload', () async {
    final calls = <MethodCall>[];
    final messenger =
        TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    messenger.setMockMethodCallHandler(DeviceServices.manufacturerChannel, (
      call,
    ) async {
      calls.add(call);
      return 'Google';
    });
    messenger.setMockMethodCallHandler(DeviceServices.alarmChannel, (
      call,
    ) async {
      calls.add(call);
      return true;
    });
    addTearDown(() {
      messenger.setMockMethodCallHandler(
        DeviceServices.manufacturerChannel,
        null,
      );
      messenger.setMockMethodCallHandler(DeviceServices.alarmChannel, null);
    });
    final service = DeviceServices();
    expect(await service.manufacturer(), 'Google');
    await service.openAlarm(9, 42, 'Morning coffee');
    expect(calls[0].method, 'getManufacturer');
    expect(calls[1].method, 'openAlarm');
    expect(calls[1].arguments, {
      'hour': 9,
      'minute': 42,
      'label': 'Morning coffee',
    });
    expect(() => service.openAlarm(24, 0, ''), throwsArgumentError);
    expect(() => service.openAlarm(9, 42, 'x' * 81), throwsArgumentError);
  });

  test(
    'alarm requires an explicit native success and propagates iOS errors',
    () async {
      final messenger =
          TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
      addTearDown(
        () => messenger.setMockMethodCallHandler(
          DeviceServices.alarmChannel,
          null,
        ),
      );
      final service = DeviceServices();
      for (final response in [false, null]) {
        messenger.setMockMethodCallHandler(
          DeviceServices.alarmChannel,
          (_) async => response,
        );
        await expectLater(
          service.openAlarm(9, 42, 'Coffee'),
          throwsA(
            isA<PlatformException>().having(
              (e) => e.code,
              'code',
              'ALARM_NOT_ACCEPTED',
            ),
          ),
        );
      }
      for (final code in [
        'ALARM_PERMISSION',
        'UNSUPPORTED_IOS_VERSION',
        'ALARM_SCHEDULE_FAILED',
      ]) {
        messenger.setMockMethodCallHandler(DeviceServices.alarmChannel, (
          _,
        ) async {
          throw PlatformException(code: code, message: 'iOS alarm unavailable');
        });
        await expectLater(
          service.openAlarm(9, 42, 'Coffee'),
          throwsA(isA<PlatformException>().having((e) => e.code, 'code', code)),
        );
      }
    },
  );

  testWidgets(
    'iOS alarm UI reports scheduling, permission denial and old iOS',
    (tester) async {
      final fake = FakeDevices()..ios = true;
      await tester.pumpWidget(CoffeeApp(services: fake));
      await tester.pumpAndSettle();
      await press(tester, find.text('Setting'));
      await press(tester, find.byKey(const Key('alarm-button')));
      expect(find.text('Установить будильник'), findsOneWidget);
      expect(
        find.textContaining('Будильник приложения установлен: 07:30'),
        findsOneWidget,
      );
      expect(find.textContaining('Часы открыты:'), findsNothing);
      for (final code in [
        'ALARM_PERMISSION',
        'UNSUPPORTED_IOS_VERSION',
        'ALARM_SCHEDULE_FAILED',
      ]) {
        fake.fail = true;
        fake.alarmErrorCode = code;
        await press(tester, find.byKey(const Key('alarm-button')));
        expect(find.textContaining(code), findsOneWidget);
        expect(
          find.textContaining('Будильник приложения установлен:'),
          findsNothing,
        );
      }
      expect(tester.takeException(), isNull);
    },
  );

  for (final size in [const Size(320, 640), const Size(393, 873)]) {
    testWidgets(
      'device results, validation, edited alarm, keyboard, cancellation and errors $size',
      (tester) async {
        await tester.binding.setSurfaceSize(size);
        addTearDown(() => tester.binding.setSurfaceSize(null));
        final fake = FakeDevices();
        await tester.pumpWidget(CoffeeApp(services: fake));
        await tester.pumpAndSettle();
        await press(tester, find.text('Setting'));
        expect(find.byType(DeviceScreen), findsOneWidget);
        await press(tester, find.byKey(const Key('manufacturer-button')));
        expect(find.text('Test manufacturer'), findsOneWidget);
        await press(tester, find.byKey(const Key('battery-button')));
        expect(find.text('73% заряжается'), findsOneWidget);
        await tester.ensureVisible(find.byKey(const Key('alarm-hour')));
        await tester.enterText(find.byKey(const Key('alarm-hour')), '24');
        await tester.enterText(find.byKey(const Key('alarm-minute')), '60');
        await press(tester, find.byKey(const Key('alarm-button')));
        expect(find.text('Введите число 0–23'), findsOneWidget);
        expect(find.text('Введите число 0–59'), findsOneWidget);
        expect(fake.hour, isNull);
        await tester.enterText(find.byKey(const Key('alarm-hour')), '9');
        await tester.enterText(find.byKey(const Key('alarm-minute')), '42');
        await tester.enterText(
          find.byKey(const Key('alarm-label')),
          'Morning coffee',
        );
        tester.view.viewInsets = const FakeViewPadding(bottom: 240);
        await tester.pumpAndSettle();
        expect(tester.takeException(), isNull);
        await press(tester, find.byKey(const Key('alarm-button')));
        tester.view.resetViewInsets();
        await tester.pumpAndSettle();
        expect([fake.hour, fake.minute, fake.label], [9, 42, 'Morning coffee']);
        expect(find.textContaining('09:42 · Morning coffee'), findsOneWidget);
        fake.fail = true;
        await press(tester, find.byKey(const Key('alarm-button')));
        expect(find.textContaining('NO_ALARM_APP'), findsOneWidget);
        await press(tester, find.byKey(const Key('camera-button')));
        expect(find.textContaining('Съёмка отменена'), findsOneWidget);
        expect(tester.takeException(), isNull);
        addTearDown(tester.view.resetViewInsets);
      },
    );
  }
}
