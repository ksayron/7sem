import os
import numpy as np
import pandas as pd
from scipy import stats

BASE = os.path.dirname(os.path.abspath(__file__))
df = pd.read_csv(os.path.join(BASE, "data.csv"))

print("=" * 70)
print("1) РАЗМЕР И ПОЛНОТА")
print(f"Строк: {len(df)}, столбцов: {df.shape[1]}")
n_feat = df.shape[1] - 2  # минус ID и целевой
print(f"Наблюдений на признак: {len(df) / n_feat:.0f} (рекомендация >= 10-20)")
na = df.isna().sum()
print(f"Столбцов с пропусками: {(na > 0).sum()}; суммарно NaN: {int(na.sum())}")
print((na[na > 0] / len(df) * 100).round(2).to_string())
dups = df.duplicated(subset="Customer_ID").sum()
print(f"Дубликатов Customer_ID: {dups}")

print("=" * 70)
print("2) ТИПЫ ПРИЗНАКОВ")
print(df.dtypes.to_string())

print("=" * 70)
print("3) РАСПРЕДЕЛЕНИЯ ЧИСЛОВЫХ ПРИЗНАКОВ (нормальность для линейной регрессии)")
num_cols = ["Age", "Income_USD", "Credit_Score", "Total_BNPL_Active_Loans",
            "Total_BNPL_Debt_USD", "Average_Transaction_Value_USD"]
for c in num_cols:
    s = df[c].dropna()
    sk, kurt = stats.skew(s), stats.kurtosis(s)
    p = stats.shapiro(s[np.isfinite(s)] if len(s) <= 5000 else
                      s.sample(5000, random_state=0))[1]
    print(f"  {c:34s} skew={sk:6.2f}  kurtosis={kurt:6.2f}  "
          f"Shapiro p={p:.2e} {'<-- сильная асимметрия' if abs(sk) > 1 else ''}")

print("=" * 70)
print("4) КОРРЕЛЯЦИИ С КАНДИДАТАМИ В ЦЕЛЕВУЮ (регрессионный анализ)")
df_r = df.copy()
df_r["Late_Payment_History"] = (df_r["Late_Payment_History"] == "Yes").astype(int)
risk_num = df_r["Default_Risk"].map({"Low": 0, "Medium": 1, "High": 2})
df_num = df_r[num_cols + ["Late_Payment_History"]].copy()
df_num["Default_Risk_ord"] = risk_num
targets = ["Total_BNPL_Debt_USD", "Credit_Score", "Default_Risk_ord"]
for t in targets:
    cor = df_num.corr(numeric_only=True)[t].drop(t).sort_values(key=abs,
                                                                ascending=False)
    print(f"\n  Целевая: {t}")
    print(cor.round(3).to_string())

print("=" * 70)
print("5) МУЛЬТИКОЛЛИНЕАРНОСТЬ (матрица Пирсона и VIF)")
corr = df_num.corr(numeric_only=True)
high = []
for i in range(len(corr)):
    for j in range(i + 1, len(corr)):
        r = corr.iloc[i, j]
        if abs(r) > 0.7:
            high.append((corr.index[i], corr.columns[j], r))
print("Пары с |r| > 0.7:")
print("\n".join(f"  {a} ~ {b}: r={r:.3f}" for a, b, r in high) or "  нет")

print("\nVIF (числовые признаки, попарно-удаленные NaN):")
from numpy.linalg import inv
sub = df_num[num_cols].dropna()
vif = {}
for c in num_cols:
    y = sub[c].values
    X = np.column_stack([np.ones(len(sub)),
                         sub.drop(columns=c).values])
    beta, *_ = np.linalg.lstsq(X, y, rcond=None)
    resid = y - X @ beta
    ss_tot = ((y - y.mean()) ** 2).sum()
    r2 = 1 - (resid ** 2).sum() / ss_tot if ss_tot > 0 else 0
    vif[c] = 1 / (1 - r2) if r2 < 1 else np.inf
print(pd.Series(vif).round(2).to_string())
print("VIF > 10 -> критично, 5-10 -> заметно, < 5 -> приемлемо")

print("=" * 70)
print("6) ЛИНЕЙНОСТЬ / ГЕТЕРОСКЕДАСТИЧНОСТЬ (для главной пары признак-цель)")
for t in ["Total_BNPL_Debt_USD", "Credit_Score"]:
    best = df_num.corr(numeric_only=True)[t].drop(t).abs().idxmax()
    x = df_num[best].fillna(df_num[best].median())
    y = df_num[t].fillna(df_num[t].median())
    lin = stats.pearsonr(x, y)
    sp = stats.spearmanr(x, y)
    print(f"\n  {t}: сильнейший признак {best}")
    print(f"  Pearson r={lin[0]:.3f} (p={lin[1]:.2e}), "
          f"Spearman rho={sp[0]:.3f} "
          f"{'-> связь монотонна, близка к линейной' if abs(lin[0]-sp[0]) < 0.05 else '-> возможна нелинейность'}")
    # Spearman-корреляция |остатков| с предсказанием ~ гетероскедастичность
    beta = np.polyfit(x, y, 1)
    resid = y - np.polyval(beta, x)
    het = stats.spearmanr(x, np.abs(resid))
    print(f"  Гетероскедастичность (corr(x,|e|)): rho={het[0]:.3f} "
          f"{'-> выражена' if het[0] > 0.3 else '-> умеренная/слабая'}")

print("=" * 70)
print("7) ВЫБРОСЫ (доля точек за пределами 3 IQR)")
for c in num_cols:
    q1, q3 = df[c].quantile([0.25, 0.75])
    iqr = q3 - q1
    out = ((df[c] < q1 - 3 * iqr) | (df[c] > q3 + 3 * iqr)).mean() * 100
    print(f"  {c:34s} {out:5.2f}%")
print("\nВЕРДИКТ: см. выводы в ответе (итоговая оценка пригодности).")
