import 'package:battery_plus/battery_plus.dart';
import 'package:flutter/services.dart';
import 'package:image_picker/image_picker.dart';

/// Dart-facing boundary, also injectable for widget tests.
class DeviceServices {
  final Battery _battery = Battery();
  final ImagePicker _picker = ImagePicker();
  static const manufacturerChannel = MethodChannel('by.bstu.lab6/device');
  static const alarmChannel = MethodChannel('by.bstu.lab6/alarm');

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
    if (hour < 0 || hour > 23 || minute < 0 || minute > 59) {
      throw ArgumentError('Некорректное время');
    }
    await alarmChannel.invokeMethod<bool>('openAlarm', {
      'hour': hour,
      'minute': minute,
      'label': label,
    });
  }

  Future<BatteryReading> battery() async =>
      BatteryReading(await _battery.batteryLevel, await _battery.batteryState);

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

class BatteryReading {
  const BatteryReading(this.level, this.state);
  final int level;
  final BatteryState state;
}
