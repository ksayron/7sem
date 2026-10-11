"""Сборка нормализованного датасета IT-вакансий для лабораторной 4.

Из исходного CSV hh.ru берутся только вакансии категории «Информационные
технологии», по названию каждой вакансии определяется роль (фронтенд, бэкенд,
devops и т.д.), считаются цель и признаки, пропуски заполняются, категориями
кодируются в числа, а числовые признаки масштабируются. Результат —
`it_vacancies.csv`, готовый для обучения моделей без нормализации в лабе.
"""
import os
import re

import pandas as pd
from sklearn.preprocessing import StandardScaler

BASE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(BASE, "hh_vacancies_2026_07.csv")
OUT = os.path.join(BASE, "it_vacancies.csv")
IT = "Информационные технологии"
MIN_RESPONSES = 100  # медиана откликов у IT-вакансий — 127

USECOLS = ["category", "name", "company", "experience", "collected_area_id",
           "employment_form", "work_schedule", "working_hours", "is_on_site",
           "is_remote", "is_hybrid", "is_field_work", "internship",
           "night_shifts", "accept_temporary", "accept_labor_contract",
           "response_letter_required", "salary_from_rub", "salary_shown",
           "salary_period", "gross", "responses_count", "publication_time",
           "snapshot_date"]
FLAG_COLS = ["is_on_site", "is_remote", "is_hybrid", "is_field_work",
             "internship", "night_shifts", "accept_temporary",
             "accept_labor_contract", "response_letter_required",
             "salary_shown"]
NUM_COLS = ["age_days", "title_len", "salary_from_rub"]
CAT_COLS = ["role", "experience", "collected_area_id", "employment_form",
            "salary_period", "gross"]
RARE_COLS = ["work_schedule", "working_hours"]
RARE_TOP = 6

# Первое совпадение по названию вакансии выигрывает: частные роли стоят выше
# общего правила «бэкенд». Сравнение идёт по названию в нижнем регистре.
ROLE_RULES = [
    ("фронтенд", r"front|фронт|html|css|javascript|typescript|react|vue|angular"),
    ("мобильная разработка", r"android|ios|mobile|мобильн|flutter"),
    ("qa и тестирование", r"qa|тестир|автотест|test engineer|quality"),
    ("дизайн", r"дизайн|designer|ui[\s/-]?ux|ux[\s/-]?ui|графическ"),
    ("безопасность", r"безопасн|security|pentest|пентест"),
    ("1с", r"1с|1c"),
    ("devops и инфраструктура",
     r"devops|devsecops|девопс|sre|инфраструктур|infrastructure|"
     r"системн\w* администратор|сисадмин|администратор|administrator|dba|"
     r"баз данных|сетев|support|поддержк|сопровожд"),
    ("данные и аналитика",
     r"data|дата|данны|аналитик|analyst|analytic|ml\b|mlops|"
     r"machine learning|машинн\w* обуч|bi\b|dwh|nlp|vision|ai\b|"
     r"искусственн|research"),
    ("управление разработкой",
     r"менеджер|manager|продакт|продуктолог|product owner|владелец продукта|"
     r"руководител|начальник|head of|\blead\b|tech lead|тимлид|scrum|директор"),
    ("бэкенд",
     r"back|бэкенд|сервер|архитектор|разработчик|программист|developer|"
     r"python|java|golang|go\b|php|ruby|scala|kotlin|c#|c\+\+|\.net|node|"
     r"sql|embedded|встроенн|rpa"),
]
ROLE_OTHER = "другое"
ROLE_RULES = [(role, re.compile(pattern)) for role, pattern in ROLE_RULES]


def role_of(title):
    """Роль по названию вакансии; если ни одно правило не подошло — «другое»."""
    text = str(title).lower()
    for role, pattern in ROLE_RULES:
        if pattern.search(text):
            return role
    return ROLE_OTHER
def main():
    df = pd.read_csv(SRC, usecols=USECOLS)
    print("Исходный CSV:", df.shape, "(нужны 24 столбца из 38)")
    df = df[df["category"] == IT].copy()
    print("IT-вакансий:", len(df))

    # Цель: перегружена ли вакансия откликами. Счётчик — только в ответ.
    df["overloaded"] = (df["responses_count"] >= MIN_RESPONSES).astype(int)
    # Повтор одного объявления у того же работодателя не усиливает выборку.
    dedup = ["name", "company", "experience", "collected_area_id",
             "employment_form", "work_schedule"]
    df = df.drop_duplicates(dedup + ["overloaded"]).copy()
    print("После удаления повторов:", len(df))
    print("Доля перегруженных (от", MIN_RESPONSES, "откликов):",
          round(df["overloaded"].mean(), 4))

    # Роль по названию — главный новый признак датасета.
    df["role"] = df["name"].map(role_of)
    roles = df["role"].value_counts()
    print("Роли:\n", roles.to_string())
    print("Доля «другое»:", round(roles.get(ROLE_OTHER, 0) / len(df), 4))

    # Возраст вакансии на момент сбора: за время висения набираются отклики.
    pub = pd.to_datetime(df["publication_time"], utc=True, format="ISO8601")
    snap = pd.to_datetime(df["snapshot_date"], utc=True)
    df["age_days"] = ((snap - pub).dt.total_seconds() / 86400).clip(lower=0)
    df["title_len"] = df["name"].str.len()

    # Пропуски: зарплата — медианой IT-вакансий, категории — «не указано».
    df["salary_from_rub"] = df["salary_from_rub"].fillna(
        df["salary_from_rub"].median())
    for col in RARE_COLS:
        df[col] = df[col].astype("string").fillna("не указано")
        top = df[col].value_counts().nlargest(RARE_TOP).index
        df[col] = df[col].where(df[col].isin(top), "другое")
    for col in CAT_COLS[1:]:
        df[col] = df[col].astype("string").fillna("не указано")

    # Числовые признаки приводятся к единой шкале (z-оценка), иначе kNN будет
    # считать расстояние почти только по зарплате в сотнях тысяч.
    df[NUM_COLS] = StandardScaler().fit_transform(df[NUM_COLS])
    flags = df[FLAG_COLS].astype(int)
    dummies = pd.get_dummies(df[CAT_COLS + RARE_COLS], dtype=int)

    out = pd.DataFrame({"publication_date": pub.dt.date.astype(str),
                        "role": df["role"], "overloaded": df["overloaded"]})
    out = pd.concat([out, df[NUM_COLS], flags, dummies], axis=1)
    out = out.sort_values("publication_date").reset_index(drop=True)
    assert out.isna().sum().sum() == 0, "В готовом датасете остались пропуски"
    out.to_csv(OUT, index=False, encoding="utf-8")
    print("Готово:", OUT)
    print("Размер:", out.shape, "— пропусков нет, все признаки числовые")
    print("Баланс классов:", out["overloaded"].value_counts()
          .sort_index().to_dict())


if __name__ == "__main__":
    main()
