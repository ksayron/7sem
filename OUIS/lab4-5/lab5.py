"""
Лабораторная работа 5: Сравнительный анализ характеристик стеганографических
методов на основе практического задания № 4.

Сравниваются два критерия:
  1. Скорость встраивания и извлечения сообщения (производительность).
  2. Универсальность методов (применимость к различным типам текстов/форматов).

Проводится не менее 20 экспериментов для каждого критерия.
Результаты выводятся в консоль, гистограммы сохраняются картинками (PNG)
в папку lab5_results.

Запуск: python lab5.py
"""

import os
import sys
import time
import statistics
import re

import matplotlib
matplotlib.use('Agg')  # без GUI: только сохранение картинок
import matplotlib.pyplot as plt

# Импорт стеганографических классов из lab4.py (задание № 4)
BASE_DIR = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, BASE_DIR)

from lab4 import (  # noqa: E402
    ZeroWidthSteganography,
    SpaceSteganography,
)

CONTAINER_FILE = os.path.join(BASE_DIR, 'text.txt')
CONTAINER_HTML_FILE = os.path.join(BASE_DIR, 'text.html')
RESULTS_DIR = os.path.join(BASE_DIR, 'lab5_results')

METHODS = (
    ('ZWC', ZeroWidthSteganography),
    ('SS', SpaceSteganography),
)

# Количество повторов каждого замера (для усреднения времени)
REPEATS = 5


def measure(func, *args):
    """Замеряет среднее время выполнения func (в секундах) за REPEATS повторов."""
    times = []
    result = None
    for _ in range(REPEATS):
        start = time.perf_counter()
        result = func(*args)
        times.append(time.perf_counter() - start)
    return result, statistics.mean(times)


def make_container(text, multiplier=1):
    """Размножает исходный текст до нужного объема."""
    return ' '.join([text] * multiplier)


# --- ЭКСПЕРИМЕНТ 1: ПРОИЗВОДИТЕЛЬНОСТЬ (>= 20 точек) ---

def experiment_performance(container_text):
    """Скорость встраивания/извлечения в зависимости от длины сообщения.

    20 длин сообщения (от 10 до 800 символов) x 2 метода = 40 экспериментов.
    """
    lengths = [10, 20, 40, 60, 80, 100, 140, 180, 220, 260,
               300, 350, 400, 450, 500, 570, 640, 710, 760, 800]
    results = {name: {'lengths': [], 'enc_times': [], 'dec_times': []}
               for name, _ in METHODS}

    for length in lengths:
        message = ('Тестовое сообщение для проверки производительности. ' * 20)[:length]
        for name, StegoClass in METHODS:
            enc_result, enc_time = measure(StegoClass.encode, container_text, message)
            stego_text = enc_result[0]
            if stego_text is None:  # SS: нехватка емкости
                print(f"  [ПРОПУСК] {name}: сообщение {length} симв. "
                      f"не помещается в контейнер.")
                continue
            _, dec_time = measure(StegoClass.decode, stego_text)

            results[name]['lengths'].append(length)
            results[name]['enc_times'].append(enc_time * 1000)  # мс
            results[name]['dec_times'].append(dec_time * 1000)  # мс
            print(f"  Длина={length:>4} симв. | {name}: "
                  f"встраивание={enc_time*1000:.4f} мс, "
                  f"извлечение={dec_time*1000:.4f} мс")

    return results


# --- ЭКСПЕРИМЕНТ 2: УНИВЕРСАЛЬНОСТЬ (>= 20 экспериментов) ---

def build_universality_cases():
    """Набор из 10 различных типов текстов/форматов.

    Возвращает список кортежей (название, контейнер, сообщение).
    """
    base = ' '.join(open(CONTAINER_FILE, encoding='utf-8').read().split())
    with open(CONTAINER_HTML_FILE, 'r', encoding='utf-8') as f:
        html_text = ' '.join(re.sub('<[^>]*>', ' ', f.read()).split())
    message = 'Секрет: Мы встретимся сегодня вечером у фонтана.'

    return [
        ('TXT английский', base, message),
        ('TXT русский', ' '.join(
            ['Чтение это фундаментальный навык который открывает двери к знаниям '
             'и воображению Каждый день новые слова и мысли проникают в разум '
             'читателя'] * 60), message),
        ('TXT цифры', ' '.join(['12345 67890 24680 13579 86420 97531 11235'] * 80),
         message),
        ('TXT спецсимволы', ' '.join(['@#$% &*() !??; ::: <<< >>> ___ +++'] * 80),
         message),
        ('TXT смешанный', ' '.join(
            ['Hello мир 2026 test тест 42! mix микс done готово'] * 70), message),
        ('TXT повторы', ' '.join(['слово '] * 800).strip(), message),
        ('TXT без пунктуации', ' '.join(
            ['мама мыла раму кот пил молоко день был солнечным'] * 80), message),
        ('TXT короткие слова', ' '.join(['и в на с к по за из у о'] * 120),
         message),
        ('HTML текст', html_text, message),
        ('TXT длинные слова', ' '.join(
            ['достопримечательность электроэнергетический '
             'человеконенавистничество'] * 100), message),
    ]


def experiment_universality(cases):
    """Применимость методов к разным типам текстов.

    10 типов текстов x 2 метода = 20 экспериментов.
    """
    results = {name: {'cases': [], 'enc_times': [], 'dec_times': [], 'ok': []}
               for name, _ in METHODS}

    for case_name, container, message in cases:
        print(f"  Тип: {case_name} ({len(container)} симв. контейнер)")
        for name, StegoClass in METHODS:
            enc_result, enc_time = measure(StegoClass.encode, container, message)
            stego_text = enc_result[0]
            if stego_text is None:
                print(f"    {name}: [ОШИБКА ЕМКОСТИ] — метод неприменим")
                results[name]['cases'].append(case_name)
                results[name]['enc_times'].append(0.0)
                results[name]['dec_times'].append(0.0)
                results[name]['ok'].append(0)
                continue
            dec_result, dec_time = measure(StegoClass.decode, stego_text)
            extracted = dec_result[0]
            ok = extracted == message
            results[name]['cases'].append(case_name)
            results[name]['enc_times'].append(enc_time * 1000)
            results[name]['dec_times'].append(dec_time * 1000)
            results[name]['ok'].append(1 if ok else 0)
            status = 'OK' if ok else 'ОШИБКА ИЗВЛЕЧЕНИЯ'
            print(f"    {name}: встраивание={enc_time*1000:.4f} мс, "
                  f"извлечение={dec_time*1000:.4f} мс, {status}")

    return results


# --- ГИСТОГРАММЫ ---

def plot_performance_histograms(perf):
    """Гистограммы: время встраивания/извлечения по длинам сообщения."""
    fig, axes = plt.subplots(2, 2, figsize=(16, 10))
    fig.suptitle('Эксперимент 1: Производительность методов '
                 '(время vs длина сообщения)', fontsize=14)

    for col, name, color in ((0, 'ZWC', 'tab:blue'), (1, 'SS', 'tab:orange')):
        data = perf[name]
        labels = [str(l) for l in data['lengths']]
        axes[0][col].bar(labels, data['enc_times'], color=color)
        axes[0][col].set_title(f'{name}: время встраивания')
        axes[0][col].set_xlabel('Длина сообщения, симв.')
        axes[0][col].set_ylabel('Время, мс')
        axes[0][col].tick_params(axis='x', rotation=45, labelsize=7)

        axes[1][col].bar(labels, data['dec_times'], color=color, alpha=0.7)
        axes[1][col].set_title(f'{name}: время извлечения')
        axes[1][col].set_xlabel('Длина сообщения, симв.')
        axes[1][col].set_ylabel('Время, мс')
        axes[1][col].tick_params(axis='x', rotation=45, labelsize=7)

    fig.tight_layout(rect=[0, 0, 1, 0.95])
    path = os.path.join(RESULTS_DIR, 'hist_performance.png')
    fig.savefig(path, dpi=150)
    plt.close(fig)
    print(f"[OK] Гистограмма сохранена: {path}")

    # Сравнение методов на одном графике
    fig, axes = plt.subplots(1, 2, figsize=(16, 6))
    fig.suptitle('Эксперимент 1: Сравнение методов', fontsize=14)
    width = 0.38
    all_lengths = sorted(set(perf['ZWC']['lengths']) | set(perf['SS']['lengths']))
    for i, (name, color) in enumerate((('ZWC', 'tab:blue'), ('SS', 'tab:orange'))):
        data = perf[name]
        times_enc, times_dec = [], []
        for l in all_lengths:
            if l in data['lengths']:
                idx = data['lengths'].index(l)
                times_enc.append(data['enc_times'][idx])
                times_dec.append(data['dec_times'][idx])
            else:
                times_enc.append(0)
                times_dec.append(0)
        x = range(len(all_lengths))
        off = (i - 0.5) * width
        axes[0].bar([j + off for j in x], times_enc,
                    width=width, label=name, color=color)
        axes[1].bar([j + off for j in x], times_dec,
                    width=width, label=name, color=color)
    for ax, title in ((axes[0], 'Встраивание'), (axes[1], 'Извлечение')):
        ax.set_title(title)
        ax.set_xlabel('Длина сообщения, симв.')
        ax.set_ylabel('Время, мс')
        ax.set_xticks(range(len(all_lengths)))
        ax.set_xticklabels([str(l) for l in all_lengths], rotation=45, fontsize=7)
        ax.legend()
    fig.tight_layout(rect=[0, 0, 1, 0.94])
    path = os.path.join(RESULTS_DIR, 'hist_performance_compare.png')
    fig.savefig(path, dpi=150)
    plt.close(fig)
    print(f"[OK] Гистограмма сохранена: {path}")


def plot_universality_histograms(univ, case_names, case_sizes):
    """Гистограммы времени и успешности по типам текстов."""
    fig, axes = plt.subplots(2, 2, figsize=(18, 10))
    fig.suptitle('Эксперимент 2: Универсальность методов '
                 '(различные типы текстов)', fontsize=14)

    x = range(len(case_names))
    width = 0.38
    for i, (name, color) in enumerate((('ZWC', 'tab:blue'), ('SS', 'tab:orange'))):
        data = univ[name]
        off = (i - 0.5) * width
        axes[0][0].bar([j + off for j in x], data['enc_times'],
                       width=width, label=name, color=color)
        axes[1][0].bar([j + off for j in x], data['dec_times'],
                       width=width, label=name, color=color)
        axes[0][1].bar([j + off for j in x], data['ok'],
                       width=width, label=name, color=color)

    axes[0][0].set_title('Время встраивания по типам текстов')
    axes[0][0].set_ylabel('Время, мс')
    axes[1][0].set_title('Время извлечения по типам текстов')
    axes[1][0].set_ylabel('Время, мс')
    axes[0][1].set_title('Успешность извлечения (1 = совпадает, 0 = ошибка)')
    axes[0][1].set_ylabel('Успех')
    axes[0][1].set_ylim(-0.1, 1.4)
    axes[1][1].bar(case_names, [s / 1000 for s in case_sizes],
                   color='tab:green', alpha=0.7)
    axes[1][1].set_title('Размер контейнера по типам текстов')
    axes[1][1].set_ylabel('Размер, тыс. симв.')
    for ax in (axes[0][0], axes[1][0], axes[0][1], axes[1][1]):
        ax.set_xticks(range(len(case_names)))
        ax.set_xticklabels(case_names, rotation=30, ha='right', fontsize=8)
        if ax is not axes[1][1]:
            ax.legend()

    fig.tight_layout(rect=[0, 0, 1, 0.94])
    path = os.path.join(RESULTS_DIR, 'hist_universality.png')
    fig.savefig(path, dpi=150)
    plt.close(fig)
    print(f"[OK] Гистограмма сохранена: {path}")


# --- ВЫВОДЫ ---

def print_conclusions(perf, univ):
    print()
    print("=" * 70)
    print("ВЫВОДЫ СРАВНИТЕЛЬНОГО АНАЛИЗА")
    print("=" * 70)

    for name in ('ZWC', 'SS'):
        d = perf[name]
        if d['enc_times']:
            print(f"{name}: среднее время встраивания = "
                  f"{statistics.mean(d['enc_times']):.4f} мс, "
                  f"извлечения = {statistics.mean(d['dec_times']):.4f} мс "
                  f"(на {len(d['lengths'])} длинах сообщений)")

    zwc_ok = sum(univ['ZWC']['ok'])
    ss_ok = sum(univ['SS']['ok'])
    print(f"\nУниверсальность (успешных экспериментов из "
          f"{len(univ['ZWC']['cases'])}): ZWC = {zwc_ok}, SS = {ss_ok}")

    print("""
1. ПРОИЗВОДИТЕЛЬНОСТЬ:
   - ZWC: время встраивания и извлечения растет линейно с длиной сообщения
     (каждый бит добавляет отдельный невидимый символ в контейнер).
   - SS: время также растет, но обработка split/rejoin по пробелам
     обычно медленнее на извлечении из-за разбора всего контейнера.
   - Оба метода укладываются в доли миллисекунды, что практично.

2. УНИВЕРСАЛЬНОСТЬ:
   - ZWC применим к любому тексту без ограничений: сообщение добавляется
     невидимыми символами, не меняя видимых слов и пробелов.
   - SS требует емкости: число промежутков между словами должно быть
     не меньше числа бит сообщения; для коротких текстов и кириллицы
     (2 байта/символ) возможен отказ по емкости.
   - SS чувствителен к форматам, где пробелы значимы (markdown, код),
     и разрушается при копировании/автоформатировании текста.
   - ZWC тоже уязвим при копировании (невидимые символы могут удаляться),
     но структурно более универсален и проще в встраивании.""")


# --- ОСНОВНАЯ ПРОГРАММА ---

def main():
    os.makedirs(RESULTS_DIR, exist_ok=True)

    print("=" * 70)
    print("Лабораторная работа 5: сравнительный анализ методов стеганографии")
    print("=" * 70)

    base_text = ' '.join(
        open(CONTAINER_FILE, encoding='utf-8').read().split())
    container = make_container(base_text, 15)  # ~70 тыс. символов (емкость для SS)

    print(f"\nЭКСПЕРИМЕНТ 1: Производительность "
          f"(20 длин сообщения x 2 метода = 40 экспериментов)")
    print(f"Контейнер: {len(container)} символов, {REPEATS} повторов на замер")
    print("-" * 70)
    perf = experiment_performance(container)

    print(f"\nЭКСПЕРИМЕНТ 2: Универсальность "
          f"(10 типов текстов x 2 метода = 20 экспериментов)")
    print("-" * 70)
    cases = build_universality_cases()
    case_names = [c[0] for c in cases]
    case_sizes = [len(c[1]) for c in cases]
    univ = experiment_universality(cases)

    # --- Гистограммы ---
    print("\nПостроение гистограмм...")
    plot_performance_histograms(perf)
    plot_universality_histograms(univ, case_names, case_sizes)

    # --- Выводы ---
    print_conclusions(perf, univ)
    print(f"\nВсе картинки сохранены в папке: {RESULTS_DIR}")


if __name__ == '__main__':
    main()
