// Учебная точка входа. Основная лабораторная запускается из lib/main.dart.
// flutter run -d emulator-5554 -t examples/layout_practice.dart
import 'package:flutter/material.dart';

void main() => runApp(const LayoutPracticeApp());

class LayoutPracticeApp extends StatelessWidget {
  const LayoutPracticeApp({super.key});

  @override
  Widget build(BuildContext context) => MaterialApp(
    debugShowCheckedModeBanner: false,
    theme: ThemeData(
      brightness: Brightness.dark,
      colorScheme: const ColorScheme.dark(primary: Color(0xFFC78050)),
      scaffoldBackgroundColor: const Color(0xFF111111),
      useMaterial3: true,
    ),
    home: const LayoutPracticeScreen(),
  );
}

class LayoutPracticeScreen extends StatefulWidget {
  const LayoutPracticeScreen({super.key});

  @override
  State<LayoutPracticeScreen> createState() => _LayoutPracticeScreenState();
}

class _LayoutPracticeScreenState extends State<LayoutPracticeScreen> {
  // Поле состояния сохраняется между вызовами build.
  int _quantity = 1;

  @override
  Widget build(BuildContext context) => Scaffold(
    body: SafeArea(
      child: SingleChildScrollView(
        child: Padding(
          // Опыт № 1: замени 18 на 30 и сделай hot reload.
          padding: const EdgeInsets.all(18),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              const Text(
                'Учимся верстать Flutter',
                style: TextStyle(fontSize: 24, fontWeight: FontWeight.w600),
              ),
              const SizedBox(height: 8),
              const Text(
                'Картинка, строка цены и кнопка — отдельные виджеты.',
                style: TextStyle(color: Colors.white70),
              ),
              const SizedBox(height: 24),
              Container(
                // Фон карточки и скругление.
                decoration: BoxDecoration(
                  color: const Color(0xFF202020),
                  borderRadius: BorderRadius.circular(16),
                ),
                padding: const EdgeInsets.all(16),
                child: Column(
                  children: [
                    SizedBox(
                      width: double.infinity,
                      // Опыт № 3: замени 220 на 160.
                      height: 220,
                      child: Image.asset(
                        'assets/images/iced_coffee.png',
                        fit: BoxFit.contain,
                      ),
                    ),
                    // Опыт № 2: замени 16 на 30.
                    const SizedBox(height: 16),
                    Row(
                      children: [
                        // Название забирает ширину, оставшуюся после цены.
                        const Expanded(
                          child: Text(
                            'Espresso',
                            style: TextStyle(fontSize: 20),
                          ),
                        ),
                        const SizedBox(width: 8),
                        Text(
                          '\$${2 * _quantity}',
                          style: const TextStyle(fontSize: 24),
                        ),
                      ],
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 24),
              Text('Количество: $_quantity'),
              const SizedBox(height: 12),
              SizedBox(
                width: double.infinity,
                height: 48,
                child: FilledButton(
                  // Опыт № 4: нажатие меняет данные и обновляет интерфейс.
                  onPressed: () => setState(() => _quantity++),
                  child: const Text('Добавить порцию'),
                ),
              ),
              TextButton(
                onPressed: () => setState(() => _quantity = 1),
                child: const Text('Сбросить количество'),
              ),
            ],
          ),
        ),
      ),
    ),
  );
}
