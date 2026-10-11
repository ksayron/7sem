"""Финальная оценка зафиксированных моделей и диагностика ошибок."""
import os
os.environ.setdefault('OMP_NUM_THREADS','4')
os.environ.setdefault('OPENBLAS_NUM_THREADS','4')
os.environ.setdefault('MKL_NUM_THREADS','4')
import gc
import json
import sys
from pathlib import Path
from time import perf_counter
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from scipy.stats import binomtest
from sklearn.metrics import (accuracy_score,balanced_accuracy_score,precision_score,recall_score,
                             roc_auc_score,ConfusionMatrixDisplay,RocCurveDisplay)
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from dota_common import load_matches,DATA_PATH,TEST_START,make_features
from dota_models import load_selected,fitted_selected,scores,make_estimator,fit_model

OUT=Path(__file__).resolve().parent
selected=load_selected()
df=load_matches()
test=df.dt_match.str[:10]>=TEST_START
y=df.loc[test,'radiant_win'].astype(int).to_numpy()
prior_ids=pd.read_csv(DATA_PATH,usecols=['match_id']).sample(n=6000,random_state=42).match_id
fresh=~df.loc[test,'match_id'].isin(prior_ids).to_numpy()
predictions=df.loc[test,['match_id','dt_match']].reset_index(drop=True)
predictions['y']=y
predictions['previously_sampled']=~fresh
results=[]
test_scores={}
train_majority=int(df.loc[~test,'radiant_win'].mean()>=.5)
baseline=np.full(len(y),train_majority)


def record(name,score,threshold,train_accuracy=np.nan,seconds=np.nan):
    pred=(score>=threshold).astype(int)
    test_scores[name]=score
    predictions[name+'_score']=score
    predictions[name+'_prediction']=pred
    results.append(dict(model=name,threshold=threshold,train_accuracy=train_accuracy,
                        test_accuracy=accuracy_score(y,pred),fresh_test_accuracy=accuracy_score(y[fresh],pred[fresh]),
                        balanced_accuracy=balanced_accuracy_score(y,pred),precision=precision_score(y,pred,zero_division=0),
                        recall=recall_score(y,pred,zero_division=0),auc=roc_auc_score(y,score),seconds=seconds))
    pd.DataFrame(results).to_csv(OUT/'final_metrics.csv',index=False)
    predictions.to_csv(OUT/'test_predictions.csv',index=False)
    print(results[-1],flush=True)


record('Majority',baseline,.5)
for family in [f for f in selected if not f.startswith('_') and f!='Ensemble']:
    start=perf_counter()
    model,x,train,config=fitted_selected(family,df)
    test_score=scores(model,x[test])
    train_score=scores(model,x[train])
    record(family,test_score,config['threshold'],accuracy_score(df.loc[train,'radiant_win'],train_score>=config['threshold']),
           perf_counter()-start)
    default=0 if family=='SVC' else .5
    predictions[family+'_default_prediction']=(test_score>=default).astype(int)
    if family in ['LightGBM','CatBoost']:
        pd.Series(model.feature_importances_,index=x.columns).sort_values(ascending=False).rename('importance').to_csv(OUT/(family.lower()+'_importance.csv'))
    del model,x
    gc.collect()
blend=selected['Ensemble']
blend_score=sum(weight*test_scores[family] for family,weight in blend['weights'].items())
record('Ensemble',blend_score,blend['threshold'])

# Ранее использовавшийся вариант SVC на 4800 матчах. Проверяется на той же
# новой test, а не сравнивается с метрикой на другом наборе из 1200 строк.
old=pd.read_csv(DATA_PATH).sample(n=6000,random_state=42).sort_values('dt_match').iloc[:4800]
old=old[old.dt_match.str[:10]<TEST_START]
old_model=fit_model(make_estimator('SVC',{'C':1,'gamma':'scale'}),make_features(old),old.radiant_win.astype(int))
record('Previous_SVC_4800',scores(old_model,make_features(df.loc[test])),0)
del old_model,old

champion=selected['_champion']
pred=predictions[champion+'_prediction'].to_numpy()
good=(pred==y).astype(float)
base_good=(baseline==y).astype(float)
daily=pd.DataFrame({'day':predictions.dt_match.str[:10],'good':good,'base_good':base_good}).groupby('day').agg(
    matches=('good','size'),correct=('good','sum'),baseline_correct=('base_good','sum'))
rng=np.random.default_rng(42)
accuracies=[]
deltas=[]
v=daily.to_numpy()
for _ in range(3000):
    s=v[rng.integers(0,len(v),len(v))].sum(axis=0)
    accuracies.append(s[1]/s[0])
    deltas.append((s[1]-s[2])/s[0])
base_only=int(((baseline==y)&(pred!=y)).sum())
model_only=int(((baseline!=y)&(pred==y)).sum())
summary=dict(champion=champion,test_n=len(y),fresh_test_n=int(fresh.sum()),
             test_accuracy=float(good.mean()),accuracy_day_bootstrap_95=np.quantile(accuracies,[.025,.975]).tolist(),
             gain_vs_majority=float((good-base_good).mean()),gain_day_bootstrap_95=np.quantile(deltas,[.025,.975]).tolist(),
             mcnemar_exact_p=binomtest(model_only,model_only+base_only,.5).pvalue,
             baseline_only_correct=base_only,model_only_correct=model_only)
(OUT/'final_summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8')

# Уверенные прогнозы: границы выбраны на OOF до финального тестирования.
oof=pd.read_csv(OUT/'oof_predictions.csv')
if champion=='Ensemble':
    oof_score=sum(weight*oof[selected[family]['candidate_id']] for family,weight in blend['weights'].items())
else:
    oof_score=oof[selected[champion]['candidate_id']]
threshold=selected[champion]['threshold']
conf=np.abs(test_scores[champion]-threshold)
oof_conf=np.abs(oof_score-threshold).dropna()
coverage=[]
for keep in [1,.75,.5,.25,.1]:
    cutoff=0 if keep==1 else float(oof_conf.quantile(1-keep))
    mask=conf>=cutoff
    coverage.append(dict(target_oof_coverage=keep,confidence_cutoff=cutoff,test_n=int(mask.sum()),
                         actual_test_coverage=float(mask.mean()),accuracy=float(good[mask].mean())))
pd.DataFrame(coverage).to_csv(OUT/'confidence_coverage.csv',index=False)

quality=make_features(df.loc[test]).isna().mean(axis=1).to_numpy()
groups=np.select([quality==1,quality>.4,quality>.1],['no_history','missing_over_40pct','missing_10_to_40pct'],default='missing_at_most_10pct')
pd.DataFrame({'group':groups,'correct':good}).groupby('group').agg(matches=('correct','size'),accuracy=('correct','mean')).to_csv(OUT/'quality_groups.csv')
predictions.assign(month=predictions.dt_match.str[:7],correct=good,baseline_correct=base_good).groupby('month').agg(
    matches=('correct','size'),accuracy=('correct','mean'),baseline=('baseline_correct','mean')).to_csv(OUT/'monthly_quality.csv')
predictions.to_csv(OUT/'test_predictions.csv',index=False)

table=pd.DataFrame(results).sort_values('test_accuracy')
fig,ax=plt.subplots(figsize=(10,6))
ax.barh(table.model,table.test_accuracy*100)
ax.axvline(accuracy_score(y,baseline)*100,color='red',linestyle='--',label='Всегда Radiant')
for i,value in enumerate(table.test_accuracy):
    ax.text(value*100+.25,i,f'{value:.2%}',va='center')
ax.set_xlim(0,75)
ax.set_xlabel('Accuracy на последних 9472 матчах, %')
ax.set_title('Полный датасет: сравнение на одной тестовой выборке')
ax.legend()
fig.tight_layout()
fig.savefig(OUT/'model_comparison.png',dpi=160)
plt.close(fig)
fig,axes=plt.subplots(1,2,figsize=(12,5))
ConfusionMatrixDisplay.from_predictions(y,pred,display_labels=['Dire','Radiant'],ax=axes[0],cmap='Blues',colorbar=False)
axes[0].set_title('Модель, выбранная до test: '+champion)
for family in ['LR','SVC','LightGBM','CatBoost','Ensemble']:
    RocCurveDisplay.from_predictions(y,test_scores[family],ax=axes[1],name=family)
axes[1].plot([0,1],[0,1],'k--')
axes[1].set_title('ROC на финальном test')
fig.tight_layout()
fig.savefig(OUT/'final_roc_confusion.png',dpi=160)
plt.close(fig)
fig,ax=plt.subplots(figsize=(8,5))
c=pd.DataFrame(coverage)
ax.plot(c.actual_test_coverage*100,c.accuracy*100,'o-')
ax.set_xlabel('Доля матчей, для которых выдан прогноз, %')
ax.set_ylabel('Accuracy выданных прогнозов, %')
ax.set_title('Пороги уверенности выбраны по OOF, не по test')
ax.grid(alpha=.3)
fig.tight_layout()
fig.savefig(OUT/'confidence_coverage.png',dpi=160)
plt.close(fig)
print('Final summary:',json.dumps(summary,indent=2),flush=True)
