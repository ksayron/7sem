import 'athlete.dart';

class Footballer extends Athlete {
  String position;

  Footballer(
    String name, {
    this.position = 'defender',
    String discipline = 'football',
    int rating = 6,
  }) : super(name, discipline, rating);

  Footballer.goalkeeper(String name)
      : position = 'goalkeepr',
        super(name, 'football', 7);

  @override
  void perform() =>
      print('$name ($position) plays football');
}

class BasketballPlayer extends Athlete {
  BasketballPlayer(String name, {int rating = 7})
      : super(name, 'basketball', rating);

  @override
  void perform() => print('$name plays basketball');
}

class Swimmer extends Athlete {
  Swimmer(String name, {int rating = 6}) : super(name, 'swinmming', rating);

  @override
  void perform() => print('$name swims');
}
