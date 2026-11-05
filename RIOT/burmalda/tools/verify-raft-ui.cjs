const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
(async()=>{
    const folder=path.resolve('lab-results/raft-verification');await fs.mkdir(folder,{recursive:true});
    const browser=await chromium.launch({headless:true,channel:process.env.BURMALDA_BROWSER??'msedge'});
    const errors=[],checks=[];
    try{
        const page=await browser.newPage({viewport:{width:1440,height:1000}});
        page.on('pageerror',e=>errors.push(e.message));
        await page.goto('http://localhost:5300/raft-lab');
        await page.getByTestId('raft-nodes').waitFor({timeout:45000});
        assert.equal(await page.locator('[data-node]').count(),5);checks.push('five-real-nodes-visible');
        await page.getByTestId('raft-quorum').filter({hasText:'Свежий обмен'}).waitFor({timeout:30000});
        await page.getByRole('heading',{name:'PostgreSQL: фактическое состояние'}).waitFor();
        await page.getByTestId('raft-log').waitFor();checks.push('quorum-postgres-log-and-operation-panels');
        if(process.argv.includes('--controls')){
            const row=page.locator('[data-node="5"]');
            await row.getByRole('button',{name:'Остановить',exact:true}).click();
            await row.getByRole('button',{name:'Запустить',exact:true}).waitFor({timeout:30000});
            await row.getByRole('button',{name:'Запустить',exact:true}).click();
            await row.getByRole('button',{name:'Остановить',exact:true}).waitFor({timeout:30000});
            await page.getByTestId('raft-quorum').filter({hasText:'Свежий обмен'}).waitFor({timeout:30000});
            checks.push('real-process-stop-and-restart-through-ui');
        }
        const download=await page.getByRole('link',{name:'Скачать JSON состояния',exact:true}).getAttribute('href');
        const exported=JSON.parse(Buffer.from(download.split(',')[1],'base64').toString());
        assert.equal((exported.snapshot.Nodes??exported.snapshot.nodes).length,5);checks.push('json-export-includes-source-observations');
        await page.getByRole('button',{name:'Обновить наблюдения',exact:true}).focus();
        assert.equal(await page.getByRole('button',{name:'Обновить наблюдения',exact:true}).evaluate(e=>e===document.activeElement),true);checks.push('keyboard-focus');
        await page.screenshot({path:path.join(folder,'raft-desktop.png'),fullPage:true});
        await page.setViewportSize({width:390,height:844});
        assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
        await page.screenshot({path:path.join(folder,'raft-mobile.png'),fullPage:true});checks.push('mobile-no-page-overflow');
        await page.route('**/api/raft-lab/status',r=>r.abort());
        await page.getByRole('button',{name:'Обновить наблюдения',exact:true}).click();
        await page.getByRole('alert').waitFor();checks.push('network-error-visible');
        assert.deepEqual(errors,[]);
        await fs.writeFile(path.join(folder,'ui-verification.json'),JSON.stringify({verifiedAtUtc:new Date().toISOString(),checks,pageErrors:errors},null,2));
        console.log(`PASS ${checks.length} UI checks`);
    }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
