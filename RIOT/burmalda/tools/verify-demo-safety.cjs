// Live-cluster lifecycle checks. Restarts only DemoControl; never scales Catalog/Gateway.
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const nativeFs=require('node:fs');
const {execFileSync,spawn}=require('node:child_process');
const base='http://localhost:5310',web='http://localhost:5300';
const evidence={startedAt:new Date().toISOString(),checks:[],samples:[]};
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
let token;
const kube=args=>execFileSync('kubectl',['--context','docker-desktop','-n','burmalda',...args],{encoding:'utf8',windowsHide:true,timeout:90000});
async function request(url,body,status=200){
    const response=await fetch(url,{method:body===undefined?'GET':'POST',headers:{'content-type':'application/json',authorization:'Bearer '+token},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(30000)});
    assert.equal(response.status,status,url+': '+(await response.clone().text()).slice(0,200));
    const text=await response.text();return text?JSON.parse(text):null;
}
const api=(route,body,status)=>request(base+'/api/demo'+route,body,status);
function mark(name){evidence.checks.push(name);console.log('PASS '+name);}
async function until(predicate,seconds=120){
    const deadline=Date.now()+seconds*1000;
    while(Date.now()<deadline){const state=await api('/state');if(predicate(state))return state;await sleep(2000);}
    throw Error('State did not settle within '+seconds+' seconds');
}
async function save(){await fs.mkdir('lab-results/demo-verification',{recursive:true});await fs.writeFile('lab-results/demo-verification/safety.json',JSON.stringify(evidence,null,2));}
async function forward(){
    const out=nativeFs.openSync('.artifacts/k8s/access-demo-control.log','a');
    const err=nativeFs.openSync('.artifacts/k8s/access-demo-control.err','a');
    const process=spawn('kubectl',['--context','docker-desktop','-n','burmalda','port-forward','service/demo-control','5310:8080','--address','127.0.0.1'],{detached:true,windowsHide:true,stdio:['ignore',out,err]});
    process.unref();nativeFs.closeSync(out);nativeFs.closeSync(err);
    await fs.writeFile('.artifacts/k8s/access-demo-control.json',JSON.stringify({pid:process.pid,port:5310}));
    for(let i=0;i<20;i++){try{const r=await fetch(base+'/health/ready');if(r.ok)return;}catch{}await sleep(1000);}
    throw Error('Demo port-forward did not recover');
}
(async()=>{
    const secret=JSON.parse(await fs.readFile('.artifacts/k8s/secret.json','utf8'));
    const login=await fetch(web+'/api/identity/login',{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify({email:'admin@burmalda.local',password:secret.stringData.ADMIN_PASSWORD})});
    assert.equal(login.status,200);token=(await login.json()).accessToken;
    let state=await api('/state');
    assert.ok(!['running','draining'].includes(state.engine.run?.state),'finish the active scenario first');
    const fixtureCount=Math.max(30,state.engine.preparation.ready);
    await api('/prepare',{clients:30},202);
    state=await until(s=>!s.engine.preparation.running,180);
    assert.equal(state.engine.preparation.error,null);assert.equal(state.engine.preparation.ready,30);mark('reused-fixtures-refreshed');
    await api('/runs',{scenario:'mixed',peak:30,rampSeconds:5,holdSeconds:30},202);
    await api('/runs',{scenario:'wave',peak:3},409);mark('concurrent-run-rejected');
    state=await until(s=>s.engine.completedOperations>=3,110);
    evidence.samples.push({at:new Date().toISOString(),engine:state.engine});
    await api('/target',{clients:5});
    await sleep(8000);
    state=await api('/state');assert.ok(state.engine.active<=5);mark('manual-client-limit-applied');
    await api('/stop',{immediate:false});await api('/stop',{immediate:false});
    state=await until(s=>!['running','draining'].includes(s.engine.run?.state));
    assert.equal(state.engine.run.state,'completed');assert.equal(state.engine.pendingOperations,0);mark('mixed-operations-drained-and-stop-idempotent');
    await api('/runs',{scenario:'wave',peak:3,rampSeconds:5,holdSeconds:30},202);
    await sleep(3000);
    await api('/stop',{immediate:true});await api('/stop',{immediate:false});
    state=await until(s=>s.engine.run.state==='aborted');assert.equal(state.engine.active,0);mark('emergency-stop-preserved');
    await api('/runs',{scenario:'wave',peak:3,rampSeconds:5,holdSeconds:30},202);
    await api('/faults',{service:'catalog',errorPercent:100,delayMs:0,durationSeconds:15});
    await sleep(3500);
    await request(web+'/api/catalog/books/search?q=Burmalda%20Demo',undefined,503);mark('normal-site-api-affected-by-fault');
    const response=await fetch(web+'/health/ready');assert.ok(response.status<500);mark('site-remains-reachable');
    const before=await api('/state');
    kube(['scale','deployment/demo-control','--replicas=0']);
    try{
        await sleep(18000);
        await request(web+'/api/catalog/books/search?q=Burmalda%20Demo');mark('fault-expired-with-controller-offline');
    }finally{
        kube(['scale','deployment/demo-control','--replicas=1']);
        kube(['rollout','status','deployment/demo-control','--timeout=75s']);
        await forward();
    }
    state=await until(s=>s.engine.run?.state==='interrupted');
    assert.equal(state.engine.run.id,before.engine.run.id);assert.equal(state.engine.active,0);assert.equal(state.engine.target,0);
    assert.equal(state.engine.preparation.ready,fixtureCount);
    assert.equal(state.engine.faults.length,0);mark('restart-preserved-fixtures-and-did-not-resume');
    const interrupted=await api('/runs/'+state.engine.run.id);assert.equal(interrupted.run?.state??interrupted.Run?.State,'interrupted');mark('interrupted-result-persisted');
    evidence.finishedAt=new Date().toISOString();evidence.samples.push({at:evidence.finishedAt,engine:state.engine});await save();
})().catch(async error=>{try{await api('/stop',{immediate:true});}catch{}evidence.error=error.message;await save();console.error(error.message);process.exitCode=1;});
