"""Отбор признаков и соседей по validation, без обращения к test."""
import os
os.environ.setdefault("OMP_NUM_THREADS", "4")
os.environ.setdefault("OPENBLAS_NUM_THREADS", "4")
os.environ.setdefault("MKL_NUM_THREADS", "4")
import json
import sys
from pathlib import Path
from time import perf_counter
import numpy as np
import pandas as pd
from sklearn.feature_selection import SelectKBest, f_classif
from sklearn.impute import SimpleImputer
from sklearn.metrics import accuracy_score, roc_auc_score, log_loss
from sklearn.neighbors import NearestNeighbors
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler
from threadpoolctl import threadpool_limits
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from dota_common import load_matches, make_features, partition_masks

OUT=Path(__file__).resolve().parent
df=load_matches()
tr,va,te=partition_masks(df)
dev=df.loc[~te].copy()
ytr=df.loc[tr,'radiant_win'].astype(int).to_numpy()
yva=df.loc[va,'radiant_win'].astype(int).to_numpy()
del df
rows=[]
for kind in ['core','contrast','raw']:
    x=make_features(dev,kind)
    prep=make_pipeline(SimpleImputer(strategy='median',keep_empty_features=True),StandardScaler())
    a=prep.fit_transform(x.iloc[:len(ytr)])
    b=prep.transform(x.iloc[len(ytr):])
    for features in [5,15,40,'all']:
        selector=SelectKBest(f_classif,k=features)
        aa=selector.fit_transform(a,ytr)
        bb=selector.transform(b)
        for p in [1,2]:
            start=perf_counter()
            with threadpool_limits(limits=4):
                nn=NearestNeighbors(n_neighbors=501,algorithm='brute',p=p,n_jobs=1).fit(aa)
                distances,indices=nn.kneighbors(bb)
            for k in [5,15,51,151,301,501]:
                for weight in ['uniform','distance']:
                    dist=distances[:,:k]
                    w=np.ones_like(dist) if weight=='uniform' else 1/np.maximum(dist,1e-12)
                    if weight=='distance':
                        exact=dist==0
                        zero=exact.any(axis=1)
                        w[zero]=exact[zero]
                    score=(w*ytr[indices[:,:k]]).sum(axis=1)/w.sum(axis=1)
                    row=dict(family='kNN',features=kind,params=json.dumps(dict(n_neighbors=k,weights=weight,p=p,select_k=features)),
                             accuracy=accuracy_score(yva,score>=.5),auc=roc_auc_score(yva,score),
                             logloss=log_loss(yva,np.clip(score,1e-8,1-1e-8)),seconds=perf_counter()-start)
                    rows.append(row)
            pd.DataFrame(rows).to_csv(OUT/'knn_validation.csv',index=False)
            print(kind,features,p,'best accuracy',max(r['accuracy'] for r in rows),flush=True)
print(pd.DataFrame(rows).sort_values('accuracy',ascending=False).head(12).to_string(index=False))
