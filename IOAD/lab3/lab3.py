import os

import matplotlib
import numpy as np
import pandas as pd
from sklearn.linear_model import LinearRegression
from sklearn.metrics import mean_squared_error, r2_score
from sklearn.model_selection import train_test_split

SHOW_PLOTS = os.environ.get("LAB3_NO_SHOW") != "1"
if not SHOW_PLOTS:
    matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

BASE = os.path.dirname(os.path.abspath(__file__))
CSV_IN = os.path.join(BASE, "data.csv")

pd.set_option("display.width", 160)
pd.set_option("display.max_columns", 30)


def save_fig(name):
    plt.savefig(os.path.join(BASE, name), dpi=150)
    if not SHOW_PLOTS:
        plt.close()
    print(f"График сохранен в {name}")


df = pd.read_csv(CSV_IN)
print("=" * 70)
print(f"shape: {df.shape}")
target = "Total_BNPL_Debt_USD"
num_cols = ["Age", "Income_USD", "Credit_Score", "Total_BNPL_Active_Loans",
            "Average_Transaction_Value_USD"]
print(f"target: {target}")
print(f"features: {num_cols + ['Late_Payment_History']}")

for c in ["Income_USD", "Credit_Score"]:
    df[c] = df[c].fillna(df[c].median())
df["Late_Payment_History"] = (df["Late_Payment_History"] == "Yes").astype(int)

q1, q3 = df[target].quantile([0.25, 0.75])
iqr = q3 - q1
mask = df[target].between(q1 - 3 * iqr, q3 + 3 * iqr)
print(f"выбросы целевой (3 IQR) удалены: {(~mask).sum()}")
df = df[mask].reset_index(drop=True)

cols = num_cols + ["Late_Payment_History", target]

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("2) corr heatmap")
print("=" * 70)

corr = df[cols].corr()
print(corr.round(3).to_string())

plt.figure(figsize=(10, 8))
im = plt.imshow(corr, cmap="RdBu_r", vmin=-1, vmax=1)
plt.colorbar(im)
plt.xticks(range(len(cols)), cols, rotation=45, ha="right")
plt.yticks(range(len(cols)), cols)
for i in range(len(cols)):
    for j in range(len(cols)):
        v = corr.iloc[i, j]
        plt.text(j, i, f"{v:.2f}", ha="center", va="center", fontsize=8,
                 color="white" if abs(v) > 0.5 else "black")
plt.title("Матрица корреляций")
plt.tight_layout()
save_fig("step2_corr_heatmap.png")

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("3) scatter matrix")
print("=" * 70)

axes = pd.plotting.scatter_matrix(df[cols], figsize=(16, 14),
                                  diagonal="hist", alpha=0.15, s=6)
for ax in axes.ravel():
    ax.set_xticks([])
    ax.set_yticks([])
    ax.xaxis.label.set_rotation(45)
    ax.yaxis.label.set_rotation(0)
    ax.yaxis.label.set_ha("right")
plt.tight_layout()
save_fig("step3_scatter_matrix.png")

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("4) выбор параметра")
print("=" * 70)

cor_t = corr[target].drop(target).sort_values(key=abs, ascending=False)
print(cor_t.round(3).to_string())
main_f = cor_t.abs().idxmax()
print(f"\n-> параметр простой регрессии: {main_f} (r={cor_t[main_f]:.3f})")

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("5-6) простая линейная регрессия")
print("=" * 70)

X_tr, X_te, y_tr, y_te = train_test_split(df[cols[:-1]], df[target],
                                          test_size=0.2, random_state=42)
m1 = LinearRegression().fit(X_tr[[main_f]], y_tr)
p1 = m1.predict(X_te[[main_f]])
r2a, r2b = m1.score(X_tr[[main_f]], y_tr), r2_score(y_te, p1)
rmse1 = np.sqrt(mean_squared_error(y_te, p1))
print(f"модель: {target} = {m1.intercept_:.2f} + {m1.coef_[0]:.2f} * {main_f}")
print(f"R^2 train: {r2a:.4f}")
print(f"R^2 test:  {r2b:.4f}")
print(f"RMSE test: {rmse1:.1f}")

plt.figure(figsize=(10, 6))
plt.scatter(df[main_f], df[target], s=6, alpha=0.15, label="данные")
xs = np.linspace(df[main_f].min(), df[main_f].max(), 100)
plt.plot(xs, m1.predict(pd.DataFrame(xs, columns=[main_f])), "r-", lw=2,
         label=f"y = {m1.intercept_:.0f} + {m1.coef_[0]:.0f}x (R^2={r2b:.3f})")
plt.xlabel(main_f)
plt.ylabel(target)
plt.title("Простая линейная регрессия")
plt.legend()
plt.tight_layout()
save_fig("step5_simple_model.png")

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("7-8) множественная регрессия")
print("=" * 70)

m2 = LinearRegression().fit(X_tr, y_tr)
p2 = m2.predict(X_te)
r2c, r2d = m2.score(X_tr, y_tr), r2_score(y_te, p2)
rmse2 = np.sqrt(mean_squared_error(y_te, p2))
print(f"intercept: {m2.intercept_:.2f}")
print(pd.Series(m2.coef_, index=cols[:-1]).round(2).to_string())
print(f"R^2 train: {r2c:.4f}")
print(f"R^2 test:  {r2d:.4f}")
print(f"RMSE test: {rmse2:.1f}")

plt.figure(figsize=(10, 6))
plt.scatter(y_te, p2, s=8, alpha=0.2)
lims = [min(y_te.min(), p2.min()), max(y_te.max(), p2.max())]
plt.plot(lims, lims, "r--", lw=2)
plt.xlabel("факт")
plt.ylabel("прогноз")
plt.title(f"Множественная регрессия (R^2={r2d:.3f})")
plt.tight_layout()
save_fig("step8_multiple_model.png")

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("9) вывод")
print("=" * 70)
print(f"простая:       R^2 test={r2b:.4f}, RMSE={rmse1:.1f}")
print(f"множественная: R^2 test={r2d:.4f}, RMSE={rmse2:.1f}")
print(f"прирост R^2: {r2d - r2b:+.4f}")
print(f"-> {main_f} (r={cor_t[main_f]:.3f}) определяет зависимость, "
      "остальные признаки не улучшают модель")

if SHOW_PLOTS:
    plt.show()
