"""Аудит кандидата hh.ru; лабораторные этим скриптом не заменяются."""
import os
os.environ.setdefault("OPENBLAS_NUM_THREADS", "1")
os.environ.setdefault("OMP_NUM_THREADS", "1")
import csv
import json
import re
import zipfile
from io import TextIOWrapper
from collections import Counter
from pathlib import Path
from hashlib import sha256

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / ".qa" / "dataset_candidates" / "hh2026"
OUT = ROOT / "research" / "hh2026"
OUT.mkdir(exist_ok=True)
IT = "Информационные технологии"


def audit():
    counters = {}
    missing = Counter()
    ids, duplicates = set(), 0
    records = []
    salary_rows = []
    it_salary = []
    contradictions = 0
    money_title = re.compile(r"(?:руб|₽|\$|евро|\b\d{4,}\b|\b\d+\s*[кk]\b)", re.I)
    with zipfile.ZipFile(DATA / "hh2026.zip") as archive:
        with TextIOWrapper(archive.open("hh_vacancies_2026_07.csv"), encoding="utf-8-sig", newline="") as f:
            reader = csv.DictReader(f)
            for col in reader.fieldnames:
                counters[col] = Counter()
            for row in reader:
                for col, value in row.items():
                    missing[col] += int(not value)
                    # Строковые тексты и ID не собираем в огромные словари.
                    if col not in ["vacancy_id", "name", "address_display", "publication_time",
                                   "salary_from", "salary_to", "salary_from_rub", "salary_to_rub"]:
                        counters[col][value] += 1
                if row["vacancy_id"] in ids:
                    duplicates += 1
                    continue
                ids.add(row["vacancy_id"])
                known_salary = bool(row["salary_from_rub"] or row["salary_to_rub"])
                contradictions += int((row["salary_shown"] == "True") != known_salary)
                # Только необходимые для моделей поля, без адресов и счётчиков откликов.
                feature_keys = ["name", "company", "category", "experience", "collected_area_id",
                    "employment_form", "is_on_site", "is_remote", "is_hybrid", "is_field_work",
                    "work_schedule", "working_hours", "internship", "night_shifts", "accept_temporary",
                    "accept_labor_contract", "response_letter_required", "civil_law_contracts", "languages"]
                record = {k: row[k] for k in feature_keys}
                record["entry_level"] = int(row["experience"] == "Без опыта")
                record["publication_time"] = row["publication_time"]
                record["snapshot_date"] = row["snapshot_date"]
                record["salary_shown"] = int(known_salary)
                record["monthly"] = int(row["salary_period"] == "За месяц")
                record["gross"] = row["gross"]
                record["lower_rub"] = float(row["salary_from_rub"]) if row["salary_from_rub"] else None
                record["upper_rub"] = float(row["salary_to_rub"]) if row["salary_to_rub"] else None
                record["money_in_title"] = bool(money_title.search(row["name"]))
                records.append(record)
                if record["monthly"] and known_salary:
                    low, high = record["lower_rub"], record["upper_rub"]
                    salary_rows.append((low + high) / 2 if low is not None and high is not None else low or high)
                    if row["category"] == IT:
                        it_salary.append(salary_rows[-1])
    import numpy as np
    result = {"rows": len(records), "columns": len(counters), "duplicate_ids": duplicates,
        "salary_shown_contradictions": contradictions,
        "zip_sha256": sha256((DATA / "hh2026.zip").read_bytes()).hexdigest(),
        "missing": dict(missing),
        "distributions": {k: v.most_common(35) for k, v in counters.items() if v},
        "cardinality": {k: len(v) for k, v in counters.items() if v},
        "monthly_salary_rows": len(salary_rows),
        "monthly_salary_quantiles_mixed_tax": dict(zip(["p01", "p10", "p50", "p90", "p99"],
            np.quantile(salary_rows, [.01, .1, .5, .9, .99]).tolist())),
        "it_monthly_salary_rows": len(it_salary),
        "it_salary_median_mixed_tax": float(np.median(it_salary)),
        "publication_min": min(r["publication_time"] for r in records),
        "publication_max": max(r["publication_time"] for r in records)}
    (OUT / "audit.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    # Индивидуальные вакансии — только в исключённой из Git папке кандидата.
    import pandas as pd
    df = pd.DataFrame(records)
    df.to_pickle(DATA / "pilot_records.pkl")
    print(json.dumps({k: v for k, v in result.items() if k not in ["missing", "distributions", "cardinality"]}, ensure_ascii=False, indent=2), flush=True)
    print("Категории:", result["distributions"]["category"], flush=True)
    print("Опыт IT:", df.loc[df.category == IT, "experience"].value_counts().to_dict(), flush=True)
    return df


def pilot(df):
    import numpy as np
    import pandas as pd
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    from sklearn import set_config
    from sklearn.compose import ColumnTransformer
    from sklearn.preprocessing import OneHotEncoder
    from sklearn.feature_extraction.text import TfidfVectorizer
    from sklearn.model_selection import StratifiedGroupKFold
    from sklearn.tree import DecisionTreeClassifier
    from sklearn.neighbors import KNeighborsClassifier
    from sklearn.linear_model import LogisticRegression
    from sklearn.svm import LinearSVC
    from sklearn.dummy import DummyClassifier
    from sklearn.metrics import accuracy_score, balanced_accuracy_score, f1_score, precision_score, recall_score, roc_auc_score
    set_config(working_memory=1)
    rows, datasets = [], []
    # 100 тыс. — фиксированное определение цели, не подобранный порог по результату.
    salary = df[(df.monthly == 1) & (df.gross == "False") & df.lower_rub.notna() & ~df.money_in_title].copy()
    salary["target"] = (salary.lower_rub >= 100000).astype(int)
    junior = df[df.category == IT].copy()
    junior["target"] = (junior.experience == "Без опыта").astype(int)
    senior = df[df.category == IT].copy()
    senior["target"] = senior.experience.isin(["3-6 лет", "Более 6 лет"]).astype(int)
    tasks = [("salary_ge_100k_net_month", salary, False), ("it_no_experience", junior, True),
             ("it_experience_ge_3_years", senior, True)]
    for task, data, is_junior in tasks:
        before = len(data)
        # Повтор одного текста объявления у того же работодателя не усиливает выборку.
        dedup = ["name", "company", "category", "experience", "collected_area_id", "employment_form", "work_schedule"]
        # У одного шаблона бывают разные зарплаты; противоречия сохраняем как шум.
        data = data.drop_duplicates(dedup + ["target"])
        after_dedup = len(data)
        if len(data) > 20000:
            data = data.sample(20000, random_state=42)
        data = data.reset_index(drop=True)
        labels = data.target.to_numpy()
        categories = ["category", "collected_area_id", "employment_form", "is_on_site", "is_remote", "is_hybrid",
            "is_field_work", "work_schedule", "working_hours", "internship", "night_shifts", "accept_temporary",
            "accept_labor_contract", "response_letter_required", "civil_law_contracts", "languages"]
        if not is_junior:
            categories.append("experience")
        else:
            categories.remove("category")  # Все строки этой задачи — IT.
        # Опыт исключён для цели junior; все зарплатные поля исключены для зарплаты.
        assert "experience" not in categories if is_junior else not any("salary" in c for c in categories)
        groups = data.company.replace("", "unknown_company")
        splits = list(StratifiedGroupKFold(3, shuffle=True, random_state=42).split(data, labels, groups))
        info = {"task": task, "eligible_rows": before, "after_template_dedup": after_dedup,
                "evaluated_rows": len(data), "positive_count": int(labels.sum()),
                "positive_share": float(labels.mean()), "companies": int(groups.nunique())}
        datasets.append(info)
        print("Задача:", info, flush=True)
        for fold, (fit, val) in enumerate(splits):
            assert set(groups.iloc[fit]).isdisjoint(groups.iloc[val])
            pre = ColumnTransformer([
                ("metadata", OneHotEncoder(handle_unknown="ignore", min_frequency=3, dtype=np.float32), categories),
                ("title", TfidfVectorizer(ngram_range=(1, 2), min_df=3, max_features=6000, dtype=np.float32), "name")
            ], transformer_weights={"metadata": .35, "title": 1.}, sparse_threshold=1.)
            xf = pre.fit_transform(data.iloc[fit]).astype(np.float32)
            xv = pre.transform(data.iloc[val]).astype(np.float32)
            yf, yv = labels[fit], labels[val]
            models = {
                "majority": DummyClassifier(strategy="most_frequent"),
                "tree": DecisionTreeClassifier(max_depth=18, min_samples_leaf=10, random_state=42),
                "knn": KNeighborsClassifier(n_neighbors=15, weights="distance", metric="cosine", n_jobs=1),
                "logistic": LogisticRegression(C=3., max_iter=1500, random_state=42),
                "linear_svc": LinearSVC(C=1., max_iter=5000, random_state=42),
            }
            for name, model in models.items():
                model.fit(xf, yf)
                pred = model.predict(xv)
                probability = model.decision_function(xv) if name == "linear_svc" else model.predict_proba(xv)[:, 1]
                row = {"task": task, "fold": fold + 1, "model": name, "train_rows": len(fit),
                    "validation_rows": len(val), "validation_positive_share": float(yv.mean()),
                    "accuracy": accuracy_score(yv, pred), "balanced_accuracy": balanced_accuracy_score(yv, pred),
                    "f1": f1_score(yv, pred, zero_division=0), "precision": precision_score(yv, pred, zero_division=0),
                    "recall": recall_score(yv, pred, zero_division=0), "roc_auc": roc_auc_score(yv, probability)}
                rows.append(row)
                print(task, fold + 1, name, "accuracy", round(row["accuracy"], 4), "F1", round(row["f1"], 4), flush=True)
                del model
            pd.DataFrame(rows).to_csv(OUT / "pilot_folds.csv", index=False)
            del xf, xv, pre, models
        del data, splits
    raw = pd.DataFrame(rows)
    summary = raw.groupby(["task", "model"]).agg(accuracy=("accuracy", "mean"),
        accuracy_std=("accuracy", "std"), balanced_accuracy=("balanced_accuracy", "mean"),
        f1=("f1", "mean"), precision=("precision", "mean"), recall=("recall", "mean"),
        roc_auc=("roc_auc", "mean")).reset_index()
    summary.to_csv(OUT / "pilot_summary.csv", index=False)
    (OUT / "protocol.json").write_text(json.dumps({"seed": 42, "folds": 3,
        "split": "StratifiedGroupKFold, group=company", "sample_limit": 20000,
        "hyperparameter_search": False, "tasks": datasets, "salary_threshold": 100000,
        "salary_definition": "Advertised lower bound in RUB / month / net; titles mentioning money excluded",
        "features": "Job title TF-IDF + metadata; no company, IDs, address, dates, response counts or salaries",
        "entry_level_definition": "experience == Без опыта; experience excluded from predictors",
        "senior_definition": "experience in [3-6 лет, Более 6 лет]; experience excluded from predictors",
        "scope": "Candidate screening; no separate final test; not a learner skill assessment"}, ensure_ascii=False, indent=2), encoding="utf-8")
    print("\nИтоги:\n", summary.round(4).to_string(index=False), flush=True)
    fig, axes = plt.subplots(1, len(tasks), figsize=(18, 4))
    for ax, task in zip(axes, [t[0] for t in tasks]):
        part = summary[summary.task == task].sort_values("accuracy")
        ax.barh(part.model, part.accuracy, xerr=part.accuracy_std, color="#489982")
        ax.set(xlim=(0, 1), xlabel="Accuracy ± SD; 3 folds по работодателям", title=task)
        for i, score in enumerate(part.accuracy):
            ax.text(score + .015, i, f"{score:.1%}", va="center")
    fig.tight_layout()
    fig.savefig(OUT / "pilot_comparison.png", dpi=130)
    plt.close(fig)


if __name__ == "__main__":
    import sys
    if "--pilot-only" in sys.argv:
        import pandas as pd
        data = pd.read_pickle(DATA / "pilot_records.pkl")
    else:
        data = audit()
    if "--audit-only" not in sys.argv:
        pilot(data)
