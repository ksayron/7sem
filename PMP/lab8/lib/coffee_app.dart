import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'coffee_repository.dart';
import 'models.dart';
import 'management_screens.dart';
import 'auth_screens.dart';

const coffeeBlack = Color(0xFF111111);
const coffeeCard = Color(0xFF202020);
const coffeeOrange = Color(0xFFC78050);
const coffeeMuted = Color(0xFFAAAAAA);

class CoffeeApp extends StatelessWidget {
  const CoffeeApp({super.key, required this.repository});
  final CoffeeRepository repository;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: repository,
    builder: (_, _) => MaterialApp(
      // A session change discards every route belonging to the previous user.
      key: ValueKey(repository.sessionUser?.id),
      title: 'Lab 8 · Hive',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        brightness: Brightness.dark,
        scaffoldBackgroundColor: coffeeBlack,
        colorScheme: const ColorScheme.dark(
          primary: coffeeOrange,
          surface: coffeeCard,
        ),
        splashFactory: InkRipple.splashFactory,
        useMaterial3: true,
        snackBarTheme: const SnackBarThemeData(
          backgroundColor: coffeeCard,
          contentTextStyle: TextStyle(color: Colors.white),
          behavior: SnackBarBehavior.floating,
        ),
      ),
      home: repository.isSignedIn
          ? CatalogScreen(repository: repository)
          : LoginScreen(repository: repository),
    ),
  );
}

Future<void> openProduct(
  BuildContext context,
  CoffeeRepository repository,
  String id,
) => Navigator.of(context).push(
  MaterialPageRoute<void>(
    settings: RouteSettings(name: '/product/$id'),
    builder: (_) => ListenableBuilder(
      listenable: repository,
      builder: (context, _) {
        final product = repository.product(id);
        return product == null
            ? Scaffold(
                appBar: AppBar(title: const Text('Product unavailable')),
                body: const Center(
                  child: Text('This product has been deleted.'),
                ),
              )
            : CoffeeDetailsScreen(coffee: product, repository: repository);
      },
    ),
  ),
);

Future<void> runCoffeeAction(
  BuildContext context,
  Future<void> Function() action,
) async {
  try {
    await action();
  } catch (error) {
    if (context.mounted) coffeeMessage(context, error.toString());
  }
}

void coffeeMessage(BuildContext context, String message) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(message)));
}

class CatalogScreen extends StatefulWidget {
  const CatalogScreen({super.key, required this.repository});
  final CoffeeRepository repository;

  @override
  State<CatalogScreen> createState() => _CatalogScreenState();
}

class _CatalogScreenState extends State<CatalogScreen> {
  CoffeeRepository get repository => widget.repository;
  List<CoffeeProduct> get coffees => repository.products;
  int _category = 1;
  int _tab = 0;
  String _query = '';
  bool _showAll = false;
  bool _sortByPrice = false;

  void _favorite(CoffeeProduct coffee) => runCoffeeAction(
    context,
    () => repository.setFavorite(coffee.id, !repository.isFavorite(coffee.id)),
  );

  Future<void> _openCoffee(CoffeeProduct coffee) async {
    FocusManager.instance.primaryFocus?.unfocus();
    await openProduct(context, repository, coffee.id);
  }

  @override
  Widget build(BuildContext context) {
    final products = coffees.where((coffee) {
      final categoryMatches = _showAll || coffee.category == _category;
      return (_tab == 1 ? repository.isFavorite(coffee.id) : categoryMatches) &&
          coffee.name.toLowerCase().contains(_query.toLowerCase().trim());
    }).toList();
    if (_sortByPrice) products.sort((a, b) => a.price.compareTo(b.price));

    return AnnotatedRegion<SystemUiOverlayStyle>(
      value: const SystemUiOverlayStyle(
        statusBarColor: Colors.transparent,
        statusBarIconBrightness: Brightness.light,
        systemNavigationBarColor: coffeeBlack,
        systemNavigationBarIconBrightness: Brightness.light,
      ),
      child: Scaffold(
        body: SafeArea(
          bottom: false,
          child: CustomScrollView(
            slivers: [
              SliverPadding(
                padding: const EdgeInsets.fromLTRB(18, 8, 18, 0),
                sliver: SliverList.list(
                  children: [
                    _header(context),
                    if (repository.currentUser.isAdmin)
                      Align(
                        alignment: Alignment.centerRight,
                        child: TextButton.icon(
                          key: const Key('add-product'),
                          onPressed: () => openProductForm(context, repository),
                          icon: const Icon(Icons.add),
                          label: const Text('Add product'),
                        ),
                      ),
                    const SizedBox(height: 26),
                    if (coffees.isNotEmpty)
                      _OfferBanner(onOrder: () => _openCoffee(coffees.first)),
                    const SizedBox(height: 22),
                    Row(
                      children: [
                        Expanded(
                          child: Container(
                            height: 46,
                            decoration: BoxDecoration(
                              color: coffeeCard,
                              borderRadius: BorderRadius.circular(30),
                            ),
                            child: TextField(
                              key: const Key('coffee-search'),
                              onChanged: (value) =>
                                  setState(() => _query = value),
                              style: const TextStyle(fontSize: 13),
                              decoration: InputDecoration(
                                border: InputBorder.none,
                                hintText: 'Search coffee...',
                                hintStyle: const TextStyle(
                                  fontSize: 11,
                                  color: coffeeMuted,
                                ),
                                prefixIcon: const Icon(
                                  Icons.search,
                                  color: coffeeMuted,
                                  size: 25,
                                ),
                                suffixIcon: IconButton(
                                  tooltip: 'Voice search',
                                  onPressed: () => coffeeMessage(
                                    context,
                                    'Type a coffee name to search the menu.',
                                  ),
                                  icon: const Icon(
                                    Icons.mic_none,
                                    color: coffeeMuted,
                                  ),
                                ),
                              ),
                            ),
                          ),
                        ),
                        const SizedBox(width: 10),
                        _RoundButton(
                          icon: Icons.tune,
                          color: coffeeOrange,
                          label: 'Sort by price',
                          onTap: () =>
                              setState(() => _sortByPrice = !_sortByPrice),
                        ),
                      ],
                    ),
                    const SizedBox(height: 14),
                    Row(
                      children: [
                        Text(
                          _tab == 1 ? 'Favorites' : 'Categories',
                          style: const TextStyle(
                            fontSize: 16,
                            fontWeight: FontWeight.w500,
                          ),
                        ),
                        const Spacer(),
                        TextButton(
                          onPressed: () => setState(() => _showAll = !_showAll),
                          style: TextButton.styleFrom(
                            foregroundColor: coffeeMuted,
                          ),
                          child: Text(
                            _showAll ? 'Show less' : 'See all',
                            style: const TextStyle(fontSize: 11),
                          ),
                        ),
                      ],
                    ),
                    _categories(),
                    const SizedBox(height: 20),
                  ],
                ),
              ),
              if (products.isEmpty)
                const SliverToBoxAdapter(
                  child: Padding(
                    padding: EdgeInsets.all(40),
                    child: Text(
                      'No coffee found',
                      textAlign: TextAlign.center,
                      style: TextStyle(color: coffeeMuted),
                    ),
                  ),
                ),
              SliverPadding(
                padding: const EdgeInsets.fromLTRB(18, 0, 18, 20),
                sliver: SliverGrid.builder(
                  gridDelegate: const SliverGridDelegateWithFixedCrossAxisCount(
                    crossAxisCount: 2,
                    mainAxisSpacing: 18,
                    crossAxisSpacing: 18,
                    mainAxisExtent: 220,
                  ),
                  itemCount: products.length,
                  itemBuilder: (context, index) {
                    final coffee = products[index];
                    return _CoffeeCard(
                      coffee: coffee,
                      favorite: repository.isFavorite(coffee.id),
                      onFavorite: () => _favorite(coffee),
                      onTap: () => _openCoffee(coffee),
                    );
                  },
                ),
              ),
            ],
          ),
        ),
        bottomNavigationBar: _bottomBar(),
      ),
    );
  }

  Widget _header(BuildContext context) => Row(
    children: [
      _RoundButton(
        icon: Icons.menu,
        label: 'Menu',
        size: 38,
        onTap: () => showModalBottomSheet<void>(
          context: context,
          backgroundColor: coffeeCard,
          builder: (context) => SafeArea(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: Column(
                mainAxisSize: MainAxisSize.min,
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  const Text('Coffee Shop', style: TextStyle(fontSize: 24)),
                  const SizedBox(height: 10),
                  const Text(
                    'Fresh coffee, made your way.',
                    style: TextStyle(color: coffeeMuted),
                  ),
                  ListTile(
                    contentPadding: EdgeInsets.zero,
                    leading: const Icon(
                      Icons.local_cafe_outlined,
                      color: coffeeOrange,
                    ),
                    title: const Text('Open Espresso'),
                    onTap: () {
                      Navigator.pop(context);
                      if (coffees.isNotEmpty) _openCoffee(coffees.first);
                    },
                  ),
                ],
              ),
            ),
          ),
        ),
      ),
      const SizedBox(width: 12),
      const Icon(Icons.location_on_outlined, size: 19, color: coffeeMuted),
      const Text(
        'Dhaka, BD',
        style: TextStyle(fontSize: 16, fontWeight: FontWeight.w500),
      ),
      const Spacer(),
      Stack(
        children: [
          _RoundButton(
            icon: Icons.notifications_none,
            label: 'Notifications',
            size: 38,
            onTap: () =>
                coffeeMessage(context, 'Today only: 20% off all Espresso!'),
          ),
          const Positioned(
            right: 10,
            top: 7,
            child: CircleAvatar(radius: 3, backgroundColor: coffeeOrange),
          ),
        ],
      ),
    ],
  );

  Widget _categories() {
    const labels = ['Hot Coffee', 'Iced Coffee', 'Frappuccino', 'Mocha'];
    const icons = [
      Icons.coffee_outlined,
      Icons.ac_unit,
      Icons.local_drink_outlined,
      Icons.local_cafe_outlined,
    ];
    return SizedBox(
      height: 36,
      child: ListView.separated(
        scrollDirection: Axis.horizontal,
        itemCount: labels.length,
        separatorBuilder: (_, _) => const SizedBox(width: 10),
        itemBuilder: (context, index) {
          final selected = !_showAll && index == _category;
          return Material(
            color: selected ? coffeeOrange : Colors.transparent,
            shape: StadiumBorder(
              side: BorderSide(
                color: selected ? coffeeOrange : const Color(0xFF484848),
              ),
            ),
            child: InkWell(
              customBorder: const StadiumBorder(),
              onTap: () => setState(() {
                _category = index;
                _showAll = false;
              }),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 10),
                child: Row(
                  children: [
                    Icon(
                      icons[index],
                      size: 17,
                      color: selected ? Colors.white : coffeeMuted,
                    ),
                    const SizedBox(width: 5),
                    Text(
                      labels[index],
                      style: TextStyle(
                        fontSize: 10,
                        color: selected ? Colors.white : coffeeMuted,
                      ),
                    ),
                  ],
                ),
              ),
            ),
          );
        },
      ),
    );
  }

  Widget _bottomBar() {
    const labels = ['Home', 'Favorites', 'Custom', 'Hive', 'Profile'];
    const icons = [
      Icons.home_outlined,
      Icons.favorite_border,
      Icons.local_cafe_outlined,
      Icons.settings_outlined,
      Icons.person_outline,
    ];
    return Container(
      color: const Color(0xFF191919),
      child: SafeArea(
        top: false,
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 6),
          child: Row(
            children: List.generate(
              5,
              (index) => Expanded(
                child: InkWell(
                  borderRadius: BorderRadius.circular(14),
                  onTap: () {
                    if (index == 0) {
                      setState(() => _tab = 0);
                    } else if (index == 1) {
                      Navigator.push(
                        context,
                        MaterialPageRoute<void>(
                          settings: const RouteSettings(name: '/favorites'),
                          builder: (_) =>
                              FavoritesScreen(repository: repository),
                        ),
                      );
                    } else if (index == 2) {
                      if (coffees.isNotEmpty) _openCoffee(coffees.first);
                    } else if (index == 3) {
                      Navigator.push(
                        context,
                        MaterialPageRoute<void>(
                          settings: const RouteSettings(name: '/hive'),
                          builder: (_) =>
                              MaintenanceScreen(repository: repository),
                        ),
                      );
                    } else {
                      Navigator.push(
                        context,
                        MaterialPageRoute<void>(
                          settings: const RouteSettings(name: '/profile'),
                          builder: (_) => ProfileScreen(repository: repository),
                        ),
                      );
                    }
                  },
                  child: Padding(
                    padding: const EdgeInsets.symmetric(vertical: 8),
                    child: Column(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Icon(
                          icons[index],
                          color: _tab == index ? coffeeOrange : Colors.white70,
                          size: 25,
                        ),
                        const SizedBox(height: 4),
                        Text(
                          labels[index],
                          style: TextStyle(
                            fontSize: 11,
                            color: _tab == index
                                ? coffeeOrange
                                : Colors.white70,
                          ),
                        ),
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

class _OfferBanner extends StatelessWidget {
  const _OfferBanner({required this.onOrder});
  final VoidCallback onOrder;

  @override
  Widget build(BuildContext context) => ClipRRect(
    borderRadius: BorderRadius.circular(18),
    child: SizedBox(
      height: 182,
      child: Stack(
        children: [
          const Positioned.fill(
            child: DecoratedBox(
              decoration: BoxDecoration(
                gradient: LinearGradient(
                  begin: Alignment.topLeft,
                  end: Alignment.bottomRight,
                  colors: [Color(0xFF262522), Color(0xFF171717)],
                ),
              ),
            ),
          ),
          Positioned(
            right: -12,
            bottom: -12,
            width: 176,
            height: 177,
            child: Image.asset(
              'assets/images/espresso.png',
              fit: BoxFit.contain,
            ),
          ),
          Positioned(
            left: 12,
            top: 12,
            right: 80,
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Text.rich(
                  TextSpan(
                    children: [
                      TextSpan(
                        text: '20% ',
                        style: TextStyle(color: coffeeOrange),
                      ),
                      TextSpan(text: 'OFF On All\nEspresso!'),
                    ],
                  ),
                  style: TextStyle(
                    fontSize: 26,
                    height: 1.23,
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 8),
                const Text(
                  'Today Only · Limited Time Offer',
                  style: TextStyle(color: coffeeMuted, fontSize: 10),
                ),
                const SizedBox(height: 18),
                SizedBox(
                  height: 33,
                  child: FilledButton(
                    onPressed: onOrder,
                    style: FilledButton.styleFrom(
                      backgroundColor: coffeeOrange,
                      foregroundColor: Colors.white,
                      padding: const EdgeInsets.symmetric(horizontal: 22),
                    ),
                    child: const Text(
                      'Order Now',
                      style: TextStyle(fontSize: 11),
                    ),
                  ),
                ),
              ],
            ),
          ),
        ],
      ),
    ),
  );
}

class _CoffeeCard extends StatelessWidget {
  const _CoffeeCard({
    required this.coffee,
    required this.favorite,
    required this.onFavorite,
    required this.onTap,
  });
  final CoffeeProduct coffee;
  final bool favorite;
  final VoidCallback onFavorite;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => Material(
    color: coffeeCard,
    borderRadius: BorderRadius.circular(10),
    clipBehavior: Clip.antiAlias,
    child: InkWell(
      key: Key('product-${coffee.name}'),
      onTap: onTap,
      child: Stack(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(12, 24, 12, 13),
            child: Column(
              children: [
                Expanded(child: Image.asset(coffee.asset, fit: BoxFit.contain)),
                const SizedBox(height: 12),
                Text(
                  coffee.name,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: const TextStyle(
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 4),
                Text(
                  'USD \$${coffee.formattedPrice}',
                  style: const TextStyle(fontSize: 12, color: coffeeOrange),
                ),
              ],
            ),
          ),
          Positioned(
            right: 4,
            top: 3,
            child: IconButton(
              tooltip: 'Favorite ${coffee.name}',
              onPressed: onFavorite,
              iconSize: 18,
              icon: Icon(
                favorite ? Icons.favorite : Icons.favorite_border,
                color: favorite ? Colors.white : const Color(0xFF505050),
              ),
            ),
          ),
        ],
      ),
    ),
  );
}

class CoffeeDetailsScreen extends StatefulWidget {
  const CoffeeDetailsScreen({
    super.key,
    required this.coffee,
    required this.repository,
  });
  final CoffeeProduct coffee;
  final CoffeeRepository repository;

  @override
  State<CoffeeDetailsScreen> createState() => _CoffeeDetailsScreenState();
}

class _CoffeeDetailsScreenState extends State<CoffeeDetailsScreen> {
  int _quantity = 1;
  int _size = 2;

  final _options = <String, bool>{
    'Extra Milk': true,
    'Less Sugar': true,
    'Add Flavor': true,
    'Extra Shot': true,
    'Whipped Cream': true,
  };

  @override
  Widget build(BuildContext context) {
    final price =
        (widget.coffee.price *
            (_size == 0
                ? 0.8
                : _size == 1
                ? 0.9
                : 1.0)) *
        _quantity;
    return AnnotatedRegion<SystemUiOverlayStyle>(
      value: const SystemUiOverlayStyle(
        statusBarColor: Colors.transparent,
        statusBarIconBrightness: Brightness.light,
        systemNavigationBarColor: coffeeBlack,
        systemNavigationBarIconBrightness: Brightness.light,
      ),
      child: Scaffold(
        backgroundColor: coffeeBlack,
        body: LayoutBuilder(
          builder: (context, constraints) {
            final heroHeight = (constraints.maxHeight * 0.46).clamp(
              300.0,
              390.0,
            );
            final topInset = MediaQuery.paddingOf(context).top;
            return Stack(
              children: [
                Positioned(
                  top: 0,
                  left: 0,
                  right: 0,
                  height: heroHeight,
                  child: const ColoredBox(color: coffeeOrange),
                ),
                Positioned.fill(
                  top: heroHeight * 0.61,
                  child: const DecoratedBox(
                    decoration: BoxDecoration(
                      color: coffeeBlack,
                      borderRadius: BorderRadius.vertical(
                        top: Radius.circular(46),
                      ),
                    ),
                  ),
                ),
                SingleChildScrollView(
                  padding: EdgeInsets.only(
                    bottom: 120 + MediaQuery.paddingOf(context).bottom,
                  ),
                  child: Column(
                    children: [
                      SizedBox(
                        height: heroHeight,
                        child: Stack(
                          children: [
                            Positioned(
                              top: topInset + 67,
                              left: 14,
                              right: 14,
                              child: FittedBox(
                                child: Text(
                                  categoryNames[widget.coffee.category],
                                  style: TextStyle(
                                    fontSize: 70,
                                    fontWeight: FontWeight.w800,
                                    color: Colors.white.withValues(alpha: 0.27),
                                  ),
                                ),
                              ),
                            ),
                            Positioned(
                              left: 28,
                              right: 28,
                              top: topInset + 8,
                              bottom: 0,
                              child: Image.asset(
                                widget.coffee.asset,
                                fit: BoxFit.contain,
                              ),
                            ),
                            Positioned(
                              top: topInset + 8,
                              left: 16,
                              right: 16,
                              child: Row(
                                children: [
                                  _RoundButton(
                                    icon: Icons.arrow_back,
                                    color: Colors.white.withValues(alpha: 0.06),
                                    label: 'Back',
                                    onTap: () => Navigator.pop(context),
                                  ),
                                  const Spacer(),
                                  _RoundButton(
                                    icon:
                                        widget.repository.isFavorite(
                                          widget.coffee.id,
                                        )
                                        ? Icons.favorite
                                        : Icons.favorite_border,
                                    color: Colors.white.withValues(alpha: 0.09),
                                    label: 'Favorite',
                                    onTap: () {
                                      runCoffeeAction(
                                        context,
                                        () => widget.repository.setFavorite(
                                          widget.coffee.id,
                                          !widget.repository.isFavorite(
                                            widget.coffee.id,
                                          ),
                                        ),
                                      );
                                    },
                                  ),
                                ],
                              ),
                            ),
                          ],
                        ),
                      ),
                      Padding(
                        padding: const EdgeInsets.fromLTRB(18, 12, 18, 0),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            Row(
                              children: [
                                Expanded(
                                  child: Text(
                                    widget.coffee.name,
                                    style: const TextStyle(
                                      fontSize: 28,
                                      fontWeight: FontWeight.w500,
                                    ),
                                  ),
                                ),
                                const Icon(
                                  Icons.star,
                                  color: coffeeOrange,
                                  size: 18,
                                ),
                                const SizedBox(width: 4),
                                Text(
                                  '${widget.coffee.rating.toStringAsFixed(1)} ',
                                  style: TextStyle(fontSize: 14),
                                ),
                                Text(
                                  '(${widget.coffee.reviewCount} Reviews)',
                                  style: TextStyle(fontSize: 10),
                                ),
                              ],
                            ),
                            const SizedBox(height: 12),
                            Text(
                              widget.coffee.location,
                              key: const Key('product-location'),
                              style: const TextStyle(color: coffeeOrange),
                            ),
                            const SizedBox(height: 8),
                            Text(
                              widget.coffee.description,
                              key: const Key('product-description'),
                              style: const TextStyle(
                                color: coffeeMuted,
                                height: 1.5,
                              ),
                            ),
                            if (widget.repository.currentUser.isAdmin)
                              Wrap(
                                spacing: 12,
                                children: [
                                  TextButton.icon(
                                    key: const Key('edit-product'),
                                    onPressed: () => openProductForm(
                                      context,
                                      widget.repository,
                                      product: widget.coffee,
                                    ),
                                    icon: const Icon(Icons.edit_outlined),
                                    label: const Text('Edit product'),
                                  ),
                                  TextButton.icon(
                                    key: const Key('delete-product'),
                                    onPressed: () => confirmDelete(
                                      context,
                                      widget.repository,
                                      widget.coffee,
                                    ),
                                    icon: const Icon(Icons.delete_outline),
                                    label: const Text('Delete product'),
                                  ),
                                ],
                              ),
                            const SizedBox(height: 20),
                            Row(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              children: [
                                Expanded(
                                  child: Column(
                                    crossAxisAlignment:
                                        CrossAxisAlignment.start,
                                    children: [
                                      const Text(
                                        'Customizations:',
                                        style: TextStyle(fontSize: 14),
                                      ),
                                      const SizedBox(height: 4),
                                      Wrap(
                                        spacing: 12,
                                        runSpacing: 4,
                                        children: [
                                          'Extra Milk',
                                          'Less Sugar',
                                          'Add Flavor',
                                        ].map(_option).toList(),
                                      ),
                                      const SizedBox(height: 12),
                                      const Text(
                                        'Add-Ons:',
                                        style: TextStyle(fontSize: 14),
                                      ),
                                      const SizedBox(height: 4),
                                      Wrap(
                                        spacing: 14,
                                        runSpacing: 4,
                                        children: [
                                          'Extra Shot',
                                          'Whipped Cream',
                                        ].map(_option).toList(),
                                      ),
                                    ],
                                  ),
                                ),
                                const SizedBox(width: 8),
                                Column(
                                  children: [
                                    _RoundButton(
                                      size: 34,
                                      icon: Icons.remove,
                                      color: coffeeOrange,
                                      label: 'Decrease quantity',
                                      onTap: () {
                                        if (_quantity > 1) {
                                          setState(() => _quantity--);
                                        }
                                      },
                                    ),
                                    Padding(
                                      padding: const EdgeInsets.symmetric(
                                        vertical: 10,
                                      ),
                                      child: Text(
                                        '$_quantity',
                                        key: const Key('quantity'),
                                        style: const TextStyle(fontSize: 16),
                                      ),
                                    ),
                                    _RoundButton(
                                      size: 34,
                                      icon: Icons.add,
                                      color: coffeeOrange,
                                      label: 'Increase quantity',
                                      onTap: () => setState(() => _quantity++),
                                    ),
                                  ],
                                ),
                              ],
                            ),
                            const SizedBox(height: 22),
                            const Text('Size', style: TextStyle(fontSize: 14)),
                            const SizedBox(height: 8),
                            Row(
                              children: List.generate(3, (index) {
                                const sizes = ['Small', 'Medium', 'Large'];
                                return Expanded(
                                  child: Column(
                                    children: [
                                      Semantics(
                                        button: true,
                                        selected: _size == index,
                                        label: '${sizes[index]} coffee',
                                        child: InkWell(
                                          key: Key('size-${sizes[index]}'),
                                          onTap: () =>
                                              setState(() => _size = index),
                                          borderRadius: BorderRadius.circular(
                                            7,
                                          ),
                                          child: Container(
                                            width: 68,
                                            height: 74,
                                            decoration: BoxDecoration(
                                              borderRadius:
                                                  BorderRadius.circular(7),
                                              border: Border.all(
                                                color: _size == index
                                                    ? coffeeOrange.withValues(
                                                        alpha: 0.55,
                                                      )
                                                    : Colors.transparent,
                                              ),
                                            ),
                                            alignment: Alignment.bottomCenter,
                                            padding: const EdgeInsets.all(5),
                                            child: Image.asset(
                                              widget.coffee.asset,
                                              height: 44 + index * 9.0,
                                              fit: BoxFit.contain,
                                            ),
                                          ),
                                        ),
                                      ),
                                      const SizedBox(height: 5),
                                      Text(
                                        sizes[index],
                                        style: const TextStyle(fontSize: 13),
                                      ),
                                    ],
                                  ),
                                );
                              }),
                            ),
                          ],
                        ),
                      ),
                    ],
                  ),
                ),
                Positioned(
                  bottom: 0,
                  left: 0,
                  right: 0,
                  child: Container(
                    color: coffeeBlack,
                    child: SafeArea(
                      top: false,
                      child: Padding(
                        padding: const EdgeInsets.fromLTRB(18, 18, 18, 24),
                        child: Row(
                          crossAxisAlignment: CrossAxisAlignment.center,
                          children: [
                            Text(
                              '\$${price == price.roundToDouble() ? price.toInt() : price.toStringAsFixed(2)}',
                              key: const Key('total-price'),
                              style: const TextStyle(
                                fontSize: 34,
                                fontWeight: FontWeight.w400,
                              ),
                            ),
                            const SizedBox(width: 16),
                            Expanded(
                              child: SizedBox(
                                height: 50,
                                child: FilledButton.icon(
                                  key: const Key('add-to-cart'),
                                  style: FilledButton.styleFrom(
                                    backgroundColor: coffeeOrange,
                                    foregroundColor: Colors.white,
                                    padding: const EdgeInsets.symmetric(
                                      horizontal: 8,
                                    ),
                                  ),
                                  onPressed: () => coffeeMessage(
                                    context,
                                    'Preview: $_quantity × ${widget.coffee.name}. No order is stored.',
                                  ),
                                  icon: const Icon(
                                    Icons.shopping_cart_outlined,
                                    size: 23,
                                  ),
                                  label: const Text(
                                    'Add to Cart',
                                    style: TextStyle(fontSize: 14),
                                  ),
                                ),
                              ),
                            ),
                          ],
                        ),
                      ),
                    ),
                  ),
                ),
              ],
            );
          },
        ),
      ),
    );
  }

  Widget _option(String label) => Semantics(
    checked: _options[label],
    child: InkWell(
      borderRadius: BorderRadius.circular(4),
      onTap: () => setState(() => _options[label] = !_options[label]!),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 4),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(
              _options[label]!
                  ? Icons.check_box_outlined
                  : Icons.check_box_outline_blank,
              size: 15,
              color: coffeeMuted,
            ),
            const SizedBox(width: 4),
            Text(
              label,
              style: const TextStyle(fontSize: 10, color: coffeeMuted),
            ),
          ],
        ),
      ),
    ),
  );
}

class _RoundButton extends StatelessWidget {
  const _RoundButton({
    required this.icon,
    required this.label,
    required this.onTap,
    this.color = const Color(0xFF191919),
    this.size = 46,
  });
  final IconData icon;
  final String label;
  final VoidCallback onTap;
  final Color color;
  final double size;

  @override
  Widget build(BuildContext context) => SizedBox(
    width: size,
    height: size,
    child: IconButton(
      tooltip: label,
      onPressed: onTap,
      style: IconButton.styleFrom(
        backgroundColor: color,
        padding: EdgeInsets.zero,
      ),
      icon: Icon(
        icon,
        size: size < 40 ? 22 : 24,
        color: Colors.white.withValues(alpha: 0.85),
      ),
    ),
  );
}
