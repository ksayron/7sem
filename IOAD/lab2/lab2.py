import os

import matplotlib
import numpy as np
import pandas as pd
from sklearn.impute import KNNImputer
from sklearn.preprocessing import MinMaxScaler, StandardScaler

SHOW_PLOTS = os.environ.get("LAB2_NO_SHOW") != "1"
if not SHOW_PLOTS:
    matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

BASE = os.path.dirname(os.path.abspath(__file__))
CSV_IN = os.path.join(BASE, "data.csv")
CSV_OUT = os.path.join(BASE, "data_processed.csv")

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
print(df.dtypes)
print("\describe:")
print(df.describe(include="all").T)

print("\n" + "=" * 70)

na_cols = df.isna().sum()
na_pct = (na_cols / len(df) * 100).round(2)
na_table = pd.DataFrame({"nan": na_cols, "%": na_pct})
print("\n")
print(na_table[na_table["nan"] > 0].to_string())
print(f"col no nan: {(na_cols == 0).sum()} из {len(na_cols)}")
print(f"Total: {int(na_cols.sum())}")

na_rows = df.isna().sum(axis=1)
print(f"\rows nan: {(na_rows > 0).sum()} "
      f"({(na_rows > 0).mean() * 100:.1f}%)")
print("distribution:")
print(na_rows.value_counts().sort_index().to_string())

num_rows_na = (na_rows > 0).sum()
if num_rows_na > 0:
    sub = df[df.isna().any(axis=1)]
    sub = sub.iloc[: min(200, len(sub))]
    plt.figure(figsize=(10, max(4, min(14, len(sub) * 0.06))))
    plt.imshow(sub.isna(), aspect="auto", cmap="Greys", interpolation="nearest")
    plt.xticks(range(df.shape[1]), df.columns, rotation=45, ha="right")
    plt.yticks([])
    plt.title(f"heatmap nan "
              f"(first {len(sub)} rows with nan)")
    plt.tight_layout()
    save_fig("step1_missing_matrix.png")

print("\n3)default:")
susp = df[(df["Employment_Status"] == "Unemployed") &
          (df["Income_USD"] == 5000.0)]
print(f"- Unemployed default income 5000 USD: {len(susp)} rows ")
print(f"- Age outside 18-100: "
      f"{((df['Age'] < 18) | (df['Age'] > 100)).sum()}")
print(f"- num < 0: "
      f"{(df.select_dtypes('number') < 0).sum().sum()}")


# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("data fillup")
print("=" * 70)

df_mean = df.copy()
df_median = df.copy()
df_group = df.copy()
df_knn = df.copy()

num_cols = ["Age", "Income_USD", "Credit_Score", "Total_BNPL_Active_Loans",
            "Total_BNPL_Debt_USD", "Average_Transaction_Value_USD"]
print(f"before:\n{df[num_cols].isna().sum()[lambda s: s > 0]}")

# Способ 1: среднее
for c in ["Income_USD", "Credit_Score"]:
    df_mean[c] = df_mean[c].fillna(df_mean[c].mean())

# Способ 2: медиана
for c in ["Income_USD", "Credit_Score"]:
    df_median[c] = df_median[c].fillna(df_median[c].median())

# Способ 3: групповая медиана (предметно обоснованно)
# Доход сильно зависит от статуса занятости (студент/безработный/наемный
# отличаются в разы), поэтому общая медиана исказила бы данные.
# Credit_Score напрямую связан с риском дефолта -> медиана по группе риска.
df_group["Income_USD"] = df_group.groupby("Employment_Status")["Income_USD"] \
    .transform(lambda s: s.fillna(s.median()))
df_group["Credit_Score"] = df_group.groupby("Default_Risk")["Credit_Score"] \
    .transform(lambda s: s.fillna(s.median()))

# Способ 4: метод ближайших соседей (KNNImputer, k=5)
mm = MinMaxScaler()
scaled = mm.fit_transform(df_knn[num_cols])
imputed = KNNImputer(n_neighbors=5).fit_transform(scaled)
df_knn[num_cols] = mm.inverse_transform(imputed)

cmp = pd.DataFrame({
    "mean": df_mean[["Income_USD", "Credit_Score"]].mean(),
    "median": df_median[["Income_USD", "Credit_Score"]].median(),
    "group": df_group[["Income_USD", "Credit_Score"]].median(),
    "KNN": df_knn[["Income_USD", "Credit_Score"]].median(),
    "KNN std": df_knn[["Income_USD", "Credit_Score"]].std(),
})
print("\mcomparison):")
print(cmp.round(2).to_string())

df = df_knn.copy()
assert df.isna().sum().sum() == 0, "nan still present!"
print(f"after: {df.isna().sum().sum()}")

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("=" * 70)
df_raw = pd.read_csv(CSV_IN)

fig, axes = plt.subplots(2, 2, figsize=(13, 8))
for i, col in enumerate(["Income_USD", "Credit_Score"]):
    axes[i, 0].hist(df_raw[col].dropna(), bins=50, alpha=0.6,
                    label="до обработки (NaN отброшены)", color="steelblue")
    axes[i, 0].set_title(f"{col}: ДО обработки")
    axes[i, 0].legend()
    axes[i, 1].hist(df_raw[col].dropna(), bins=50, alpha=0.5,
                    label="до обработки", color="steelblue")
    axes[i, 1].hist(df[col], bins=50, alpha=0.5,
                    label="после заполнения (KNN)", color="darkorange")
    axes[i, 1].set_title(f"{col}: ДО и ПОСЛЕ обработки пропусков")
    axes[i, 1].legend()
plt.tight_layout()
save_fig("step4_histograms.png")

print("\comparison (std, mean, asim):")
dist = pd.DataFrame({
    "std before": df_raw[["Income_USD", "Credit_Score"]].std(),
    "std after": df[["Income_USD", "Credit_Score"]].std(),
    "median do": df_raw[["Income_USD", "Credit_Score"]].median(),
    "median after": df[["Income_USD", "Credit_Score"]].median(),
    "skew before": df_raw[["Income_USD", "Credit_Score"]].skew(),
    "skew after": df[["Income_USD", "Credit_Score"]].skew(),
})
print(dist.round(3).to_string())

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("anomaly check")
print("=" * 70)
# Boxplot
fig, axes = plt.subplots(2, 3, figsize=(14, 7))
for ax, col in zip(axes.ravel(), num_cols):
    df.boxplot(column=col, ax=ax)
    ax.set_title(col)
plt.suptitle("Boxplot")
plt.tight_layout()
save_fig("step6_boxplots.png")

print("\n IQR:")
outlier_mask = pd.Series(False, index=df.index)
for col in num_cols:
    q1, q3 = df[col].quantile([0.25, 0.75])
    iqr = q3 - q1
    lo, hi = q1 - 1.5 * iqr, q3 + 1.5 * iqr
    n_out = ((df[col] < lo) | (df[col] > hi)).sum()
    print(f"  {col:32s} границы [{lo:8.1f}; {hi:8.1f}]  выбросов: {n_out}")
    outlier_mask |= (df[col] < lo) | (df[col] > hi)

# Анализ природы выбросов: интересные ли они?
debt_hi = df[df["Total_BNPL_Debt_USD"] >
             df["Total_BNPL_Debt_USD"].quantile(0.75) +
             1.5 * (df["Total_BNPL_Debt_USD"].quantile(0.75) -
                    df["Total_BNPL_Debt_USD"].quantile(0.25))]
print(f"\nАнализ выбросов по долгу (кандидаты на 'интересные' записи): "
      f"{len(debt_hi)} строк")
print("Распределение Default_Risk среди них, %:")
print((debt_hi["Default_Risk"].value_counts(normalize=True) * 100)
      .round(1).to_string())
print("Примеры (топ-5 по долгу):")
print(debt_hi.nlargest(5, "Total_BNPL_Debt_USD")[
    ["Customer_ID", "Age", "Employment_Status", "Income_USD",
     "Total_BNPL_Active_Loans", "Total_BNPL_Debt_USD", "Default_Risk"]
].to_string(index=False))


before = len(df)
df = df[~outlier_mask].reset_index(drop=True)
print(f"\nУдалено записей-выбросов: {before - len(df)} "
      f"({(before - len(df)) / before * 100:.1f}%)")
print(f"Размер после шага 6: {df.shape}")

# ---------------------------------------------------------------------------
print("\n" + "=" * 70)
print("Label Encoding")
print("=" * 70)
late_map = {"No": 0, "Yes": 1}
df["Late_Payment_History"] = df["Late_Payment_History"].map(late_map)
print(f"Label Encoding Late_Payment_History: {late_map}")

risk_map = {"Low": 0, "Medium": 1, "High": 2}
df["Default_Risk"] = df["Default_Risk"].map(risk_map)
print(f"Label Encoding Default_Risk: {risk_map}")

# One-Hot Encoding для номинальных признаков
cat_cols = ["Employment_Status", "Shopping_Category_Most_Frequent"]
df = pd.get_dummies(df, columns=cat_cols, dtype=int)
print(f"One-Hot Encoding {cat_cols}: добавлено столбцов "
      f"{[c for c in df.columns if any(p in c for p in ('Employment_', 'Shopping_'))]}")
df = df.drop(columns=["Customer_ID"])
print(f"Столбец Customer_ID удален. Размер: {df.shape}")
print(df.head(3).to_string())

print("\n" + "=" * 70)
print("normalization")
features = [c for c in df.columns if c != "Default_Risk"]
df_mm = df.copy()
df_mm[features] = MinMaxScaler().fit_transform(df[features])
df_std = df.copy()
df_std[features] = StandardScaler().fit_transform(df[features])


df.to_csv(CSV_OUT, index=False)

if SHOW_PLOTS:
    print("\nОткрываю окна с графиками (закройте их, чтобы завершить "
          "работу скрипта)...")
    plt.show()
    print("Все окна закрыты. Работа завершена.")
