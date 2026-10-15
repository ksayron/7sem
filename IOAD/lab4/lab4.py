import os

import matplotlib
import numpy as np
import pandas as pd
from sklearn.metrics import (ConfusionMatrixDisplay, accuracy_score,
                             classification_report, f1_score)
from sklearn.model_selection import train_test_split
from sklearn.neighbors import KNeighborsClassifier
from sklearn.tree import DecisionTreeClassifier, export_text, plot_tree

SHOW_PLOTS = os.environ.get("LAB4_NO_SHOW") != "1"
if not SHOW_PLOTS:
    matplotlib.use("Agg")
import matplotlib.pyplot as plt

BASE = os.path.dirname(os.path.abspath(__file__))
CSV_IN = os.path.join(BASE, "..", "hh-data", "it_vacancies.csv")
CLASS_NAMES = ["до 100 откликов", "от 100 откликов"]
pd.set_option("display.width", 160)


def save_fig(name):
    plt.savefig(os.path.join(BASE, name), dpi=150)
    if not SHOW_PLOTS:
        plt.close()
    print("График сохранён:", name)


df = pd.read_csv(CSV_IN)
print("2) Таблица:", df.shape, "— пропусков:", int(df.isna().sum().sum()))
y = df["overloaded"].astype(int)
print("Период публикаций:", df["publication_date"].min(), "—",
      df["publication_date"].max())
print("Баланс классов:", y.value_counts().sort_index().to_dict())

roles = df.groupby("role")["overloaded"].agg(count="count",
                                              overload_share="mean")
print("Роли и доля перегруженных вакансий:\n",
      roles.sort_values("overload_share", ascending=False).round(3).to_string())

counts = y.value_counts().sort_index()
plt.figure(figsize=(7, 4))
plt.bar(CLASS_NAMES, counts.values, color=["tab:green", "tab:red"])
for i, count in enumerate(counts):
    plt.text(i, count + 30, f"{count} ({count / len(y):.1%})", ha="center")
plt.title("Перегрузка IT-вакансий откликами (hh.ru, 2026)")
plt.ylabel("Количество вакансий")
plt.tight_layout()
save_fig("step2_class_balance.png")

X = df.drop(columns=["overloaded", "role", "publication_date"])
print("3) X:", X.shape, "y:", y.shape)
print("Все признаки уже числовые: кодирование категорий, заполнение пропусков "
      "и масштабирование выполнены при сборке датасета.")

X_tr, X_te, y_tr, y_te = train_test_split(
    X, y, test_size=0.2, stratify=y, random_state=42)
X_fit, X_val, y_fit, y_val = train_test_split(
    X_tr, y_tr, test_size=0.25, stratify=y_tr, random_state=42)
print("4) Для подбора параметров: train", X_fit.shape, "validation", X_val.shape)
print("Отдельная test:", X_te.shape)

tree_base = DecisionTreeClassifier(random_state=42).fit(X_fit, y_fit)
knn_base = KNeighborsClassifier().fit(X_fit, y_fit)
print("5) Базовое дерево: train=", round(tree_base.score(X_fit, y_fit), 4),
      "validation=", round(tree_base.score(X_val, y_val), 4))
print("Базовый kNN: train=", round(knn_base.score(X_fit, y_fit), 4),
      "validation=", round(knn_base.score(X_val, y_val), 4))

depths = list(range(1, 13))
tree_train, tree_val = [], []
for depth in depths:
    model = DecisionTreeClassifier(max_depth=depth, random_state=42)
    model.fit(X_fit, y_fit)
    tree_train.append(1 - model.score(X_fit, y_fit))
    tree_val.append(1 - model.score(X_val, y_val))
best_depth = depths[np.argmin(tree_val)]
plt.figure(figsize=(9, 5))
plt.plot(depths, tree_train, "o--", label="train")
plt.plot(depths, tree_val, "o-", label="validation")
plt.axvline(best_depth, color="green", ls="--", label=f"depth={best_depth}")
plt.xlabel("Глубина дерева")
plt.ylabel("Ошибка: 1 - accuracy")
plt.title("Подбор глубины дерева")
plt.legend()
plt.grid(alpha=0.3)
plt.tight_layout()
save_fig("step6_tree_depth.png")

ks = [1, 3, 5, 7, 9, 11, 15, 21, 31]
knn_train, knn_val = [], []
for k in ks:
    model = KNeighborsClassifier(n_neighbors=k).fit(X_fit, y_fit)
    knn_train.append(1 - model.score(X_fit, y_fit))
    knn_val.append(1 - model.score(X_val, y_val))
best_k = ks[np.argmin(knn_val)]
plt.figure(figsize=(9, 5))
plt.plot(ks, knn_train, "o--", label="train")
plt.plot(ks, knn_val, "o-", label="validation")
plt.axvline(best_k, color="green", ls="--", label=f"k={best_k}")
plt.xlabel("Количество соседей")
plt.ylabel("Ошибка: 1 - accuracy")
plt.title("Подбор k для ближайших соседей")
plt.legend()
plt.grid(alpha=0.3)
plt.tight_layout()
save_fig("step6_knn_k.png")
best_name = "Дерево" if min(tree_val) <= min(knn_val) else "kNN"
print("6) Выбраны max_depth=", best_depth, "k=", best_k)
print("Лучшая модель по validation:", best_name)

tree = DecisionTreeClassifier(max_depth=best_depth, random_state=42).fit(X_tr, y_tr)
knn = KNeighborsClassifier(n_neighbors=best_k).fit(X_tr, y_tr)
models = [("Дерево", tree, X_tr, X_te), ("kNN", knn, X_tr, X_te)]
rows = []
fig, axes = plt.subplots(1, 2, figsize=(11, 5))
for ax, (name, model, train, test) in zip(axes, models):
    pred = model.predict(test)
    print("\n7)", name)
    print(classification_report(y_te, pred, target_names=CLASS_NAMES, zero_division=0))
    ConfusionMatrixDisplay.from_predictions(
        y_te, pred, display_labels=CLASS_NAMES, ax=ax, cmap="Blues", colorbar=False)
    ax.set_title(f"{name}: accuracy={accuracy_score(y_te, pred):.3f}")
    rows.append({"model": name, "train_accuracy": model.score(train, y_tr),
                 "test_accuracy": accuracy_score(y_te, pred),
                 "test_f1": f1_score(y_te, pred)})
plt.tight_layout()
save_fig("step7_confusion_matrix.png")
results = pd.DataFrame(rows).set_index("model")
print("8) Итоговая проверка на test:\n", results.round(4).to_string())
print("Базовый прогноз большинством train:",
      round((y_te == y_tr.mode()[0]).mean(), 4))
print("Выбор по validation:", best_name)
results.to_csv(os.path.join(BASE, "metrics.csv"))

tree_viz = DecisionTreeClassifier(max_depth=3, random_state=42).fit(X_tr, y_tr)
plt.figure(figsize=(24, 11))
plot_tree(tree_viz, feature_names=list(X.columns), class_names=CLASS_NAMES,
          filled=True, rounded=True, fontsize=7)
plt.title("Дерево решений IT-вакансий hh.ru, max_depth=3")
plt.tight_layout()
save_fig("step9_tree_viz.png")
with open(os.path.join(BASE, "tree_rules.txt"), "w", encoding="utf-8") as f:
    f.write(export_text(tree_viz, feature_names=list(X.columns)))
print("9) Accuracy дерева для рисунка:", round(tree_viz.score(X_te, y_te), 4))
print("Важность признаков:\n",
      pd.Series(tree.feature_importances_, index=X.columns).nlargest(10).round(4))
print("Gini = 1 - p(до 100 откликов)^2 - p(от 100 откликов)^2. "
      "В чистом узле gini=0, при равных долях двух классов gini=0.5. "
      "Дерево выбирает разделение, которое уменьшает взвешенную смешанность "
      "дочерних узлов.")

if SHOW_PLOTS:
    plt.show()

