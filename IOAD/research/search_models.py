"""Подбор только на train/validation; финальные ответы test здесь не читаются."""
import os
os.environ.setdefault("OMP_NUM_THREADS", "4")
os.environ.setdefault("OPENBLAS_NUM_THREADS", "4")
os.environ.setdefault("MKL_NUM_THREADS", "4")

import argparse
import gc
import json
import sys
from pathlib import Path
from time import perf_counter

import numpy as np
import pandas as pd
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.feature_selection import SelectKBest, f_classif
from sklearn.impute import SimpleImputer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import accuracy_score, log_loss, roc_auc_score
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler
from sklearn.svm import SVC
from sklearn.tree import DecisionTreeClassifier
from threadpoolctl import threadpool_limits

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from dota_common import load_matches, make_features, partition_masks

OUT = Path(__file__).resolve().parent
parser = argparse.ArgumentParser()
parser.add_argument("--stage", choices=["pilot", "tune"], default="pilot")
args = parser.parse_args()
df = load_matches()
train, validation, test = partition_masks(df)
# Набор разработки содержит все строки до заранее выбранной даты test.
dev = df.loc[~test].copy()
y_tr = dev.loc[train[~test], "radiant_win"].astype(int).to_numpy()
y_val = dev.loc[validation[~test], "radiant_win"].astype(int).to_numpy()
print("Train",len(y_tr),"validation",len(y_val),"test reserved",int(test.sum()),flush=True)
del df
gc.collect()
rows = []
features_cache = {}


def arrays(kind, scale):
    key = (kind, scale)
    if key not in features_cache:
        x = make_features(dev, kind)
        a, b = x.iloc[:len(y_tr)], x.iloc[len(y_tr):]
        if scale:
            prep = make_pipeline(SimpleImputer(strategy="median", keep_empty_features=True), StandardScaler())
            a, b = prep.fit_transform(a), prep.transform(b)
        else:
            a, b = a.to_numpy(), b.to_numpy()
        features_cache[key] = (a, b)
    return features_cache[key]


def run(family, kind, params):
    a, b = arrays(kind, family in ["LR", "SVC"])
    model_params = dict(params)
    if family == "SVC" and "select_k" in model_params:
        selector = SelectKBest(f_classif, k=model_params.pop("select_k"))
        a = selector.fit_transform(a, y_tr)
        b = selector.transform(b)
    if family == "LR":
        model = LogisticRegression(solver="liblinear", max_iter=3000, random_state=42, **params)
    elif family == "SVC":
        model = SVC(cache_size=192, **model_params)
    elif family == "HGB":
        model = HistGradientBoostingClassifier(random_state=42, early_stopping=False, **params)
    elif family == "Tree":
        model = DecisionTreeClassifier(random_state=42, **params)
    elif family == "CatBoost":
        from catboost import CatBoostClassifier
        model = CatBoostClassifier(thread_count=4, random_seed=42, verbose=False,
                                   allow_writing_files=False, loss_function="Logloss", **params)
    elif family == "LightGBM":
        from lightgbm import LGBMClassifier
        model = LGBMClassifier(n_jobs=4, random_state=42, verbosity=-1, **params)
    start = perf_counter()
    with threadpool_limits(limits=4):
        model.fit(a, y_tr)
        score = model.decision_function(b) if family == "SVC" else model.predict_proba(b)[:, 1]
        proba = 1 / (1 + np.exp(-score)) if family == "SVC" else score
        pred = (score >= 0).astype(int) if family == "SVC" else (score >= 0.5).astype(int)
    row = dict(family=family, features=kind, params=json.dumps(params, sort_keys=True),
               accuracy=accuracy_score(y_val,pred), auc=roc_auc_score(y_val,score),
               logloss=log_loss(y_val,proba), seconds=perf_counter()-start)
    rows.append(row)
    pd.DataFrame(rows).to_csv(OUT / (args.stage + "_validation.csv"), index=False)
    print(row,flush=True)
    del model
    gc.collect()


if args.stage == "pilot":
    for kind in ["raw", "contrast", "core", "extended"]:
        for family, params in [
            ("LR", {"C":0.01,"l1_ratio":0}),
            ("HGB", {"max_iter":300,"max_leaf_nodes":15,"learning_rate":0.05,"l2_regularization":10}),
            ("LightGBM", {"n_estimators":400,"num_leaves":15,"learning_rate":0.03,"reg_lambda":10,"min_child_samples":100}),
            ("CatBoost", {"iterations":500,"depth":5,"learning_rate":0.05,"l2_leaf_reg":10}),
        ]:
            run(family, kind, params)
        features_cache.clear()
    for kind in ["raw", "contrast", "core"]:
        run("SVC",kind,{"C":1,"gamma":"scale"})
        features_cache.clear()
else:
    # Детерминированный широкий поиск. Изменение набора/параметров сохраняется
    # как строка эксперимента; test отсутствует в функции run.
    rng=np.random.default_rng(42)
    for kind in ["raw", "contrast", "core"]:
        for c in [.001,.01,.1,1,10,100]:
            for ratio in [0,1]:
                run("LR",kind,{"C":c,"l1_ratio":ratio})
        features_cache.clear()
    for kind in ["raw", "contrast", "core", "extended"]:
        for depth in [3,5,7,10,15,None]:
            for leaf in [25,100,300]:
                run("Tree",kind,{"max_depth":depth,"min_samples_leaf":leaf})
        for _ in range(12):
            run("LightGBM",kind,{"n_estimators":int(rng.choice([200,500,1000])),
                "learning_rate":float(rng.choice([.02,.05,.1])),
                "num_leaves":int(rng.choice([7,15,31,63])),
                "min_child_samples":int(rng.choice([50,100,200,400])),
                "reg_lambda":float(rng.choice([1,10,50,100])),
                "colsample_bytree":float(rng.choice([.6,.8,1.]))})
        features_cache.clear()
    for kind in ["core", "contrast"]:
        for depth in [3,5,7]:
            for reg in [3,30]:
                run("CatBoost",kind,{"iterations":1000,"depth":depth,"learning_rate":.03,"l2_leaf_reg":reg})
        features_cache.clear()
    for kind in ["core", "contrast", "raw"]:
        for k in [15,40]:
            for c in [.1,1,10]:
                for gamma in ["scale",.001]:
                    run("SVC",kind,{"C":c,"gamma":gamma,"select_k":k})
        features_cache.clear()
print("Best validation candidates:\n",pd.DataFrame(rows).sort_values("accuracy",ascending=False).head(15).to_string(index=False),flush=True)
