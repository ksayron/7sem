import 'athlete.dart';

mixin Trainable {
  void warmUp() => print('warming up.');

  void coolDown() => print('cooling down.');

  void fullTrainingCycle(void Function(String message) log) {
    warmUp();
    log('in training');
    coolDown();
  }
}

mixin Awardable {
  final Set<String> awards = {};

  void addAward(String award) {
    final added = awards.add(award);
    print(added ? ' new award "$award"' : '"$award" exists');
  }

  void showAwards() =>
      print('awards: ${awards.isEmpty ? "no awards" : awards.join(", ")}');
}

mixin Endurance on Athlete {
  void buildEndurance(int weeks) {
    for (var week = 1; week <= weeks; week++) {
      print('week $week: $name is training endurance for ($discipline)');
    }
    print('$name finished');
  }
}

mixin Showman on Athlete {
  @override
  void perform() {
    print('showman performs');
    super.perform();
  }
}
