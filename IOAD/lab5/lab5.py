import os
from pathlib import Path

import matplotlib
import numpy as np
import pandas as pd
import sklearn
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (ConfusionMatrixDisplay, RocCurveDisplay,
                             accuracy_score, confusion_matrix, f1_score,
                             precision_score, recall_score, roc_auc_score)
from sklearn.model_selection import (GridSearchCV, StratifiedKFold,
                                     cross_val_score, train_test_split)
from sklearn.neighbors import KNeighborsClassifier
from sklearn.svm import SVC
from sklearn.tree import DecisionTreeClassifier

SHOW_PLOTS = os.environ.get("LAB5_NO_SHOW") != "1"
if not SHOW_PLOTS:
    matplotlib.use("Agg")
import matplotlib.pyplot as plt

BASE = os.path.dirname(os.path.abspath(__file__))
CSV_IN = os.path.join(BASE, "..", "hh-data", "it_vacancies.csv")
CLASS_NAMES = ["до 100 откликов", "от 100 откликов"]
pd.set_option("display.width", 180)


def save_fig(name):
    plt.savefig(os.path.join(BASE, name), dpi=150)
    if not SHOW_PLOTS:
        plt.close()
    print("График сохранён:", name)


# 1. Те же вакансии, цель и разбиение 80/20, что в работе 4.
df = pd.read_csv(CSV_IN)
y = df["overloaded"].astype(int)
# role уже представлена столбцами role_*. Дата не используется как признак;
# возраст вакансии age_days оставляем: задача — оценка на момент сбора.
X = df.drop(columns=["overloaded", "role", "publication_date"])
X_tr, X_te, y_tr, y_te = train_test_split(
    X, y, test_size=0.2, stratify=y, random_state=42)
print("scikit-learn:", sklearn.__version__)
print("Датасет:", Path(CSV_IN).resolve())
print("1) Вакансий:", len(df), "признаков:", X.shape[1])
print("Цель overloaded: 0 — до 100 откликов, 1 — от 100 откликов")
print("Период публикаций:", df.publication_date.min(), "—", df.publication_date.max())
print("train:", X_tr.shape, "test:", X_te.shape)
print("Классы train:", y_tr.value_counts().to_dict())
print("Классы test:", y_te.value_counts().to_dict())
print("Пропусков в X:", int(X.isna().sum().sum()))
print("Используется готовый CSV: пропуски заполнены, категории закодированы, "
      "три непрерывных признака стандартизированы при сборке. "
      "Повторная предобработка не выполняется.")
cv = StratifiedKFold(n_splits=5, shuffle=True, random_state=42)

# 2. Логистическая регрессия: C=1, 100, 0.01 для L1 и L2
# В установленном sklearn 1.9 l1_ratio=1 задаёт L1, l1_ratio=0 — L2.
lr_rows, lr_models, coefficients = [], [], {}
for name, ratio in [("L1", 1), ("L2", 0)]:
    for c in [1, 100, 0.01]:
        model = LogisticRegression(
            C=c, l1_ratio=ratio, solver="liblinear", max_iter=3000, random_state=42)
        cv_scores = cross_val_score(model, X_tr, y_tr, cv=cv, scoring="accuracy")
        model.fit(X_tr, y_tr)
        coef = model.coef_[0]
        label = f"{name}, C={c}"
        coefficients[label] = coef
        row = {"regularization": name, "C": c, "cv_accuracy": cv_scores.mean(),
               "cv_std": cv_scores.std(),
               "train_accuracy": model.score(X_tr, y_tr),
               "test_accuracy": model.score(X_te, y_te),
               "zero_coefficients": int(np.isclose(coef, 0, atol=1e-8).sum())}
        lr_rows.append(row)
        lr_models.append(model)
        print("2)", row)

lr_results = pd.DataFrame(lr_rows)
lr_results.to_csv(os.path.join(BASE, "regularization.csv"), index=False)
coef_table = pd.DataFrame(coefficients, index=X.columns).rename_axis("feature")
coef_table.to_csv(os.path.join(BASE, "coefficients.csv"))
print("\nКоэффициенты всех моделей:\n", coef_table.round(4).to_string())
best_lr_index = int(lr_results.cv_accuracy.idxmax())
lr = lr_models[best_lr_index]
print("Выбор логистической регрессии по CV:\n", lr_results.iloc[best_lr_index])
print("Свободный член выбранной модели:", lr.intercept_[0])

fig, axes = plt.subplots(1, 2, figsize=(12, 5))
for name in ["L1", "L2"]:
    table = lr_results[lr_results.regularization == name].sort_values("C")
    axes[0].plot(table.C, table.train_accuracy, "o--", label=f"{name}, train")
    axes[0].plot(table.C, table.cv_accuracy, "o-", label=f"{name}, CV")
axes[0].set_xscale("log")
axes[0].set_xlabel("C (логарифмическая шкала)")
axes[0].set_ylabel("Accuracy")
axes[0].set_title("Влияние регуляризации")
axes[0].legend(fontsize=8)
axes[0].grid(alpha=0.3)
top = coef_table.abs().max(axis=1).nlargest(12).index
pos = np.arange(len(top))
axes[1].barh(pos - 0.18, coef_table.loc[top, "L1, C=0.01"], height=0.36, label="L1")
axes[1].barh(pos + 0.18, coef_table.loc[top, "L2, C=0.01"], height=0.36, label="L2")
axes[1].set_yticks(pos, top, fontsize=8)
axes[1].set_title("Коэффициенты при сильной регуляризации, C=0.01")
axes[1].set_xlabel("Коэффициент признака готового CSV")
axes[1].legend()
plt.tight_layout()
save_fig("step2_regularization.png")

# 3. SVC по умолчанию и сетка из задания
svc_base = SVC()
base_cv = cross_val_score(svc_base, X_tr, y_tr, cv=cv, scoring="accuracy").mean()
svc_base.fit(X_tr, y_tr)
print("3) SVC по умолчанию: CV=", round(base_cv, 4),
      "train=", round(svc_base.score(X_tr, y_tr), 4),
      "test=", round(svc_base.score(X_te, y_te), 4))
params = {"C": [0.1, 1, 10], "gamma": [0.2, 0.6, 1]}
grid = GridSearchCV(SVC(), params, cv=cv, n_jobs=2, scoring="accuracy")
grid.fit(X_tr, y_tr)
print("Сетка: best_score=", round(grid.best_score_, 4), "best_params=", grid.best_params_)
print("Лучшая SVC из сетки: train=", round(grid.score(X_tr, y_tr), 4),
      "test=", round(grid.score(X_te, y_te), 4))
svc = grid.best_estimator_ if grid.best_score_ >= base_cv else svc_base
svc_rows = []
for label, model, score in [("default", svc_base, base_cv),
                            ("grid_best", grid.best_estimator_, grid.best_score_)]:
    svc_rows.append({"variant": label, "C": model.C,
                     "gamma": model.gamma, "cv_accuracy": score,
                     "train_accuracy": model.score(X_tr, y_tr),
                     "test_accuracy": model.score(X_te, y_te),
                     "selected": model is svc})
pd.DataFrame(svc_rows).to_csv(os.path.join(BASE, "svc_comparison.csv"), index=False)
print("Для итоговой SVC выбраны параметры:", svc.get_params())

grid_results = pd.DataFrame(grid.cv_results_)
grid_results[["param_C", "param_gamma", "mean_test_score",
              "std_test_score", "rank_test_score"]].to_csv(
    os.path.join(BASE, "svc_grid.csv"), index=False)
heat = grid_results.pivot(index="param_C", columns="param_gamma",
                          values="mean_test_score")
plt.figure(figsize=(7, 5))
plt.imshow(heat, cmap="Blues", aspect="auto")
plt.colorbar(label="Accuracy CV")
plt.xticks(range(len(heat.columns)), heat.columns)
plt.yticks(range(len(heat.index)), heat.index)
for i in range(heat.shape[0]):
    for j in range(heat.shape[1]):
        plt.text(j, i, f"{heat.iloc[i, j]:.4f}", ha="center", va="center")
plt.xlabel("gamma")
plt.ylabel("C")
plt.title("SVC: сетка параметров из задания")
plt.tight_layout()
save_fig("step3_svc_grid.png")

# 4. Дерево и kNN обучаются здесь же на тех же вакансиях.
tree = DecisionTreeClassifier(random_state=42).fit(X_tr, y_tr)
knn = KNeighborsClassifier().fit(X_tr, y_tr)
models = [("LogisticRegression", lr), ("SVC", svc), ("DecisionTree", tree), ("kNN", knn)]
rows = []
confusions = []
predictions = pd.DataFrame({"row_index": X_te.index, "y_true": y_te.to_numpy()})
for name, model in models:
    pred = model.predict(X_te)
    scores = (model.decision_function(X_te) if hasattr(model, "decision_function")
              else model.predict_proba(X_te)[:, 1])
    tn, fp, fn, tp = confusion_matrix(y_te, pred, labels=[0, 1]).ravel()
    confusions.append({"model": name, "TN": tn, "FP": fp, "FN": fn, "TP": tp})
    predictions[f"{name}_prediction"] = pred
    predictions[f"{name}_score"] = scores
    print(f"Матрица ошибок {name} (строки — факт, столбцы — прогноз):\n",
          np.array([[tn, fp], [fn, tp]]))
    rows.append({"model": name, "train_accuracy": model.score(X_tr, y_tr),
                 "test_accuracy": accuracy_score(y_te, pred),
                 "precision": precision_score(y_te, pred, zero_division=0),
                 "recall": recall_score(y_te, pred, zero_division=0),
                 "specificity": tn / (tn + fp),
                 "f1": f1_score(y_te, pred, zero_division=0),
                 "auc": roc_auc_score(y_te, scores)})
results = pd.DataFrame(rows).set_index("model")
print("4) Сравнение моделей:\n", results.round(4).to_string())
results.to_csv(os.path.join(BASE, "metrics.csv"))
pd.DataFrame(confusions).to_csv(os.path.join(BASE, "confusion_matrices.csv"), index=False)
predictions.to_csv(os.path.join(BASE, "predictions.csv"), index=False)

fig, axes = plt.subplots(1, 2, figsize=(11, 5))
for ax, (name, model) in zip(axes, models[:2]):
    ConfusionMatrixDisplay.from_predictions(
        y_te, model.predict(X_te), display_labels=CLASS_NAMES,
        ax=ax, cmap="Blues", colorbar=False)
    ax.set_title(name)
plt.tight_layout()
save_fig("step4_confusion_matrix.png")

# 5. Все четыре ROC-кривые в одних осях. Положительный класс — от 100 откликов.
fig, ax = plt.subplots(figsize=(8, 6))
for name, model in models:
    RocCurveDisplay.from_estimator(model, X_te, y_te, ax=ax, name=name)
ax.plot([0, 1], [0, 1], "k--", label="Случайный прогноз")
ax.set_title("ROC-кривые: от 100 откликов на IT-вакансию hh.ru")
ax.legend()
ax.grid(alpha=0.3)
plt.tight_layout()
save_fig("step5_roc.png")
print("5) Наибольшая AUC на test:", results.auc.idxmax())
print("Наибольшая accuracy на test:", results.test_accuracy.idxmax())
print("Прогноз большинством train: accuracy=", round((y_te == y_tr.mode()[0]).mean(), 4))
print("L1 может обнулить коэффициенты, L2 уменьшает их величины. "
      "Чем меньше C, тем сильнее регуляризация. Большая gamma RBF делает "
      "влияние отдельных обучающих точек более локальным.")
print("Параметры выбраны по CV на train; test использован для итоговой оценки. "
      "Это оценка на случайном разбиении готового CSV, а не проверка "
      "прогноза для будущих публикаций или новых работодателей.")
print("Подготовка CSV выполнена по всей выборке до разбиения; "
      "это ограничение независимости оценки.")

if SHOW_PLOTS:
    plt.show()
