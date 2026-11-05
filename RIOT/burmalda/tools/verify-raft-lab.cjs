// Real participants, real Docker effects, and a disposable catalog fixture. No reset of existing data.
const assert=require('node:assert/strict');
const {spawnSync}=require('node:child_process');
const {randomUUID,createHmac}=require('node:crypto');
const fs=require('node:fs/promises');
const path=require('node:path');
const base=process.env.BURMALDA_LAB_URL??'http://localhost:5000';
const key=process.env.Raft__InternalKey??'burmalda-raft-local-only';
const headers={'X-Raft-Key':key};
const folder=path.resolve('lab-results/raft-verification');
const checks=[],evidence={operations:[]};
const stopped=new Set();
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const check=name=>{checks.push(name);console.log('PASS '+name);};
function command(command,args,input){const r=spawnSync(command,args,{input,encoding:'utf8',timeout:180000,windowsHide:true});assert.equal(r.status,0,r.stderr||r.stdout);return r.stdout.trim();}
const compose=JSON.parse(command('docker',['compose','config','--format','json']));
const admin=compose.services.postgres.environment.POSTGRES_USER;
const sql=(node,text)=>command('docker',['compose','exec','-T',node,'psql','-X','-U',admin,'-d','catalog_db','-At','-v','ON_ERROR_STOP=1'],text);
async function request(url,body,status=200,extra={}){
    const r=await fetch(url,{method:body===undefined?'GET':'POST',headers:{'Content-Type':'application/json',...extra},body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(60000)});
    const text=await r.text();assert.equal(r.status,status,`${url}: ${text}`);return text?JSON.parse(text):null;
}
const api=(route,body,status)=>request(base+'/api/raft-lab'+route,body,status);
const internal=(id,route,body,status)=>request(`http://127.0.0.1:${5120+id}/internal${route}`,body,status,headers);
async function wait(fn,description,timeout=90000){const end=Date.now()+timeout;while(Date.now()<end){const value=await fn();if(value)return value;await sleep(250);}throw Error('Timeout: '+description);}
async function nodes(){return Promise.all([1,2,3,4,5].map(async id=>{try{return await internal(id,'/status');}catch{return null;}}));}
async function leader(){return wait(async()=>{const ns=(await nodes()).filter(Boolean).sort((a,b)=>b.term-a.term);for(const n of ns.filter(n=>n.role==='Leader')){try{return(await internal(n.nodeId,'/state'));}catch{}}return null;},'authoritative Raft leader');}
const state=async()=>(await leader()).state;
function nodeProcess(id,action){
    // Background participants must not retain spawnSync's capture pipes.
    const r=spawnSync('pwsh',['-NoProfile','-File',path.resolve('tools/Set-RaftNode.ps1'),'-NodeId',String(id),'-Action',action],{stdio:'ignore',timeout:45000,windowsHide:true});
    assert.equal(r.status,0,`Node ${id} ${action}: ${r.error?.message??'inspect raft-runtime logs'}`);
    if(action==='Stop')stopped.add(id);else stopped.delete(id);
}
async function submit(kind,node,id=randomUUID()){const body={commandId:id,kind,node};await api('/operations',body,202);return id;}
async function settled(id,expected='Completed'){
    const op=await wait(async()=>{const s=await state();const op=s.operations[id];return op&&op.status!=='Running'?op:null;},'operation '+id,180000);
    evidence.operations.push(op);assert.equal(op.status,expected,JSON.stringify(op));return op;
}
async function operate(kind,node){const id=await submit(kind,node);return settled(id);}
async function healthy(){
    let s=await api('/status');
    if(s.state?.fencedSource)await operate('Recover',s.state.fencedSource);
    await operate('Synchronize');
    s=await api('/status');assert.equal(s.postgres.filter(n=>n.role==='Primary'&&!n.readOnly&&!n.fenced).length,1);assert.equal(s.postgres.filter(n=>n.role==='Standby').length,1);return s;
}
async function fault(id,blocked=[],delayMs=0,dropEvery=0){return internal(id,'/fault',{blocked,delayMs,dropEvery});}
async function heal(){for(let id=1;id<=5;id++)if(!stopped.has(id))await fault(id);}
const bookId=randomUUID(),copyId=randomUUID(),ownerId=randomUUID();let fixture=false,success=false,token,initialPrimary;
async function catalog(){return request(`${base}/api/catalog/books/${bookId}`,undefined,200,{Authorization:'Bearer '+token});}
(async()=>{
    await fs.mkdir(folder,{recursive:true});
    let initial=await healthy();evidence.initial=initial;initialPrimary=initial.state.primary;
    assert.equal(initial.nodes.filter(n=>n.available).length,5);check('five-independent-processes-and-authoritative-majority');
    const config=JSON.parse(await fs.readFile('src/Services/Catalog/Burmalda.Catalog.Api/appsettings.json','utf8'));
    const enc=o=>Buffer.from(JSON.stringify(o)).toString('base64url');const payload=`${enc({alg:'HS256',typ:'JWT'})}.${enc({iss:config.Jwt.Issuer,aud:config.Jwt.Audience,sub:ownerId,role:'Admin',exp:Math.floor(Date.now()/1000)+7200})}`;
    token=`${payload}.${createHmac('sha256',config.Jwt.SigningKey).update(payload).digest('base64url')}`;
    sql(initialPrimary,`BEGIN;
INSERT INTO books ("Id","Isbn","Title","Description","AuthorsJson","GenresJson","CreatedAt") VALUES ('${bookId}','raft-${bookId}','Raft disposable verification','','["Raft verifier"]','[]',now());
INSERT INTO book_copies ("Id","BookId","OwnerId","Condition","Status","LendingPrice","MaximumLoanDays","Location","IsHidden","Version","CreatedAt") VALUES ('${copyId}','${bookId}','${ownerId}',0,0,1,7,'Raft verifier',false,1,now());
INSERT INTO book_search_projection ("BookId","Isbn","Title","AuthorsText","GenresText","AvailableCopies","MinimumPrice","UpdatedAt") VALUES ('${bookId}','raft-${bookId}','Raft disposable verification','Raft verifier','',1,1,now());COMMIT;`);
    fixture=true;evidence.fixture={bookId,copyId};
    await fs.writeFile(path.join(folder,`fixture-${bookId}.json`),JSON.stringify(evidence.fixture));await catalog();
    const run=await request(base+'/api/concurrency-lab/replication/runs',{});
    await request(base+`/api/concurrency-lab/replication/runs/${run.id}/update`,{clientId:'Raft verifier',copyId,available:false,expectedVersion:1});await operate('Synchronize');
    check('ordinary-catalog-and-lr5-copy-update-remain-postgresql-operations');
    const repeat=await submit('Synchronize');await settled(repeat);await api('/operations',{commandId:repeat,kind:'Synchronize'},202);
    await api('/operations',{commandId:repeat,kind:'Pause'},409);check('same-command-id-idempotence-and-conflicting-content-rejection');
    await operate('Pause');const blocked=await submit('Switchover');const failed=await settled(blocked,'Failed');
    assert.match(failed.error,/WAL|replay|Standby/);assert.notEqual((await state()).fencedSource,null);check('paused-standby-cannot-be-promoted');
    await healthy();
    let old=(await leader()).node.nodeId;nodeProcess(old,'Stop');let current=await leader();assert.notEqual(current.node.nodeId,old);await operate('Synchronize');check('leader-process-failure-and-continued-work-with-four-voters');
    const second=[1,2,3,4,5].find(id=>id!==old&&id!==current.node.nodeId);nodeProcess(second,'Stop');
    await operate('Switchover');await healthy();check('real-postgresql-switchover-with-three-voters');
    const third=[1,2,3,4,5].find(id=>!stopped.has(id)&&id!==current.node.nodeId);nodeProcess(third,'Stop');
    const unknown=randomUUID();await api('/operations',{commandId:unknown,kind:'Synchronize'},503);await catalog();check('two-voters-cannot-confirm-new-command-while-catalog-remains-available');
    for(const id of [...stopped])nodeProcess(id,'Start');await leader();
    await api('/operations',{commandId:unknown,kind:'Synchronize'},202);await settled(unknown);check('unknown-outcome-retried-with-original-command-id');
    old=(await leader()).node.nodeId;const minority=[old,old===5?4:5];
    for(let id=1;id<=5;id++)await fault(id,[1,2,3,4,5].filter(other=>minority.includes(id)!==minority.includes(other)));
    await sleep(3500);
    const minorityId=randomUUID();const entry=JSON.stringify({kind:'Begin',operationId:minorityId,request:{commandId:minorityId,kind:'Synchronize'}});
    await internal(old,'/commands',{commandId:minorityId,payload:entry},503);
    current=await leader();assert.ok(!minority.includes(current.node.nodeId));await operate('Synchronize');await heal();await sleep(1500);
    assert.equal((await state()).operations[minorityId],undefined);check('partition-3-plus-2-truncates-isolated-leader-uncommitted-command');
    old=(await leader()).node.nodeId;await internal(old,'/executor-fault',{pauseStage:'Planned'});
    const committed=await submit('Switchover');await wait(async()=>{const op=(await state()).operations[committed];return op?.stage==='Planned';},'committed plan before effect');
    nodeProcess(old,'Stop');await leader();await settled(committed);nodeProcess(old,'Start');await healthy();check('new-leader-resumes-committed-operation-before-external-effect');
    old=(await leader()).node.nodeId;await internal(old,'/executor-fault',{pauseStage:'AfterPromotionEffect'});
    const promotion=await submit('Switchover');
    await wait(async()=>{const view=await api('/status');const op=view.state?.operations[promotion];return op?.stage==='PromotionAuthorized'&&view.postgres.some(n=>n.id===op.target&&n.role==='Primary'&&!n.readOnly);},'promotion before Raft result');
    nodeProcess(old,'Stop');await leader();await settled(promotion);nodeProcess(old,'Start');await healthy();check('promotion-result-reconciled-after-leader-crash-without-repeating-effect');
    current=await api('/status');const source=current.state.primary;const target=current.postgres.find(n=>n.id!==source).id;
    command('docker',['compose','stop',source]);await sleep(2000);
    await request(base+`/api/concurrency-lab/replication/runs/${run.id}/read`,{clientId:'Raft verifier'},503);
    const outage=await api('/status');assert.equal(outage.postgres.find(n=>n.id===target).role,'Standby');await operate('Recover',source);await healthy();check('unplanned-async-primary-loss-and-read-never-authorize-promotion');
    const before=(await leader()).node.commitIndex;
    for(let id=1;id<=5;id++)nodeProcess(id,'Stop');for(let id=1;id<=5;id++)nodeProcess(id,'Start');
    current=await leader();assert.ok(current.node.commitIndex>=before);await catalog();check('full-participant-restart-preserves-committed-history-and-catalog');
    const lagging=[1,2,3,4,5].find(id=>id!==(current.node.nodeId));nodeProcess(lagging,'Stop');
    for(let batch=0;batch<35;batch++){
        const l=(await leader()).node.nodeId;
        await Promise.all(Array.from({length:8},()=>{const id=randomUUID();return internal(l,'/commands',{commandId:id,payload:JSON.stringify({kind:'Probe',operationId:id})});}));
    }
    nodeProcess(lagging,'Start');await wait(async()=>{const n=await internal(lagging,'/status');return n.snapshotIndex>0&&n.lastApplied>before+250;},'InstallSnapshot on lagging process');
    check('real-snapshot-compaction-and-installation-on-lagging-node');
    const final=await healthy();assert.equal(sql(final.state.primary,`SELECT "Status"||':'||"Version" FROM book_copies WHERE "Id"='${copyId}';`),'3:2');
    await catalog();evidence.final=final;check('catalog-state-survives-failures-and-restored-streaming');success=true;
})().catch(e=>{evidence.failure=e.stack;console.error(e);process.exitCode=1;}).finally(async()=>{
    try{
        for(const id of [...stopped])nodeProcess(id,'Start');await heal();
        const final=await healthy();
        if(fixture)sql(final.state.primary,`BEGIN;DELETE FROM book_copies WHERE "Id"='${copyId}';DELETE FROM book_search_projection WHERE "BookId"='${bookId}';DELETE FROM books WHERE "Id"='${bookId}';COMMIT;`);
        evidence.cleanup={fixtureRemoved:fixture,primary:final.state.primary};
    }catch(e){evidence.cleanupError=e.message;process.exitCode=1;}
    await fs.mkdir(folder,{recursive:true});await fs.writeFile(path.join(folder,'integration.json'),JSON.stringify({verifiedAtUtc:new Date().toISOString(),success,checks,...evidence},null,2));
    console.log(`${checks.length} integration checks; success=${success}`);
});
