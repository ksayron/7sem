const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const browser = await chromium.launch({headless:true, channel:'msedge'});
  const out = path.resolve(__dirname, '../artifacts');
  fs.mkdirSync(out, {recursive:true});
  const checks = [];
  try {
    for (const [port, policy] of [[8101,'sticky'], [8102,'round-robin']]) {
      const context = await browser.newContext({viewport:{width:1440,height:1100}});
      const page = await context.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      await page.goto(`http://localhost:${port}/`);
      const click = async (selector, expected) => {
        await Promise.all([page.waitForResponse(response => response.url().includes('/stack')), page.locator(selector).click()]);
        await page.waitForFunction(value => document.getElementById('result').textContent === String(value) && !document.querySelector('button').disabled, expected);
      };
      await click('[data-method="GET"]',0);
      await click('#post-form button',10);
      await click('#put-form button',15);
      await page.locator('#add').fill('8');
      await click('#put-form button',18);
      await click('[data-method="GET"]',18);
      const instances = await page.locator('#journal [data-instance]').allTextContents();
      assert.equal(instances.length,5);
      if (policy === 'sticky') assert.equal(new Set(instances).size,1);
      else assert.ok(instances.every((value,index) => index === 0 || value !== instances[index-1]));
      assert.deepEqual(await page.locator('#stack span').allTextContents(),['5','8']);
      await page.screenshot({path:path.join(out,`${policy}-desktop.png`),fullPage:true});
      await click('[data-method="DELETE"]',15);
      await click('[data-method="DELETE"]',10);
      await page.locator('[data-method="DELETE"]').click();
      await page.waitForFunction(() => document.getElementById('status').textContent.includes('409'));
      assert.equal(await page.locator('#result').textContent(),'10');
      await page.setViewportSize({width:390,height:844});
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      await page.screenshot({path:path.join(out,`${policy}-mobile.png`),fullPage:true});
      assert.deepEqual(errors,[]);
      checks.push({policy,passed:true,instances,viewportWidths:[1440,390]});
      await context.close();
    }
    fs.writeFileSync(path.join(out,'browser-verification.json'),JSON.stringify({passed:true,checks},null,2));
    console.log('[ok] Both gateways: controls, RESULT, stack, routing, error display, desktop and mobile widths.');
  } finally {await browser.close();}
})().catch(error => {console.error(error);process.exitCode=1;});
