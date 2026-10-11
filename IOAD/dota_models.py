"""Модели, одинаковые в подборе параметров и итоговых лабораторных."""
import json
import hashlib
import os
import numpy as np
import pandas as pd
from pathlib import Path

from sklearn.feature_selection import SelectKBest, f_classif
from sklearn.impute import SimpleImputer
from sklearn.linear_model import LogisticRegression
from sklearn.neighbors import KNeighborsClassifier
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler
from sklearn.svm import SVC
from sklearn.tree import DecisionTreeClassifier
from threadpoolctl import threadpool_limits

from dota_common import ROOT
from dota_common import TEST_START, make_features


def make_estimator(family, params):
    params = dict(params)
    params.pop('half_life_days',None)
    imputer = SimpleImputer(strategy="median", keep_empty_features=True)
    if family == "LR":
        return make_pipeline(imputer, StandardScaler(), LogisticRegression(
            solver="liblinear", max_iter=3000, random_state=42, **params))
    if family == "SVC":
        select_k = params.pop("select_k", "all")
        return make_pipeline(imputer, StandardScaler(), SelectKBest(f_classif, k=select_k),
                             SVC(cache_size=192, **params))
    if family == "kNN":
        select_k = params.pop("select_k", "all")
        return make_pipeline(imputer, StandardScaler(), SelectKBest(f_classif, k=select_k),
                             KNeighborsClassifier(algorithm="brute", n_jobs=1, **params))
    if family == "Tree":
        # Деревья поддерживают пропуски напрямую в sklearn 1.9.
        return DecisionTreeClassifier(random_state=42, **params)
    if family == "CatBoost":
        from catboost import CatBoostClassifier
        return CatBoostClassifier(thread_count=4, random_seed=42, verbose=False,
                                  allow_writing_files=False, loss_function="Logloss", **params)
    if family == "LightGBM":
        from lightgbm import LGBMClassifier
        return LGBMClassifier(n_jobs=4, random_state=42, verbosity=-1, **params)
    if family == "HGB":
        from sklearn.ensemble import HistGradientBoostingClassifier
        return HistGradientBoostingClassifier(random_state=42, early_stopping=False, **params)
    raise ValueError(family)


def training_weights(dates, params):
    half_life=params.get('half_life_days')
    if half_life is None:
        return None
    dates=pd.to_datetime(dates)
    age=(dates.max()-dates).dt.total_seconds()/86400
    weight=np.power(2.,-age.to_numpy()/half_life)
    return weight/weight.mean()


def fit_model(model, x, y, sample_weight=None):
    with threadpool_limits(limits=4):
        if sample_weight is None:
            model.fit(x, y)
        elif hasattr(model,'steps'):
            model.fit(x,y,**{model.steps[-1][0]+'__sample_weight':sample_weight})
        else:
            model.fit(x,y,sample_weight=sample_weight)
    return model


def scores(model, x):
    with threadpool_limits(limits=4):
        if hasattr(model, "predict_proba"):
            return model.predict_proba(x)[:, 1]
        return model.decision_function(x)


def load_selected():
    with (ROOT / "research" / "selected_models.json").open(encoding="utf-8") as f:
        return json.load(f)


def fitted_selected(family, df, retrain=False):
    """Повторно использовать только свой кэш с теми же данными и кодом."""
    import joblib
    config=load_selected()[family]
    train=df.dt_match.str[:10]<TEST_START
    x=make_features(df,config['features'])
    fingerprint=hashlib.sha256(json.dumps(config,sort_keys=True).encode() +
                               (ROOT/'dota_common.py').read_bytes() +
                               Path(__file__).read_bytes() +
                               (ROOT/'research'/'protocol.json').read_bytes()).hexdigest()
    folder=ROOT/'research'/'models'
    folder.mkdir(exist_ok=True)
    path=folder/(family+'_'+fingerprint[:16]+'.joblib')
    if path.exists() and not retrain and os.environ.get('IOAD_RETRAIN')!='1':
        model=joblib.load(path)
    else:
        model=fit_model(make_estimator(family,config['params']),x[train],df.loc[train,'radiant_win'].astype(int),
                        training_weights(df.loc[train,'dt_match'],config['params']))
        joblib.dump(model,path,compress=3)
    return model,x,train,config
