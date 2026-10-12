// Uses new, identifiable development fixtures; no credentials are written to evidence.
const { chromium } = require('playwright');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const path = require('node:path');
(async () => {
    const base = 'http://localhost:5000';
    const tag = `saga-demo-${Date.now()}`;
    const password = `Lab-${crypto.randomUUID()}!`;
    async function api(url, body, token) {
        const response = await fetch(base + url, { method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
        if (response.status === 404 && url === '/api/economy/wallet') return { balance: -1 };
        assert.ok(response.ok, `${url}: HTTP ${response.status} ${await response.clone().text()}`);
        return response.status === 204 ? null : response.json();
    }
    async function user(role) {
        const email = `${tag}-${role}@example.test`;
        await api('/api/identity/register', { email, displayName: `Lab ${role}`, password });
        return { ...await api('/api/identity/login', { email, password }), email };
    }
    async function waitFor(get, test) {
        for (let i = 0; i < 40; i++) { const value = await get(); if (test(value)) return value; await new Promise(r => setTimeout(r, 500)); }
        throw new Error('Saga did not reach the expected state in 20 seconds');
    }
    const owner = await user('owner'); const borrower = await user('borrower');
    await waitFor(() => api('/api/economy/wallet', undefined, borrower.accessToken), x => x.balance === 30);
    const book = await api('/api/catalog/books', { isbn: tag, title: tag, authors: ['Lab verification'], genres: ['Учебный опыт'], description: 'Development saga verification', publisher: null, publicationYear: 2026, coverUrl: null }, owner.accessToken);
    const copy = async price => api('/api/catalog/copies', { bookId: book.id, condition: 2, lendingPrice: price, maximumLoanDays: 14, location: 'Lab' }, owner.accessToken);
    const normal = await copy(10); const expensive = await copy(50);
    const request = async copyId => { const receipt = await api('/api/loans/requests', { copyId }, borrower.accessToken); return waitFor(() => api(`/api/loans/requests/${receipt.id}`, undefined, borrower.accessToken), x => [2,4,5].includes(x.state)); };
    const failed = await request(expensive.id); assert.equal(failed.state, 4);
    const available = await api(`/api/catalog/books/${book.id}/copies`, undefined, borrower.accessToken);
    assert.ok(available.some(x => x.id === expensive.id), 'Compensation must release the copy');
    const completed = await request(normal.id); assert.equal(completed.state, 2); assert.ok(completed.loanId);
    const occupied = await request(normal.id); assert.equal(occupied.state, 4);
    await api(`/api/loans/${completed.loanId}/cancel`, {}, borrower.accessToken);
    await waitFor(() => api(`/api/catalog/books/${book.id}/copies`, undefined, borrower.accessToken), x => x.some(c => c.id === normal.id));
    const folder = path.resolve('lab-results/real-saga-2026-10-05'); await fs.mkdir(folder, { recursive: true });
    const browser = await chromium.launch({ headless: true, channel: process.env.BURMALDA_BROWSER ?? 'msedge' });
    const pageErrors = [];
    let uiSuccess;
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        page.on('pageerror', e => pageErrors.push(e.message));
        page.on('console', message => { if (message.type() === 'error') console.error(message.text()); });
        await page.goto('http://localhost:5300/login');
        await page.getByLabel('Email address').fill(borrower.email);
        await page.getByLabel('Password', { exact: true }).fill(password);
        await page.getByRole('button', { name: 'Sign in', exact: true }).click();
        await page.waitForURL('**/dashboard');
        await page.goto('http://localhost:5300/discover');
        await page.getByLabel('Search the catalogue').fill(tag);
        await page.getByRole('button', { name: 'Search', exact: true }).click();
        await page.getByRole('button', { name: 'Choose a copy →', exact: true }).click();
        await page.locator('.copy-row').filter({ hasText: '50 BD' }).getByRole('button', { name: 'Request', exact: true }).click();
        try { await page.getByRole('heading', { name: 'Сага завершилась отказом', exact: true }).waitFor({ timeout: 20000 }); }
        catch (e) { await page.screenshot({ path: path.join(folder, 'diagnostic.png'), fullPage: true }); console.error(await page.locator('main').innerText()); throw e; }
        await page.screenshot({ path: path.join(folder, 'real-saga-failed.png'), fullPage: true });
        await page.locator('.copy-row').filter({ hasText: '10 BD' }).getByRole('button', { name: 'Request', exact: true }).click();
        await page.getByRole('heading', { name: 'Сага завершена: выдача создана', exact: true }).waitFor({ timeout: 20000 });
        await page.screenshot({ path: path.join(folder, 'real-saga-completed.png'), fullPage: true });
        const loans = await api('/api/loans/mine', undefined, borrower.accessToken);
        uiSuccess = loans.find(x => x.copyId === normal.id && x.status === 0); assert.ok(uiSuccess);
        assert.deepEqual(pageErrors, []);
        await api(`/api/loans/${uiSuccess.id}/cancel`, {}, borrower.accessToken);
    } finally { await browser.close(); }
    await fs.writeFile(path.join(folder, 'evidence.json'), JSON.stringify({ verifiedAtUtc: new Date().toISOString(), fixtureBookId: book.id, cases: { compensated: failed, completed, occupied }, browserChecks: ['failureReason','completed','switchBetweenSagaIds','noPageErrors'], pageErrors, cleanup: 'Successful loans cancelled through API; test users/book/history retained as development fixtures' }, null, 2));
    console.log(`[info] Real RabbitMQ saga and browser tracker verified: ${folder}`);
})().catch(e => { console.error(e); process.exitCode = 1; });
