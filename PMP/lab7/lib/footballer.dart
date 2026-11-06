
class Footballer {
  Footballer({
    this.id,
    required this.name,
    this.discipline = 'Футбол',
    required this.position,
    required this.rating,
    required this.club,
  }) {
    if (name.trim().isEmpty ||
        name.length > 80 ||
        discipline.trim().isEmpty ||
        discipline.length > 80 ||
        position.trim().isEmpty ||
        position.length > 80 ||
        club.trim().isEmpty ||
        club.length > 80 ||
        rating < 1 ||
        rating > 10) {
      throw const FormatException(
        'Заполните поля (до 80 символов). Рейтинг: 1–10.',
      );
    }
  }
  final int? id;
  final String name;
  final String discipline;
  final String position;
  final int rating;
  final String club;
  bool get isPro => rating >= 8;

  factory Footballer.fromMap(Map<String, Object?> map) => Footballer(
    id: map['id'] as int?,
    name: map['name'] as String,
    discipline: map['discipline'] as String,
    position: map['position'] as String,
    rating: map['rating'] as int,
    club: map['club'] as String,
  );
  Map<String, Object?> toMap() => {
    if (id != null) 'id': id,
    'name': name,
    'discipline': discipline,
    'position': position,
    'rating': rating,
    'club': club,
  };
  Footballer fileVariant(int index) => Footballer(
    id: id,
    name: '${name.length > 60 ? name.substring(0, 60) : name} · ${index + 1}',
    discipline: discipline,
    position: position,
    rating: (rating + index) % 10 + 1,
    club:
        '${club.length > 55 ? club.substring(0, 55) : club} · копия ${index + 1}',
  );
}

List<Footballer> demoFootballers() => [
  Footballer(
    name: 'Митя Дупкин',
    position: 'Нападающий',
    rating: 9,
    club: 'Динамо',
  ),
  Footballer(
    name: 'Илья Лупкин',
    position: 'Вратарь',
    rating: 8,
    club: 'Динамо',
  ),
  Footballer(
    name: 'Максим Орупкин',
    position: 'Защитник',
    rating: 7,
    club: 'Неман',
  ),
  Footballer(
    name: 'Даниил Козупкин',
    position: 'Полузащитник',
    rating: 8,
    club: 'БАТЭ',
  ),
  Footballer(
    name: 'Никита Белупкин',
    position: 'Защитник',
    rating: 6,
    club: 'Неман',
  ),
  Footballer(
    name: 'Артём Морупкин',
    position: 'Нападающий',
    rating: 9,
    club: 'БАТЭ',
  ),
];
