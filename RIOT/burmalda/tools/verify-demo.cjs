// Real-cluster acceptance. Credentials stay in ignored .artifacts; public evidence contains no tokens.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const base = process.env.BURMALDA_DEMO_URL ?? 'http://localhost:5310';
const library = process.env.BURMALDA_WEB_URL ?? 'http://localhost:5300';
const clients = Number(process.env.BURMALDA_DEMO_CLIENTS ?? 300);
const phase = process.env.BURMALDA_DEMO_PHASE ?? 'all';
const folder = 'lab-results/demo-verification';
const result = {startedAt:new Date().toISOString(),base,clients,checks:[],samples:[]};
let token,tokenUntil=0;
const pause = ms => new Promise(resolve=>setTimeout(resolve,ms));
async function admin() {
    if(Date.now()<tokenUntil)return;
    const secret=JSON.parse(await fs.readFile('.artifacts/k8s/secret.json','utf8'));
    const response=await fetch(library+'/api/identity/login',{method:'POST',headers:{'content-type':'application/json'},
        body:JSON.stringify({email:'admin@burmalda.local',password:secret.stringData.ADMIN_PASSWORD}),signal:AbortSignal.timeout(30000)});
    assert.equal(response.status,200,'admin login');
    const session=await response.json();token=session.accessToken;tokenUntil=Date.now()+20*60*1000;
}
async function api(route,body,status=200,authenticated=true) {
    if(authenticated)await admin();
    const response=await fetch(base+'/api/demo'+route,{method:body===undefined?'GET':'POST',
        headers:{'content-type':'application/json',...(authenticated?{authorization:'Bearer '+token}:{})},
        body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});
    const text=await response.text();assert.equal(response.status,status,route+': '+text.slice(0,300));
    return text?JSON.parse(text):null;
}
function mark(name){result.checks.push(name);console.log('PASS '+name);}
async function save(name='result.json'){await fs.mkdir(folder,{recursive:true});await fs.writeFile(path.join(folder,name),JSON.stringify(result,null,2));}
(async()=>{
    assert.ok(Number.isInteger(clients)&&clients>=1&&clients<=500);
    await api('/state',undefined,401,false);mark('anonymous-control-denied');
    await api('/faults',{service:'postgres',durationSeconds:30},409);mark('dependency-fault-denied');
    await api('/faults',{service:'catalog',errorPercent:100,delayMs:0,durationSeconds:121},409);mark('unbounded-fault-denied');
    let state=await api('/state');
    const protectedPod=state.cluster.pods.find(p=>p.app==='postgres');
    assert.ok(protectedPod,'Kubernetes Pod data available');
    await api('/pods/'+protectedPod.name+'/delete',{uid:protectedPod.uid},409);mark('stateful-pod-delete-denied');
    assert.ok(state.cluster.hpas.some(h=>h.name==='catalog')&&state.cluster.hpas.some(h=>h.name==='gateway'));mark('both-hpas-observed');
    if(phase==='checks'){await save('checks.json');return;}
    if(phase!=='run'){
        await api('/prepare',{clients},202);
        const deadline=Date.now()+25*60*1000;let shown=-1;
        while(Date.now()<deadline){
            state=await api('/state');const p=state.engine.preparation;
            if(p.ready!==shown){shown=p.ready;console.log('PREPARE '+p.ready+'/'+p.requested);}
            assert.equal(p.error,null,'fixture preparation');
            if(!p.running&&p.ready>=clients)break;
            await pause(5000);
        }
        assert.ok(!state.engine.preparation.running&&state.engine.preparation.ready>=clients,'preparation deadline');
        mark('clients-prepared-through-gateway');
    }
    if(phase==='prepare'){await save('prepare.json');return;}
    // A previous run can still be scaling down. Establish the actual one-Pod baseline
    // without writing replicas, so an already scaled cluster cannot satisfy this run.
    const baselineDeadline=Date.now()+5*60*1000;
    while(Date.now()<baselineDeadline){
        state=await api('/state');
        assert.ok(!['running','draining'].includes(state.engine.run?.state),'another run is active');
        const baseline=name=>state.cluster.pods.filter(p=>p.app===name&&p.ready&&p.deletingAt===null).length===1
            && state.cluster.hpas.find(h=>h.name===name)?.desired===1;
        if(baseline('catalog')&&baseline('gateway'))break;
        console.log('Waiting for HPA to return both services to one Pod...');await pause(10000);
    }
    for(const name of ['catalog','gateway'])assert.equal(state.cluster.hpas.find(h=>h.name===name)?.desired,1,'one-Pod HPA baseline: '+name);
    await api('/runs',{scenario:'recovery',peak:clients,rampSeconds:90,holdSeconds:120},202);
    await api('/runs',{scenario:'wave',peak:clients,rampSeconds:90,holdSeconds:120},409);mark('concurrent-run-denied');
    const initial=await api('/state');
    const runId=initial.engine.run.id,startedAt=Date.parse(initial.engine.run.startedAt);let scaledCatalog=false,scaledGateway=false,deleted=false,faultStarted=false,faultExpired=false,downscaled=false;
    let replacementReady=false,deletedUidGone=false;
    const deadline=Date.now()+11*60*1000;
    while(Date.now()<deadline){
        state=await api('/state');const e=state.engine,c=state.cluster;
        const ready=name=>c.pods.filter(p=>p.app===name&&p.ready&&p.deletingAt===null).length;
        scaledCatalog||=ready('catalog')>=2;scaledGateway||=ready('gateway')>=2;
        const runEvents=state.events.filter(x=>Date.parse(x.at)>=startedAt);
        deleted||=runEvents.some(x=>x.kind==='pod_deleted');faultStarted||=runEvents.some(x=>x.kind==='fault_start');faultExpired||=runEvents.some(x=>x.kind==='fault_expired');
        const deletion=runEvents.find(x=>x.kind==='pod_deleted');
        if(deletion){
            const oldUid=deletion.message.match(/UID ([0-9a-f-]+)/i)?.[1];
            deletedUidGone||=!!oldUid&&!c.pods.some(p=>p.uid===oldUid);
            const replacements=new Set(runEvents.filter(x=>x.kind==='pod_created'&&Date.parse(x.at)>=Date.parse(deletion.at)).map(x=>x.message.match(/Новый Pod (\S+)/)?.[1]));
            replacementReady||=c.pods.some(p=>p.app==='catalog'&&p.ready&&replacements.has(p.name));
        }
        if(scaledCatalog&&scaledGateway&&e.target===0&&ready('catalog')===1&&ready('gateway')===1)downscaled=true;
        result.samples.push({at:new Date().toISOString(),engine:{run:e.run,target:e.target,active:e.active,sentRps:e.sentRps,successRps:e.successRps,p95Ms:e.p95Ms,errorPercent:e.errorPercent,outcomes:e.outcomes,pending:e.pendingOperations},pods:c.pods,hpas:c.hpas,metricsAt:c.metricsAt});
        console.log(`${e.phase}: ${e.active}/${e.target} clients, ${e.successRps.toFixed(1)} RPS, p95 ${e.p95Ms.toFixed(0)} ms; catalog=${ready('catalog')} gateway=${ready('gateway')}`);
        await save('running.json');
        if(e.run?.id===runId&&!['running','draining'].includes(e.run.state)&&downscaled)break;
        await pause(10000);
    }
    assert.equal(state.engine.run.state,'completed','scenario completed');mark('seven-minute-scenario-completed');
    assert.ok(scaledCatalog,'Catalog must scale from measured CPU');mark('catalog-automatically-scaled');
    assert.ok(scaledGateway,'Gateway must scale from measured CPU');mark('gateway-automatically-scaled');
    assert.ok(deleted&&deletedUidGone&&replacementReady,'deleted UID disappears and a replacement becomes Ready during the run');mark('catalog-pod-replaced-and-ready');
    assert.ok(faultStarted&&faultExpired,'fault lifecycle');mark('api-fault-expired');
    assert.ok(downscaled,'both services must return to one Ready Pod');mark('both-services-automatically-scaled-down');
    assert.ok(state.cluster.metricsAt&&Date.now()-Date.parse(state.cluster.metricsAt)<30000);mark('prometheus-metrics-fresh');
    result.finishedAt=new Date().toISOString();result.run=await api('/runs/'+runId);result.events=state.events;result.charts=state.cluster.charts;
    result.highestObservedHealthyRps=Math.max(0,...result.samples.filter(s=>s.engine.target>0&&s.engine.errorPercent<1&&s.engine.p95Ms<=1000).map(s=>s.engine.successRps));
    await save();console.log('Saved '+folder+'/result.json');
})().catch(async error=>{result.error=error.message;await save('failed.json');console.error(error.message);process.exitCode=1;});
