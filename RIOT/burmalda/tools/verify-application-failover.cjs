// Real PostgreSQL failover through ordinary Catalog HTTP requests.
// Only this run's GUID book/copies are removed; existing inventory is preserved.
const assert = require('node:assert/strict');
const {spawnSync} = require('node:child_process');
const {randomUUID, createHmac} = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');
const lab = 'http://localhost:5107/api/concurrency-lab/replication/runs';
const api = 'http://localhost:5000/api/catalog';
const checks = [], evidence = {}, folder = path.resolve('lab-results/application-failover');
let run, bookId, sourceToRestore, targetToRestore, success = false;
const sleep = ms => new Promise(r => setTimeout(r, ms));
function docker(args, input) {
    const r = spawnSync('docker', args, {input, encoding:'utf8', timeout:180000, windowsHide:true});
    assert.equal(r.status, 0, r.stderr); return r.stdout.trim();
}
const compose = JSON.parse(docker(['compose','config','--format','json']));
const admin = compose.services.postgres.environment.POSTGRES_USER;
const sql = (node, text) => docker(['compose','exec','-T',node,'psql','-X','-U',admin,'-d','catalog_db','-At','-v','ON_ERROR_STOP=1'], text);
const check = name => {checks.push(name); console.log('PASS '+name);};
async function request(url, body, status=200, headers={}) {
    const response = await fetch(url, {method:body===undefined?'GET':'POST', headers:{'Content-Type':'application/json',...headers}, body:body===undefined?undefined:JSON.stringify(body), signal:AbortSignal.timeout(45000)});
    const text = await response.text(); assert.equal(response.status,status,`${url}: ${text}`);
    return text ? JSON.parse(text) : null;
}
async function snapshot() {return run = await request(`${lab}/${run.id}`);}
async function action(node, available) {
    run = await request(`${lab}/${run.id}/instances/${node}`, {available}, 202);
    const deadline = Date.now()+180000;
    do {
        await sleep(300); await snapshot();
        if(run.actionStatus!=='Running'&&!run.clusterBusy) {assert.equal(run.actionStatus,'Completed',run.actionError); return;}
    } while(Date.now()<deadline);
    throw Error('Cluster action deadline exceeded');
}
async function aligned() {
    const deadline=Date.now()+45000;
    do {await snapshot(); if(run.consistent)return; await sleep(400);}while(Date.now()<deadline);
    throw Error('Standby did not catch up');
}
async function writer() {
    for(const node of ['postgres','postgres-replica']) {
        const r=spawnSync('docker',['compose','exec','-T',node,'psql','-X','-U',admin,'-d','postgres','-Atc',"select not pg_is_in_recovery() and not current_setting('default_transaction_read_only')::boolean"],{encoding:'utf8',timeout:10000,windowsHide:true});
        if(r.status===0&&r.stdout.trim()==='t')return node;
    }
    return null;
}
(async()=>{
    await fs.mkdir(folder,{recursive:true});
    const config=JSON.parse(await fs.readFile('src/Services/Catalog/Burmalda.Catalog.Api/appsettings.json','utf8'));
    const enc=o=>Buffer.from(JSON.stringify(o)).toString('base64url');
    const user=randomUUID(), payload=`${enc({alg:'HS256',typ:'JWT'})}.${enc({iss:config.Jwt.Issuer,aud:config.Jwt.Audience,sub:user,role:'Reader',exp:Math.floor(Date.now()/1000)+3600})}`;
    const token=`${payload}.${createHmac('sha256',compose.services.gateway?.environment?.Jwt__SigningKey??config.Jwt.SigningKey).update(payload).digest('base64url')}`;
    const auth={Authorization:'Bearer '+token};
    run=await request(lab,{});
    // Also restores the node left stopped by a prior manual demonstration.
    assert.ok(run.activeInstance,'Start the automatic coordinator before verification');
    for(const node of run.nodes.filter(n=>n.state!=='AVAILABLE')) await action(node.id,true);
    await aligned(); evidence.initial=run;
    evidence.initialCounts={books:run.nodes[0].books.length,copies:run.nodes[0].copies.length};
    const created=await request(api+'/books',{isbn:'failover-'+randomUUID(),title:'Disposable failover verification',authors:['LR5 verifier'],genres:[],description:'',publisher:null,publicationYear:null,coverUrl:null},201,auth);
    bookId=created.id;
    await fs.writeFile('.artifacts/failover-runtime/fixture.json',JSON.stringify({bookId,runId:run.id}));
    const copy={bookId,condition:0,lendingPrice:1,maximumLoanDays:7,location:'Temporary integration fixture'};
    const first=await request(api+'/copies',copy,201,auth);
    check('ordinary-create-copy-before-failover'); await aligned();
    const old=run.activeInstance;
    // Do not call the lab read/update endpoints: only an ordinary Catalog POST follows stop.
    await action(old,false);
    const started=Date.now();
    const second=await request(api+'/copies',copy,201,auth);
    evidence.createDuringFailoverMs=Date.now()-started;
    const promoted=await writer(); assert.ok(promoted); assert.notEqual(promoted,old);
    assert.equal(sql(promoted,`select count(*) from book_copies where "BookId"='${bookId}';`),'2');
    check('ordinary-create-copy-waits-for-automatic-promotion-without-lab-read');
    assert.notEqual(first.id,second.id); check('no-duplicate-copy-from-connection-retry');
    assert.equal((await request(api+`/books/${bookId}/copies`,undefined,200,auth)).length,2);
    check('catalog-read-after-role-change');
    await action(old,true); await aligned(); evidence.afterPromotion=run;
    check('former-primary-rejoins-as-standby-and-replays-new-copy');
    const source=run.activeInstance, target=run.nodes.find(n=>n.role==='Standby').id;
    sourceToRestore=source; targetToRestore=target;
    await action(target,false); await action(source,false);
    const outageStarted=Date.now();
    const problem=await request(api+'/copies',copy,503,auth);
    evidence.unavailableRequestMs=Date.now()-outageStarted; evidence.unavailableResponse=problem;
    assert.match(problem.message,/PostgreSQL/); assert.ok(evidence.unavailableRequestMs<25000);
    check('both-nodes-down-returns-bounded-503-with-friendly-message');
    await request('http://localhost:5102/health/live');
    check('catalog-process-remains-alive-during-database-outage');
    if(process.env.BURMALDA_VERIFY_BROWSER==='1') {
        const {chromium}=require('playwright');
        const browser=await chromium.launch({headless:true,channel:'msedge'});
        try {
            const context=await browser.newContext({viewport:{width:1280,height:720}});
            await context.addInitScript(session=>localStorage.setItem('burmalda.session',JSON.stringify(session)),
                {accessToken:token,expiresAt:new Date(Date.now()+3600000).toISOString(),refreshToken:'verification-only',id:user,displayName:'Failover verifier',role:'Reader'});
            const page=await context.newPage(), errors=[]; page.on('pageerror',e=>errors.push(e.message));
            await page.goto('http://localhost:5300/shelf');
            await page.getByLabel('Book ID',{exact:true}).fill(bookId);
            const response=page.waitForResponse(r=>new URL(r.url()).pathname==='/api/catalog/copies'&&r.request().method()==='POST',{timeout:35000});
            await page.getByRole('button',{name:'List this copy',exact:true}).click();
            assert.equal((await response).status(),503);
            await page.waitForFunction(()=>document.querySelector('.form-error')?.textContent.includes('PostgreSQL'));
            assert.equal(await page.getByRole('button',{name:'List this copy',exact:true}).isEnabled(),true);
            assert.notEqual(await page.locator('#blazor-error-ui').evaluate(e=>getComputedStyle(e).display),'block');
            await page.screenshot({path:path.join(folder,'database-unavailable-1280.png'),fullPage:true});
            await page.getByRole('link',{name:'ЛР №5: репликация',exact:true}).click();
            await page.waitForURL('**/replication-lab'); assert.deepEqual(errors,[]);
            evidence.browserErrors=errors;
            check('real-browser-copy-form-handles-503-and-navigation-stays-usable');
        } finally {await browser.close();}
    }
    await action(source,true); await action(target,true); await aligned();
    sourceToRestore=targetToRestore=null;
    assert.equal(sql(run.activeInstance,`select count(*) from book_copies where "BookId"='${bookId}';`),'2');
    check('failed-request-does-not-create-a-copy');
    await request(api+'/copies',copy,201,auth); await aligned();
    check('same-catalog-process-accepts-writes-after-recovery');
    evidence.final=run; success=true;
})().catch(error=>{evidence.error=error.stack; throw error;}).finally(async()=>{
    if(bookId) {
        if(sourceToRestore) {await action(sourceToRestore,true); await action(targetToRestore,true); await aligned();}
        let current=await writer();
        for(let i=0;!current&&i<20;i++){await sleep(500);current=await writer();}
        if(!current)throw Error('No writer: fixture retained for diagnosis; recover the source using LR5 controls.');
        sql(current,`BEGIN; DELETE FROM book_search_projection WHERE "BookId"='${bookId}'; DELETE FROM book_copies WHERE "BookId"='${bookId}'; DELETE FROM books WHERE "Id"='${bookId}'; COMMIT;`);
        await aligned();
        assert.deepEqual({books:run.nodes[0].books.length,copies:run.nodes[0].copies.length},evidence.initialCounts);
        check('only-verification-fixture-removed-existing-inventory-preserved');
        evidence.afterCleanup=run;
    }
    await fs.writeFile(path.join(folder,'verification.json'),JSON.stringify({atUtc:new Date().toISOString(),passed:success,checks,evidence},null,2));
}).catch(error=>{console.error(error);process.exitCode=1;});
