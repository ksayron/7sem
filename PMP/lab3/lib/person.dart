class InvalidRatingException implements Exception {
  final int rating;
  final String message;

  InvalidRatingException(this.rating)
      : message = '$rating not in range (1-10)';

  @override
  String toString() => 'InvalidRatingException: $message';
}

class Person {
  final String name;

  Person(this.name);

  void introduce() => print('I\'m $name.');
  void say(String phrase) => print('$name says: "$phrase"');
}
