import 'dart:async';

import 'athlete.dart';

class ServerException implements Exception {
  final String message;

  ServerException(this.message);

  @override
  String toString() => 'server error: $message';
}

Future<Athlete> fetchAthlete(
  String name, {
  Duration delay = const Duration(milliseconds: 300),
}) async {
  await Future.delayed(delay);
  print('[api call] $name data was fetched');
  return Athlete(name, 'football', 8);
}

Future<int> fetchRating(String name) async {
  await Future.delayed(const Duration(milliseconds: 150));
  if (name.isEmpty) {
    throw ServerException('name not defined');
  }
  return 5 + name.length % 5;
}


Stream<int> trainingLoad({int days = 7}) async* {
  for (var day = 1; day <= days; day++) {
    await Future.delayed(const Duration(milliseconds: 80));
    yield day * 10;
  }
}

Stream<int> unstableLoad() async* {
  await Future.delayed(const Duration(milliseconds: 80));
  yield 10;
  await Future.delayed(const Duration(milliseconds: 80));
  throw ServerException('server load error');
}
