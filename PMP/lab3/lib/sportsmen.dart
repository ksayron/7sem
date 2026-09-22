import 'athlete.dart';
import 'mixins.dart';

class Footballer extends Athlete with Trainable, Awardable {
  final String position;

  Footballer(
    String name, {
    this.position = 'defender',
    String discipline = 'football',
    int rating = 6,
  }) : super(name, discipline, rating);

  Footballer.goalkeeper(String name)
      : position = 'goalkeeper',
        super(name, 'football', 7);

  @override
  void perform() =>
      print('$name ($position) plays football.');
}

class BasketballPlayer extends Athlete with Trainable, Awardable, Showman {
  BasketballPlayer(String name, {int rating = 7})
      : super(name, 'basketball', rating);

  @override
  void perform() {
    print('plays basketball...');
    super.perform();
  }
}

class Swimmer extends Athlete with Awardable, Endurance {
  Swimmer(String name, {int rating = 6}) : super(name, 'swimming', rating);

  @override
  void perform() => print('$name swims.');
}
