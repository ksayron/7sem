import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:path_provider/path_provider.dart';

import 'coffee_app.dart';
import 'coffee_repository.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  await SystemChrome.setPreferredOrientations([DeviceOrientation.portraitUp]);
  try {
    final root = await getApplicationSupportDirectory();
    final repository = await CoffeeRepository.open(
      Directory('${root.path}/coffee_lab8'),
    );
    runApp(CoffeeApp(repository: repository));
  } catch (error) {
    runApp(
      MaterialApp(
        theme: ThemeData.dark(),
        home: Scaffold(
          appBar: AppBar(title: const Text('Hive could not open')),
          body: Padding(
            padding: const EdgeInsets.all(24),
            child: SelectableText(
              '$error\n\nExisting files were preserved. Restore the original 32-byte '
              'hive.key file before restarting. No replacement key was generated for an existing database.',
            ),
          ),
        ),
      ),
    );
  }
}
