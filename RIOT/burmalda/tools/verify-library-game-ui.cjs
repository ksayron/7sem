// Browser acceptance tests only; the game and Pub/Sub implementation are C#.
const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
(async()=>{
 const folder=path.resolve('lab-results/pubsub-ui');await fs.mkdir(folder,{recursive:true});
 const browser=await chromium.launch({headless:process.env.BURMALDA_HEADED!=='1',channel:process.env.BURMALDA_BROWSER??'msedge'});
 const context=await browser.newContext({viewport:{width:1920,height:1080}});
 const page=await context.newPage(),errors=[],checks=[];
 page.on('pageerror',e=>errors.push(e.message));
 const check=(v,name)=>{assert.ok(v,name);checks.push(name);console.log('PASS '+name);};
 async function hold(key,ms){await page.keyboard.down(key);await page.waitForTimeout(ms);await page.keyboard.up(key);await page.waitForTimeout(100);}
 try {
  const account=JSON.parse(await fs.readFile('.artifacts/library-game/browser-account.json','utf8'));
  assert.ok(account.email.startsWith('lr6-browser-'),'Only the dedicated disposable browser account is reset');
  const fixtureLogin=await context.request.post('http://localhost:5000/api/identity/login',{data:{email:account.email,password:account.password}});
  const fixtureAuth={Authorization:'Bearer '+(await fixtureLogin.json()).accessToken};
  for(const demo of [false,true]) {
   const fixtureState=await (await context.request.get('http://localhost:5000/api/library-game/state?demo='+demo,{headers:fixtureAuth})).json();
   assert.ok((await context.request.post('http://localhost:5000/api/library-game/reset?demo='+demo,{headers:fixtureAuth,data:{sessionId:fixtureState.state.sessionId}})).ok());
  }
  await page.goto('http://localhost:5300/login');
  await page.getByLabel('Email address').fill(account.email);await page.getByLabel('Password',{exact:true}).fill(account.password);
  await page.getByRole('button',{name:'Sign in',exact:true}).click();await page.waitForURL('**/dashboard');
  await page.getByRole('link',{name:'Тихая библиотека',exact:true}).click();
  const enter=page.getByTestId('enter-library');await enter.waitFor();await assert.doesNotReject(()=>enter.click({timeout:60000}));
  await page.waitForFunction(()=>!!document.pointerLockElement);
  check(await page.locator('canvas').count()===1,'one-blazor-canvas-and-pointer-lock');
  const before=await page.getByTestId('game-surface').getAttribute('data-position');
  await hold('w',450);const moved=await page.getByTestId('game-surface').getAttribute('data-position');check(before!==moved,'real-wasd-updates-csharp-camera');
  // Real mouse input changes the view; reset through a small opposite move.
  await page.mouse.move(700,450);await page.mouse.move(720,450);await page.mouse.move(700,450);await page.waitForTimeout(120);
  await page.keyboard.press('Escape');await enter.waitFor();check(await page.getByTestId('game-pause').isVisible(),'escape-releases-pointer-and-pauses');
  // Re-enter a fresh component so starting pose is deterministic after the mouse check.
  await page.getByRole('link',{name:'ЛР №6: Pub/Sub',exact:true}).click();await page.getByRole('heading',{name:'Независимые подписчики',exact:true}).waitFor();
  check(await page.locator('canvas').count()===0,'navigation-disposes-canvas');
  await page.getByRole('link',{name:'Открыть 3D-библиотеку',exact:true}).click();await enter.click({timeout:60000});await page.waitForFunction(()=>!!document.pointerLockElement);
  await hold('d',110);await hold('w',430);await hold('ArrowDown',450);
  let found=false;
  for(let i=0;i<12;i++){
   const text=await page.getByTestId('target-label').innerText();
   if(text.includes('том')){found=true;break;}
   await hold('ArrowDown',45);
  }
  check(found,'csharp-ray-picking-finds-book-using-real-input');
  await page.keyboard.press('e');await page.waitForFunction(()=>document.querySelector('.library-hand')?.textContent.includes('том'));
  check(true,'real-e-picks-book-through-authenticated-api');
  // Level the view and walk toward the shelf; ray checks still enforce reach.
  await hold('ArrowUp',750);await hold('w',950);
  let shelf=false;
  for(let i=0;i<12;i++){
   if((await page.getByTestId('target-label').innerText()).includes('полка «')){shelf=true;break;}
   await hold('ArrowDown',65);
  }
  check(shelf,'csharp-ray-picking-finds-correct-shelf');
  await page.keyboard.press('e');await page.waitForFunction(()=>document.querySelector('[data-testid="sorted-count"]')?.textContent.trim()==='1');
  check(true,'real-input-completes-pick-and-shelve');
  await page.keyboard.press('Escape');await enter.waitFor();
  await page.getByRole('button',{name:/Улучшения ·/}).click();await page.getByRole('heading',{name:'Маленькие помощники'}).waitFor();
  check(await page.getByRole('button',{name:/Изучить · 4 искр/}).isDisabled(),'upgrade-insufficient-funds-state');
  await page.getByRole('button',{name:/Достижения ·/}).click();await page.getByRole('heading',{name:'Ваша история'}).waitFor();
  await page.waitForFunction(()=>document.querySelector('.library-achievement.unlocked'));
  check(true,'asynchronous-achievement-appears-in-blazor-hud');
  await enter.click();await page.waitForFunction(()=>!!document.pointerLockElement);
  await hold('s',900);await hold('ArrowRight',450);await hold('ArrowDown',120);
  await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:path.join(folder,'library-1920.png'),fullPage:true});
  await page.keyboard.press('Escape');
  await page.setViewportSize({width:1280,height:720});await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:path.join(folder,'library-1280.png'),fullPage:true});
  check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'1280-no-horizontal-overflow');
  await page.reload();await enter.waitFor({timeout:60000});
  check((await page.getByTestId('sorted-count').innerText()).trim()==='1','save-restored-after-browser-reload');
  await page.getByRole('link',{name:'ЛР №6: Pub/Sub',exact:true}).click();
  await page.getByRole('heading',{name:'Независимые подписчики',exact:true}).waitFor();
  await page.getByRole('button',{name:'Расставить одну книгу',exact:true}).click();await page.waitForFunction(()=>document.querySelector('.ps-session')?.textContent.includes('1 / 72'));
  await page.getByRole('button',{name:'Задержки 1 / 3 / 2 секунды',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('tbody')?.textContent.includes('3000 мс'));
  const logger=page.locator('tr').filter({has:page.getByText('EventLogger',{exact:true})}).first();await logger.getByRole('button',{name:'Отключить',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('tbody')?.textContent.includes('UNAVAILABLE'));
  await logger.getByRole('button',{name:'Восстановить',exact:true}).click();
  await page.getByRole('button',{name:'Подключить AnalyticsSubscriber',exact:true}).click();
  check(true,'lab-controls-delay-failure-and-dynamic-subscription');
  await page.getByRole('combobox',{name:'Событие',exact:true}).selectOption('BookShelved');
  await page.waitForTimeout(2000);check((await page.locator('.ps-journal tbody').innerText()).includes('BookShelved'),'event-journal-filter');
  const [download]=await Promise.all([page.waitForEvent('download'),page.getByRole('link',{name:'Скачать JSON',exact:true}).click()]);
  await download.saveAs(path.join(folder,'lab-export.json'));check(JSON.parse(await fs.readFile(path.join(folder,'lab-export.json'),'utf8')).state.demo,'journal-export-is-demo-only');
  await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:path.join(folder,'lab-1280.png'),fullPage:true});
  await page.setViewportSize({width:1920,height:1080});await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:path.join(folder,'lab-1920.png'),fullPage:true});
  await page.setViewportSize({width:390,height:844});await page.getByRole('link',{name:'Открыть 3D-библиотеку',exact:true}).click();await enter.waitFor({timeout:60000});
  check(await page.getByText('Для прогулки нужны клавиатура и мышь.',{exact:false}).isVisible(),'narrow-screen-keyboard-and-mouse-guidance');
  check(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'390-no-horizontal-overflow');
  await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:path.join(folder,'library-390.png'),fullPage:true});
  check(errors.length===0,'no-browser-runtime-errors');
  await fs.writeFile(path.join(folder,'verification.json'),JSON.stringify({checks,errors,at:new Date().toISOString()},null,2));
  await fs.rm(path.join(folder,'failure.json'),{force:true});await fs.rm(path.join(folder,'failure.png'),{force:true});
 }catch(e){await page.evaluate(()=>window.scrollTo(0,0));await page.screenshot({path:path.join(folder,'failure.png'),fullPage:true});await fs.writeFile(path.join(folder,'failure.json'),JSON.stringify({checks,errors,error:e.stack},null,2));throw e;}finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

