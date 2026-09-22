abstract interface class Match {
  bool finished = false;
  void play();

  void finish() => finished = true;

  String getScore();

  bool get isFinished;
}

class FootballMatch implements Match {
   @override
  bool finished = false;
  int _homeGoals = 0;
  int _awayGoals = 0;

  String home;
  String away;


  FootballMatch({required this.home, required this.away});

  void goal(String side) {
    if (side == home) {
      _homeGoals++;
    } else {
      _awayGoals++;
    }
  }

  @override
  void play() => print('match $home — $away is live: $_homeGoals:$_awayGoals');

  @override
  String getScore() => '$_homeGoals:$_awayGoals';

  @override
  bool get isFinished => finished;

  @override
  void finish() => finished = true;

}
