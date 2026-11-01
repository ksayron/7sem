import 'package:flutter/material.dart';

import 'coffee_theme.dart';
import 'footballer.dart';
import 'footballer_repository.dart';
import 'file_screen.dart';

class SportsApp extends StatelessWidget {
  const SportsApp({super.key, this.repository});
  final FootballerRepository? repository;
  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'Sports Lab 7',
    debugShowCheckedModeBanner: false,
    theme: ThemeData(
      brightness: Brightness.dark,
      scaffoldBackgroundColor: coffeeBlack,
      colorScheme: const ColorScheme.dark(
        primary: coffeeOrange,
        surface: coffeeCard,
      ),
      useMaterial3: true,
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: coffeeCard,
        border: OutlineInputBorder(
          borderRadius: BorderRadius.circular(14),
          borderSide: BorderSide.none,
        ),
      ),
    ),
    home: SportsHome(repository: repository ?? FootballerRepository()),
  );
}

void showError(BuildContext context, Object error) {
  ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('$error')));
}

class SportsHome extends StatefulWidget {
  const SportsHome({super.key, required this.repository});
  final FootballerRepository repository;
  @override
  State<SportsHome> createState() => _SportsHomeState();
}

class _SportsHomeState extends State<SportsHome> {
  int tab = 0;
  String query = '';
  String? position;
  List<SortRule> sort = const [SortRule(SortField.name)];
  List<Footballer> players = [];
  bool busy = true;
  String? error;
  int generation = 0;
  @override
  void initState() {
    super.initState();
    refresh();
  }

  @override
  void dispose() {
    widget.repository.close();
    super.dispose();
  }

  Future<void> refresh() async {
    final ticket = ++generation;
    setState(() {
      busy = true;
      error = null;
    });
    try {
      final result = await widget.repository.list(
        query: query,
        position: position,
        sort: sort,
      );
      if (mounted && ticket == generation) {
        setState(() {
          players = result;
          busy = false;
        });
      }
    } catch (e) {
      if (mounted && ticket == generation) {
        setState(() {
          error = '$e';
          busy = false;
        });
      }
    }
  }

  Future<void> edit([Footballer? player]) async {
    final saved = await Navigator.push<bool>(
      context,
      MaterialPageRoute(
        builder: (_) =>
            PlayerForm(repository: widget.repository, player: player),
      ),
    );
    if (saved == true) await refresh();
  }

  Future<void> details(Footballer player) async {
    final changed = await Navigator.push<bool>(
      context,
      MaterialPageRoute(
        builder: (_) =>
            PlayerDetails(repository: widget.repository, id: player.id!),
      ),
    );
    if (changed == true) await refresh();
  }

  Future<void> sorting() async {
    final result = await showModalBottomSheet<List<SortRule>>(
      context: context,
      isScrollControlled: true,
      builder: (_) => SortSheet(initial: sort),
    );
    if (result != null) {
      sort = result;
      await refresh();
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: SafeArea(
      child: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 18, 20, 16),
            child: Row(
              children: [
                const Icon(Icons.sports_soccer, color: coffeeOrange, size: 28),
                const SizedBox(width: 12),
                Expanded(
                  child: Text(
                    tab == 0 ? 'Спортивный клуб' : 'Файловая система',
                    style: const TextStyle(
                      fontSize: 25,
                      fontWeight: FontWeight.bold,
                    ),
                  ),
                ),
                if (tab == 0)
                  IconButton(
                    key: const Key('add-player'),
                    tooltip: 'Добавить игрока',
                    onPressed: () => edit(),
                    icon: const Icon(
                      Icons.add_circle_outline,
                      color: coffeeOrange,
                      size: 30,
                    ),
                  ),
              ],
            ),
          ),
          Expanded(
            child: tab == 0
                ? _catalog()
                : FileScreen(repository: widget.repository),
          ),
        ],
      ),
    ),
    bottomNavigationBar: NavigationBar(
      selectedIndex: tab,
      backgroundColor: coffeeCard,
      indicatorColor: coffeeOrange.withValues(alpha: .25),
      onDestinationSelected: (value) => setState(() => tab = value),
      destinations: const [
        NavigationDestination(
          icon: Icon(Icons.groups_outlined),
          selectedIcon: Icon(Icons.groups),
          label: 'Игроки',
        ),
        NavigationDestination(
          icon: Icon(Icons.folder_outlined),
          selectedIcon: Icon(Icons.folder),
          label: 'Файлы',
        ),
      ],
    ),
  );
  Widget _catalog() => Column(
    children: [
      Padding(
        padding: const EdgeInsets.symmetric(horizontal: 18),
        child: Column(
          children: [
            Container(
              width: double.infinity,
              padding: const EdgeInsets.all(20),
              decoration: BoxDecoration(
                color: coffeeCard,
                borderRadius: BorderRadius.circular(16),
              ),
              child: const Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    'СОСТАВ КОМАНД',
                    style: TextStyle(
                      color: coffeeOrange,
                      fontSize: 12,
                      letterSpacing: 1.5,
                    ),
                  ),
                  SizedBox(height: 8),
                  Text(
                    'Игроки и их рейтинг',
                    style: TextStyle(fontSize: 23, fontWeight: FontWeight.bold),
                  ),
                  SizedBox(height: 5),
                  Text(
                    'Выберите игрока, чтобы посмотреть карточку.',
                    style: TextStyle(color: coffeeMuted),
                  ),
                ],
              ),
            ),
            const SizedBox(height: 14),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    key: const Key('player-search'),
                    onChanged: (value) {
                      query = value;
                      refresh();
                    },
                    decoration: const InputDecoration(
                      prefixIcon: Icon(Icons.search),
                      hintText: 'Имя, клуб, позиция...',
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                IconButton(
                  key: const Key('sort-button'),
                  tooltip: 'Сортировка по нескольким полям',
                  onPressed: sorting,
                  icon: const Icon(Icons.tune, color: coffeeOrange),
                ),
              ],
            ),
            const SizedBox(height: 12),
            SizedBox(
              height: 38,
              child: ListView(
                scrollDirection: Axis.horizontal,
                children: [
                  for (final value in <String?>[
                    null,
                    'Нападающий',
                    'Полузащитник',
                    'Защитник',
                    'Вратарь',
                  ])
                    Padding(
                      padding: const EdgeInsets.only(right: 8),
                      child: ChoiceChip(
                        label: Text(value ?? 'Все'),
                        selected: position == value,
                        onSelected: (_) {
                          position = value;
                          refresh();
                        },
                      ),
                    ),
                ],
              ),
            ),
            const SizedBox(height: 12),
          ],
        ),
      ),
      if (busy)
        const Expanded(child: Center(child: CircularProgressIndicator()))
      else if (error != null)
        Expanded(
          child: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(
                  'Не удалось загрузить список: $error',
                  textAlign: TextAlign.center,
                ),
                TextButton(onPressed: refresh, child: const Text('Повторить')),
              ],
            ),
          ),
        )
      else if (players.isEmpty)
        Expanded(
          child: Center(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                const Text('Игроки не найдены', style: TextStyle(fontSize: 18)),
                TextButton(
                  onPressed: () {
                    position = null;
                    query = '';
                    refresh();
                  },
                  child: const Text('Сбросить фильтр'),
                ),
              ],
            ),
          ),
        )
      else
        Expanded(
          child: RefreshIndicator(
            onRefresh: refresh,
            child: ListView.builder(
              key: const Key('players-list'),
              itemExtent: 104,
              itemCount: players.length,
              itemBuilder: (context, index) {
                final player = players[index];
                return Padding(
                  padding: const EdgeInsets.symmetric(
                    horizontal: 18,
                    vertical: 5,
                  ),
                  child: Material(
                    color: coffeeCard,
                    borderRadius: BorderRadius.circular(14),
                    child: InkWell(
                      key: Key('player-${player.id}'),
                      borderRadius: BorderRadius.circular(14),
                      onTap: () => details(player),
                      child: Padding(
                        padding: const EdgeInsets.all(14),
                        child: Row(
                          children: [
                            CircleAvatar(
                              backgroundColor: coffeeOrange.withValues(
                                alpha: .18,
                              ),
                              child: Text(
                                player.name.characters.first,
                                style: const TextStyle(color: coffeeOrange),
                              ),
                            ),
                            const SizedBox(width: 13),
                            Expanded(
                              child: Column(
                                crossAxisAlignment: CrossAxisAlignment.start,
                                mainAxisAlignment: MainAxisAlignment.center,
                                children: [
                                  Text(
                                    player.name,
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                    style: const TextStyle(
                                      fontSize: 16,
                                      fontWeight: FontWeight.w600,
                                    ),
                                  ),
                                  const SizedBox(height: 4),
                                  Text(
                                    '${player.club} · ${player.position}',
                                    maxLines: 1,
                                    overflow: TextOverflow.ellipsis,
                                    style: const TextStyle(color: coffeeMuted),
                                  ),
                                ],
                              ),
                            ),
                            Text(
                              '${player.rating}/10',
                              style: const TextStyle(
                                color: coffeeOrange,
                                fontSize: 16,
                                fontWeight: FontWeight.bold,
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                );
              },
            ),
          ),
        ),
    ],
  );
}

class SortSheet extends StatefulWidget {
  const SortSheet({super.key, required this.initial});
  final List<SortRule> initial;
  @override
  State<SortSheet> createState() => _SortSheetState();
}

class _SortSheetState extends State<SortSheet> {
  late List<SortRule> rules = [...widget.initial];
  @override
  Widget build(BuildContext context) => SafeArea(
    child: Padding(
      padding: const EdgeInsets.all(22),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          const Text(
            'Сортировка',
            style: TextStyle(fontSize: 22, fontWeight: FontWeight.bold),
          ),
          const SizedBox(height: 8),
          const Text(
            'Поля применяются сверху вниз.',
            style: TextStyle(color: coffeeMuted),
          ),
          const SizedBox(height: 12),
          for (var index = 0; index < rules.length; index++)
            Row(
              children: [
                Text('${index + 1}. '),
                Expanded(
                  child: DropdownButton<SortField>(
                    isExpanded: true,
                    value: rules[index].field,
                    items: SortField.values
                        .map(
                          (field) => DropdownMenuItem(
                            value: field,
                            child: Text(field.label),
                          ),
                        )
                        .toList(),
                    onChanged: (field) {
                      if (field == null ||
                          rules.any(
                            (rule) =>
                                rule.field == field && rule != rules[index],
                          )) {
                        return;
                      }
                      setState(
                        () => rules[index] = SortRule(
                          field,
                          ascending: rules[index].ascending,
                        ),
                      );
                    },
                  ),
                ),
                IconButton(
                  tooltip: 'Изменить направление',
                  onPressed: () => setState(
                    () => rules[index] = SortRule(
                      rules[index].field,
                      ascending: !rules[index].ascending,
                    ),
                  ),
                  icon: Icon(
                    rules[index].ascending
                        ? Icons.arrow_upward
                        : Icons.arrow_downward,
                  ),
                ),
                IconButton(
                  tooltip: 'Убрать поле',
                  onPressed: rules.length == 1
                      ? null
                      : () => setState(() => rules.removeAt(index)),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
          if (rules.length < SortField.values.length)
            TextButton.icon(
              onPressed: () => setState(
                () => rules.add(
                  SortRule(
                    SortField.values.firstWhere(
                      (field) => !rules.any((rule) => rule.field == field),
                    ),
                  ),
                ),
              ),
              icon: const Icon(Icons.add),
              label: const Text('Добавить поле'),
            ),
          const SizedBox(height: 12),
          FilledButton(
            onPressed: () => Navigator.pop(context, rules),
            child: const Text('Применить'),
          ),
        ],
      ),
    ),
  );
}

class PlayerDetails extends StatefulWidget {
  const PlayerDetails({super.key, required this.repository, required this.id});
  final FootballerRepository repository;
  final int id;
  @override
  State<PlayerDetails> createState() => _PlayerDetailsState();
}

class _PlayerDetailsState extends State<PlayerDetails> {
  late Future<Footballer?> future = widget.repository.get(widget.id);
  Future<void> remove() async {
    final yes = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Удалить игрока?'),
        content: const Text('Запись будет удалена из SQLite.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Отмена'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Удалить'),
          ),
        ],
      ),
    );
    if (yes != true || !mounted) return;
    try {
      await widget.repository.delete(widget.id);
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) showError(context, e);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Карточка игрока'),
      backgroundColor: coffeeBlack,
    ),
    body: FutureBuilder<Footballer?>(
      future: future,
      builder: (context, snapshot) {
        if (!snapshot.hasData) {
          return Center(
            child: snapshot.hasError
                ? Text('Ошибка: ${snapshot.error}')
                : snapshot.connectionState == ConnectionState.done
                ? const Text('Игрок удалён')
                : const CircularProgressIndicator(),
          );
        }
        final player = snapshot.data!;
        return ListView(
          padding: const EdgeInsets.all(20),
          children: [
            const Icon(Icons.sports_soccer, size: 76, color: coffeeOrange),
            const SizedBox(height: 20),
            Text(
              player.name,
              textAlign: TextAlign.center,
              style: const TextStyle(fontSize: 27, fontWeight: FontWeight.bold),
            ),
            const SizedBox(height: 20),
            Card(
              child: Column(
                children: [
                  _info('Клуб', player.club),
                  _info('Позиция', player.position),
                  _info('Дисциплина', player.discipline),
                  _info('Рейтинг', '${player.rating}/10'),
                  _info('Профессионал', player.isPro ? 'Да' : 'Нет'),
                  _info('SQLite ID', '${player.id}'),
                ],
              ),
            ),
            const SizedBox(height: 20),
            FilledButton.icon(
              key: const Key('edit-player'),
              icon: const Icon(Icons.edit),
              label: const Text('Изменить'),
              onPressed: () async {
                final saved = await Navigator.push<bool>(
                  context,
                  MaterialPageRoute(
                    builder: (_) => PlayerForm(
                      repository: widget.repository,
                      player: player,
                    ),
                  ),
                );
                if (saved == true && context.mounted) {
                  Navigator.pop(context, true);
                }
              },
            ),
            OutlinedButton.icon(
              key: const Key('delete-player'),
              icon: const Icon(Icons.delete_outline),
              label: const Text('Удалить'),
              onPressed: remove,
            ),
          ],
        );
      },
    ),
  );
  Widget _info(String label, String value) => ListTile(
    title: Text(label),
    trailing: SizedBox(
      width: 170,
      child: Text(
        value,
        textAlign: TextAlign.end,
        maxLines: 2,
        overflow: TextOverflow.ellipsis,
        style: const TextStyle(color: coffeeOrange),
      ),
    ),
  );
}

class PlayerForm extends StatefulWidget {
  const PlayerForm({super.key, required this.repository, this.player});
  final FootballerRepository repository;
  final Footballer? player;
  @override
  State<PlayerForm> createState() => _PlayerFormState();
}

class _PlayerFormState extends State<PlayerForm> {
  final form = GlobalKey<FormState>();
  late final name = TextEditingController(text: widget.player?.name);
  late final discipline = TextEditingController(
    text: widget.player?.discipline ?? 'Футбол',
  );
  late final position = TextEditingController(text: widget.player?.position);
  late final rating = TextEditingController(
    text: widget.player?.rating.toString() ?? '5',
  );
  late final club = TextEditingController(text: widget.player?.club);
  bool saving = false;
  @override
  void dispose() {
    for (final c in [name, discipline, position, rating, club]) {
      c.dispose();
    }
    super.dispose();
  }

  String? requiredText(String? value) => value == null || value.trim().isEmpty
      ? 'Обязательное поле'
      : value.trim().length > 80
      ? 'Не более 80 символов'
      : null;
  Future<void> submit() async {
    if (!form.currentState!.validate()) return;
    setState(() => saving = true);
    try {
      await widget.repository.save(
        Footballer(
          id: widget.player?.id,
          name: name.text.trim(),
          discipline: discipline.text.trim(),
          position: position.text.trim(),
          rating: int.parse(rating.text.trim()),
          club: club.text.trim(),
        ),
      );
      if (mounted) Navigator.pop(context, true);
    } catch (e) {
      if (mounted) {
        showError(context, e);
        setState(() => saving = false);
      }
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: Text(
        widget.player == null ? 'Добавить игрока' : 'Изменить игрока',
      ),
      backgroundColor: coffeeBlack,
    ),
    body: Form(
      key: form,
      child: ListView(
        padding: const EdgeInsets.all(20),
        children: [
          _field(name, 'Имя', key: const Key('name-field')),
          _field(discipline, 'Дисциплина'),
          _field(position, 'Позиция'),
          _field(club, 'Клуб'),
          Padding(
            padding: const EdgeInsets.only(bottom: 14),
            child: TextFormField(
              key: const Key('rating-field'),
              controller: rating,
              keyboardType: TextInputType.number,
              decoration: const InputDecoration(labelText: 'Рейтинг 1–10'),
              validator: (value) {
                final n = int.tryParse(value?.trim() ?? '');
                return n == null || n < 1 || n > 10
                    ? 'Введите целое число от 1 до 10'
                    : null;
              },
            ),
          ),
          const SizedBox(height: 12),
          FilledButton(
            key: const Key('save-player'),
            onPressed: saving ? null : submit,
            child: Text(saving ? 'Сохранение...' : 'Сохранить'),
          ),
        ],
      ),
    ),
  );
  Widget _field(TextEditingController controller, String label, {Key? key}) =>
      Padding(
        padding: const EdgeInsets.only(bottom: 14),
        child: TextFormField(
          key: key,
          controller: controller,
          maxLength: 80,
          decoration: InputDecoration(labelText: label),
          validator: requiredText,
        ),
      );
}
