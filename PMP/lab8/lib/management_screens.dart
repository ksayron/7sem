import 'package:flutter/material.dart';

import 'coffee_app.dart';
import 'coffee_repository.dart';
import 'models.dart';

Future<void> openProductForm(
  BuildContext context,
  CoffeeRepository repository, {
  CoffeeProduct? product,
}) => Navigator.push(
  context,
  MaterialPageRoute<void>(
    settings: RouteSettings(
      name: product == null ? '/products/add' : '/products/${product.id}/edit',
    ),
    builder: (_) => ProductFormScreen(repository: repository, product: product),
  ),
);

Future<void> confirmDelete(
  BuildContext context,
  CoffeeRepository repository,
  CoffeeProduct product,
) async {
  final confirmed = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('Delete product?'),
      content: Text(
        '${product.name} will also be removed from everyone’s favorites.',
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context, false),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(context, true),
          child: const Text('Delete'),
        ),
      ],
    ),
  );
  if (confirmed != true || !context.mounted) return;
  await runCoffeeAction(context, () async {
    await repository.deleteProduct(product.id);
    if (context.mounted) Navigator.pop(context);
  });
}

class FavoritesScreen extends StatelessWidget {
  const FavoritesScreen({super.key, required this.repository});
  final CoffeeRepository repository;
  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: repository,
    builder: (context, _) {
      final products = repository.favorites;
      return Scaffold(
        appBar: AppBar(title: const Text('Favorites')),
        body: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Padding(
              padding: const EdgeInsets.all(18),
              child: Text(
                '${repository.currentUser.name} · ${products.length} saved coffees',
                style: const TextStyle(color: coffeeMuted),
              ),
            ),
            Expanded(
              child: products.isEmpty
                  ? const Center(
                      child: Text(
                        'No favorites yet. Tap a heart in the catalog.',
                      ),
                    )
                  : ListView.separated(
                      itemCount: products.length,
                      separatorBuilder: (_, _) =>
                          const Divider(indent: 18, endIndent: 18),
                      itemBuilder: (context, index) {
                        final p = products[index];
                        return ListTile(
                          key: Key('favorite-${p.id}'),
                          leading: Image.asset(p.asset, width: 56, height: 64),
                          title: Text(p.name),
                          subtitle: Text(
                            '\$${p.formattedPrice} · ${p.location}',
                          ),
                          trailing: IconButton(
                            tooltip: 'Remove ${p.name} from favorites',
                            icon: const Icon(
                              Icons.favorite,
                              color: coffeeOrange,
                            ),
                            onPressed: () => runCoffeeAction(
                              context,
                              () => repository.setFavorite(p.id, false),
                            ),
                          ),
                          onTap: () => openProduct(context, repository, p.id),
                        );
                      },
                    ),
            ),
          ],
        ),
      );
    },
  );
}

class ProductFormScreen extends StatefulWidget {
  const ProductFormScreen({super.key, required this.repository, this.product});
  final CoffeeRepository repository;
  final CoffeeProduct? product;
  @override
  State<ProductFormScreen> createState() => _ProductFormScreenState();
}

class _ProductFormScreenState extends State<ProductFormScreen> {
  final _form = GlobalKey<FormState>();
  late final Map<String, TextEditingController> _fields;
  late String _asset;
  late int _category;
  bool _saving = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    final p = widget.product;
    _fields = {
      'Name': TextEditingController(text: p?.name ?? ''),
      'Price (USD)': TextEditingController(text: p?.formattedPrice ?? ''),
      'Location': TextEditingController(text: p?.location ?? ''),
      'Description': TextEditingController(text: p?.description ?? ''),
      'Rating': TextEditingController(text: p?.rating.toString() ?? '0'),
      'Review count': TextEditingController(
        text: p?.reviewCount.toString() ?? '0',
      ),
    };
    _asset = p?.asset ?? imagePaths.first;
    _category = p?.category ?? 1;
  }

  @override
  void dispose() {
    for (final controller in _fields.values) {
      controller.dispose();
    }
    super.dispose();
  }

  String _value(String key) => _fields[key]!.text.trim();
  String? _validate(String label, String? raw) {
    final value = (raw ?? '').trim();
    if (value.isEmpty) return 'Required';
    if (label == 'Price (USD)') {
      if (!RegExp(r'^\d+(?:[.,]\d{1,2})?$').hasMatch(value)) {
        return 'Use a price with up to 2 decimals';
      }
      final number = double.tryParse(value.replaceAll(',', '.'));
      if (number == null ||
          !number.isFinite ||
          number <= 0 ||
          number > 1000000) {
        return 'Enter a price above 0 and up to 1,000,000';
      }
    }
    if (label == 'Rating') {
      final number = double.tryParse(value.replaceAll(',', '.'));
      if (number == null || !number.isFinite || number < 0 || number > 5) {
        return 'Enter a rating from 0 to 5';
      }
    }
    if (label == 'Review count') {
      final number = int.tryParse(value);
      if (number == null || number < 0) {
        return 'Enter a non-negative whole number';
      }
    }
    return null;
  }

  Future<void> _save() async {
    if (_saving || !_form.currentState!.validate()) return;
    FocusManager.instance.primaryFocus?.unfocus();
    setState(() {
      _saving = true;
      _error = null;
    });
    final p = CoffeeProduct(
      id:
          widget.product?.id ??
          'coffee-${DateTime.now().microsecondsSinceEpoch}',
      name: _value('Name'),
      asset: _asset,
      priceCents:
          (double.parse(_value('Price (USD)').replaceAll(',', '.')) * 100)
              .round(),
      category: _category,
      location: _value('Location'),
      description: _value('Description'),
      rating: double.parse(_value('Rating').replaceAll(',', '.')),
      reviewCount: int.parse(_value('Review count')),
    );
    try {
      await widget.repository.saveProduct(p, creating: widget.product == null);
      if (mounted) Navigator.pop(context);
    } catch (error) {
      if (mounted) {
        setState(() {
          _saving = false;
          _error = error.toString();
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: Text(widget.product == null ? 'Add product' : 'Edit product'),
    ),
    body: SafeArea(
      child: Form(
        key: _form,
        child: SingleChildScrollView(
          padding: const EdgeInsets.all(18),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                'PRODUCT DETAILS',
                style: Theme.of(context).textTheme.labelLarge
                    ?.copyWith(color: coffeeOrange),
              ),
              const SizedBox(height: 8),
              const Text(
                'Set the details customers see in the menu.',
                style: TextStyle(color: coffeeMuted),
              ),
              const SizedBox(height: 18),
              for (final entry in _fields.entries)
                Padding(
                  padding: const EdgeInsets.only(bottom: 16),
                  child: TextFormField(
                    key: Key('field-${entry.key}'),
                    controller: entry.value,
                    enabled: !_saving,
                    decoration: InputDecoration(
                      labelText: entry.key,
                      border: const OutlineInputBorder(),
                    ),
                    maxLines: entry.key == 'Description' ? 3 : 1,
                    keyboardType:
                        [
                          'Price (USD)',
                          'Rating',
                          'Review count',
                        ].contains(entry.key)
                        ? const TextInputType.numberWithOptions(decimal: true)
                        : entry.key == 'Description'
                        ? TextInputType.multiline
                        : TextInputType.text,
                    textInputAction: entry.key == 'Description'
                        ? TextInputAction.newline
                        : TextInputAction.next,
                    validator: (value) => _validate(entry.key, value),
                    autovalidateMode: AutovalidateMode.onUserInteraction,
                  ),
                ),
              DropdownButtonFormField<int>(
                initialValue: _category,
                isExpanded: true,
                decoration: const InputDecoration(
                  labelText: 'Category',
                  border: OutlineInputBorder(),
                ),
                items: List.generate(
                  categoryNames.length,
                  (i) =>
                      DropdownMenuItem(value: i, child: Text(categoryNames[i])),
                ),
                onChanged: _saving
                    ? null
                    : (value) => setState(() => _category = value!),
              ),
              const SizedBox(height: 16),
              DropdownButtonFormField<String>(
                initialValue: _asset,
                isExpanded: true,
                decoration: const InputDecoration(
                  labelText: 'Image',
                  border: OutlineInputBorder(),
                ),
                items: imagePaths
                    .map(
                      (path) => DropdownMenuItem(
                        value: path,
                        child: Text(path.split('/').last),
                      ),
                    )
                    .toList(),
                onChanged: _saving
                    ? null
                    : (value) => setState(() => _asset = value!),
              ),
              Padding(
                padding: const EdgeInsets.all(16),
                child: Image.asset(_asset, height: 130),
              ),
              Text(
                _asset,
                textAlign: TextAlign.center,
                style: const TextStyle(color: coffeeMuted, fontSize: 12),
              ),
              if (_error != null)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 16),
                  child: Text(
                    _error!,
                    style: TextStyle(
                      color: Theme.of(context).colorScheme.error,
                    ),
                  ),
                ),
              const SizedBox(height: 20),
              FilledButton.icon(
                key: const Key('save-product'),
                onPressed: _saving ? null : _save,
                icon: const Icon(Icons.check),
                label: Text(_saving ? 'Saving…' : 'Save product'),
              ),
            ],
          ),
        ),
      ),
    ),
  );
}

class MaintenanceScreen extends StatefulWidget {
  const MaintenanceScreen({super.key, required this.repository});
  final CoffeeRepository repository;
  @override
  State<MaintenanceScreen> createState() => _MaintenanceScreenState();
}

class _MaintenanceScreenState extends State<MaintenanceScreen> {
  bool _busy = false;
  String _result = '';
  Future<void> _run(Future<String> Function() action) async {
    setState(() {
      _busy = true;
      _result = 'Working…';
    });
    try {
      final result = await action();
      if (mounted) setState(() => _result = result);
    } catch (error) {
      if (mounted) setState(() => _result = 'Failed: $error');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Hive')),
    body: SafeArea(
      child: ListView(
        padding: const EdgeInsets.all(18),
        children: [
          const SizedBox(height: 12),
          SelectableText(
            widget.repository.directory.path,
            style: const TextStyle(fontSize: 12, color: coffeeMuted),
          ),
          const SizedBox(height: 20),
          FilledButton.icon(
            key: const Key('demo-keys'),
            onPressed: _busy
                ? null
                : () => _run(widget.repository.demonstrateKeys),
            icon: const Icon(Icons.key),
            label: const Text('test keys'),
          ),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            key: const Key('compact-boxes'),
            onPressed: _busy
                ? null
                : () => _run(widget.repository.compactBoxes),
            icon: const Icon(Icons.compress),
            label: const Text('compact boxes'),
          ),
          const SizedBox(height: 12),
          OutlinedButton.icon(
            key: const Key('demo-compaction'),
            onPressed: _busy
                ? null
                : () => _run(widget.repository.demonstrateCompaction),
            icon: const Icon(Icons.science_outlined),
            label: const Text('compact demo'),
          ),
          const SizedBox(height: 12),
          const SizedBox(height: 20),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: SelectableText(
                _result,
                key: const Key('maintenance-result'),
                style: const TextStyle(height: 1.5),
              ),
            ),
          ),
        ],
      ),
    ),
  );
}
