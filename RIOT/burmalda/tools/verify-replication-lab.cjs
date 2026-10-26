// Integration evidence uses real Docker containers and temporary Catalog rows, never runtime seeds.
const assert = require('node:assert/strict');
const { spawnSync } = require('node:child_process');
const { randomUUID, createHmac } = require('node:crypto');
const fs = require('node:fs/promises');
const path = require('node:path');
const base = (process.env.BURMALDA_LAB_URL ?? 'http://localhost:5000') + '/api/concurrency-lab/replication/runs';
const folder = path.resolve('lab-results/streaming-replication-verification');
const checks = [], evidence = {};
const check = name => { checks.push(name); console.log(`[info] ${name}`); };
function docker(args, input, allowFailure = false) {
    const r = spawnSync('docker', args, { input, encoding: 'utf8', timeout: 180000, windowsHide: true });
    if (!allowFailure && r.status !== 0) throw new Error(`Docker exit ${r.status}: ${r.stderr}`);
    return r;
}
const compose = JSON.parse(docker(['compose', 'config', '--format', 'json']).stdout);
const admin = compose.services.postgres.environment.POSTGRES_USER;
const sql = (node, text, allowFailure = false) => docker(['compose', 'exec', '-T', node, 'psql', '-X', '-U', admin, '-d', 'catalog_db', '-At', '-v', 'ON_ERROR_STOP=1'], text, allowFailure);
async function request(url, body, status = 200) {
    const response = await fetch(url, body === undefined ? {} : { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) });
    const result = await response.json(); assert.equal(response.status, status, JSON.stringify(result)); return result;
}
const create = () => request(base, {});
const post = (run, route, body, status = 200) => request(`${base}/${run.id}/${route}`, body, status);
const get = run => request(`${base}/${run.id}`);
const writer = run => run.nodes.find(n => n.id === run.activeInstance);
const standby = run => run.nodes.find(n => n.role === 'Standby' && n.state === 'AVAILABLE');
async function settled(run, expected = 'Completed') {
    const deadline = Date.now() + 360000;
    while (Date.now() < deadline) {
        run = await get(run);
        if (run.actionStatus !== 'Running' && !run.clusterBusy) { assert.equal(run.actionStatus, expected, run.actionError); return run; }
        await new Promise(r => setTimeout(r, 700));
    }
    throw new Error('Cluster action did not finish; inspect journal before recovery.');
}
const control = async (run, node, available) => settled(await post(run, `instances/${node}`, { available }, 202));
async function aligned(run) {
    for(let attempt=0;attempt<60;attempt++) {
        run=await get(run); if(run.consistent) return run;
        await new Promise(r=>setTimeout(r,300));
    }
    throw new Error('No aligned PostgreSQL observation within the verification deadline.');
}
const sync = async run => aligned(await settled(await post(run, 'synchronize', {}, 202)));
async function catalogRead(bookId) {
    const config = JSON.parse(await fs.readFile('src/Services/Catalog/Burmalda.Catalog.Api/appsettings.json', 'utf8'));
    const enc = obj => Buffer.from(JSON.stringify(obj)).toString('base64url');
    const payload = `${enc({ alg: 'HS256', typ: 'JWT' })}.${enc({ iss: config.Jwt.Issuer, aud: config.Jwt.Audience, sub: randomUUID(), role: 'Admin', exp: Math.floor(Date.now()/1000)+3600 })}`;
    const token = `${payload}.${createHmac('sha256', config.Jwt.SigningKey).update(payload).digest('base64url')}`;
    const r = await fetch(`http://localhost:5102/api/catalog/books/${bookId}`, { headers: { Authorization: `Bearer ${token}` } });
    assert.equal(r.status, 200, 'Start-Lab.ps1 -Full is required for service reconnection coverage');
    assert.equal((await r.json()).id, bookId);
}
const bookId = randomUUID(), copyId = randomUUID(), ownerId = randomUUID();
let fixtureCreated = false, current, success = false;
(async () => {
    await fs.mkdir(folder, { recursive: true });
    current = await aligned(await create()); assert.equal(current.consistent, true);
    assert.equal(writer(current).role, 'Primary'); assert.equal(standby(current).readOnly, true);
    evidence.initial = current; check('two-actual-postgres-nodes-and-streaming');
    const initialWriter = current.activeInstance;
    evidence.initialCounts = { books: writer(current).books.length, copies: writer(current).copies.length };
    const readonly = sql(standby(current).id, 'BEGIN; CREATE TABLE lr5_should_be_readonly(id int); ROLLBACK;', true);
    assert.notEqual(readonly.status, 0); assert.match(readonly.stderr, /read-only/); check('standby-rejects-sql-writes');
    sql(initialWriter, `BEGIN;
      INSERT INTO books ("Id","Isbn","Title","Description","AuthorsJson","GenresJson","CreatedAt") VALUES ('${bookId}','lr5-${bookId}','LR5 SQL fixture ${bookId}','','["SQL verifier"]','[]',now());
      INSERT INTO book_copies ("Id","BookId","OwnerId","Condition","Status","LendingPrice","MaximumLoanDays","Location","IsHidden","Version","CreatedAt") VALUES ('${copyId}','${bookId}','${ownerId}',0,0,10,7,'LR5 verifier',false,1,now());
      INSERT INTO book_search_projection ("BookId","Isbn","Title","AuthorsText","GenresText","AvailableCopies","MinimumPrice","UpdatedAt") VALUES ('${bookId}','lr5-${bookId}','LR5 SQL fixture ${bookId}','SQL verifier','',1,10,now()); COMMIT;`);
    fixtureCreated = true; current = await sync(current);
    assert.equal(writer(current).copies.find(c => c.id === copyId).version, 1);
    assert.equal(standby(current).books.find(b => b.id === bookId).available, 1);
    await catalogRead(bookId); check('external-catalog-rows-discovered-and-visible-through-catalog-api');
    const update = (version, available, status = 200) => post(current, 'update', { clientId: 'SQL Client', copyId, available, expectedVersion: version }, status);
    current = await update(1, false); assert.equal(current.operations.at(-1).committed, true);
    assert.equal(sql(current.activeInstance, `SELECT "AvailableCopies" FROM book_search_projection WHERE "BookId"='${bookId}';`).stdout.trim(), '0');
    await update(1, true, 409); check('transactional-availability-projection-and-stale-version-conflict');
    // Two simultaneous requests hold the same expected version; only one may commit.
    const responses = await Promise.all([1,2].map(i => fetch(`${base}/${current.id}/update`, { method:'POST', headers:{'content-type':'application/json'}, body:JSON.stringify({clientId:`Parallel ${i}`,copyId,available:true,expectedVersion:2}) })));
    assert.deepEqual(responses.map(r => r.status).sort(), [200,409]); current = await sync(current); check('concurrent-clients-no-lost-update');
    sql(current.activeInstance, `UPDATE book_copies SET "Status"=1,"Version"="Version"+1 WHERE "Id"='${copyId}';`);
    await update(4, false, 409);
    sql(current.activeInstance, `UPDATE book_copies SET "Status"=2,"Version"="Version"+1 WHERE "Id"='${copyId}';`);
    await update(5, false, 409);
    sql(current.activeInstance, `UPDATE book_copies SET "Status"=0,"Version"="Version"+1 WHERE "Id"='${copyId}';`);
    current = await sync(current); check('reserved-and-on-loan-copies-protected');
    current = await post(current, 'replication', { enabled:false });
    let version = writer(current).copies.find(c=>c.id===copyId).version;
    current = await update(version, false); assert.equal(current.operations.at(-1).replication,'PENDING');
    const old = current.activeInstance; current = await control(current, old, false);
    await post(current, 'read', { clientId:'Client 2' },409);
    assert.equal(sql(standby(current).id,'SELECT pg_is_in_recovery();').stdout.trim(),'t');
    const guardedStart = docker(['compose','start',old]);
    await new Promise(r=>setTimeout(r,1000));
    const nodeId = docker(['compose','ps','-aq',old]).stdout.trim();
    assert.notEqual(docker(['inspect','--format','{{.State.Health.Status}}',nodeId]).stdout.trim(),'healthy');
    current = await control(current, old, true); current = await sync(current); check('paused-replay-stale-promotion-rejected-and-startup-fence-enforced');
    version = writer(current).copies.find(c=>c.id===copyId).version; current = await update(version,true); current = await sync(current);
    for(let cycle=1;cycle<=2;cycle++) {
        const before = current.activeInstance;
        current = await aligned(await settled(await post(current,'cycle',{copyId},202)));
        assert.notEqual(current.activeInstance,before); assert.equal(current.consistent,true);
        assert.equal(writer(current).copies.find(c=>c.id===copyId).status,'Available');
        assert.equal(current.nodes.filter(n=>n.role==='Primary').length,1);
        await catalogRead(bookId); evidence[`cycle${cycle}`]=current;
        check(`real-stop-promote-write-pg-rewind-cycle-${cycle}-and-catalog-reconnect`);
    }
    const former = current.activeInstance;
    current = await control(current,former,false);
    current = await post(current,'read',{clientId:'Client 2'});
    const promoted = current.activeInstance;
    docker(['compose','stop','--timeout','30',promoted]);
    current = await settled(await post(current,`instances/${former}`,{available:true},202),'Failed');
    evidence.interruptedRecovery = current;
    const restart = spawnSync('pwsh',['-NoProfile','-File','tools/Restart-LabApi.ps1'],{encoding:'utf8',timeout:60000,windowsHide:true});
    assert.equal(restart.status,0,restart.stderr);
    current = await get(current); assert.equal(current.actionStatus,'Failed');
    current = await control(current,promoted,true);
    current = await aligned(await control(current,former,true));
    assert.equal(current.activeInstance,promoted); assert.equal(current.consistent,true);
    check('failed-recovery-preserves-fence-and-resumes-after-real-api-restart');
    // Stop both, then recover the same source without inventing promotion authority.
    const source = current.activeInstance, target = standby(current).id;
    current = await control(current,target,false); current = await control(current,source,false);
    await post(current,'read',{clientId:'Client 1'},503);
    current = await control(current,source,true); current = await control(current,target,true); current = await sync(current);
    check('both-databases-unavailable-and-safe-source-recovery');
    await post(current,'update',{clientId:'',copyId,available:false,expectedVersion:1},400);
    await post(current,'instances/bogus',{available:false},400);
    await request(base+'/00000000-0000-0000-0000-000000000001',undefined,404);
    check('invalid-requests-and-unknown-session');
    evidence.final=current;
    success = true;
    console.log(`[info] ${checks.length} real PostgreSQL integration checks passed`);
})().finally(async()=>{
    if(fixtureCreated) {
        current=await get(current);
        if(!current.activeInstance) throw new Error('Fixture retained for diagnosis; recover writable database before cleanup.');
        sql(current.activeInstance,`BEGIN; DELETE FROM book_search_projection WHERE "BookId"='${bookId}'; DELETE FROM book_copies WHERE "Id"='${copyId}'; DELETE FROM books WHERE "Id"='${bookId}'; COMMIT;`);
        current=await sync(current);
        assert.deepEqual({books:writer(current).books.length,copies:writer(current).copies.length},evidence.initialCounts);
        evidence.afterFixtureCleanup=current;
    }
    await fs.writeFile(path.join(folder,'http-verification.json'),JSON.stringify({verifiedAtUtc:new Date().toISOString(),passed:success,base,checks,evidence},null,2));
}).catch(e=>{console.error(e);process.exitCode=1;});
