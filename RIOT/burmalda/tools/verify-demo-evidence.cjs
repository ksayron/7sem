// Re-evaluate saved samples after observation ends; a replacement may later be removed by HPA.
const fs=require('node:fs');const assert=require('node:assert/strict');
const folder='lab-results/demo-verification/';
const result=JSON.parse(fs.readFileSync(folder+'running.json','utf8'));
const state=JSON.parse(fs.readFileSync(folder+'final-state.json','utf8'));
const start=Date.parse(state.engine.run.startedAt);
const events=state.events.filter(e=>Date.parse(e.at)>=start);
const ready=(s,name)=>s.pods.filter(p=>p.app===name&&p.ready&&!p.deletingAt).length;
assert.equal(state.engine.run.state,'completed');assert.equal(state.engine.run.options.durationSeconds,420);
for(const name of ['catalog','gateway']){
 assert.equal(ready(result.samples[0],name),1,'baseline '+name);
 assert.ok(result.samples.some(s=>ready(s,name)>=2),'scale up '+name);
 assert.equal(ready(result.samples.at(-1),name),1,'scale down '+name);
}
const deletion=events.find(e=>e.kind==='pod_deleted');assert.ok(deletion);
const oldUid=deletion.message.match(/UID ([0-9a-f-]+)/i)?.[1];assert.ok(oldUid);
assert.ok(!state.cluster.pods.some(p=>p.uid===oldUid));
const replacements=new Set(events.filter(e=>e.kind==='pod_created'&&Date.parse(e.at)>=Date.parse(deletion.at)).map(e=>e.message.match(/Новый Pod (\S+)/)?.[1]));
assert.ok(result.samples.some(s=>s.pods.some(p=>p.app==='catalog'&&p.ready&&replacements.has(p.name))),'replacement Ready during run');
assert.ok(events.some(e=>e.kind==='fault_start')&&events.some(e=>e.kind==='fault_expired'));
const last=result.samples.at(-1);
assert.ok(Math.abs(Date.parse(last.at)-Date.parse(last.metricsAt))<30000,'metrics fresh at observation time');
result.checks=[...new Set([...result.checks,'seven-minute-scenario-completed','catalog-automatically-scaled','gateway-automatically-scaled','catalog-pod-replaced-and-ready','api-fault-expired','both-services-automatically-scaled-down','prometheus-metrics-fresh'])];
result.events=events;result.charts=state.cluster.charts;result.run=state.engine.run;result.finishedAt=new Date().toISOString();
result.highestObservedHealthyRps=Math.max(...result.samples.filter(s=>s.engine.target>0&&s.engine.errorPercent<1&&s.engine.p95Ms<=1000).map(s=>s.engine.successRps));
result.evidenceValidation='Saved samples and final state; replacement Ready is checked before subsequent HPA removal.';
fs.writeFileSync(folder+'result.json',JSON.stringify(result,null,2));
console.log('PASS all recorded scenario evidence; healthy RPS '+result.highestObservedHealthyRps.toFixed(1));
