import os
from time import perf_counter

import matplotlib
import numpy as np
import pandas as pd
from sklearn.decomposition import PCA
from sklearn.metrics import accuracy_score, roc_auc_score
from sklearn.model_selection import train_test_split
from sklearn.preprocessing import StandardScaler
from sklearn.svm import SVC

SHOW_PLOTS = os.environ.get("LAB7_NO_SHOW") != "1"
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


# 1. Те же исходные признаки и тот же test, что в работах 4 и 5.
df = pd.read_csv(CSV_IN)
y = df["overloaded"].astype(int)
X = df.drop(columns=["overloaded", "role", "publication_date"])
X_tr, X_te, y_tr, y_te = train_test_split(
    X, y, test_size=0.2, stratify=y, random_state=42)
# Дополнительная стандартизация всех столбцов нужна для сравнения PCA
# по заданию: в готовом CSV стандартизированы только три непрерывных признака.
scaler = StandardScaler()
X_tr_s = scaler.fit_transform(X_tr)
X_te_s = scaler.transform(X_te)
print("1) train:", X_tr.shape, "test:", X_te.shape)
print("Готовый CSV: пропусков", int(X.isna().sum().sum()),
      "— повторное заполнение и кодирование не нужны.")

# 2. Веса компонент в шкалах готового CSV и после StandardScaler.
pca_raw = PCA(n_components=2, svd_solver="full").fit(X_tr)
pca_scaled = PCA(svd_solver="full").fit(X_tr_s)
weights = pd.DataFrame({"raw_PC1": pca_raw.components_[0],
                        "raw_PC2": pca_raw.components_[1],
                        "scaled_PC1": pca_scaled.components_[0],
                        "scaled_PC2": pca_scaled.components_[1]}, index=X.columns)
weights.to_csv(os.path.join(BASE, "component_weights.csv"))
fig, axes = plt.subplots(2, 2, figsize=(13, 8))
for ax, column in zip(axes.ravel(), weights.columns):
    ax.bar(np.arange(X.shape[1]), weights[column], width=1)
    ax.set_title(column)
    ax.set_xlabel("Номер признака в component_weights.csv (от 0)")
    ax.set_ylabel("Вес признака")
    ax.grid(axis="y", alpha=0.3)
plt.suptitle(f"Веса первых двух главных компонент: все {X.shape[1]} признака")
plt.tight_layout()
save_fig("step2_all_weights.png")

fig, axes = plt.subplots(2, 2, figsize=(16, 10))
for ax, column in zip(axes.ravel(), weights.columns):
    top = weights[column].abs().nlargest(10).index
    ax.barh(top, weights.loc[top, column], color="steelblue")
    ax.tick_params(axis="y", labelsize=8)
    ax.set_title(column + ": 10 наибольших по модулю весов")
    ax.set_xlabel("Вес в главной компоненте")
    print("2)", column, "наибольшие веса:\n", weights.loc[top, column].round(4).to_string())
plt.tight_layout()
save_fig("step2_top_weights.png")
print("Дисперсия двух компонент в шкалах готового CSV:", pca_raw.explained_variance_ratio_)
print("Дисперсия двух компонент после StandardScaler:", pca_scaled.explained_variance_ratio_[:2])

# 3. Все вакансии; исследовательские PCA и scaler обучены только на train.
raw_2d = pca_raw.transform(X)
scaled_2d = pca_scaled.transform(scaler.transform(X))[:, :2]
fig, axes = plt.subplots(1, 2, figsize=(13, 5))
for ax, points, title in [(axes[0], raw_2d, "Готовый CSV без доп. масштабирования"),
                          (axes[1], scaled_2d, "Все признаки после StandardScaler")]:
    for label, name, color in [(0, "До 100 откликов", "tab:green"),
                                (1, "От 100 откликов", "tab:red")]:
        mask = y == label
        ax.scatter(points[mask, 0], points[mask, 1], s=10, alpha=0.3,
                   c=color, label=name)
    ax.set_title(title)
    ax.set_xlabel("PC1")
    ax.set_ylabel("PC2")
    ax.legend()
    ax.grid(alpha=0.3)
plt.suptitle("Распределение классов в двух главных компонентах")
plt.tight_layout()
save_fig("step3_pca_classes.png")

# 4. SVC на всех масштабированных признаках
svc_full = SVC(C=1, gamma="scale")
start = perf_counter()
svc_full.fit(X_tr_s, y_tr)
full_seconds = perf_counter() - start
full_pred = svc_full.predict(X_te_s)
full_accuracy = accuracy_score(y_te, full_pred)
print("4) SVC без PCA: accuracy test=", round(full_accuracy, 4),
      "fit секунд=", round(full_seconds, 3))

# 5. Минимальное число компонент, сохраняющее не менее 90% дисперсии train
cumulative = np.cumsum(pca_scaled.explained_variance_ratio_)
n90 = int(np.searchsorted(cumulative, 0.9) + 1)
print("5) Для 90% дисперсии нужно компонент:", n90, "из", X.shape[1])
print("Сохранено:", round(float(cumulative[n90 - 1]), 4))
pd.DataFrame({"components": np.arange(1, len(cumulative) + 1),
              "variance_ratio": pca_scaled.explained_variance_ratio_,
              "cumulative_variance": cumulative}).to_csv(
                  os.path.join(BASE, "explained_variance.csv"), index=False)
plt.figure(figsize=(9, 5))
plt.plot(np.arange(1, len(cumulative) + 1), cumulative * 100)
plt.axhline(90, c="red", ls="--", label="90% дисперсии")
plt.axvline(n90, c="green", ls="--", label=f"{n90} компонент")
plt.xlabel("Количество главных компонент")
plt.ylabel("Накопленная объяснённая дисперсия, %")
plt.title("Выбор количества компонент после StandardScaler")
plt.legend()
plt.grid(alpha=0.3)
plt.tight_layout()
save_fig("step5_variance.png")

# Отдельно измеряем обучение PCA и обучение SVC после уменьшения размерности.
start = perf_counter()
pca90 = PCA(n_components=n90, svd_solver="full")
X_tr_pca = pca90.fit_transform(X_tr_s)
pca_seconds = perf_counter() - start
X_te_pca = pca90.transform(X_te_s)
svc_pca = SVC(C=1, gamma="scale")
start = perf_counter()
svc_pca.fit(X_tr_pca, y_tr)
svc_seconds = perf_counter() - start
pca_pred = svc_pca.predict(X_te_pca)
pca_accuracy = accuracy_score(y_te, pca_pred)
results = pd.DataFrame([
    {"model": "SVC", "features": X.shape[1], "accuracy": full_accuracy,
     "auc": roc_auc_score(y_te, svc_full.decision_function(X_te_s)),
     "pca_seconds": 0, "svc_seconds": full_seconds, "total_seconds": full_seconds},
    {"model": "PCA + SVC", "features": n90, "accuracy": pca_accuracy,
     "auc": roc_auc_score(y_te, svc_pca.decision_function(X_te_pca)),
     "pca_seconds": pca_seconds, "svc_seconds": svc_seconds,
     "total_seconds": pca_seconds + svc_seconds}
]).set_index("model")
results.to_csv(os.path.join(BASE, "metrics.csv"))
print("\nИтог:\n", results.round(4).to_string())
pd.DataFrame({"row_index": X_te.index, "y_true": y_te.to_numpy(),
              "SVC_prediction": full_pred,
              "SVC_score": svc_full.decision_function(X_te_s),
              "PCA_SVC_prediction": pca_pred,
              "PCA_SVC_score": svc_pca.decision_function(X_te_pca)}).to_csv(
                  os.path.join(BASE, "predictions.csv"), index=False)
print("Изменение accuracy:", round(pca_accuracy - full_accuracy, 4))
print("Отношение времени SVC / (PCA + SVC):",
      round(full_seconds / (pca_seconds + svc_seconds), 3))

fig, axes = plt.subplots(1, 2, figsize=(11, 5))
axes[0].bar(results.index, results.accuracy, color=["steelblue", "darkorange"])
axes[0].set_ylim(0, 1)
axes[0].set_ylabel("Accuracy test")
axes[0].set_title("Точность на одних и тех же вакансиях")
for i, value in enumerate(results.accuracy):
    axes[0].text(i, value + 0.02, f"{value:.4f}", ha="center")
axes[1].bar(results.index, results.svc_seconds, label="Обучение SVC")
axes[1].bar(results.index, results.pca_seconds, bottom=results.svc_seconds,
            label="PCA: обучение и преобразование train")
axes[1].set_ylabel("Секунды")
axes[1].set_title("Время обучения на этом компьютере")
axes[1].legend(fontsize=8)
plt.tight_layout()
save_fig("step5_comparison.png")

if SHOW_PLOTS:
    plt.show()
