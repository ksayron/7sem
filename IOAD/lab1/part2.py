import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
df = pd.read_csv('data.csv')

print(df.describe())

plt.figure(figsize=(8, 5))
plt.hist(df['Credit_Score'], bins=15, edgecolor='black', alpha=0.7, color='skyblue')
plt.title('credit gist')
plt.xlabel('credit')
plt.ylabel('cleint amount')
plt.grid(True, alpha=0.3)
plt.show()

mean_val = df['Credit_Score'].mean()
median_val = df['Credit_Score'].median()
print(f"mean credit: {mean_val:.2f}")
print(f"median credit: {median_val:.2f}")

# 5. Box plot
plt.ion()
plt.figure(figsize=(6, 6))
plt.boxplot(df['Credit_Score'].dropna(), orientation='vertical', patch_artist=True, 
            boxprops=dict(facecolor='lightgreen'))
plt.title('box_plot credit')
plt.ylabel('credit')
plt.grid(True, axis='y', alpha=0.3)
plt.show(block=True)

# Объяснение Box plot (выводится текстом)
#print("\n--- Объяснение Box plot ---")
#print("1. Зеленая линия внутри прямоугольника — это МЕДИАНА (50-й процентиль).")
#print("2. Нижняя граница прямоугольника — 25-й процентиль (Q1).")
#print("3. Верхняя граница прямоугольника — 75-й процентиль (Q3).")
#print("4. Высота прямоугольника = межквартильный размах (IQR = Q3 - Q1).")
#print("5. 'Усы' (усы) показывают границы нормы (обычно Q1 - 1.5*IQR и Q3 + 1.5*IQR).")
#print("6. Точки за пределами усов — это ВЫБРОСЫ (аномально низкий или высокий рейтинг).")

# 6. Метод describe
description = df['Credit_Score'].describe()
print("describe credit:")
print(description)

#print("\n--- Пояснение параметров метода .describe() ---")
#print("count  -> Общее количество НЕПУСТЫХ значений (сколько клиентов учтено).")
#print("mean   -> Среднее арифметическое.")
#print("std    -> Стандартное отклонение (разброс данных вокруг среднего).")
#print("min    -> Минимальный кредитный рейтинг в выборке.")
#print("25%    -> 1-й квартиль (Q1): 25% клиентов имеют рейтинг НИЖЕ этого числа.")
#print("50%    -> 2-й квартиль (Медиана): 50% клиентов имеют рейтинг НИЖЕ этого числа.")
#print("75%    -> 3-й квартиль (Q3): 75% клиентов имеют рейтинг НИЖЕ этого числа.")
#print("max    -> Максимальный кредитный рейтинг в выборке.")

grouped = df.groupby(['Employment_Status', 'Default_Risk']).agg({
    'Income_USD': ['mean', 'count'],
    'Credit_Score': ['mean', 'min', 'max'],
    'Total_BNPL_Debt_USD': ['mean', 'sum']
})

print()
print(grouped)