import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:image_picker/image_picker.dart';

class DeviceServices {
  bool get usesAlarmKit => defaultTargetPlatform == TargetPlatform.iOS;

  final ImagePicker _picker = ImagePicker();
  static const manufacturerChannel = MethodChannel('by.bstu.lab6/device');
  static const alarmChannel = MethodChannel('by.bstu.lab6/alarm');
  static const batteryChannel = MethodChannel('by.bstu.lab6/battery');

  Future<String> manufacturer() async {
    final name = await manufacturerChannel.invokeMethod<String>(
      'getManufacturer',
    );
    if (name == null || name.trim().isEmpty) {
      throw PlatformException(
        code: 'UNAVAILABLE',
        message: 'Производитель недоступен.',
      );
    }
    return name;
  }

  Future<void> openAlarm(int hour, int minute, String label) async {
    if (hour < 0 ||
        hour > 23 ||
        minute < 0 ||
        minute > 59 ||
        label.length > 80) {
      throw ArgumentError('Некорректное время или подпись длиннее 80 символов');
    }
    final accepted = await alarmChannel.invokeMethod<bool>('openAlarm', {
      'hour': hour,
      'minute': minute,
      'label': label,
    });
    if (accepted != true) {
      throw PlatformException(
        code: 'ALARM_NOT_ACCEPTED',
        message: 'Платформа не подтвердила выполнение запроса будильника.',
      );
    }
  }

  Future<BatteryReading> battery() async {
    final reading = await batteryChannel.invokeMapMethod<String, Object?>(
      'getBatteryReading',
    );
    final level = reading?['level'];
    if (level is! int || level < 0 ) {
      throw PlatformException(
        code: 'UNAVAILABLE',
        message: 'Уровень заряда батареи недоступен.',
      );
    }
    final state = BatteryState.values.firstWhere(
      (state) => state.name == reading?['state'],
      orElse: () => BatteryState.unknown,
    );
    return BatteryReading(level, state);
  }

  Future<XFile?> takePhoto() => _picker.pickImage(
    source: ImageSource.camera,
    maxWidth: 1600,
    imageQuality: 85,
  );

  Future<XFile?> recoverPhoto() async {
    final response = await _picker.retrieveLostData();
    if (response.exception != null) throw response.exception!;
    return response.files?.firstOrNull;
  }
}

enum BatteryState { charging, discharging, full, connectedNotCharging, unknown }

class BatteryReading {
  const BatteryReading(this.level, this.state);
  final int level;
  final BatteryState state;
}
