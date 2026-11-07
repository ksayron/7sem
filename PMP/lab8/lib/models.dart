import 'package:hive/hive.dart';

const imagePaths = [
  'assets/images/iced_coffee.png',
  'assets/images/coffee_cup.png',
  'assets/images/espresso.png',
];
const categoryNames = ['Hot Coffee', 'Iced Coffee', 'Frappuccino', 'Mocha'];

class AppUser {
  const AppUser(this.id, this.name, this.role);
  final String id;
  final String name;
  final String role;
  bool get isAdmin => role == 'admin';
}

class CoffeeProduct {
  const CoffeeProduct({
    required this.id,
    required this.name,
    required this.asset,
    required this.priceCents,
    required this.category,
    required this.location,
    required this.description,
    required this.rating,
    required this.reviewCount,
  });
  final String id;
  final String name;
  final String asset;
  final int priceCents;
  final int category;
  final String location;
  final String description;
  final double rating;
  final int reviewCount;
  double get price => priceCents / 100;
  String get formattedPrice => price.toStringAsFixed(2);

  void validate() {
    if (id.trim().isEmpty ||
        name.trim().isEmpty ||
        location.trim().isEmpty ||
        description.trim().isEmpty ||
        !imagePaths.contains(asset) ||
        priceCents <= 0 ||
        category < 0 ||
        category >= categoryNames.length ||
        !rating.isFinite ||
        rating < 0 ||
        rating > 5 ||
        reviewCount < 0) {
      throw ArgumentError('Invalid product fields.');
    }
  }
}

class Favorite {
  const Favorite(this.userId, this.productId);
  final String userId;
  final String productId;
  String get key => '$userId:$productId';
}

class AppUserAdapter extends TypeAdapter<AppUser> {
  @override
  int get typeId => 0;
  @override
  AppUser read(BinaryReader reader) {
    final f = readFields(reader);
    return AppUser(f[0] as String, f[1] as String, f[2] as String);
  }

  @override
  void write(BinaryWriter writer, AppUser obj) =>
      writeFields(writer, {0: obj.id, 1: obj.name, 2: obj.role});
}

class CoffeeProductAdapter extends TypeAdapter<CoffeeProduct> {
  @override
  int get typeId => 1;
  @override
  CoffeeProduct read(BinaryReader reader) {
    final f = readFields(reader);
    return CoffeeProduct(
      id: f[0] as String,
      name: f[1] as String,
      asset: f[2] as String,
      priceCents: f[3] as int,
      category: f[4] as int,
      location: f[5] as String,
      description: f[6] as String,
      rating: f[7] as double,
      reviewCount: f[8] as int,
    );
  }

  @override
  void write(BinaryWriter writer, CoffeeProduct obj) => writeFields(writer, {
    0: obj.id,
    1: obj.name,
    2: obj.asset,
    3: obj.priceCents,
    4: obj.category,
    5: obj.location,
    6: obj.description,
    7: obj.rating,
    8: obj.reviewCount,
  });
}

class FavoriteAdapter extends TypeAdapter<Favorite> {
  @override
  int get typeId => 2;
  @override
  Favorite read(BinaryReader reader) {
    final f = readFields(reader);
    return Favorite(f[0] as String, f[1] as String);
  }

  @override
  void write(BinaryWriter writer, Favorite obj) =>
      writeFields(writer, {0: obj.userId, 1: obj.productId});
}

Map<int, dynamic> readFields(BinaryReader reader) {
  final count = reader.readByte();
  return {for (var i = 0; i < count; i++) reader.readByte(): reader.read()};
}

void writeFields(BinaryWriter writer, Map<int, dynamic> fields) {
  writer.writeByte(fields.length);
  for (final entry in fields.entries) {
    writer.writeByte(entry.key);
    writer.write(entry.value);
  }
}

List<CoffeeProduct> seedProducts() {
  const names = [
    'Espresso',
    'Vanilla Latte',
    'Cappuccino',
    'Caramel Mocha',
    'Frappuccino',
    'Iced Americano',
    'Caramel Latte',
  ];
  const images = [0, 1, 1, 0, 0, 2, 0];
  const prices = [300, 200, 300, 400, 400, 300, 400];
  const categories = [1, 1, 0, 3, 2, 1, 1];
  return List.generate(
    names.length,
    (i) => CoffeeProduct(
      id: 'coffee-$i',
      name: names[i],
      asset: imagePaths[images[i]],
      priceCents: prices[i],
      category: categories[i],
      location: 'Dhaka, BD',
      description:
          '${names[i]} made with freshly roasted coffee beans. '
          'Smooth, aromatic and prepared to order.',
      rating: i == 0 ? 4.9 : 4.5 + (i % 4) / 10,
      reviewCount: 200 + i * 17,
    ),
  );
}
