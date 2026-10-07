import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

const coffeeBlack = Color(0xFF111111);
const coffeeCard = Color(0xFF202020);
const coffeeOrange = Color(0xFFC78050);
const coffeeMuted = Color(0xFFAAAAAA);

void main() {
  WidgetsFlutterBinding.ensureInitialized();
  SystemChrome.setPreferredOrientations([DeviceOrientation.portraitUp]);
  runApp(const CoffeeApp());
}

class CoffeeApp extends StatelessWidget {
  const CoffeeApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
    title: 'Lab 4–5',
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
    home: const CatalogScreen(),
  );
}

class Coffee {
  const Coffee(this.name, this.asset, this.price, this.category);
  final String name;
  final String asset;
  final int price;
  final int category;
}

const coffees = [
  Coffee('Espresso', 'assets/images/iced_coffee.png', 3, 1),
  Coffee('Vanilla Latte', 'assets/images/coffee_cup.png', 2, 1),
  Coffee('Cappuccino', 'assets/images/coffee_cup.png', 3, 0),
  Coffee('Caramel Mocha', 'assets/images/iced_coffee.png', 4, 3),
  Coffee('Frappuccino', 'assets/images/iced_coffee.png', 4, 2),
  Coffee('Iced Americano', 'assets/images/espresso.png', 3, 1),
  Coffee('Caramel Latte', 'assets/images/iced_coffee.png', 4, 1),
];

void coffeeMessage(BuildContext context, String message) {
  ScaffoldMessenger.of(context)
    ..hideCurrentSnackBar()
    ..showSnackBar(SnackBar(content: Text(message)));
}

class CatalogScreen extends StatefulWidget {
  const CatalogScreen({super.key});

  @override
  State<CatalogScreen> createState() => _CatalogScreenState();
}

class _CatalogScreenState extends State<CatalogScreen> {
  final _favorites = <String>{'Vanilla Latte'};
  int _category = 1;
  int _tab = 0;
  String _query = '';
  bool _showAll = false;
  bool _sortByPrice = false;

  void _favorite(Coffee coffee) => setState(() {
    if (!_favorites.add(coffee.name)) _favorites.remove(coffee.name);
  });

  Future<void> _openCoffee(Coffee coffee) async {
    FocusManager.instance.primaryFocus?.unfocus();
    await Navigator.of(context).push(
      MaterialPageRoute<void>(
        builder: (_) => CoffeeDetailsScreen(
          coffee: coffee,
          favorite: _favorites.contains(coffee.name),
          onFavorite: () => _favorite(coffee),
        ),
      ),
    );
    if (mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) {
    final products = coffees.where((coffee) {
      final categoryMatches = _showAll || coffee.category == _category;
      return (_tab == 1 ? _favorites.contains(coffee.name) : categoryMatches) &&
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
                    const SizedBox(height: 26),
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
                    childAspectRatio: 0.74,
                  ),
                  itemCount: products.length,
                  itemBuilder: (context, index) {
                    final coffee = products[index];
                    return _CoffeeCard(
                      coffee: coffee,
                      favorite: _favorites.contains(coffee.name),
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
                      _openCoffee(coffees.first);
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
    const labels = ['Home', 'Favorite', 'Custom', 'Setting', 'Profile'];
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
                    if (index < 2) {
                      setState(() => _tab = index);
                    } else if (index == 2) {
                      _openCoffee(coffees.first);
                    } else {
                      coffeeMessage(
                        context,
                        index == 3
                            ? 'Dark coffee theme is enabled.'
                            : 'Welcome, coffee lover!',
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
  final Coffee coffee;
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
                Row(
                  children: [
                    Expanded(
                      child: Text(
                        coffee.name,
                        style: const TextStyle(
                          fontSize: 11,
                          fontWeight: FontWeight.w500,
                        ),
                      ),
                    ),
                    const Text(
                      'USD ',
                      style: TextStyle(fontSize: 7, color: coffeeMuted),
                    ),
                    Text(
                      '\$${coffee.price}',
                      style: const TextStyle(
                        fontSize: 12,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                  ],
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
    required this.favorite,
    required this.onFavorite,
  });
  final Coffee coffee;
  final bool favorite;
  final VoidCallback onFavorite;

  @override
  State<CoffeeDetailsScreen> createState() => _CoffeeDetailsScreenState();
}

class _CoffeeDetailsScreenState extends State<CoffeeDetailsScreen> {
  int _quantity = 1;
  int _size = 2;
  late bool _favorite = widget.favorite;
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
        (widget.coffee.price - 1 + (_size == 0 ? -0.5 : 0)) * _quantity;
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
                                  'Iced Coffee',
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
                                    icon: _favorite
                                        ? Icons.favorite
                                        : Icons.favorite_border,
                                    color: Colors.white.withValues(alpha: 0.09),
                                    label: 'Favorite',
                                    onTap: () {
                                      widget.onFavorite();
                                      setState(() => _favorite = !_favorite);
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
                                const Text(
                                  '4.9 ',
                                  style: TextStyle(fontSize: 14),
                                ),
                                const Text(
                                  '(200+ Reviews)',
                                  style: TextStyle(fontSize: 10),
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
                            const SizedBox(width: 8),
                            Text(
                              '\$${(price * 1.25).toStringAsFixed(2)}',
                              style: const TextStyle(
                                color: coffeeMuted,
                                fontSize: 16,
                                decoration: TextDecoration.lineThrough,
                              ),
                            ),
                            const SizedBox(width: 25),
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
                                    '$_quantity × ${widget.coffee.name} added to cart',
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
