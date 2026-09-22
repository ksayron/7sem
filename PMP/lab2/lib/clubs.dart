import 'athlete.dart';
import 'sports_club.dart';

class FootballClub extends SportsClub {
  final List<Athlete> roster = <Athlete>[];

  FootballClub(super.name, super.city);

  void signAthlete(Athlete athlete) {
    roster.add(athlete);
    print('$name signed: ${athlete.name}');
  }

  @override
  void trainAthletes() {
    print('fc $name in training:');
    for (var athlete in roster) {
      athlete.train(2, (message) => print('  > $message'));
    }
  }

  @override
  String describe() => 'football club ${SportsClub.formatTitle(name, city)} (players count: ${roster.length})';
}

class BasketballClub extends SportsClub {
  BasketballClub(super.name, super.city);

  @override
  void trainAthletes() =>
      print('bc $name are training.');

  @override
  String describe() => 'bc ${SportsClub.formatTitle(name, city)}';
}
