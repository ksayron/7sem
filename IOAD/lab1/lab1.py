import numpy as np
import pandas as pd

# 1
print("task 1\n")
arr = np.random.randint(0, 10, size=(2, 10))
print(arr,"\n")

arr1 = arr[0, :]
arr2 = arr[1, :]
print(arr1)
print(arr2, "\n")

target_value = 5
found_elements = arr1[arr1 == target_value]
print(found_elements)
print(found_elements.size)

# 2
print("\n\n task 2\n")
series = pd.Series(arr1)
print(series)

print("умножение:\n", series * 2)
print("сумма:", series.sum())
print("среднее:", series.mean())
print("std:", series.std())
print("unique:", series.unique())

df = pd.DataFrame(np.random.randint(45, 55, size=(5, 4)))
print(df)

df.columns = ['A', 'B', 'C', 'D']
print("\n заголовки:")
print(df)

df = df.drop(index=0)
print("\n delete index 0:")
print(df)

df = df.drop(columns='A')
print("\n delete column 'A':")
print(df)

print(f"\n shape: {df.shape}")

search_value = 50
mask = df == search_value
print(f"\n искомое значение {search_value}:")
print(mask)
print(f"\n найдено : {mask.sum().sum()}")