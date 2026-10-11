"""Проверки источника, разбиений и независимости признаков от ответов."""
import hashlib
import json
import sys
from pathlib import Path
import pandas as pd
import numpy as np

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from dota_common import DATA_PATH, load_matches, make_features, partition_masks
OUT=Path(__file__).resolve().parent
df=load_matches()
raw=make_features(df)
tr,va,te=partition_masks(df)
assert not (tr&va).any() and not (tr&te).any() and not (va&te).any()
assert (tr|va|te).all()
assert df.loc[tr,'dt_match'].max() < df.loc[va,'dt_match'].min()
assert df.loc[va,'dt_match'].max() < df.loc[te,'dt_match'].min()
assert set(df.loc[tr,'dt_match'].str[:10]).isdisjoint(df.loc[va,'dt_match'].str[:10])
assert not np.isinf(raw.to_numpy()).any()
altered=df.head(100).copy()
altered['radiant_win']=~altered.radiant_win
altered['match_id']=np.arange(100)
altered['dt_match']='2099-01-01 00:00:00'
for kind in ['raw','contrast','core','extended']:
    a,b=make_features(df.head(100),kind),make_features(altered,kind)
    pd.testing.assert_frame_equal(a,b)
    assert not np.isinf(a.to_numpy()).any()
feature_hash=pd.util.hash_pandas_object(raw,index=False)
groups=df.groupby(feature_hash).radiant_win.agg(['size','sum','nunique'])
audit={
    'sha256':hashlib.file_digest(DATA_PATH.open('rb'),'sha256').hexdigest(),
    'rows':len(df),'original_columns':len(df.columns),'raw_features':raw.shape[1],
    'duplicate_match_ids':int(df.match_id.duplicated().sum()),
    'missing_input_cells':int(raw.isna().sum().sum()),
    'no_input_history':int(raw.isna().all(axis=1).sum()),
    'duplicate_feature_rows':int(raw.duplicated().sum()),
    'conflicting_feature_groups':int((groups['nunique']>1).sum()),
    'features_shared_train_validation':int(feature_hash[va].isin(feature_hash[tr]).sum()),
    'features_shared_development_test':int(feature_hash[te].isin(feature_hash[~te]).sum()),
    'partitions':{}
}
assert audit['sha256']=='5844598356d0d8a156260457e3f961008410519083cc3e0b020fb230e9534d07'
for name,mask in [('train',tr),('validation',va),('test',te)]:
    part=df.loc[mask]
    audit['partitions'][name]={'rows':len(part),'start':part.dt_match.min(),'end':part.dt_match.max(),
                               'radiant_share':float(part.radiant_win.mean())}
df[['match_id','dt_match']].assign(partition=np.select([tr,va,te],['train','validation','test'],default='')).to_csv(OUT/'split_manifest.csv',index=False)
df[['dt_match','radiant_win']].assign(month=df.dt_match.str[:7]).groupby('month').agg(
    matches=('radiant_win','size'),radiant_share=('radiant_win','mean')).to_csv(OUT/'monthly_data.csv')
raw.isna().mean().rename('missing_share').to_csv(OUT/'missing_features.csv')
(OUT/'data_audit.json').write_text(json.dumps(audit,indent=2,ensure_ascii=False),encoding='utf-8')
print(json.dumps(audit,indent=2,ensure_ascii=False))
print('All data integrity, split and target-independence checks passed')
