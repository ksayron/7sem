import 'dart:convert';
import 'dart:io';
import 'dart:isolate';

import 'package:path_provider/path_provider.dart';

import 'footballer.dart';

enum StorageLocation {
  temporary('Temporary'),
  support('Application Support'),
  library('Application Library'),
  documents('Application Documents'),
  cache('Application Cache'),
  external('External Storage'),
  externalCache('External Cache Directories'),
  externalDirectories('External Storage Directories'),
  downloads('Downloads');

  const StorageLocation(this.label);
  final String label;
  List<String> get unsupported => switch (this) {
    StorageLocation.library => ['Android', 'Linux', 'Windows'],
    StorageLocation.external ||
    StorageLocation.externalCache ||
    StorageLocation.externalDirectories => ['iOS', 'macOS', 'Linux', 'Windows'],
    _ => [],
  };
}

String get currentPlatform => Platform.isAndroid
    ? 'Android'
    : Platform.isIOS
    ? 'iOS'
    : Platform.isMacOS
    ? 'macOS'
    : Platform.isWindows
    ? 'Windows'
    : Platform.isLinux
    ? 'Linux'
    : Platform.operatingSystem;

class DirectoryResult {
  const DirectoryResult(this.location, this.paths, {this.error});
  final StorageLocation location;
  final List<String> paths;
  final String? error;
}

class FileEntry {
  const FileEntry({
    required this.path,
    required this.bytes,
    required this.modified,
  });
  final String path;
  final int bytes;
  final DateTime modified;
}

class FileReadResult {
  const FileReadResult(
    this.player,
    this.source,
    this.worker,
    this.elapsedMicroseconds,
  );
  final Footballer player;
  final String source;
  final String worker;
  final int elapsedMicroseconds;
}

class FileWriteResult {
  const FileWriteResult(this.entries, this.worker, this.elapsedMicroseconds);
  final List<FileEntry> entries;
  final String worker;
  final int elapsedMicroseconds;
}

typedef DirectoryResolver = Future<List<String>> Function(
  StorageLocation location,
);

class FileService {
  FileService({DirectoryResolver? resolver, String? platform})
    : _resolver = resolver ?? _resolve,
      platform = platform ?? currentPlatform;
  final DirectoryResolver _resolver;
  final String platform;
  // Plugins run on the root isolate. Workers receive paths and maps only.
  static Future<List<String>> _resolve(StorageLocation location) async {
    final List<Directory?> directories = switch (location) {
      StorageLocation.temporary => [await getTemporaryDirectory()],
      StorageLocation.support => [await getApplicationSupportDirectory()],
      StorageLocation.library => [await getLibraryDirectory()],
      StorageLocation.documents => [await getApplicationDocumentsDirectory()],
      StorageLocation.cache => [await getApplicationCacheDirectory()],
      StorageLocation.external => [await getExternalStorageDirectory()],
      StorageLocation.externalCache =>
        await getExternalCacheDirectories() ?? [],
      StorageLocation.externalDirectories =>
        await getExternalStorageDirectories(type: StorageDirectory.documents) ??
            [],
      StorageLocation.downloads => [await getDownloadsDirectory()],
    };
    return directories
        .whereType<Directory>()
        .map((d) => d.path)
        .toSet()
        .toList();
  }

  Future<List<DirectoryResult>> discover() async {
    final results = <DirectoryResult>[];
    for (final location in StorageLocation.values) {
      final unsupported = location.unsupported;
      if (unsupported.contains(platform)) {
        results.add(
          DirectoryResult(
            location,
            const [],
            error:
                '${location.label} не поддерживается на ${unsupported.join(', ')}. Текущая платформа: $platform.',
          ),
        );
        continue;
      }
      try {
        final paths = await _resolver(location);
        if (paths.isEmpty) {
          throw const FileSystemException('ОС не вернула доступную директорию');
        }
        results.add(DirectoryResult(location, paths));
      } catch (error) {
        results.add(
          DirectoryResult(
            location,
            const [],
            error:
                '${location.label} на $platform: $error. Попробуйте обновить список директорий.',
          ),
        );
      }
    }
    return results;
  }

  // Some APIs share a base directory (e.g. Android temp/cache). Namespace each
  // API so its distinct entity cannot overwrite a sample from another API.
  static String folder(String base, StorageLocation location) =>
      Directory(base).uri.resolve('sports_lab7/${location.name}/').toFilePath();
  Future<FileWriteResult> write(DirectoryResult directory, Footballer player) {
    if (directory.error != null || directory.paths.isEmpty) {
      throw FileSystemException(directory.error ?? 'Директория недоступна');
    }
    return _writeIsolated(directory.paths, directory.location, player.toMap());
  }

  Future<List<FileEntry>> scan(DirectoryResult directory) =>
      _scanIsolated(directory.paths, directory.location);
  Future<FileReadResult> read(String path) => _readIsolated(path);
}

// Top-level boundaries keep database handles, services and UI out of captures.
Future<FileWriteResult> _writeIsolated(
  List<String> bases,
  StorageLocation location,
  Map<String, Object?> map,
) => Isolate.run(() async {
  final watch = Stopwatch()..start();
  final files = <FileEntry>[];
  for (var index = 0; index < bases.length; index++) {
    final directory = Directory(FileService.folder(bases[index], location));
    await directory.create(recursive: true);
    final player = Footballer.fromMap(map)
        .fileVariant(location.index * 100 + index);
    final file = File.fromUri(directory.uri.resolve('footballer.json'));
    final pending = File('${file.path}.tmp');
    await pending.writeAsString(
      const JsonEncoder.withIndent('  ').convert({
        'version': 1,
        'location': location.label,
        'entity': player.toMap(),
      }),
      flush: true,
    );
    await pending.rename(file.path);
    final stat = await file.stat();
    files.add(
      FileEntry(path: file.path, bytes: stat.size, modified: stat.modified),
    );
  }
  return FileWriteResult(
    files,
    Isolate.current.debugName ?? 'file-writer',
    watch.elapsedMicroseconds,
  );
}, debugName: 'lab7-file-writer');
Future<List<FileEntry>> _scanIsolated(
  List<String> bases,
  StorageLocation location,
) => Isolate.run(() async {
  final entries = <FileEntry>[];
  for (final base in bases) {
    final directory = Directory(FileService.folder(base, location));
    if (!await directory.exists()) continue;
    await for (final item in directory.list(followLinks: false)) {
      if (item is! File || !item.path.endsWith('.json')) continue;
      final stat = await item.stat();
      entries.add(
        FileEntry(path: item.path, bytes: stat.size, modified: stat.modified),
      );
    }
  }
  entries.sort((a, b) => a.path.compareTo(b.path));
  return entries;
}, debugName: 'lab7-file-scanner');
Future<FileReadResult> _readIsolated(String path) => Isolate.run(() async {
  final watch = Stopwatch()..start();
  final file = File(path);
  if (await file.length() > 1024 * 1024) {
    throw const FormatException('Файл больше 1 МБ.');
  }
  final payload = jsonDecode(await file.readAsString());
  if (payload is! Map<String, dynamic> ||
      payload['version'] != 1 ||
      payload['location'] is! String ||
      payload['entity'] is! Map<String, dynamic>) {
    throw const FormatException('Ожидался JSON Footballer, версия 1.');
  }
  final player = Footballer.fromMap(
    Map<String, Object?>.from(payload['entity'] as Map),
  );
  return FileReadResult(
    player,
    payload['location'] as String,
    Isolate.current.debugName ?? 'file-reader',
    watch.elapsedMicroseconds,
  );
}, debugName: 'lab7-file-reader');
