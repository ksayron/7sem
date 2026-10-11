"""Адаптивный подбор бустинга и веса более свежих матчей на validation."""
import os
os.environ.setdefault('OMP_NUM_THREADS','4')
os.environ.setdefault('OPENBLAS_NUM_THREADS','4')
os.environ.setdefault('MKL_NUM_THREADS','4')
import gc
import json
import sys
from pathlib import Path
from time import perf_counter
import numpy as np
import pandas as pd
import optuna
from sklearn.metrics import accuracy_score,roc_auc_score,log_loss
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from dota_common import load_matches,make_features,partition_masks
from dota_models import make_estimator,fit_model,scores,training_weights
OUT=Path(__file__).resolve().parent
df=load_matches()
tr,va,te=partition_masks(df)
df=df[~te].reset_index(drop=True)
tr=tr[~te].to_numpy()
va=va[~te].to_numpy()
y=df.radiant_win.astype(int).to_numpy()
rows=[]
cache={}
optuna.logging.set_verbosity(optuna.logging.WARNING)


def objective(trial,family):
    kind=trial.suggest_categorical('features',['raw','core','contrast','extended'])
    if kind not in cache:
        cache.clear()
        cache[kind]=make_features(df,kind)
    x=cache[kind]
    params={'half_life_days':trial.suggest_categorical('half_life_days',[None,120,240,480])}
    if family=='LightGBM':
        params.update(n_estimators=trial.suggest_int('n_estimators',300,1500,step=100),
                      learning_rate=trial.suggest_float('learning_rate',.015,.12,log=True),
                      num_leaves=trial.suggest_categorical('num_leaves',[7,15,31,63]),
                      min_child_samples=trial.suggest_int('min_child_samples',40,400,step=40),
                      reg_lambda=trial.suggest_float('reg_lambda',.1,100,log=True),
                      reg_alpha=trial.suggest_float('reg_alpha',.01,10,log=True),
                      colsample_bytree=trial.suggest_float('colsample_bytree',.5,1),
                      subsample=trial.suggest_float('subsample',.65,1),subsample_freq=1)
    else:
        params.update(iterations=trial.suggest_int('iterations',400,1400,step=200),
                      depth=trial.suggest_int('depth',3,7),
                      learning_rate=trial.suggest_float('learning_rate',.015,.1,log=True),
                      l2_leaf_reg=trial.suggest_float('l2_leaf_reg',1,100,log=True),
                      random_strength=trial.suggest_float('random_strength',.1,5,log=True))
    start=perf_counter()
    model=fit_model(make_estimator(family,params),x[tr],y[tr],training_weights(df.loc[tr,'dt_match'],params))
    prob=scores(model,x[va])
    row=dict(family=family,features=kind,params=json.dumps(params,sort_keys=True),
             accuracy=accuracy_score(y[va],prob>=.5),auc=roc_auc_score(y[va],prob),logloss=log_loss(y[va],prob),
             seconds=perf_counter()-start)
    rows.append(row)
    pd.DataFrame(rows).to_csv(OUT/'adaptive_validation.csv',index=False)
    print(row,flush=True)
    del model
    gc.collect()
    return row['accuracy']


for family,count in [('LightGBM',40),('CatBoost',20)]:
    study=optuna.create_study(direction='maximize',sampler=optuna.samplers.TPESampler(seed=42))
    study.optimize(lambda trial:objective(trial,family),n_trials=count)
print(pd.DataFrame(rows).sort_values('accuracy',ascending=False).head(12).to_string(index=False))
