// Full Compose acceptance. Uses labelled new fixtures and never removes volumes.
const assert = require('node:assert/strict');
const {spawnSync} = require('node:child_process');
const {randomUUID} = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');
const folder = path.resolve(process.env.BURMALDA_RESULT_DIR ?? 'lab-results/docker-verification');
const base = process.env.BURMALDA_WEB_URL ?? 'http://localhost:5300';
const checks = [], evidence = {fixtures: {}};
const delay = ms => new Promise(r => setTimeout(r, ms));
const mark = name => { checks.push(name); console.log('PASS ' + name); };
function docker(...args) {
    const result = spawnSync('docker', args, {encoding:'utf8', timeout:240000, windowsHide:true});
    assert.equal(result.status, 0, result.stderr || result.stdout); return result.stdout.trim();
}
async function request(route, body, token, expected = 200) {
    const response = await fetch(base + route, {method:body === undefined ? 'GET' : 'POST', headers:{'Content-Type':'application/json', ...(token ? {Authorization:'Bearer ' + token} : {})},
        body:body === undefined ? undefined : JSON.stringify(body), signal:AbortSignal.timeout(45000)});
    const text = await response.text();
    assert.equal(response.status, expected, `${route}: ${text}`);
    return text ? JSON.parse(text) : null;
}
async function wait(probe, description, timeout = 150000) {
    const end = Date.now() + timeout; let last;
    while (Date.now() < end) {
        try { const result = await probe(); if (result) return result; } catch(e) { last = e; }
        await delay(500);
    }
    throw new Error(description + ': ' + (last?.message ?? 'deadline exceeded'));
}
async function cluster() {
    return wait(async () => {const s = await request('/api/raft-lab/status');return s.nodes.filter(n=>n.available).length === 5 && s.authoritative && s.state?.initialized && s;}, 'five ready nodes');
}
async function user(tag) {
    const credentials = {email:`docker-${tag}-${randomUUID()}@example.test`, password:`Verify-${randomUUID()}!`};
    await request('/api/identity/register', {...credentials, displayName:'Docker verifier ' + tag}, null, 201);
    return request('/api/identity/login', credentials);
}
async function sameFixture(token) {
    const book = await wait(() => request('/api/catalog/books/' + evidence.fixtures.bookId, undefined, token), 'fixture read after restart');
    assert.equal(book.id, evidence.fixtures.bookId);
    const s = await cluster(); assert.ok(s.authoritative.node.commitIndex >= evidence.before.commitIndex);
    assert.ok(s.authoritative.node.term >= evidence.before.term);
    assert.equal(s.state.operations[evidence.commandId].status, 'Completed');
}
async function operate(kind, node) {
    const commandId = randomUUID(); await request('/api/raft-lab/operations', {commandId, kind, node}, null, 202);
    await wait(async()=>{const s = await request('/api/raft-lab/status');const op=s.state?.operations[commandId];if(op?.status==='NeedsInspection'||op?.status==='Failed')throw Error(JSON.stringify(op));return op?.status==='Completed';},'operation ' + kind);
    return commandId;
}
(async()=>{
    await fs.mkdir(folder,{recursive:true});
    const config = JSON.parse(docker('compose','--profile','pubsub','config','--format','json'));
    for (let id=1; id<=5; id++) assert.ok(config.services['raft-node-'+id]);
    assert.deepEqual(config.services['library-game'].profiles, ['pubsub']);
    mark('default-compose-and-optional-pubsub-profile');
    let s = await cluster();
    assert.equal(s.majority,3); assert.equal(s.nodes.filter(n=>n.state?.role==='Leader').length,1);
    for (const route of ['/raft-lab','/replication-lab','/discover']) {const r=await fetch(base+route);assert.equal(r.status,200);assert.match(await r.text(), /blazor.webassembly.js/);}
    mark('five-containers-one-leader-and-spa-deep-links');
    const owner = await user('owner'), borrower = await user('borrower');
    await wait(async()=>{const r=await request('/api/economy/wallet',undefined,borrower.accessToken);return r.balance>=10;},'borrower wallet');
    const tag = 'docker-delivery-' + randomUUID();
    const book = await request('/api/catalog/books', {isbn:tag,title:tag,authors:['Docker verifier'],genres:['Lab'],description:'LR7+8 acceptance fixture',publicationYear:2026},owner.accessToken,201);
    evidence.fixtures.bookId = book.id;
    const copy = await request('/api/catalog/copies',{bookId:book.id,condition:2,lendingPrice:10,maximumLoanDays:14,location:'Docker lab'},owner.accessToken,201);
    evidence.fixtures.copyId = copy.id;
    const receipt=await request('/api/loans/requests',{copyId:copy.id},borrower.accessToken,202);
    const saga=await wait(async()=>{const x=await request('/api/loans/requests/'+receipt.id,undefined,borrower.accessToken);return x.state===2&&x;},'loan saga');
    evidence.fixtures.loanId=saga.loanId;
    const accepted=await request('/api/loans/'+saga.loanId+'/accept',{},owner.accessToken);
    await request('/api/loans/'+saga.loanId+'/handover/confirm',{code:accepted.handoverCode},borrower.accessToken);
    await request('/api/loans/'+saga.loanId+'/return',{},borrower.accessToken);
    const returned=await request('/api/loans/'+saga.loanId+'/return/confirm',{},owner.accessToken);
    assert.equal(returned.status,7);
    await wait(async()=>{const copies=await request('/api/catalog/books/'+book.id+'/copies',undefined,owner.accessToken);return copies.some(c=>c.id===copy.id);},'returned copy available');
    mark('register-login-book-loan-handover-return-and-async-projections');
    await request('/api/raft-lab/processes',{nodeId:9,action:'Stop'},null,400);
    await request('/api/raft-lab/operations',{commandId:randomUUID(),kind:'invalid'},null,400);
    mark('invalid-node-and-operation-requests-are-rejected');
    try {
        docker('compose','stop','catalog');
        const r=await fetch(base+'/api/catalog/books/'+book.id,{headers:{Authorization:'Bearer '+owner.accessToken}});
        assert.equal(r.status,503);assert.ok((await r.json()).message);
    } finally { docker('compose','start','catalog'); }
    await wait(()=>request('/api/catalog/books/'+book.id,undefined,owner.accessToken),'catalog recovery');
    mark('service-outage-has-readable-503-and-recovers');
    evidence.commandId=await operate('Synchronize');
    s=await cluster();evidence.before={commitIndex:s.authoritative.node.commitIndex,term:s.authoritative.node.term};
    docker('compose','restart'); await sameFixture(owner.accessToken);mark('restart-preserves-business-data-and-raft-history');
    if(process.argv.includes('--recreate')){
        docker('compose','up','-d','--no-build','--force-recreate'); await sameFixture(owner.accessToken);
        mark('container-recreation-preserves-business-data-and-raft-history');
        docker('compose','--profile','pubsub','down');
        docker('compose','up','-d','--no-build'); await sameFixture(owner.accessToken);
        mark('down-up-without-volume-removal-preserves-data-and-raft-history');
    }
    const limiter=await user('limiter');
    const responses=await Promise.all(Array.from({length:130},()=>fetch(base+'/api/catalog/books/'+book.id,{headers:{Authorization:'Bearer '+limiter.accessToken}})));
    evidence.rateLimitStatuses=responses.reduce((counts,r)=>{counts[r.status]=(counts[r.status]??0)+1;return counts;},{});
    assert.ok(responses.some(r=>r.status===200));assert.ok(responses.some(r=>r.status===429));
    assert.ok(responses.every(r=>r.status===200||r.status===429));mark('gateway-authenticated-rate-limiting');
    evidence.final=await cluster(); evidence.success=true;
})().catch(e=>{evidence.failure=e.stack;console.error(e);process.exitCode=1;}).finally(async()=>{
    await fs.mkdir(folder,{recursive:true});
    await fs.writeFile(path.join(folder,'integration.json'),JSON.stringify({verifiedAtUtc:new Date().toISOString(),checks,...evidence},null,2));
    console.log(`${checks.length} Docker acceptance checks; success=${!!evidence.success}`);
});
