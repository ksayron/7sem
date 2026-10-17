import 'dart:io';

import 'package:battery_plus/battery_plus.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:image_picker/image_picker.dart';

import 'coffee_theme.dart';
import 'device_services.dart';

class DeviceScreen extends StatefulWidget {
  const DeviceScreen({
    super.key,
    required this.services,
    this.recoveredPhoto,
    this.recoveryError,
  });
  final DeviceServices services;
  final XFile? recoveredPhoto;
  final String? recoveryError;
  @override
  State<DeviceScreen> createState() => _DeviceScreenState();
}

class _DeviceScreenState extends State<DeviceScreen> {
  final _form = GlobalKey<FormState>();
  final _hour = TextEditingController(text: '7');
  final _minute = TextEditingController(text: '30');
  final _label = TextEditingController(text: 'Coffee time');
  final _busy = <String>{};
  final _messages = <String, String>{};
  late XFile? _photo = widget.recoveredPhoto;

  @override
  void initState() {
    super.initState();
    if (widget.recoveryError != null) {
      _messages['camera'] = widget.recoveryError!;
    }
    if (_photo != null) {
      _messages['camera'] = 'Фото восстановлено после перезапуска Activity.';
    }
  }

  @override
  void dispose() {
    _hour.dispose();
    _minute.dispose();
    _label.dispose();
    super.dispose();
  }

  Future<void> _run(String key, Future<String> Function() action) async {
    if (_busy.contains(key)) return;
    setState(() {
      _busy.add(key);
      _messages[key] = 'Выполняется…';
    });
    String message;
    try {
      message = await action();
    } on MissingPluginException {
      message = 'Функция недоступна на этой платформе.';
    } on PlatformException catch (e) {
      message = 'Ошибка ${e.code}: ${e.message ?? "повторите попытку"}';
    } catch (e) {
      message = 'Не удалось выполнить действие: $e';
    }
    if (!mounted) return;
    setState(() {
      _busy.remove(key);
      _messages[key] = message;
    });
  }

  String? _validateTime(String? value, int max) {
    final number = int.tryParse(value?.trim() ?? '');
    return number == null || number < 0 || number > max
        ? 'Введите число 0–$max'
        : null;
  }

  Widget _section(String title, List<Widget> children) => Card(
    margin: const EdgeInsets.only(bottom: 16),
    child: Padding(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(title, style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 12),
          ...children,
        ],
      ),
    ),
  );

  Widget _button(String key, String text, VoidCallback action) => FilledButton(
    key: Key('$key-button'),
    onPressed: _busy.contains(key) ? null : action,
    child: Text(_busy.contains(key) ? 'Подождите…' : text),
  );

  Widget _message(String key) => Padding(
    padding: const EdgeInsets.only(top: 8),
    child: Semantics(
      liveRegion: true,
      child: Text(
        _messages[key] ?? 'Нажмите кнопку для получения данных.',
        key: Key('$key-result'),
      ),
    ),
  );

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Lab 6 · Устройство')),
    body: SafeArea(
      child: ListView(
        padding: const EdgeInsets.all(16),
        children: [
          _section('Производитель · вариант 8', [
            const Text(
              'Собственный Android MethodChannel → Build.MANUFACTURER',
              style: TextStyle(color: coffeeMuted),
            ),
            const SizedBox(height: 12),
            _button(
              'manufacturer',
              'Получить производителя',
              () => _run('manufacturer', widget.services.manufacturer),
            ),
            _message('manufacturer'),
          ]),
          _section('Заряд батареи', [
            const Text(
              'battery_plus: общий Dart API для Android и iOS. На экране показаны данные текущего устройства.',
              style: TextStyle(color: coffeeMuted),
            ),
            const SizedBox(height: 12),
            _button(
              'battery',
              'Обновить заряд',
              () => _run('battery', () async {
                final reading = await widget.services.battery();
                final state = switch (reading.state) {
                  BatteryState.charging => 'заряжается',
                  BatteryState.discharging => 'разряжается',
                  BatteryState.full => 'полностью заряжена',
                  BatteryState.connectedNotCharging =>
                    'подключена, не заряжается',
                  BatteryState.unknown => 'состояние неизвестно',
                };
                return '${reading.level}% · $state';
              }),
            ),
            _message('battery'),
          ]),
          _section('Будильник · вариант 8', [
            const Text(
              'Параметры передаются из полей через собственный MethodChannel. Подтвердите будильник в приложении часов.',
            ),
            const SizedBox(height: 16),
            Form(
              key: _form,
              child: Column(
                children: [
                  Row(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Expanded(
                        child: TextFormField(
                          key: const Key('alarm-hour'),
                          controller: _hour,
                          keyboardType: TextInputType.number,
                          decoration: const InputDecoration(
                            labelText: 'Часы (0–23)',
                          ),
                          validator: (v) => _validateTime(v, 23),
                        ),
                      ),
                      const SizedBox(width: 16),
                      Expanded(
                        child: TextFormField(
                          key: const Key('alarm-minute'),
                          controller: _minute,
                          keyboardType: TextInputType.number,
                          decoration: const InputDecoration(
                            labelText: 'Минуты (0–59)',
                          ),
                          validator: (v) => _validateTime(v, 59),
                        ),
                      ),
                    ],
                  ),
                  const SizedBox(height: 12),
                  TextFormField(
                    key: const Key('alarm-label'),
                    controller: _label,
                    decoration: const InputDecoration(
                      labelText: 'Подпись будильника',
                    ),
                    maxLength: 80,
                  ),
                ],
              ),
            ),
            _button('alarm', 'Открыть часы', () {
              if (!_form.currentState!.validate()) return;
              final hour = int.parse(_hour.text.trim());
              final minute = int.parse(_minute.text.trim());
              final label = _label.text.trim();
              FocusManager.instance.primaryFocus?.unfocus();
              _run('alarm', () async {
                await widget.services.openAlarm(hour, minute, label);
                return 'Часы открыты: ${hour.toString().padLeft(2, '0')}:${minute.toString().padLeft(2, '0')} · $label. Подтвердите установку в часах.';
              });
            }),
            _message('alarm'),
          ]),
          _section('Фотография с камеры', [
            _button(
              'camera',
              'Сделать фото',
              () => _run('camera', () async {
                final photo = await widget.services.takePhoto();
                if (photo == null) {
                  return 'Съёмка отменена. Предыдущее фото сохранено.';
                }
                if (mounted) setState(() => _photo = photo);
                return 'Фотография получена с камеры.';
              }),
            ),
            _message('camera'),
            if (_photo != null) ...[
              const SizedBox(height: 12),
              ClipRRect(
                borderRadius: BorderRadius.circular(12),
                child: Image.file(
                  File(_photo!.path),
                  key: const Key('camera-photo'),
                  height: 260,
                  fit: BoxFit.contain,
                  semanticLabel: 'Фотография с камеры',
                  errorBuilder: (_, _, _) => const Text(
                    'Файл фотографии недоступен. Сделайте новое фото.',
                  ),
                ),
              ),
            ],
          ]),
        ],
      ),
    ),
  );
}
