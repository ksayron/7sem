import http from 'k6/http';
import { check, fail, sleep } from 'k6';

export const options = {
    vus: 10,
    duration: '60s',
    summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(50)', 'p(95)', 'p(99)'],
    thresholds: { 'http_req_failed{phase:load}': ['rate<0.01'], 'http_req_duration{phase:load}': ['p(95)<1000'], checks: ['rate>0.99'] },
};
const identity = __ENV.IDENTITY_URL || 'http://identity:8080';
const catalog = __ENV.CATALOG_URL || 'http://catalog:8080';
function post(url, body, token) {
    return http.post(url, JSON.stringify(body), { headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }, tags: { phase: 'setup' } });
}
export function setup() {
    const tag = `docker-load-${Date.now()}`;
    const password = `Load-${tag}-Verify!`;
    const credentials = { email: `${tag}@example.test`, password };
    const register = post(`${identity}/api/identity/register`, { ...credentials, displayName: 'Docker load verification' });
    if (register.status !== 200 && register.status !== 201) fail(`Register: HTTP ${register.status}`);
    const login = post(`${identity}/api/identity/login`, credentials);
    if (login.status !== 200) fail(`Login: HTTP ${login.status}`);
    const token = login.json('accessToken');
    const book = post(`${catalog}/api/catalog/books`, { isbn: tag, title: tag, authors: ['Load verifier'], genres: ['Lab'], description: 'Disposable Docker load fixture', publicationYear: 2026 }, token);
    if (book.status !== 201 && book.status !== 200) fail(`Create book: HTTP ${book.status}`);
    return { token, bookId: book.json('id') };
}
export default function (data) {
    const response = http.get(`${catalog}/api/catalog/books/${data.bookId}`, { headers: { Authorization: `Bearer ${data.token}` }, tags: { phase: 'load' } });
    check(response, { 'catalog returns the fixture': r => r.status === 200 && r.json('id') === data.bookId });
    sleep(0.1);
}
export function teardown(data) { console.log(`Load fixture book ID: ${data.bookId}`); }
export function handleSummary(data) {
    return { '/results/load-summary.json': JSON.stringify(data, null, 2), stdout: JSON.stringify({ metrics: data.metrics, thresholds: data.thresholds }, null, 2) + '\n' };
}
