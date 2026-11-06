import 'package:flutter/material.dart';

import 'coffee_theme.dart';
import 'file_service.dart';
import 'footballer.dart';
import 'footballer_repository.dart';

class FileScreen extends StatefulWidget {
  const FileScreen({super.key, required this.repository, this.service});
  final FootballerRepository repository;
  final FileService? service;
  @override
  State<FileScreen> createState() => _FileScreenState();
}

class _FileScreenState extends State<FileScreen> {
  late final FileService service = widget.service ?? FileService();
  List<DirectoryResult> directories = [];
  final files = <StorageLocation, List<FileEntry>>{};
  final readings = <String, FileReadResult>{};
  final failures = <String, String>{};
  final working = <StorageLocation>{};
  List<Footballer> players = [];
  int? selectedId;
  String? error;
  bool loading = true;
  String? worker;
  @override
  void initState() {
    super.initState();
    load();
  }

  Future<void> load() async {
    setState(() {
      loading = true;
      error = null;
    });
    try {
      final discovered = await service.discover();
      final people = await widget.repository.list();
      final found = <StorageLocation, List<FileEntry>>{};
      for (final directory in discovered) {
        if (directory.error != null) continue;
        try {
          found[directory.location] = await service.scan(directory);
        } catch (e) {
          failures[directory.location.name] = '$e';
        }
      }
      if (mounted) {
        setState(() {
          directories = discovered;
          players = people;
          files.addAll(found);
          if (!people.any((p) => p.id == selectedId)) {
            selectedId = people.firstOrNull?.id;
          }
          loading = false;
        });
      }
    } catch (e) {
      if (mounted) {
        setState(() {
          error = '$e';
          loading = false;
        });
      }
    }
  }

  Future<void> write(DirectoryResult directory) async {
    final player = players.where((p) => p.id == selectedId).firstOrNull;
    if (player == null) return;
    setState(() {
      working.add(directory.location);
      failures.remove(directory.location.name);
    });
    try {
      final result = await service.write(directory, player);
      if (mounted) {
        setState(() {
          files[directory.location] = result.entries;
          worker = '${result.worker} · ${result.elapsedMicroseconds} мкс';
        });
      }
    } catch (e) {
      if (mounted) setState(() => failures[directory.location.name] = '$e');
    } finally {
      if (mounted) setState(() => working.remove(directory.location));
    }
  }

  Future<void> read(FileEntry entry) async {
    setState(() => failures.remove(entry.path));
    try {
      final result = await service.read(entry.path);
      if (mounted) {
        setState(() {
          readings[entry.path] = result;
          worker = '${result.worker} · ${result.elapsedMicroseconds} мкс';
        });
      }
    } catch (e) {
      if (mounted) setState(() => failures[entry.path] = '$e');
    }
  }

  Future<void> writeAll() async {
    for (final directory in directories) {
      if (directory.error == null) await write(directory);
    }
  }

  @override
  Widget build(BuildContext context) => loading
      ? const Center(child: CircularProgressIndicator())
      : error != null
      ? Center(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              Text('Ошибка: $error'),
              TextButton(onPressed: load, child: const Text('Повторить')),
            ],
          ),
        )
      : RefreshIndicator(
          onRefresh: load,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(18, 0, 18, 24),
            children: [
              const Text(
                'Работа с папками',
                style: TextStyle(fontSize: 22, fontWeight: FontWeight.bold),
              ),
              const SizedBox(height: 14),
              if (players.isEmpty)
                const Text('Сначала добавьте игрока на вкладке «Игроки».')
              else
                DropdownButtonFormField<int>(
                  initialValue: selectedId,
                  decoration: const InputDecoration(
                    labelText: 'SQLite',
                  ),
                  items: players
                      .map(
                        (p) =>
                            DropdownMenuItem(value: p.id, child: Text(p.name)),
                      )
                      .toList(),
                  onChanged: (id) => setState(() => selectedId = id),
                ),
              const SizedBox(height: 12),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                children: [
                  FilledButton.icon(
                    key: const Key('write-all'),
                    onPressed: selectedId == null ? null : writeAll,
                    icon: const Icon(Icons.save_alt),
                    label: const Text('Записать во все доступные'),
                  ),
                  OutlinedButton.icon(
                    key: const Key('refresh-files'),
                    onPressed: load,
                    icon: const Icon(Icons.refresh),
                    label: const Text('Найти файлы'),
                  ),
                ],
              ),

              const SizedBox(height: 16),
              for (final directory in directories) _directory(directory),
            ],
          ),
        );

  Widget _directory(DirectoryResult directory) {
    final entries = files[directory.location] ?? [];
    final busy = working.contains(directory.location);
    return Card(
      color: coffeeCard,
      margin: const EdgeInsets.only(bottom: 12),
      child: Padding(
        padding: const EdgeInsets.all(14),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.folder_outlined, color: coffeeOrange),
                const SizedBox(width: 9),
                Expanded(
                  child: Text(
                    directory.location.label,
                    style: const TextStyle(
                      fontSize: 17,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                if (directory.error == null)
                  IconButton(
                    key: Key('write-${directory.location.name}'),
                    tooltip: 'Записать игрока',
                    onPressed: selectedId == null || busy
                        ? null
                        : () => write(directory),
                    icon: const Icon(Icons.save_outlined),
                  ),
              ],
            ),
            if (directory.error != null)
              Text(
                directory.error!,
                style: const TextStyle(color: Colors.orangeAccent),
              )
            else ...[
              for (final base in directory.paths)
                SelectableText(
                  'Папка: $base',
                  style: const TextStyle(color: coffeeMuted, fontSize: 12),
                ),
              if (entries.isEmpty)
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 8),
                  child: Text('Файлов пока нет'),
                ),
              for (final entry in entries) ...[
                const Divider(),
                SelectableText(
                  entry.path,
                  key: Key('path-${directory.location.name}'),
                  style: const TextStyle(fontSize: 12, color: coffeeOrange),
                ),
                Text(
                  '${entry.bytes} байт · ${entry.modified.toLocal()}',
                  style: const TextStyle(color: coffeeMuted, fontSize: 12),
                ),
                TextButton.icon(
                  key: Key('read-${directory.location.name}'),
                  onPressed: () => read(entry),
                  icon: const Icon(Icons.file_open_outlined),
                  label: const Text('Прочитать файл'),
                ),
                if (readings[entry.path] case final result?)
                  Text(
                    '${result.player.name}\nКлуб: ${result.player.club}\nПозиция: ${result.player.position}\n'
                    'Дисциплина: ${result.player.discipline}\nРейтинг: ${result.player.rating}/10\n'
                    'Источник: ${result.source}\nИзолят: ${result.worker} (${result.elapsedMicroseconds} мкс)',
                    key: Key('content-${directory.location.name}'),
                  ),
                if (failures[entry.path] case final failure?)
                  Text(
                    'Ошибка чтения: $failure',
                    style: const TextStyle(color: Colors.orangeAccent),
                  ),
              ],
              if (failures[directory.location.name] case final failure?)
                Text(
                  'Ошибка записи: $failure',
                  style: const TextStyle(color: Colors.orangeAccent),
                ),
            ],
          ],
        ),
      ),
    );
  }
}
