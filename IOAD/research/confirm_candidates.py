"""Проверка финалистов на четырёх временных окнах внутри разработки."""
import os
os.environ.setdefault('OMP_NUM_THREADS','4')
os.environ.setdefault('OPENBLAS_NUM_THREADS','4')
os.environ.setdefault('MKL_NUM_THREADS','4')
import gc
import itertools
import json
import sys
from pathlib import Path
import numpy as np
import pandas as pd
from sklearn.metrics import accuracy_score,roc_auc_score

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from dota_common import load_matches,make_features,TEST_START
from dota_models import make_estimator,fit_model,scores,training_weights
OUT=Path(__file__).resolve().parent
tables=[pd.read_csv(OUT/name) for name in ['pilot_validation.csv','tune_validation.csv','knn_validation.csv']]
if (OUT/'adaptive_validation.csv').exists():
    tables.append(pd.read_csv(OUT/'adaptive_validation.csv'))
if (OUT/'required_grid_validation.csv').exists():
    tables.append(pd.read_csv(OUT/'required_grid_validation.csv'))
search=pd.concat(tables,ignore_index=True).drop_duplicates(['family','features','params'])
search.to_csv(OUT/'all_validation.csv',index=False)
df=load_matches()
df=df[df.dt_match.str[:10]<TEST_START].reset_index(drop=True)
y=df.radiant_win.astype(int).to_numpy()
day=df.dt_match.str[:10]
folds=[('2020-01-01','2020-04-01'),('2020-04-01','2020-07-01'),
       ('2020-07-01','2020-09-01'),('2020-09-01',TEST_START)]
candidates=[]
for family,g in search.groupby('family'):
    # Сравниваем два разных представления признаков, а не почти одинаковые C.
    best=g.sort_values(['accuracy','auc'],ascending=False).drop_duplicates('features').head(2)
    for _,row in best.iterrows():
        candidates.append({'family':family,'features':row.features,'params':json.loads(row.params),
                           'validation_accuracy':row.accuracy,'validation_auc':row.auc})
rows=[]
oof=pd.DataFrame({'match_id':df.match_id,'dt_match':df.dt_match,'y':y})
for number,config in enumerate(candidates):
    x=make_features(df,config['features'])
    candidate_id=f"{config['family']}_{number}"
    oof[candidate_id]=np.nan
    for start,end in folds:
        tr=day<start
        va=(day>=start)&(day<end)
        model=fit_model(make_estimator(config['family'],config['params']),x[tr],y[tr],
                        training_weights(df.loc[tr,'dt_match'],config['params']))
        score=scores(model,x[va])
        threshold=0 if config['family']=='SVC' else .5
        rows.append(dict(candidate_id=candidate_id,family=config['family'],features=config['features'],
                         params=json.dumps(config['params'],sort_keys=True),fold_start=start,fold_end=end,
                         train_n=int(tr.sum()),validation_n=int(va.sum()),
                         accuracy=accuracy_score(y[va],score>=threshold),auc=roc_auc_score(y[va],score)))
        oof.loc[va,candidate_id]=score
        pd.DataFrame(rows).to_csv(OUT/'temporal_checks.csv',index=False)
        print(candidate_id,start,rows[-1]['accuracy'],rows[-1]['auc'],flush=True)
        del model
        gc.collect()
    config['candidate_id']=candidate_id
    r=pd.DataFrame(rows)
    r=r[r.candidate_id==candidate_id]
    config['cv_accuracy']=float(np.average(r.accuracy,weights=r.validation_n))
    config['cv_auc']=float(np.average(r.auc,weights=r.validation_n))
    del x
    gc.collect()
oof.to_csv(OUT/'oof_predictions.csv',index=False)
selected={}
for family in sorted(search.family.unique()):
    winner=max([c for c in candidates if c['family']==family],key=lambda c:(c['cv_accuracy'],c['cv_auc']))
    # Порог подбирается по out-of-fold прогнозам. Test сюда не входит.
    s=oof[winner['candidate_id']]
    mask=s.notna()
    grid=np.linspace(-.25,.25,51) if family=='SVC' else np.linspace(.4,.6,41)
    baseline=0 if family=='SVC' else .5
    acc=np.array([accuracy_score(y[mask],s[mask]>=t) for t in grid])
    best=np.flatnonzero(acc==acc.max())
    threshold=float(grid[best[np.argmin(np.abs(grid[best]-baseline))]])
    winner['threshold']=threshold
    winner['oof_threshold_accuracy']=float(acc.max())
    selected[family]=winner
# Простое голосование вероятностей. Веса и порог определяются только по OOF;
# kNN и SVC не смешиваются с вероятностями без отдельной калибровки.
blend_families=sorted([f for f in ['LR','HGB','LightGBM','CatBoost'] if f in selected],
                      key=lambda f:selected[f]['oof_threshold_accuracy'],reverse=True)[:3]
prob=np.column_stack([oof[selected[f]['candidate_id']] for f in blend_families])
valid=np.isfinite(prob).all(axis=1)
best_blend=None
for units in itertools.product(range(5),repeat=len(blend_families)):
    if sum(units)!=4 or sum(u>0 for u in units)<2:
        continue
    weights=np.array(units)/4
    s=prob[valid]@weights
    for threshold in np.linspace(.4,.6,41):
        accuracy=accuracy_score(y[valid],s>=threshold)
        if best_blend is None or accuracy>best_blend['oof_threshold_accuracy']:
            best_blend={'weights':dict(zip(blend_families,weights.tolist())),
                        'threshold':float(threshold),'oof_threshold_accuracy':float(accuracy)}
selected['Ensemble']=best_blend
singles={f:c['oof_threshold_accuracy'] for f,c in selected.items() if f!='Ensemble'}
champion=max(singles,key=singles.get)
if best_blend['oof_threshold_accuracy']>=singles[champion]+.002:
    champion='Ensemble'
selected['_champion']=champion
(OUT/'selected_models.json').write_text(json.dumps(selected,indent=2,ensure_ascii=False),encoding='utf-8')
(OUT/'all_candidates.json').write_text(json.dumps(candidates,indent=2,ensure_ascii=False),encoding='utf-8')
print('Frozen selected models:',json.dumps(selected,indent=2),flush=True)
