"""Сетка SVC из условия, 5 временных CV на train, затем validation."""
import os
os.environ.setdefault('OMP_NUM_THREADS','4')
os.environ.setdefault('OPENBLAS_NUM_THREADS','4')
os.environ.setdefault('MKL_NUM_THREADS','4')
import json
import sys
from pathlib import Path
import numpy as np
import pandas as pd
from sklearn.metrics import accuracy_score,roc_auc_score
from sklearn.model_selection import GridSearchCV,TimeSeriesSplit
from threadpoolctl import threadpool_limits
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from dota_common import load_matches,make_features,partition_masks
from dota_models import make_estimator,scores
OUT=Path(__file__).resolve().parent
search=pd.read_csv(OUT/'tune_validation.csv')
search=search[search.family=='SVC'].sort_values('accuracy',ascending=False)
candidate=search.iloc[0]
params=json.loads(candidate.params)
df=load_matches()
tr,va,te=partition_masks(df)
x=make_features(df,candidate.features)
dates=df.loc[tr,'dt_match'].str[:10].to_numpy()
unique_days=np.unique(dates)
folds=[]
for a,b in TimeSeriesSplit(n_splits=5).split(unique_days):
    folds.append((np.flatnonzero(np.isin(dates,unique_days[a])),np.flatnonzero(np.isin(dates,unique_days[b]))))
pipe=make_estimator('SVC',{'select_k':params['select_k']})
grid=GridSearchCV(pipe,{'svc__C':[.1,1,10],'svc__gamma':[.2,.6,1]},cv=folds,n_jobs=1,scoring='accuracy',verbose=2)
with threadpool_limits(limits=4):
    grid.fit(x[tr],df.loc[tr,'radiant_win'].astype(int))
cv=pd.DataFrame(grid.cv_results_)
cv['features']=candidate.features
cv['select_k']=params['select_k']
cv.to_csv(OUT/'svc_required_cv.csv',index=False)
chosen={'C':grid.best_params_['svc__C'],'gamma':grid.best_params_['svc__gamma'],'select_k':params['select_k']}
score=scores(grid.best_estimator_,x[va])
row=dict(family='SVC',features=candidate.features,params=json.dumps(chosen,sort_keys=True),
         accuracy=accuracy_score(df.loc[va,'radiant_win'],score>=0),auc=roc_auc_score(df.loc[va,'radiant_win'],score),
         cv_accuracy=grid.best_score_)
pd.DataFrame([row]).to_csv(OUT/'required_grid_validation.csv',index=False)
print(row,flush=True)
