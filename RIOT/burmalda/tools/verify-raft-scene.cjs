const {chromium}=require('playwright');
const assert=require('node:assert/strict');
const fs=require('node:fs/promises');
const path=require('node:path');
const root='http://localhost:5300';
const output=path.resolve('lab-results/raft-scene');
const delay=ms=>new Promise(r=>setTimeout(r,ms));
async function api(route,body){const response=await fetch(root+'/api/raft-lab/'+route,body?{method:'POST',headers:{'content-type':'application/json'},body:JSON.stringify(body)}:{});const text=await response.text();assert.ok(response.ok,`${route}: ${response.status} ${text}`);return text?JSON.parse(text):null;}
async function until(action,test,timeout=35000){const end=Date.now()+timeout;let value;while(Date.now()<end){value=await action();if(test(value))return value;await delay(350);}throw new Error('Condition timed out: '+JSON.stringify(value).slice(0,500));}
(async()=>{
 await fs.mkdir(output,{recursive:true});
 const browser=await chromium.launch({headless:true,channel:process.env.BURMALDA_BROWSER??'msedge'});
 const checks=[],errors=[];let changed=false;
 try{
  const page=await browser.newPage({viewport:{width:1600,height:1150}});page.on('pageerror',e=>errors.push(e.message));
  await page.goto(root+'/raft-lab');const scene=page.getByTestId('raft-scene');await scene.waitFor({timeout:45000});
  await scene.getByTestId('crew-quorum').filter({hasText:'Большинство на связи'}).waitFor({timeout:30000});
  assert.equal(await scene.locator('[data-crew-node]').count(),5);checks.push('five-live-crew-and-authority');
  await scene.locator('[data-crew-node="1"]').focus();await page.keyboard.press('Enter');
  await scene.getByRole('heading',{name:/Node 1 ·/}).waitFor();checks.push('keyboard-node-inspection');
  await scene.getByRole('button',{name:'Пауза',exact:true}).click();
  await scene.getByText(/Просмотр записи — кластер продолжает работать/).waitFor();
  assert.ok(await scene.getByRole('button',{name:'Разделить сеть 3 + 2',exact:true}).isDisabled());
  assert.ok(await page.getByRole('button',{name:'Переключить primary',exact:true}).isDisabled());
  assert.ok(await page.locator('[data-node="1"]').getByRole('button',{name:'Остановить',exact:true}).isDisabled());
  const range=scene.getByRole('slider');const frozen=await range.inputValue();
  await until(()=>range.getAttribute('max'),m=>Number(m)>Number(frozen));assert.equal(await range.inputValue(),frozen);
  await scene.getByRole('button',{name:'В прямой эфир',exact:true}).click();checks.push('pause-keeps-collecting-and-blocks-all-mutations');
  if(process.argv.includes('--live')){
   changed=true;
   const commandId=crypto.randomUUID();await api('operations',{commandId,kind:'Synchronize'});
   await until(()=>api('operations/'+commandId),o=>o.status==='Completed');
   await scene.getByRole('button',{name:'Все узлы',exact:true}).click();
   await scene.getByRole('button').filter({hasText:'Большинство подтвердило запись'}).first().waitFor({timeout:15000});
   await scene.getByRole('button').filter({hasText:'Большинство подтвердило запись'}).first().click();
   await scene.getByRole('heading',{name:'Большинство подтвердило запись',exact:true}).waitFor();
   await page.screenshot({path:path.join(output,'crew-commit-desktop.png'),fullPage:true});
   await scene.getByRole('button',{name:'В прямой эфир',exact:true}).click();checks.push('real-command-commit-and-step-inspection');
   let frame=await api('visualization');let cursors=frame.nodes.map(n=>`${n.nodeId},${n.trace.runId},${n.trace.cursor}`).join(';');
   const leader=frame.nodes.find(n=>n.checkpoint?.authorityConfirmed).nodeId;
   await api('processes',{nodeId:leader,action:'Stop'});
   frame=await until(()=>api('visualization'),f=>f.nodes.some(n=>n.nodeId!==leader&&n.checkpoint?.authorityConfirmed));
   assert.equal(frame.nodes.filter(n=>n.available).length,4);
   await api('processes',{nodeId:leader,action:'Start'});
   await until(()=>api('visualization'),f=>f.nodes.every(n=>n.available)&&f.nodes.some(n=>n.checkpoint?.authorityConfirmed));
   frame=await api('visualization?cursors='+encodeURIComponent(cursors));
   assert.ok(frame.nodes.find(n=>n.nodeId===leader).trace.gap);checks.push('real-leader-failure-election-and-run-reset');
   const fresh=await api('visualization');const captain=fresh.nodes.find(n=>n.checkpoint?.authorityConfirmed).nodeId;
   const minority=[captain,captain===5?4:5];
   for(let i=1;i<=5;i++)await api('faults/'+i,{blocked:[1,2,3,4,5].filter(j=>minority.includes(i)!==minority.includes(j))});
   frame=await until(()=>api('visualization'),f=>f.nodes.some(n=>!minority.includes(n.nodeId)&&n.checkpoint?.authorityConfirmed));
   assert.ok(!frame.nodes.some(n=>minority.includes(n.nodeId)&&n.checkpoint?.authorityConfirmed));
   await scene.getByText('Связь разделена',{exact:true}).waitFor({timeout:15000});
   await page.screenshot({path:path.join(output,'crew-partition-desktop.png'),fullPage:true});checks.push('real-partition-minority-has-no-authority');
   // Collect evidence before healing; the old captain really attempted blocked RPCs.
   const trace=await api('visualization?cursors='+encodeURIComponent(frame.nodes.filter(n=>n.trace).map(n=>`${n.nodeId},${n.trace.runId},0`).join(';')));
   assert.ok(trace.nodes.flatMap(n=>n.trace?.entries??[]).some(e=>e.kind==='Blocked'));
   await fs.writeFile(path.join(output,'partition-trace.json'),JSON.stringify(trace,null,2));
   for(let i=1;i<=5;i++)await api('faults/'+i,{blocked:[]});
   await until(()=>api('visualization'),f=>f.nodes.every(n=>n.available&&n.checkpoint.blocked.length===0)&&f.nodes.some(n=>n.checkpoint.authorityConfirmed));
   checks.push('real-cluster-restored');changed=false;
  }
  const downloadEvent=page.waitForEvent('download');await page.getByRole('link',{name:'Скачать JSON состояния',exact:true}).click();
  const download=await downloadEvent;const exported=JSON.parse(await fs.readFile(await download.path(),'utf8'));
  assert.ok(exported.visualization.history.length>0);checks.push('json-includes-scene-history');
  await page.setViewportSize({width:390,height:844});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));
  await scene.scrollIntoViewIfNeeded();await page.screenshot({path:path.join(output,'crew-mobile.png'),fullPage:true});
  await page.emulateMedia({reducedMotion:'reduce'});
  assert.equal(await scene.locator('.crew-packet').first().evaluate(e=>getComputedStyle(e).display),'none');checks.push('mobile-and-reduced-motion');
  await page.route('**/api/raft-lab/visualization?**',r=>r.abort());await scene.getByRole('alert').waitFor({timeout:10000});
  await page.unroute('**/api/raft-lab/visualization?**');await until(()=>scene.getByRole('alert').count(),n=>n===0);checks.push('transport-outage-and-observation-recovery');
  // Deterministic state fixtures: two local leaders, only one fresh authority; then no quorum.
  let fixture=await api('visualization');fixture.nodes=fixture.nodes.map(n=>({...n,trace:null,checkpoint:{...n.checkpoint,authorityConfirmed:n.nodeId===3,state:{...n.checkpoint.state,role:[1,3].includes(n.nodeId)?'Leader':'Follower',term:80}}}));
  await page.route('**/api/raft-lab/visualization?**',r=>r.fulfill({json:fixture}));
  await until(()=>scene.locator('.crew-authority').count(),n=>n===1);
  await until(()=>scene.locator('[data-crew-node="1"]').getAttribute('aria-label'),s=>s.includes('капитан'));
  await until(()=>scene.locator('[data-crew-node="3"]').getAttribute('aria-label'),s=>s.includes('капитан'));
  fixture={...fixture,nodes:fixture.nodes.map(n=>({...n,checkpoint:{...n.checkpoint,authorityConfirmed:false}}))};
  await scene.getByTestId('crew-quorum').filter({hasText:'Нет большинства'}).waitFor();
  assert.ok(await scene.getByRole('button',{name:'Возобновить и дождаться WAL',exact:true}).isDisabled());checks.push('two-local-captains-and-no-quorum-fixtures');
  await page.unroute('**/api/raft-lab/visualization?**');
  assert.deepEqual(errors,[]);
  await fs.writeFile(path.join(output,'verification.json'),JSON.stringify({verifiedAt:new Date().toISOString(),checks,errors},null,2));
  console.log('PASS '+checks.length+' scene checks: '+checks.join(', '));
 }finally{
  if(changed){for(let i=1;i<=5;i++){try{await api('processes',{nodeId:i,action:'Start'});await api('faults/'+i,{blocked:[]});}catch(e){console.error('Restore Node '+i+': '+e.message);}}}
  await browser.close();
 }
})().catch(e=>{console.error(e);process.exitCode=1;});
