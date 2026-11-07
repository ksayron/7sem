import 'dart:convert';
import 'dart:io';

import 'package:integration_test/integration_test_driver_extended.dart';

Future<void> main() => integrationDriver(
  onScreenshot: (name, bytes, [args]) async {
    final file = File('docs/screenshots/$name.png');
    await file.parent.create(recursive: true);
    await file.writeAsBytes(bytes);
    return true;
  },
  responseDataCallback: (data) async {
    // Screenshot bytes are already written above; keep the report readable.
    final report = Map<String, dynamic>.from(data ?? {})..remove('screenshots');
    await File('docs/android-results.json')
        .writeAsString(const JsonEncoder.withIndent('  ').convert(report));
  },
);
