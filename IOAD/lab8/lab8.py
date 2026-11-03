from pathlib import Path

import cv2
import matplotlib.pyplot as plt
import numpy as np


BASE = Path(__file__).resolve().parent
ORIGINAL_PATH = BASE / "images/original.png"
DARK_PATH = BASE / "images/dark.jpg"
NOISY_PATH = BASE / "images/noisy.png"
OBJECTS_PATH = BASE / "images/objects.png"
SHOW_PLOTS = True

THRESHOLD = 127
BLOCK_SIZE = 31
C = 10
FILTER_SIZE = 5
MORPH_SIZE = 5
ITERATIONS = 1

output = BASE / "output"
processed = output / "processed"
processed.mkdir(parents=True, exist_ok=True)

images = []
for path in (ORIGINAL_PATH, DARK_PATH, NOISY_PATH, OBJECTS_PATH):
    image = cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError(f"Не удалось прочитать изображение: {path}")
    images.append(image)
original, dark, noisy, objects = images


# 2
gray = cv2.cvtColor(original, cv2.COLOR_BGR2GRAY)
_, binary = cv2.threshold(gray, THRESHOLD, 255, cv2.THRESH_BINARY)
adaptive = cv2.adaptiveThreshold(
    gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C,
    cv2.THRESH_BINARY, BLOCK_SIZE, C)

fig, axes = plt.subplots(2, 2, figsize=(10, 9))
fig.suptitle("2. Оттенки серого и бинаризация")
images = [cv2.cvtColor(original, cv2.COLOR_BGR2RGB), gray, binary, adaptive]
titles = ["Исходное изображение", "Оттенки серого: один канал",
          f"threshold: T={THRESHOLD}",
          f"adaptiveThreshold: {BLOCK_SIZE}×{BLOCK_SIZE}, C={C}"]
for ax, image, title in zip(axes.flat, images, titles):
    ax.imshow(image, cmap="gray", vmin=0, vmax=255, interpolation="nearest")
    ax.set_title(title)
    ax.axis("off")
fig.tight_layout(rect=(0, 0, 1, 0.95))
fig.savefig(output / "step2_thresholds.png", dpi=150)


# 3
dark_gray = cv2.cvtColor(dark, cv2.COLOR_BGR2GRAY)
equalized = cv2.equalizeHist(dark_gray)
hist_before = cv2.calcHist([dark_gray], [0], None, [256], [0, 256]).ravel()
hist_after = cv2.calcHist([equalized], [0], None, [256], [0, 256]).ravel()

fig, axes = plt.subplots(2, 2, figsize=(12, 8))
fig.suptitle("3. Выравнивание гистограммы тёмного снимка")
ymax = max(hist_before.max(), hist_after.max()) * 1.05
for col, image, histogram, title in [
    (0, dark_gray, hist_before, "До equalizeHist"),
    (1, equalized, hist_after, "После equalizeHist"),
]:
    axes[0, col].imshow(image, cmap="gray", vmin=0, vmax=255)
    axes[0, col].set_title(title)
    axes[0, col].axis("off")
    axes[1, col].bar(np.arange(256), histogram, width=1, color="slateblue")
    axes[1, col].set(xlim=(0, 255), ylim=(0, ymax),
                     xlabel="Яркость", ylabel="Количество пикселей")
fig.tight_layout(rect=(0, 0, 1, 0.95))
fig.savefig(output / "step3_equalization.png", dpi=150)

np.savetxt(output / "histograms.csv",
           np.column_stack((np.arange(256), hist_before, hist_after)),
           fmt="%d", delimiter=",", header="intensity,before,after", comments="")


# 4
blurred = cv2.blur(noisy, (FILTER_SIZE, FILTER_SIZE))
gaussian = cv2.GaussianBlur(noisy, (FILTER_SIZE, FILTER_SIZE), 0)
median = cv2.medianBlur(noisy, FILTER_SIZE)

images = [noisy, blurred, gaussian, median]
titles = ["Исходное изображение", f"blur: {FILTER_SIZE}×{FILTER_SIZE}",
          f"GaussianBlur: {FILTER_SIZE}×{FILTER_SIZE}",
          f"medianBlur: {FILTER_SIZE}×{FILTER_SIZE}"]
height, width = noisy.shape[:2]
for detail in (False, True):
    fig, axes = plt.subplots(2, 2, figsize=(12, 8))
    fig.suptitle("4. Сравнение фильтров" + (" — фрагмент" if detail else ""))
    for ax, image, title in zip(axes.flat, images, titles):
        if detail:
            image = image[height // 3:2 * height // 3, width // 3:2 * width // 3]
        ax.imshow(cv2.cvtColor(image, cv2.COLOR_BGR2RGB), interpolation="nearest")
        ax.set_title(title)
        ax.axis("off")
    fig.tight_layout(rect=(0, 0, 1, 0.95))
    filename = "step4_filter_details.png" if detail else "step4_filters.png"
    fig.savefig(output / filename, dpi=150)


# 5
objects_gray = cv2.cvtColor(objects, cv2.COLOR_BGR2GRAY)
otsu, mask = cv2.threshold(objects_gray, 0, 255,
                           cv2.THRESH_BINARY_INV | cv2.THRESH_OTSU)
kernel = cv2.getStructuringElement(cv2.MORPH_RECT, (MORPH_SIZE, MORPH_SIZE))
eroded = cv2.erode(mask, kernel, iterations=ITERATIONS)
dilated = cv2.dilate(mask, kernel, iterations=ITERATIONS)
opened = cv2.morphologyEx(mask, cv2.MORPH_OPEN, kernel, iterations=ITERATIONS)
closed = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel, iterations=ITERATIONS)

fig, axes = plt.subplots(2, 3, figsize=(14, 8))
fig.suptitle(f"5. Морфология: ядро {MORPH_SIZE}×{MORPH_SIZE}, итераций {ITERATIONS}")
images = [cv2.cvtColor(objects, cv2.COLOR_BGR2RGB), mask, eroded, dilated, opened, closed]
titles = ["Исходное изображение", f"Бинаризация Оцу: T={otsu:g}",
          "Эрозия", "Дилатация", "Размыкание", "Замыкание"]
for ax, image, title in zip(axes.flat, images, titles):
    ax.imshow(image, cmap="gray", vmin=0, vmax=255, interpolation="nearest")
    ax.set_title(title)
    ax.axis("off")
fig.tight_layout(rect=(0, 0, 1, 0.95))
fig.savefig(output / "step5_morphology.png", dpi=150)


for name, image in [
    ("step2_gray", gray), ("step2_binary", binary), ("step2_adaptive", adaptive),
    ("step3_dark_gray", dark_gray), ("step3_equalized", equalized),
    ("step4_blur", blurred), ("step4_gaussian", gaussian), ("step4_median", median),
    ("step5_binary", mask), ("step5_eroded", eroded), ("step5_dilated", dilated),
    ("step5_opened", opened), ("step5_closed", closed),
]:
    _, encoded = cv2.imencode(".png", image)
    encoded.tofile(processed / f"{name}.png")

results = (
    f"OpenCV {cv2.__version__}\n"
    f"2) Серое изображение: shape={gray.shape}, dtype={gray.dtype}\n"
    f"Порог: {THRESHOLD}; адаптивный порог: окно {BLOCK_SIZE}, C={C}\n"
    f"3) До equalizeHist: mean={dark_gray.mean():.2f}, std={dark_gray.std():.2f}\n"
    f"После equalizeHist: mean={equalized.mean():.2f}, std={equalized.std():.2f}\n"
    f"4) Размер ядра фильтров: {FILTER_SIZE}×{FILTER_SIZE}\n"
    f"5) Порог Оцу: {otsu:g}\n"
    f"Белых пикселей в маске: {cv2.countNonZero(mask)}\n"
    f"После эрозии: {cv2.countNonZero(eroded)}\n"
    f"После дилатации: {cv2.countNonZero(dilated)}\n"
    f"После размыкания: {cv2.countNonZero(opened)}\n"
    f"После замыкания: {cv2.countNonZero(closed)}\n"
    f"Результаты: {output}\n"
)
print(results)
(output / "results.txt").write_text(results, encoding="utf-8")

if SHOW_PLOTS:
    plt.show()
