// Reusable domain acceptance for the minimal Compose and Kubernetes stacks.
const assert = require('node:assert/strict');
const {randomUUID} = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');
const base = process.env.BURMALDA_WEB_URL ?? 'http://localhost:5300';
const folder = path.resolve(process.env.BURMALDA_RESULT_DIR ?? 'lab-results/core-verification');
const sessionFile = path.resolve(process.env.BURMALDA_SESSION_FILE ?? '.artifacts/k8s/check-session.json');
const evidence = {startedAtUtc:new Date().toISOString(),base,checks:[],fixtures:{}};
const mark = name => {evidence.checks.push(name); console.log('PASS ' + name);};
async function request(route, body, token, status=200) {
    const response = await fetch(base+route, {method:body === undefined ? 'GET' : 'POST',
        headers:{'Content-Type':'application/json',...(token ? {Authorization:'Bearer '+token} : {})},
        body:body === undefined ? undefined : JSON.stringify(body),signal:AbortSignal.timeout(30000)});
    const text = await response.text();
    assert.equal(response.status,status,`${route}: ${text.slice(0,500)}`);
    return text ? JSON.parse(text) : null;
}
async function wait(probe, label, timeout=90000) {
    const end=Date.now()+timeout; let last;
    while(Date.now()<end) {
        try {const value=await probe(); if(value) return value;} catch(error){last=error;}
        await new Promise(resolve=>setTimeout(resolve,500));
    }
    throw new Error(label+': '+(last?.message ?? 'deadline exceeded'));
}
async function user(role) {
    const credentials={email:`lr9-${role}-${randomUUID()}@example.test`,password:`Test-${randomUUID()}!`};
    await request('/api/identity/register',{...credentials,displayName:'LR9 '+role},null,201);
    return {...await request('/api/identity/login',credentials),...credentials};
}
async function browserCheck(owner,borrower) {
    const {chromium}=require(path.resolve(process.env.BURMALDA_PLAYWRIGHT ?? '.artifacts/browser/node_modules/playwright'));
    const browser=await chromium.launch({headless:true,channel:process.env.BURMALDA_BROWSER ?? 'msedge'});
    const errors=[],demoRequests=[];
    try {
        const page=await browser.newPage({viewport:{width:1440,height:1000}});
        page.on('pageerror',e=>errors.push(e.message));
        page.on('request',r=>{if(/\/api\/(concurrency-lab|raft-lab|library-game)/.test(r.url()))demoRequests.push(r.url());});
        async function login(account) {
            await page.goto(base+'/login');
            await page.getByLabel('Email address').fill(account.email);
            await page.getByLabel('Password',{exact:true}).fill(account.password);
            await page.getByRole('button',{name:'Sign in',exact:true}).click();
            await page.waitForURL('**/dashboard');
        }
        await login(owner);
        assert.equal(await page.locator('nav a').filter({hasText:/Learning|Тихая|ЛР №|БД и/}).count(),0);
        const title='Kubernetes UI '+randomUUID();
        await page.goto(base+'/shelf');
        await page.getByLabel('ISBN',{exact:true}).fill(title);
        await page.getByLabel('Title',{exact:true}).fill(title);
        await page.getByLabel('Authors',{exact:false}).fill('LR9 browser');
        await page.getByRole('button',{name:'Create book record',exact:true}).click();
        await page.getByText('Book record created. Now describe your physical copy.',{exact:true}).waitFor();
        await page.getByLabel('Handover location').fill('Minsk');
        await page.getByRole('button',{name:'List this copy',exact:true}).click();
        await page.getByText('Your copy is now available to the community.',{exact:true}).waitFor();
        await page.screenshot({path:path.join(folder,'shelf-desktop.png'),fullPage:true});
        const uiBook=await request('/api/catalog/books/search?q='+encodeURIComponent(title),undefined,owner.accessToken);
        evidence.fixtures.uiBookId=uiBook[0].bookId;
        // A separate context avoids changing the owner's session or exposing tokens in page code.
        await page.context().clearCookies();
        await page.evaluate(()=>localStorage.clear());
        await login(borrower);
        await page.goto(base+'/discover');
        await page.getByLabel('Search the catalogue').fill(title);
        await page.getByRole('button',{name:'Search',exact:true}).click();
        await page.getByRole('button',{name:'Choose a copy →',exact:true}).click();
        await page.getByRole('button',{name:'Request',exact:true}).click();
        await page.getByRole('heading',{name:'Сага завершена: выдача создана',exact:true}).waitFor({timeout:90000});
        await page.screenshot({path:path.join(folder,'discover-desktop.png'),fullPage:true});
        await page.setViewportSize({width:390,height:844});
        await page.evaluate(()=>window.scrollTo(0,0));
        await page.screenshot({path:path.join(folder,'discover-mobile.png')});
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=window.innerWidth+1),'Mobile page overflow');
        const uiCopies=await request('/api/catalog/copies/mine',undefined,owner.accessToken);
        const uiCopy=uiCopies.find(copy=>copy.bookId===uiBook[0].bookId);
        assert.ok(uiCopy,'UI-created physical copy exists');
        const uiLoans=await request('/api/loans/mine',undefined,borrower.accessToken);
        const uiLoan=uiLoans.find(loan=>loan.copyId===uiCopy.id);
        assert.ok(uiLoan,'UI-created loan exists');
        async function switchAccount(account) {
            await page.evaluate(()=>localStorage.clear());
            await login(account);
            await page.goto(base+'/loans');
        }
        const card=()=>page.locator('.loan-card').filter({hasText:'Loan '+uiLoan.id.slice(0,8)});
        await switchAccount(owner);
        await card().getByRole('button',{name:'Accept',exact:true}).click();
        const handover=await card().locator('.handover-code strong').textContent();
        assert.match(handover,/^\d{6}$/);
        await switchAccount(borrower);
        await card().getByPlaceholder('6-digit code').fill(handover);
        await card().getByRole('button',{name:'Confirm handover',exact:true}).click();
        await card().getByRole('button',{name:'Start return',exact:true}).click();
        await page.getByText('Return started. The owner can now confirm receipt.',{exact:true}).waitFor();
        await switchAccount(owner);
        await card().getByRole('button',{name:'Confirm returned',exact:true}).click();
        await card().locator('.status-pill').filter({hasText:'Completed'}).waitFor();
        await wait(async()=>{const rows=await request('/api/catalog/books/search?q='+encodeURIComponent(title),undefined,owner.accessToken);return rows[0]?.availableCopies===1;},'UI return projection');
        await page.screenshot({path:path.join(folder,'loan-completed-mobile.png')});
        evidence.fixtures.uiLoanId=uiLoan.id;
        for(const route of ['/concurrency-lab','/replication-lab','/pubsub-lab','/raft-lab','/library-game']) {
            await page.goto(base+route);
            await page.getByRole('heading',{name:'Page unavailable',exact:true}).waitFor();
        }
        assert.deepEqual(demoRequests,[]);
        assert.deepEqual(errors,[]);
        evidence.browser={desktop:[1440,1000],mobile:[390,844],pageErrors:errors,disabledDemoRequests:demoRequests};
        mark('browser-login-book-copy-loan-handover-return-and-disabled-labs');
    } finally {await browser.close();}
}
(async()=>{
    await fs.mkdir(folder,{recursive:true});
    if(process.argv.includes('--probe') || process.argv.includes('--unavailable')) {
        const session=JSON.parse(await fs.readFile(sessionFile,'utf8'));
        if(process.argv.includes('--unavailable')) {
            const r=await fetch(base+'/api/catalog/books/'+session.bookId,{headers:{Authorization:'Bearer '+session.accessToken},signal:AbortSignal.timeout(30000)});
            assert.equal(r.status,503); const problem=await r.json(); assert.ok(problem.message || problem.title);
            evidence.outage={status:r.status,message:problem.message ?? problem.title};
            mark('dependency-outage-readable-503');
        } else {
            const runtime=await request('/api/catalog/runtime',undefined,session.accessToken);
            if(process.env.BURMALDA_EXPECTED_MODE)assert.equal(runtime.mode,process.env.BURMALDA_EXPECTED_MODE);
            const book=await request('/api/catalog/books/'+session.bookId,undefined,session.accessToken);
            assert.equal(book.id,session.bookId);
            const copies=await request('/api/catalog/books/'+session.bookId+'/copies',undefined,session.accessToken);
            assert.ok(copies.some(x=>x.id===session.copyId));
            evidence.runtime=runtime;evidence.fixtures={bookId:session.bookId,copyId:session.copyId};
            mark('same-book-and-copy-preserved-and-mode-confirmed');
        }
        evidence.success=true; return;
    }
    const response=await fetch(base+'/');assert.equal(response.status,200);
    assert.match(await response.text(),/blazor.webassembly.js/);
    const settings=await request('/appsettings.json');assert.equal(settings.Features.LearningLabs,false);
    mark('client-and-feature-configuration');
    const owner=await user('owner'),borrower=await user('borrower'),rival=await user('rival');
    await wait(async()=>{const w=await request('/api/economy/wallet',undefined,borrower.accessToken);return w.balance>=20;},'borrower wallet');
    await wait(async()=>{const w=await request('/api/economy/wallet',undefined,rival.accessToken);return w.balance>=20;},'rival wallet');
    const runtime=await request('/api/catalog/runtime',undefined,owner.accessToken);
    if(process.env.BURMALDA_EXPECTED_MODE)assert.equal(runtime.mode,process.env.BURMALDA_EXPECTED_MODE);
    evidence.runtime=runtime;
    await request('/api/catalog/runtime',undefined,null,401);
    await request('/api/admin/catalog/copies',undefined,owner.accessToken,403);
    const tag='lr9-'+randomUUID();
    await request('/api/catalog/books',{isbn:tag,title:'',authors:null,genres:[]},owner.accessToken,400);
    await request('/api/catalog/books/'+randomUUID(),undefined,owner.accessToken,404);
    await request('/api/catalog/copies',{bookId:randomUUID(),condition:2,lendingPrice:10,maximumLoanDays:14,location:'Minsk'},owner.accessToken,404);
    mark('configuration-auth-validation-not-found-and-forbidden');
    for(const route of ['/api/concurrency-lab/status','/api/raft-lab/status','/api/library-game/status']) {
        const r=await fetch(base+route);assert.equal(r.status,404);
    }
    mark('no-gateway-routes-to-learning-services');
    const batchBook=await request('/api/catalog/books',{isbn:tag+'-parallel',title:tag+'-parallel',authors:['LR9 parallel HTTP'],genres:[]},owner.accessToken,201);
    const parallelCopies=await Promise.all(Array.from({length:6},(_,i)=>request('/api/catalog/copies',{
        bookId:batchBook.id,condition:2,lendingPrice:i+1,maximumLoanDays:14,location:'Minsk'},owner.accessToken,201)));
    const batchProjection=await request('/api/catalog/books/search?q='+tag+'-parallel',undefined,owner.accessToken);
    assert.equal(batchProjection[0].availableCopies,6);
    assert.equal(batchProjection[0].minimumPrice,1);
    evidence.parallelCopies={bookId:batchBook.id,count:parallelCopies.length,availableCopies:batchProjection[0].availableCopies,minimumPrice:batchProjection[0].minimumPrice};
    mark('parallel-http-copy-creation-keeps-book-projection-consistent');
    const book=await request('/api/catalog/books',{isbn:tag,title:tag,authors:['LR9 acceptance'],genres:['Lab'],publicationYear:2026},owner.accessToken,201);
    const copy=await request('/api/catalog/copies',{bookId:book.id,condition:2,lendingPrice:10,maximumLoanDays:14,location:'Minsk'},owner.accessToken,201);
    evidence.fixtures={...evidence.fixtures,bookId:book.id,copyId:copy.id};
    const receipt=await request('/api/loans/requests',{copyId:copy.id},borrower.accessToken,202);
    const saga=await wait(async()=>{const s=await request('/api/loans/requests/'+receipt.id,undefined,borrower.accessToken);return s.state===2&&s;},'loan saga');
    const accepted=await request('/api/loans/'+saga.loanId+'/accept',{},owner.accessToken);
    await request('/api/loans/'+saga.loanId+'/handover/confirm',{code:accepted.handoverCode},borrower.accessToken);
    await request('/api/loans/'+saga.loanId+'/return',{},borrower.accessToken);
    const returned=await request('/api/loans/'+saga.loanId+'/return/confirm',{},owner.accessToken);assert.equal(returned.status,7);
    await wait(async()=>{const rows=await request('/api/catalog/books/search?q='+tag,undefined,owner.accessToken);return rows[0]?.availableCopies===1;},'return projection');
    mark('book-copy-loan-handover-return-and-projection');
    const requests=await Promise.all([borrower,rival].map(async account=>({account,receipt:await request('/api/loans/requests',{copyId:copy.id},account.accessToken,202)})));
    const outcomes=await Promise.all(requests.map(async item=>({...item,saga:await wait(async()=>{const s=await request('/api/loans/requests/'+item.receipt.id,undefined,item.account.accessToken);return [2,4,5].includes(s.state)&&s;},'competing saga')})));
    assert.equal(outcomes.filter(x=>x.saga.state===2).length,1);
    assert.equal(outcomes.filter(x=>x.saga.state===4).length,1);
    const winner=outcomes.find(x=>x.saga.state===2);
    await request('/api/loans/'+winner.saga.loanId+'/cancel',{},winner.account.accessToken,204);
    await wait(async()=>{const rows=await request('/api/catalog/books/search?q='+tag,undefined,owner.accessToken);return rows[0]?.availableCopies===1;},'cancel projection');
    evidence.competingRequests=outcomes.map(x=>({sagaId:x.saga.id,state:x.saga.state,loanId:x.saga.loanId}));
    mark('competing-borrowers-one-copy-one-winner');
    await wait(async()=>{const notices=await request('/api/notifications',undefined,owner.accessToken);return Array.isArray(notices)&&notices.length>0;},'notification projection');
    await request('/api/reputation/me',undefined,borrower.accessToken);
    mark('notifications-and-reputation');
    await fs.mkdir(path.dirname(sessionFile),{recursive:true});
    await fs.writeFile(sessionFile,JSON.stringify({accessToken:owner.accessToken,bookId:book.id,copyId:copy.id}));
    if(process.argv.includes('--browser'))await browserCheck(owner,borrower);
    evidence.success=true;
})().catch(error=>{evidence.failure=error.stack;console.error(error.message);process.exitCode=1;}).finally(async()=>{
    evidence.finishedAtUtc=new Date().toISOString();await fs.mkdir(folder,{recursive:true});
    await fs.writeFile(path.join(folder,'integration.json'),JSON.stringify(evidence,null,2));
    console.log(`${evidence.checks.length} checks; success=${!!evidence.success}`);
});
