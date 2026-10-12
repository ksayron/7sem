const { spawn } = require('node:child_process');
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
(async () => {
    const cases = [];
    const directory = path.resolve('src/Services/ConcurrencyLab/Burmalda.ConcurrencyLab.Api');
    for (const [port, database, label] of [[5108,'Host=localhost;Port=1;Database=concurrency_lab_db;Username=invalid;Password=invalid;Timeout=2','unavailable-database'], [5109,'Host=localhost;Port=5547;Database=concurrency_lab_db;Username=lab_invalid_credentials;Password=invalid;Timeout=2','invalid-database-credentials']]) {
        const child = spawn('dotnet', ['bin/Debug/net9.0/Burmalda.ConcurrencyLab.Api.dll','--urls',`http://localhost:${port}`], { cwd: directory, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', ConnectionStrings__Database: database }, windowsHide: true, stdio: 'ignore' });
        try {
            for (let i=0;i<50;i++) { try { if ((await fetch(`http://localhost:${port}/health/ready`)).ok) break; } catch {} await new Promise(r=>setTimeout(r,100)); }
            for (let repeat=0;repeat<2;repeat++) {
                const response = await fetch(`http://localhost:${port}/api/concurrency-lab/database/runs`, {method:'POST',headers:{'Content-Type':'application/json'},body:'{"mode":1}'});
                assert.equal(response.status,503); assert.match((await response.json()).message,/База данных/);
            }
            assert.equal((await fetch(`http://localhost:${port}/health/ready`)).status,200);
            const memory = await fetch(`http://localhost:${port}/api/concurrency-lab/experiments`, {method:'POST',headers:{'Content-Type':'application/json'},body:'{"mode":1,"plannedRequests":2,"workDelayMs":10}'});
            assert.equal(memory.status,201);
            cases.push({label,httpStatus:503,repeated:2,serverSurvived:true,memoryLabAvailable:true});
        } finally { child.kill(); }
    }
    const browser = await chromium.launch({headless:true,channel:process.env.BURMALDA_BROWSER??'msedge'});
    try {
        const page=await browser.newPage(); await page.goto('http://localhost:5300/concurrency-lab');
        await page.getByRole('heading',{name:/Одна книга/}).waitFor();
        await page.route('**/api/concurrency-lab/database/**',route=>route.abort('connectionrefused'));
        await page.getByRole('button',{name:'Запустить опыт →',exact:true}).click();
        await page.locator('.database-lab [role="alert"]').waitFor();
        cases.push({label:'unavailable-server',browserErrorShown:true});
        await page.unroute('**/api/concurrency-lab/database/**');
        await page.route('**/api/concurrency-lab/database/runs',route=>route.fulfill({status:503,contentType:'application/json',body:'{"message":"База данных недоступна"}'}));
        await page.getByRole('button',{name:'Запустить опыт →',exact:true}).click();
        await page.locator('.database-lab [role="alert"]').filter({hasText:'База данных недоступна'}).waitFor();
        cases.push({label:'database-error-ui',browserErrorShown:true});
    } finally { await browser.close(); }
    const folder=path.resolve('lab-results/errors-2026-10-05');await fs.mkdir(folder,{recursive:true});
    await fs.writeFile(path.join(folder,'evidence.json'),JSON.stringify({verifiedAtUtc:new Date().toISOString(),cases},null,2));
    console.log(`[info] Error handling verified: ${folder}`);
})().catch(e=>{console.error(e);process.exitCode=1;});
