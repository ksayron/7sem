import os

import matplotlib
import numpy as np
import pandas as pd
from scipy.cluster.hierarchy import dendrogram, linkage
from sklearn.cluster import AgglomerativeClustering, KMeans
from sklearn.metrics import (calinski_harabasz_score, davies_bouldin_score,
                             silhouette_samples)
from sklearn.preprocessing import MinMaxScaler

SHOW_PLOTS = os.environ.get("LAB6_NO_SHOW") != "1"
if not SHOW_PLOTS:
    matplotlib.use("Agg")
import matplotlib.pyplot as plt

BASE = os.path.dirname(os.path.abspath(__file__))
CSV_IN = os.path.join(BASE, "..", "hh-data", "it_vacancies.csv")


def save_fig(name):
    plt.savefig(os.path.join(BASE, name), dpi=150)
    if not SHOW_PLOTS:
        plt.close()
    print("График сохранён:", name)


# 1. Группируем IT-вакансии по характеристикам, без метки overloaded.
df = pd.read_csv(CSV_IN)
X = df.drop(columns=["overloaded", "role", "publication_date"])
print("1) Объекты — IT-вакансии, X:", X.shape)
print("Признаки:\n", X.columns.tolist())
print("Метка overloaded и дата исключены; роль уже закодирована в role_*.")

# 2–3. CSV подготовлен заранее. MinMaxScaler — отдельный пункт задания.
print("2) Пропусков:", int(X.isna().sum().sum()),
      "— заполнение и кодирование выполнены при сборке CSV.")
X_s = MinMaxScaler().fit_transform(X)
print("3) Диапазон после MinMax:", X_s.min(), X_s.max())

# 4. Метод локтя. Выбираем наиболее удалённую от прямой точку кривой.
ks = np.arange(1, 11)
inertia = []
for k in ks:
    model = KMeans(n_clusters=int(k), n_init=10, random_state=42).fit(X_s)
    inertia.append(model.inertia_)
inertia = np.array(inertia)
curve = (inertia - inertia[-1]) / (inertia[0] - inertia[-1])
line = 1 - (ks - ks[0]) / (ks[-1] - ks[0])
best_k = int(ks[1:-1][np.argmax((line - curve)[1:-1])])
print("4) По локтю выбрано k=", best_k)
plt.figure(figsize=(8, 5))
plt.plot(ks, inertia, "o-")
plt.axvline(best_k, color="green", ls="--", label=f"Локоть: k={best_k}")
plt.xlabel("Количество кластеров")
plt.ylabel("Inertia: сумма квадратов расстояний до центров")
plt.title("K-means: метод локтя")
plt.xticks(ks)
plt.legend()
plt.grid(alpha=0.3)
plt.tight_layout()
save_fig("step4_elbow.png")
kmeans = KMeans(n_clusters=best_k, n_init=10, random_state=42).fit(X_s)
labels_k = kmeans.labels_

# 5 и 9. Два признака готового CSV и одна конкретная вакансия.
chosen = 0
col_x, col_y = "age_days", "salary_from_rub"


def cluster_plot(labels, name, filename):
    plt.figure(figsize=(9, 6))
    for cluster in np.unique(labels):
        mask = labels == cluster
        plt.scatter(X.loc[mask, col_x], X.loc[mask, col_y], s=12, alpha=0.35,
                    label=f"Кластер {cluster}: {mask.sum()}")
    plt.scatter(X.loc[chosen, col_x], X.loc[chosen, col_y], s=230,
                c="magenta", marker="*", edgecolors="black",
                label=f"Строка CSV {chosen}, кластер {labels[chosen]}")
    plt.xlabel("Возраст вакансии (z-оценка из CSV)")
    plt.ylabel("Нижняя граница зарплаты (z-оценка из CSV)")
    plt.title(name + f" — проекция {X.shape[1]} признаков на два")
    plt.legend(fontsize=8)
    plt.grid(alpha=0.3)
    plt.tight_layout()
    save_fig(filename)


cluster_plot(labels_k, "K-means", "step5_kmeans.png")

# 6. Иерархия Ward. Самый большой скачок последних объединений
# задаёт высоту разреза, а значит и число кластеров.
Z = linkage(X_s, method="ward")
heights = Z[-10:, 2]
gap = int(np.argmax(np.diff(heights)))
cut = (heights[gap] + heights[gap + 1]) / 2
hier_k = len(heights) - gap
print("6) По дендрограмме выбрано k=", hier_k, "высота разреза=", round(cut, 3))
plt.figure(figsize=(12, 6))
dendrogram(Z, truncate_mode="lastp", p=25, show_leaf_counts=True,
           color_threshold=cut)
plt.axhline(cut, c="red", ls="--", label=f"Разрез: {hier_k} кластеров")
plt.xlabel("Группы вакансий; в скобках число объектов")
plt.ylabel("Расстояние объединения Ward")
plt.title("Дендрограмма, последние 25 групп")
plt.legend()
plt.tight_layout()
save_fig("step6_dendrogram.png")
labels_h = AgglomerativeClustering(n_clusters=hier_k, linkage="ward").fit_predict(X_s)
cluster_plot(labels_h, "Иерархическая кластеризация Ward", "step7_hierarchical.png")

# 8. Метрики на всех вакансиях в одинаковом пространстве признаков.
rows = []
fig, axes = plt.subplots(1, 2, figsize=(12, 5))
for ax, (name, labels) in zip(axes, [("K-means", labels_k), ("Ward", labels_h)]):
    silhouettes = silhouette_samples(X_s, labels)
    rows.append({"method": name, "clusters": len(np.unique(labels)),
                 "silhouette": silhouettes.mean(),
                 "davies_bouldin": davies_bouldin_score(X_s, labels),
                 "calinski_harabasz": calinski_harabasz_score(X_s, labels)})
    table = pd.DataFrame({"cluster": labels, "silhouette": silhouettes})
    summary = table.groupby("cluster").agg(
        count=("silhouette", "size"), mean_silhouette=("silhouette", "mean"),
        negative_share=("silhouette", lambda s: (s < 0).mean()))
    print("\n8)", name, "качество отдельных кластеров:\n", summary.round(4).to_string())
    summary.to_csv(os.path.join(BASE, f"silhouette_{name.lower()}.csv"))
    ax.bar(summary.index, summary.mean_silhouette, color="steelblue")
    ax.axhline(silhouettes.mean(), color="red", ls="--", label="Общее среднее")
    ax.set_xlabel("Кластер")
    ax.set_ylabel("Средний силуэт")
    ax.set_title(name)
    ax.set_xticks(summary.index)
    ax.legend()
plt.tight_layout()
save_fig("step8_silhouette.png")
results = pd.DataFrame(rows).set_index("method")
print("\nСравнение методов:\n", results.round(4).to_string())
print("Лучший по силуэту:", results.silhouette.idxmax())
print("Силуэт и Calinski-Harabasz: больше лучше; Davies-Bouldin: меньше лучше.")
results.to_csv(os.path.join(BASE, "metrics.csv"))

for name, labels in [("kmeans", labels_k), ("ward", labels_h)]:
    profiles = X.assign(cluster=labels).groupby("cluster").mean()
    profiles.to_csv(os.path.join(BASE, f"profiles_{name}.csv"))
    print("\nСредние исходные показатели", name, ":\n",
          profiles[["age_days", "salary_from_rub", "is_remote",
                    "is_hybrid"]].round(3).to_string())
pd.DataFrame({"row_index": df.index, "publication_date": df.publication_date,
              "role": df.role, "kmeans_cluster": labels_k,
              "ward_cluster": labels_h}).to_csv(os.path.join(BASE, "clusters.csv"), index=False)
pd.DataFrame({"k": ks, "inertia": inertia}).to_csv(os.path.join(BASE, "elbow.csv"), index=False)
print("9) Выбрана строка CSV:", chosen, "дата:", df.loc[chosen, "publication_date"],
      "роль:", df.loc[chosen, "role"])
print("Кластер K-means:", labels_k[chosen], "кластер Ward:", labels_h[chosen])
print("Показатели выбранной вакансии:\n", X.iloc[chosen].round(3).to_string())

if SHOW_PLOTS:
    plt.show()
