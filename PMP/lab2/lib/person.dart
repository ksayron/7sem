

class Person {
  String name;

  Person(this.name);

  void introduce() => print('i\'m $name.');
  void say(String phrase) => print('$name says: "$phrase"');
}

class Referee implements Person {
  @override
  String name;
  int yellowCards;

  Referee(this.name, {this.yellowCards = 0});

  @override
  void introduce() => print('Referee $name');

  @override
  void say(String phrase) => print('Referee $name said: "$phrase"');
}
