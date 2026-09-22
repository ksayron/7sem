class InvalidRatingException implements Exception {
  int rating;
  String message;

  InvalidRatingException(this.rating)
      : message = 'passed value $rating is out of valid range (1 - 10)';

  @override
  String toString() => 'InvalidRatingException: $message';
}
