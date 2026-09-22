import 'excepction.dart';
import 'person.dart';


class Athlete extends Person {
  String _discipline;
  int _rating = 5;

  Athlete(super.name, [this._discipline = 'none', int initialRating = 5]) {
    rating = initialRating;
  }

  Athlete.fromMap(Map<String, dynamic> map)
      : _discipline = map['discipline'] as String? ?? 'спорт',
        super(map['name'] as String) {
    rating = map['rating'] as int? ?? 5;
  }

  String get discipline => _discipline;

  bool get isPro => _rating >= 8;

  set rating(int value) {
    if (value < 1 || value > 10) {
      throw InvalidRatingException(value);
    }
    _rating = value;
    print('$name new rating is: $_rating/10');
  }

  int get rating => _rating;

  void train(int days, void Function(String message) onProgress) {
    for (var day = 1; day <= days; day++) {
      if (day % 2 == 0) continue;
      onProgress('$name trains on day $day in $discipline');
      if (day >= 8) break; 
    }
    print('$name finished training.');
  }

  void perform() => print('$name perform in "$discipline"');

  @override
  void introduce() =>
      print('Athlete $name, discipline: $discipline, rating: $_rating/10.');

  @override
  String toString() => '$name (discipline: $discipline, rating: $_rating)';
}
