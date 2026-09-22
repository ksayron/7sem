
abstract class SportsClub {
  static int totalClubs = 0;

  String name;
  String city;

  SportsClub(this.name, this.city) {
    totalClubs++;
  }

  static String formatTitle(String name, String city) => '$city "$name"';

  void trainAthletes();
  String describe();
}
