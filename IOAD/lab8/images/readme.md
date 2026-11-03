# Демонстрационные исходники

Эти изображения включены для автономного запуска лабораторной. Они не являются фотографиями пользователя. Приложение не генерирует изображения и не добавляет искусственные дефекты. Для собственных фотографий замените `ORIGINAL_PATH`, `DARK_PATH`, `NOISY_PATH`, `OBJECTS_PATH` в начале `lab8.py`.

| Файл | Назначение | Источник |
| --- | --- | --- |
| `original.png` | Судоку с неравномерным освещением; сравнение бинаризации | [OpenCV 4.13.0, sudoku.png](https://github.com/opencv/opencv/blob/4.13.0/samples/data/sudoku.png) |
| `dark.jpg` | Ночная фотография Эйфелевой башни; гистограмма и контраст | [Zero-DCE, DICM/01.jpg](https://github.com/Li-Chongyi/Zero-DCE/blob/master/Zero-DCE_code/data/test_data/DICM/01.jpg) |
| `noisy.png` | Шумный цветной фрагмент, используемый авторами для демонстрации денойзинга с моделью SIDD | [NAFNet, demo/noisy.png](https://github.com/megvii-research/NAFNet/blob/2b4af71ebe098a92a75910c233a3965a3e93ede4/demo/noisy.png) |
| `objects.png` | Конфеты на светлом фоне; бинарная маска и морфология | [OpenCV 4.13.0, smarties.png](https://github.com/opencv/opencv/blob/4.13.0/samples/data/smarties.png) |

Скачаны 03.11.2026. Исходные байты сохранены без изменений. Прямые адреса загрузки и SHA-256 каждого файла записаны в `sources.json`. Это позволяет проверить неизменность данных независимо от последующих изменений веток репозиториев.

OpenCV распространяется по [Apache License 2.0](https://github.com/opencv/opencv/blob/4.13.0/LICENSE). Авторы Zero-DCE указывают академическое некоммерческое использование и CC BY-NC 4.0 в [разделе License](https://github.com/Li-Chongyi/Zero-DCE#license); DICM предоставлен в составе их тестовых данных. NAFNet — Liangyu Chen, Xiaojie Chu, Xiangyu Zhang, Jian Sun, *Simple Baselines for Image Restoration*, ECCV 2022; [репозиторий и условия](https://github.com/megvii-research/NAFNet). Здесь взяты только примеры входных изображений; код и обученные модели этих проектов не используются. Сведения о лицензии кода репозиториев не заменяют условия исходных наборов фотографий.

У демонстрационного снимка для задания 5 есть блики и частично обрезанные предметы. Для аккуратной собственной демонстрации снимайте целые тёмные предметы без бликов на белой бумаге.
