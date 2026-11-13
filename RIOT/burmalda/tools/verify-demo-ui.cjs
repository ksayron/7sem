const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const {chromium}=require(process.env.PLAYWRIGHT_MODULE??path.resolve('.artifacts/browser/node_modules/playwright'));
const base=process.env.BURMALDA_DEMO_URL??'http://localhost:5310';
(async()=>{
    const browser=await chromium.launch({headless:true,channel:'msedge'});
    const errors=[],checks=[];
    try{
        const context=await browser.newContext({viewport:{width:1440,height:1000},reducedMotion:'reduce'});
        const page=await context.newPage();page.on('pageerror',e=>errors.push(e.message));
        await page.goto(base);await page.getByLabel('Электронная почта').waitFor();
        const secret=JSON.parse(await fs.readFile('.artifacts/k8s/secret.json','utf8'));
        await page.getByLabel('Электронная почта').fill('admin@burmalda.local');
        await page.keyboard.press('Tab');assert.equal(await page.locator(':focus').getAttribute('id'),'password');
        await page.keyboard.type(secret.stringData.ADMIN_PASSWORD);await page.keyboard.press('Tab');await page.keyboard.press('Enter');
        await page.waitForURL(base+'/');await page.getByRole('heading',{name:'Сценарий',exact:true}).waitFor();
        checks.push('existing-admin-login-by-keyboard');
        const csrf=await context.request.post(base+'/api/demo/target',{data:{clients:0},headers:{Authorization:'Bearer invalid'}});
        assert.equal(csrf.status(),400);checks.push('invalid-bearer-cannot-bypass-cookie-csrf');
        await page.waitForTimeout(2200);await fs.mkdir('lab-results/demo-verification',{recursive:true});
        await page.screenshot({path:'lab-results/demo-verification/pult-desktop.png',fullPage:true});
        assert.ok(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth));
        await page.setViewportSize({width:390,height:844});
        await page.screenshot({path:'lab-results/demo-verification/pult-mobile.png',fullPage:true});
        assert.ok(!await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth));
        checks.push('desktop-and-mobile-without-page-overflow');
        assert.ok(await page.getByRole('button',{name:'Запустить сценарий',exact:true}).isVisible());
        checks.push('mobile-controls-visible');assert.deepEqual(errors,[]);checks.push('no-browser-runtime-errors');
        await fs.writeFile('lab-results/demo-verification/ui.json',JSON.stringify({at:new Date().toISOString(),base,checks,errors},null,2));
        console.log(checks.map(x=>'PASS '+x).join('\n'));
    }finally{await browser.close();}
})().catch(error=>{console.error(error.message);process.exitCode=1;});
