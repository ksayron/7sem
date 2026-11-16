const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
const {chromium} = require(process.env.PLAYWRIGHT_MODULE ?? path.resolve('.artifacts/browser/node_modules/playwright'));
const control = 'http://localhost:5310', library = 'http://localhost:30530';
const output = process.env.BURMALDA_LIVE_RESULTS ?? 'lab-results/live-release-verification';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

(async () => {
    await fs.mkdir(output, {recursive:true});
    const secret = JSON.parse(await fs.readFile('.artifacts/k8s/secret.json','utf8'));
    const credentials = {email:'admin@burmalda.local',password:secret.stringData.ADMIN_PASSWORD};
    const login = await fetch(library+'/api/identity/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(credentials)});
    assert.equal(login.status,200);
    const token = (await login.json()).accessToken;
    const headers = {Authorization:'Bearer '+token};
    const browser = await chromium.launch({headless:true,channel:'msedge'});
    const context = await browser.newContext({viewport:{width:1440,height:1050},reducedMotion:'reduce'});
    const page = await context.newPage(), errors = [], probes = [], timeline = [];
    page.on('pageerror',e=>errors.push(e.message));
    const state = async () => { const r=await fetch(control+'/api/demo/state',{headers,signal:AbortSignal.timeout(15000)}); assert.equal(r.status,200); return r.json(); };
    const post = async (route,data) => { const r=await fetch(control+'/api/demo/'+route,{method:'POST',headers:{...headers,'Content-Type':'application/json'},body:JSON.stringify(data)}); assert.ok(r.ok,route+': '+r.status); };
    async function probe() {
        const at=Date.now();
        try {
            const [web,api]=await Promise.all([fetch(library+'/',{signal:AbortSignal.timeout(5000)}),fetch(library+'/api/catalog/books/search?q=Burmalda%20Demo&take=10',{headers,signal:AbortSignal.timeout(5000)})]);
            await Promise.all([web.text(),api.text()]);
            probes.push({at,ms:Date.now()-at,web:web.status,api:api.status,commit:api.headers.get('X-Burmalda-Catalog-Version'),edition:api.headers.get('X-Burmalda-Catalog-Edition')});
        } catch { probes.push({at,ms:Date.now()-at,web:0,api:0}); }
    }
    try {
        await page.goto(control);
        await page.getByLabel('Электронная почта').fill(credentials.email);
        await page.getByLabel('Пароль',{exact:true}).fill(credentials.password);
        await page.getByRole('button',{name:'Войти в пульт'}).click();
        await page.waitForURL(control+'/');
        await page.getByRole('heading',{name:'Выпуск обновления',exact:true}).waitFor();
        const unauth = await fetch(control+'/api/demo/release',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});
        assert.equal(unauth.status,401);
        const noCsrf = await context.request.post(control+'/api/demo/release',{data:{}});
        assert.equal(noCsrf.status(),400);
        let current = await state();
        if (!['running','draining'].includes(current.engine.run?.state)) {
            await page.getByLabel('Точное число клиентов').fill('20');
            await page.getByLabel('Точное число клиентов').press('Tab');
            await page.getByRole('button',{name:'Подготовить 20 клиентов',exact:true}).click();
            const prepareDeadline=Date.now()+240000;
            while ((current=await state()).engine.preparation.running || current.engine.preparation.ready<20) {
                if (current.engine.preparation.error) throw Error(current.engine.preparation.error);
                assert.ok(Date.now()<prepareDeadline,'Preparation timeout'); await sleep(2000);
            }
            await page.getByRole('button',{name:'Запустить сценарий',exact:true}).click();
            await sleep(2500);
            current=await state();
            assert.equal(current.engine.run?.state,'running',await page.locator('.notice').allTextContents());
        }
        console.log('Traffic running. Waiting for versioned baseline.');
        const baselineDeadline=Date.now()+1200000;
        while (true) {
            await probe(); current=await state();
            if (current.release?.build && !current.release.build.building && current.release.build.result==='SUCCESS' && probes.at(-1).edition && current.engine.active>=15) break;
            assert.ok(Date.now()<baselineDeadline,'Baseline timeout'); await sleep(2000);
        }
        const baseline=current.release.build;
        await page.getByRole('button',{name:'Выпустить обновление',exact:true}).click();
        console.log('Clicked Publish. Baseline Jenkins #'+baseline.number+'.');
        const deadline=Date.now()+1200000;
        let captured=false, final;
        while (Date.now()<deadline) {
            await probe(); current=await state();
            timeline.push({at:Date.now(),active:current.engine.active,completed:current.engine.completedOperations,failed:current.engine.failedOperations,
                build:current.release.build?.number,building:current.release.build?.building,
                pods:current.cluster.pods.filter(p=>['catalog','gateway','web'].includes(p.app)),traffic:current.release.traffic});
            const build=current.release.build;
            if (current.release.error) throw Error(current.release.error);
            if (build?.number>baseline.number && build.stages.some(s=>s.name==='Deploy') && !captured) {
                await page.screenshot({path:path.join(output,'release-desktop.png'),fullPage:true});
                assert.ok(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),'Desktop overflow');
                await page.setViewportSize({width:390,height:844});
                await page.screenshot({path:path.join(output,'release-mobile.png'),fullPage:true});
                assert.ok(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),'Mobile overflow');
                await page.setViewportSize({width:1440,height:1050});captured=true;
            }
            if (build?.number>baseline.number && !build.building) { final=current; break; }
            await sleep(1500);
        }
        assert.ok(final,'Release timeout');
        assert.equal(final.release.build.result,'SUCCESS');
        const deploy=final.release.build.stages.find(s=>s.name==='Deploy').startedAtMs;
        const end=final.release.build.startedAtMs+final.release.build.durationMs;
        const measured=probes.filter(x=>x.at>=deploy && x.at<=end);
        const failures=measured.filter(x=>x.web!==200 || x.api!==200);
        const proof=final.release.traffic;
        const live=timeline.filter(x=>x.at>=deploy && x.at<=end);
        const duringUpdate=live.length>1 ? live.at(-1).completed-live[0].completed : 0;
        const minReady=Object.fromEntries(['catalog','gateway'].map(service=>[service,Math.min(...live.map(x=>x.pods.filter(p=>p.app===service && p.ready && !p.deletingAt).length))]));
        const report={at:new Date().toISOString(),baseline:baseline.number,build:final.release.build,traffic:proof,
            external:{requests:measured.length,errors:failures.length,failures,editions:[...new Set(measured.map(x=>x.edition))]},
            operations:{completed:final.engine.completedOperations,duringUpdate,failed:final.engine.failedOperations,pending:final.engine.pendingOperations},minReady,browserErrors:errors};
        await fs.writeFile(path.join(output,'result.json'),JSON.stringify(report,null,2));
        await fs.writeFile(path.join(output,'timeline.json'),JSON.stringify(timeline,null,2));
        assert.ok(proof.covered && proof.requests>100,'Insufficient traffic coverage');
        assert.equal(proof.errors,0,'Traffic errors during release');
        assert.ok(Object.keys(proof.versions).length>=2,'Both source revisions must serve readers');
        assert.ok(measured.length>=15,'Insufficient external probes');
        assert.equal(failures.length,0,'External Web/API interruption');
        assert.ok(final.engine.completedOperations>0,'No completed loan cycles');
        assert.ok(duringUpdate>0,'No loan cycles completed during deployment');
        assert.ok(Object.values(minReady).every(x=>x>=2),'HPA minimum replicas lost during deployment');
        assert.equal(final.engine.failedOperations,0,'Loan cycle failures');
        assert.deepEqual(errors,[]);
        console.log('PASS live release #'+report.build.number+': '+proof.requests+' traffic requests, zero errors, two revisions; '+measured.length+' external Web/API probes.');
    } finally {
        await post('stop',{immediate:false}).catch(()=>{});
        await browser.close();
    }
})().catch(error=>{console.error(error.stack);process.exitCode=1;});
