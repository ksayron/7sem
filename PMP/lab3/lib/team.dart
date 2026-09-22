import 'dart:convert';

import 'athlete.dart';

class Team implements Iterable<Athlete> {
  final String title;
  final List<Athlete> _members = [];

  Team(this.title);

  void add(Athlete athlete) => _members.add(athlete);

  @override
  Iterator<Athlete> get iterator => TeamIterator(_members);

  @override
  int get length => _members.length;

  @override
  bool get isEmpty => _members.isEmpty;

  @override
  bool get isNotEmpty => _members.isNotEmpty;

  @override
  Athlete get first => _members.first;

  @override
  Athlete get last => _members.last;

  @override
  Athlete get single => throw UnimplementedError();

  @override
  Athlete elementAt(int index) => _members[index];

  @override
  bool contains(Object? element) => _members.contains(element);

  @override
  Iterable<R> cast<R>() => throw UnimplementedError();

  @override
  Iterable<Athlete> followedBy(Iterable<Athlete> other) =>
      throw UnimplementedError();

  @override
  Iterable<T> map<T>(T Function(Athlete e) toElement) =>
      throw UnimplementedError();

  @override
  Iterable<Athlete> where(bool Function(Athlete e) test) =>
      throw UnimplementedError();

  @override
  Iterable<T> whereType<T>() => throw UnimplementedError();

  @override
  Iterable<R> expand<R>(Iterable<R> Function(Athlete e) toElements) =>
      throw UnimplementedError();

  @override
  void forEach(void Function(Athlete element) action) =>
      throw UnimplementedError();

  @override
  Athlete reduce(Athlete Function(Athlete value, Athlete element) combine) =>
      throw UnimplementedError();

  @override
  T fold<T>(
          T initialValue, T Function(T previousValue, Athlete element) combine) =>
      throw UnimplementedError();

  @override
  bool every(bool Function(Athlete element) test) =>
      throw UnimplementedError();

  @override
  bool any(bool Function(Athlete element) test) => throw UnimplementedError();

  @override
  String join([String separator = '']) => throw UnimplementedError();

  @override
  Iterable<Athlete> take(int count) => throw UnimplementedError();

  @override
  Iterable<Athlete> takeWhile(bool Function(Athlete element) test) =>
      throw UnimplementedError();

  @override
  Iterable<Athlete> skip(int count) => throw UnimplementedError();

  @override
  Iterable<Athlete> skipWhile(bool Function(Athlete element) test) =>
      throw UnimplementedError();

  @override
  Athlete firstWhere(bool Function(Athlete element) test,
          {Athlete Function()? orElse}) =>
      throw UnimplementedError();

  @override
  Athlete lastWhere(bool Function(Athlete element) test,
          {Athlete Function()? orElse}) =>
      throw UnimplementedError();

  @override
  Athlete singleWhere(bool Function(Athlete element) test,
          {Athlete Function()? orElse}) =>
      throw UnimplementedError();

  @override
  List<Athlete> toList({bool growable = true}) => throw UnimplementedError();

  @override
  Set<Athlete> toSet() => throw UnimplementedError();

  @override
  String toString() => 'Team($title): $length игроков';

  String toJson() => jsonEncode({
        'title': title,
        'members': _members.map((a) => a.toMap()).toList(),
      });

  factory Team.fromJson(String source) {
    final map = jsonDecode(source) as Map<String, dynamic>;
    final team = Team(map['title'] as String);
    for (final member in map['members'] as List<dynamic>) {
      team.add(Athlete.fromMap(member as Map<String, dynamic>));
    }
    return team;
  }
}

class TeamIterator implements Iterator<Athlete> {
  final List<Athlete> _members;
  int _index = -1;

  TeamIterator(this._members);

  @override
  bool moveNext() {
    if (_index + 1 < _members.length) {
      _index++;
      return true;
    }
    return false;
  }

  @override
  Athlete get current {
    if (_index < 0 || _index >= _members.length) {
      throw StateError('no current. Use moveNext()');
    }
    return _members[_index];
  }
}
