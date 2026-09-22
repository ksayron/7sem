"""
Лабораторная работа 4-5: Сравнительный анализ методов текстовой стеганографии.
Консольная (bare-mode) версия без Streamlit: значения и пути захардкожены.

Запуск: python lab4.py
"""

import re
import os
import time

# --- 1. ГЛОБАЛЬНЫЕ НАСТРОЙКИ МАРКЕРОВ ---
# Маркеры для Zero-Width Characters (ZWC)
ZW_CHARS = {
    '0': '\u200B',  # ZERO WIDTH SPACE
    '1': '\u200C'   # ZERO WIDTH NON-JOINER
}
ZW_REVERSE = {v: k for k, v in ZW_CHARS.items()}
ZW_PATTERN = re.compile('[' + ''.join(re.escape(c) for c in ZW_CHARS.values()) + ']')

# Маркеры для Space Steganography (SS): 1 пробел = 0, 2 пробела = 1
SPACE_CHARS = {
    '0': ' ',     
    '1': '  '    
}
# Маркер конца сообщения для SS: 3 пробела.
EOM_MARKER = '   '


# --- 2. ФУНКЦИИ СТЕГАНОГРАФИИ ---

class ZeroWidthSteganography:
    
    @staticmethod
    def _text_to_binary(text):
        """Конвертирует текст в бинарную строку (UTF-8)."""
        return ''.join(format(byte, '08b') for byte in text.encode('utf-8'))

    @staticmethod
    def _binary_to_text(binary):
        """Конвертирует бинарную строку обратно в текст (UTF-8)."""
        byte_list = []
        for i in range(0, len(binary), 8):
            byte_segment = binary[i:i+8]
            if len(byte_segment) == 8:
                byte_list.append(int(byte_segment, 2))
        
        byte_data = bytes(byte_list)
        
        try:
            return byte_data.decode('utf-8')
        except UnicodeDecodeError:
            return "ОШИБКА: Невозможно декодировать сообщение (Проблема с UTF-8)."

    @staticmethod
    def encode(container_text, secret_message):
        """Функция скрытия сообщения методом ZWC (без ограничения емкости)."""
        start_time = time.time()
        
        binary_message = ZeroWidthSteganography._text_to_binary(secret_message)
        stego_message = ''.join(ZW_CHARS[bit] for bit in binary_message)
        required_capacity_bits = len(stego_message)
        
        stego_text = ""
        message_index = 0

        # Внедрение: если контейнера не хватает, символы добавляются в конец.
        for char in container_text:
            stego_text += char
            if message_index < required_capacity_bits:
                stego_text += stego_message[message_index]
                message_index += 1

        if message_index < required_capacity_bits:
            stego_text += stego_message[message_index:]
            
        return stego_text, len(secret_message), time.time() - start_time

    @staticmethod
    def decode(stego_text):
        """Функция извлечения сообщения методом ZWC."""
        start_time = time.time()
        
        hidden_chars = ZW_PATTERN.findall(stego_text)
        binary_message = ''.join(ZW_REVERSE.get(c, '') for c in hidden_chars)
        
        extracted_message = ZeroWidthSteganography._binary_to_text(binary_message)
        
        return extracted_message, time.time() - start_time


class SpaceSteganography:
    
    @staticmethod
    def _text_to_binary(text):
        return ''.join(format(byte, '08b') for byte in text.encode('utf-8'))

    @staticmethod
    def _binary_to_text(binary):
        byte_list = []
        for i in range(0, len(binary), 8):
            byte_segment = binary[i:i+8]
            if len(byte_segment) == 8:
                byte_list.append(int(byte_segment, 2))
        
        byte_data = bytes(byte_list)
        
        try:
            return byte_data.decode('utf-8')
        except UnicodeDecodeError:
            return "ОШИБКА: Невозможно декодировать сообщение (Проблема с UTF-8)."
        
    @staticmethod
    def encode(container_text, secret_message):
        """Функция скрытия сообщения методом SS (с проверкой емкости)."""
        start_time = time.time()
        
        words = container_text.split(' ')
        binary_message = SpaceSteganography._text_to_binary(secret_message)
        
        available_capacity_bits = len(words) - 1
        required_capacity_bits = len(binary_message)
        
        # Проверка Емкости: если не хватает места между словами, возвращаем ошибку.
        if required_capacity_bits > available_capacity_bits:
            required_capacity_chars = required_capacity_bits / 8.0
            diff_needed_words = required_capacity_bits - available_capacity_bits
            return None, len(secret_message), required_capacity_chars, diff_needed_words, 0
            
        stego_text_list = []
        message_embedded = False
        
        for i, word in enumerate(words):
            stego_text_list.append(word)
            
            # Вставляем разделитель только между словами
            if i < available_capacity_bits:
                
                if i < required_capacity_bits: 
                    # 1. Встраиваем бит сообщения (1 или 2 пробела)
                    bit = binary_message[i]
                    stego_text_list.append(SPACE_CHARS[bit])
                
                elif not message_embedded:
                    # 2. Сообщение закончилось, вставляем EOM-маркер (3 пробела)
                    stego_text_list.append(EOM_MARKER)
                    message_embedded = True
                
                else:
                    # 3. После EOM используем обычный разделитель (1 пробел)
                    stego_text_list.append(' ') 
            
        return "".join(stego_text_list), len(secret_message), time.time() - start_time

    @staticmethod
    def decode(stego_text):
        """Функция извлечения сообщения методом SS."""
        start_time = time.time()
        
        # Разделяем текст, включая последовательности пробелов
        parts = re.split('( +)', stego_text)
        
        binary_message = ""
        
        for part in parts:
            if part == EOM_MARKER:
                # Найдена отметка конца сообщения -> прекращаем обработку
                break
            elif part == ' ':
                binary_message += '0'
            elif part == '  ':
                binary_message += '1'
        
        # Обрезка до полного байта
        binary_message = binary_message[:len(binary_message) - (len(binary_message) % 8)]

        extracted_message = SpaceSteganography._binary_to_text(binary_message)

        return extracted_message, time.time() - start_time



BASE_DIR = os.path.dirname(os.path.abspath(__file__))

# Файл-контейнер (.txt или .html)
CONTAINER_FILE = os.path.join(BASE_DIR, 'text.txt')
# Секретное сообщение для сокрытия
SECRET_MESSAGE = "Секрет: Мы встретимся сегодня вечером у фонтана."
# Методы обрабатываются все сразу: stego_zwc_* и stego_ss_*
# Файлы вывода ({method} подставляется автоматически)
STEGO_OUTPUT_FILE = os.path.join(BASE_DIR, 'stego_{method}.txt')
STEGO_HTML_OUTPUT_FILE = os.path.join(BASE_DIR, 'stego_{method}.html')

METHODS = (
    ('zwc', 'Zero-Width Characters (ZWC)', ZeroWidthSteganography),
    ('ss', 'Space Steganography (SS)', SpaceSteganography),
)


def read_container_file(path):
    """Читает контейнер из файла. Возвращает (clean_text, file_type, original_content)."""
    with open(path, 'r', encoding='utf-8') as f:
        original_content = f.read()

    if path.lower().endswith('.html'):
        clean_text = re.sub('<[^>]*>', ' ', original_content)
        return ' '.join(clean_text.split()), 'text/html', original_content
    return original_content, 'text/plain', original_content


# --- 4. ОСНОВНОЙ КОНСОЛЬНЫЙ ИНТЕРФЕЙС ---

def run_method(method_key, method_name, StegoClass,
               container_text, file_type, original_content, results):
    """Кодирует и декодирует сообщение одним методом, печатает метрики."""
    print(f"Метод: {method_name}")
    print("-" * 60)

    # --- КОДИРОВАНИЕ ---
    result = StegoClass.encode(container_text, SECRET_MESSAGE)

    if len(result) == 5:  # Ошибка емкости (только для Space Steganography)
        _, secret_len, required_capacity_chars, diff_needed_words, _ = result
        print("[ОШИБКА ЕМКОСТИ] Контейнер слишком короткий "
              f"({secret_len} симв. сообщения).")
        print(f"Требуемая емкость (слов): {int(required_capacity_chars) + 1}")
        print(f"Решение: добавьте еще {int(diff_needed_words)} слов в контейнер.")
        print()
        return

    stego_text, secret_len, encoding_time = result
    print("[OK] Сообщение успешно скрыто.")

    if file_type == 'text/html':
        clean_text_original = ' '.join(
            re.sub('<[^>]*>', ' ', original_content).split())
        stego_output = original_content.replace(clean_text_original, stego_text, 1)
        output_file = STEGO_HTML_OUTPUT_FILE.format(method=method_key)
    else:
        stego_output = stego_text
        output_file = STEGO_OUTPUT_FILE.format(method=method_key)

    with open(output_file, 'w', encoding='utf-8') as f:
        f.write(stego_output)
    print(f"Стего-файл сохранен: {output_file}")

    stealth_ratio = (len(stego_text) - len(container_text)) / len(container_text) * 100
    print("Метрики кодирования:")
    print(f"  Длина контейнера      : {len(container_text)} симв.")
    print(f"  Длина стеганотекста   : {len(stego_text)} симв.")
    print(f"  Коэффициент скрытности: {stealth_ratio:.2f}%")
    print(f"  Время кодирования     : {encoding_time:.4f} сек")

    # --- ДЕКОДИРОВАНИЕ ---
    extracted_message, decoding_time = StegoClass.decode(stego_text)
    print("Метрики декодирования:")
    print(f"  Извлеченное сообщение: \"{extracted_message}\"")
    print(f"  Время декодирования  : {decoding_time:.4f} сек")

    accuracy = extracted_message == SECRET_MESSAGE
    print(f"  Точность извлечения  : "
          f"{'Совпадает' if accuracy else 'Не совпадает (Потеря данных или ошибка)'}")
    print()

    results.append({
        'method': method_name,
        'stego_len': len(stego_text),
        'ratio': stealth_ratio,
        'enc_time': encoding_time,
        'dec_time': decoding_time,
        'ok': accuracy,
    })


def print_comparison(results):
    """Печатает сводную таблицу сравнения методов."""
    if not results:
        return
    print("=" * 60)
    print("СВОДНАЯ ТАБЛИЦА СРАВНЕНИЯ")
    print("=" * 60)
    header = f"{'Метод':<35}{'Скрытность':>12}{'Кодир., сек':>13}{'Декодир., сек':>15}{'OK':>5}"
    print(header)
    print("-" * len(header))
    for r in results:
        print(f"{r['method']:<35}{r['ratio']:>11.2f}%{r['enc_time']:>13.4f}"
              f"{r['dec_time']:>15.4f}{'да' if r['ok'] else 'нет':>5}")


def main():
    print("=" * 60)
    print("Сравнительный анализ методов текстовой стеганографии")
    print("=" * 60)

    if not os.path.exists(CONTAINER_FILE):
        print(f"[ОШИБКА] Файл-контейнер не найден: {CONTAINER_FILE}")
        return

    container_text, file_type, original_content = read_container_file(CONTAINER_FILE)
    print(f"Контейнер : {CONTAINER_FILE} ({file_type}, {len(container_text)} симв.)")
    print(f"Сообщение : \"{SECRET_MESSAGE}\" ({len(SECRET_MESSAGE)} симв.)")
    print()

    results = []
    for method_key, method_name, StegoClass in METHODS:
        run_method(method_key, method_name, StegoClass,
                   container_text, file_type, original_content, results)

    print_comparison(results)


if __name__ == '__main__':
    main()

