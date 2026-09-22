import 'dart:convert';
import 'person.dart';

class Athlete extends Person implements Comparable<Athlete> {
  final String _discipline;
  int _rating = 5;

  Athlete(super.name, [this._discipline = 'general', int initialRating = 5,]) {
    rating = initialRating;
  }

  Athlete.fromMap(Map<String, dynamic> map)
      : _discipline = map['discipline'] as String? ?? 'general',
        super(map['name'] as String) {
    rating = map['rating'] as int? ?? 5;
  }


  Map<String, dynamic> toMap() => {
        'name': name,
        'discipline': _discipline,
        'rating': _rating,
      };

  String toJson() => jsonEncode(toMap());
  factory Athlete.fromJson(String source) =>
      Athlete.fromMap(jsonDecode(source) as Map<String, dynamic>);


  String get discipline => _discipline;

  bool get isPro => _rating >= 8;

  set rating(int value) {
    if (value < 1 || value > 10) {
      throw InvalidRatingException(value);
    }
    _rating = value;
    print('$name new rating: $_rating/10');
  }

  int get rating => _rating;


  @override
  int compareTo(Athlete other) => _rating.compareTo(other._rating);
  
  void train(int days, void Function(String message) onProgress) {
    for (var day = 1; day <= days; day++) {
      onProgress('$name trains on day $day ($discipline)');
    }
    print('$name завершил цикл тренировок.');
  }

  void perform() => print('$name выступает в дисциплине "$discipline"');

  @override
  void introduce() =>
      print('$name, discipline: $discipline, rating: $_rating/10.');

  @override
  String toString() => '$name (discipline: $discipline, rating: $_rating)';
}
