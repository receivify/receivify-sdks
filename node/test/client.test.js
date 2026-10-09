import test from 'node:test';
import assert from 'node:assert/strict';

import {
  Receivify,
  ReceivifyApiError,
  ReceivifyConnectionError,
  NotFoundError,
  NotAllowedError,
  RateLimitedError,
  UnauthorizedError,
  UpstreamError,
  VERSION,
} from '../src/index.js';
import { constantFetch, mockFetch, pathOf, queryOf, sequenceFetch } from './helpers.js';

const URL_BASE = 'https://receivify.io';
const KEY = 'rcv_live_supersecretkeyvalue';

function client(fetchImpl, opts = {}) {
  return new Receivify(URL_BASE, KEY, { fetch: fetchImpl, sleep: async () => {}, ...opts });
}

const INVOICE = {
  id: '7f1c0e4a-2b3d-4e5f-8a9b-0c1d2e3f4a5b',
  reference: 'order-1001',
  asset: 'eth',
  amount_expected: '2100000000000000001',
  amount_received: '0',
  amount_fiat: '25.00',
  fiat_currency: 'USD',
  chain: 'ethereum',
  provider: 'evm',
  address: '0xabc',
  status: 'pending',
  expires_at: '2026-08-24T13:00:00.000Z',
  paid_at: null,
  created_at: '2026-08-24T12:00:00.000Z',
  metadata: {},
  checkout_url: 'https://receivify.io/pay/7f1c0e4a',
};

/* ------------------------------------------------------------------ *
 * Request construction
 * ------------------------------------------------------------------ */

test('builds an authenticated GET with the documented headers', async () => {
  const f = constantFetch({ status: 200, body: INVOICE });
  await client(f).getInvoice(INVOICE.id);

  const call = f.only();
  assert.equal(call.method, 'GET');
  assert.equal(call.url, `${URL_BASE}/api/v1/invoices/${INVOICE.id}`);
  assert.equal(call.headers.authorization, `Bearer ${KEY}`);
  assert.equal(call.headers.accept, 'application/json');
  assert.equal(call.headers['user-agent'], `receivify-node/${VERSION}`);
  assert.equal(call.body, undefined);
});

test('the API key is never placed in a URL and never enumerable on the client', async () => {
  const f = constantFetch({ status: 200, body: INVOICE });
  const c = client(f);
  await c.getInvoice(INVOICE.id);

  assert.equal(f.only().url.includes(KEY), false);
  assert.equal(JSON.stringify(c).includes(KEY), false);
  assert.equal(Object.keys(c).includes('_apiKey'), false);
  assert.equal(JSON.stringify(c.toJSON()).includes(KEY), false);
});

test('a trailing slash on apiUrl is trimmed rather than doubling the path separator', async () => {
  const f = constantFetch({ status: 200, body: INVOICE });
  await new Receivify(`${URL_BASE}///`, KEY, { fetch: f }).getInvoice('abc');
  assert.equal(f.only().url, `${URL_BASE}/api/v1/invoices/abc`);
});

test('path ids are URL-encoded', async () => {
  const f = constantFetch({ status: 200, body: {} });
  await client(f).getPaymentLink('a/b?c');
  assert.equal(pathOf(f.only().url), '/api/v1/payment-links/a%2Fb%3Fc');
});

test('a missing id is a TypeError raised before any request is made', () => {
  const f = constantFetch({ status: 200, body: {} });
  assert.throws(() => client(f).getInvoice(''), TypeError);
  assert.throws(() => client(f).getAddress(null), TypeError);
  assert.equal(f.calls.length, 0);
});

test('createInvoice posts JSON and sets Idempotency-Key when asked', async () => {
  const f = constantFetch({ status: 201, body: INVOICE });
  await client(f).createInvoice(
    { amount_fiat: '25.00', currency: 'USD', asset: 'eth', reference: 'order-1001' },
    { idempotencyKey: 'order-1001-attempt-1' },
  );

  const call = f.only();
  assert.equal(call.method, 'POST');
  assert.equal(call.headers['content-type'], 'application/json');
  assert.equal(call.headers['idempotency-key'], 'order-1001-attempt-1');
  assert.deepEqual(call.json, { amount_fiat: '25.00', currency: 'USD', asset: 'eth', reference: 'order-1001' });
});

test('list query params are filtered and serialised; null/undefined are dropped', async () => {
  const f = constantFetch({ status: 200, body: { data: [], has_more: false, next_cursor: null } });
  await client(f).listInvoices({ limit: 100, status: 'paid', cursor: undefined, bogus: 'x' });
  assert.deepEqual(queryOf(f.only().url), { limit: '100', status: 'paid' });
});

test('boolean query params serialise as true/false, including false', async () => {
  const f = constantFetch({ status: 200, body: { data: [], has_more: false, next_cursor: null } });
  await client(f).listPaymentLinks({ active: false });
  assert.deepEqual(queryOf(f.only().url), { active: 'false' });
});

test('public catalogue and public invoice routes send no Authorization header', async () => {
  const f = constantFetch({ status: 200, body: { data: [], has_more: false, next_cursor: null } });
  const c = client(f);
  await c.listTickers();
  await c.listAssets();
  await c.getPublicInvoice(INVOICE.id);
  for (const call of f.calls) assert.equal(call.headers.authorization, undefined);
});

test('a userAgent suffix is appended, never replacing the SDK identity', async () => {
  const f = constantFetch({ status: 200, body: {} });
  await client(f, { userAgent: 'my-shop/2.1' }).getSettings();
  assert.equal(f.only().headers['user-agent'], `receivify-node/${VERSION} my-shop/2.1`);
});

/* ------------------------------------------------------------------ *
 * Money stays a string
 * ------------------------------------------------------------------ */

test('base-unit amounts come back as exact strings, never parsed to Number', async () => {
  const f = constantFetch({ status: 201, body: INVOICE });
  const invoice = await client(f).createInvoice({ amount_fiat: '25.00' });

  assert.equal(typeof invoice.amount_expected, 'string');
  assert.equal(invoice.amount_expected, '2100000000000000001');
  assert.equal(typeof invoice.amount_received, 'string');
  assert.equal(invoice.amount_received, '0');
  // Number() would corrupt this value; proving the SDK did not go anywhere near it.
  assert.notEqual(String(Number(invoice.amount_expected)), invoice.amount_expected);
});

test('amount_fiat is a decimal STRING, not a float', async () => {
  const f = constantFetch({ status: 201, body: INVOICE });
  const invoice = await client(f).createInvoice({ amount_fiat: '25.00' });
  assert.equal(typeof invoice.amount_fiat, 'string');
  assert.equal(invoice.amount_fiat, '25.00'); // a Number would render as 25
});

test('a satoshi value above 2^53 survives the round trip untouched', async () => {
  const huge = '9007199254740993'; // 2^53 + 1
  const f = constantFetch({ status: 200, body: { ...INVOICE, amount_expected: huge } });
  const invoice = await client(f).getInvoice(INVOICE.id);
  assert.equal(invoice.amount_expected, huge);
  assert.notEqual(String(Number(huge)), huge);
});

/* ------------------------------------------------------------------ *
 * Forwards
 * ------------------------------------------------------------------ */

const FORWARD = {
  id: 'aa11bb22-cc33-4d44-8e55-ff6677889900',
  forward_id: 'aa11bb22-cc33-4d44-8e55-ff6677889900',
  ticker: 'btc',
  provider: 'bitcoind',
  chain: null,
  address: 'bc1qdeposit',
  destination: 'bc1qdestination',
  callback_url: 'https://shop.example/hooks/receivify',
  confirmations_required: 3,
  min_forward: '0.000100',
  status: 'active',
  expires_at: null,
  created_at: '2026-08-24T12:00:00.000Z',
  updated_at: null,
};

test("a forward's canonical id is `id`, with `forward_id` carrying the same deprecated value", async () => {
  const f = constantFetch({ status: 201, body: FORWARD });
  const forward = await client(f).createAddress({ ticker: 'btc', destination: 'bc1qdestination' });
  assert.equal(forward.id, FORWARD.id);
  assert.equal(forward.forward_id, forward.id);
});

test('createForward / getForward / listForwards are aliases of the address methods', async () => {
  const f = mockFetch(() => ({ status: 200, body: FORWARD }));
  const c = client(f);
  await c.createForward({ ticker: 'btc', destination: 'bc1q' });
  await c.getForward(FORWARD.id);
  assert.deepEqual(f.calls.map((x) => `${x.method} ${pathOf(x.url)}`), [
    'POST /api/v1/address',
    `GET /api/v1/address/${FORWARD.id}`,
  ]);
});

test('requestRaw exposes the status and Idempotent-Replay header (200 vs 201 on createAddress)', async () => {
  const f = constantFetch({ status: 200, body: FORWARD, headers: { 'Idempotent-Replay': 'true' } });
  const res = await client(f).requestRaw('POST', '/api/v1/address', {
    body: { ticker: 'btc', destination: 'bc1q' },
  });
  assert.equal(res.status, 200);
  assert.equal(res.headers['idempotent-replay'], 'true');
  assert.equal(res.body.id, FORWARD.id);
});

test('updateAddress PATCHes only the mutable fields; disableAddress DELETEs', async () => {
  const f = mockFetch(() => ({ status: 200, body: { ...FORWARD, status: 'paused' } }));
  const c = client(f);
  await c.updateAddress(FORWARD.id, { status: 'paused' });
  await c.disableAddress(FORWARD.id);
  assert.equal(f.calls[0].method, 'PATCH');
  assert.deepEqual(f.calls[0].json, { status: 'paused' });
  assert.equal(f.calls[1].method, 'DELETE');
  assert.equal(f.calls[1].body, undefined);
});

/* ------------------------------------------------------------------ *
 * List envelope + auto-pagination
 * ------------------------------------------------------------------ */

test('a list returns the { data, has_more, next_cursor } envelope untouched', async () => {
  const envelope = { data: [INVOICE], has_more: true, next_cursor: 'cursor-2' };
  const f = constantFetch({ status: 200, body: envelope });
  const page = await client(f).listInvoices({ limit: 1 });
  assert.deepEqual(Object.keys(page).sort(), ['data', 'has_more', 'next_cursor']);
  assert.equal(page.has_more, true);
  assert.equal(page.next_cursor, 'cursor-2');
});

test('auto-pagination follows next_cursor until has_more is false', async () => {
  const f = sequenceFetch([
    { status: 200, body: { data: [{ id: 'a' }, { id: 'b' }], has_more: true, next_cursor: 'c2' } },
    { status: 200, body: { data: [{ id: 'c' }], has_more: true, next_cursor: 'c3' } },
    { status: 200, body: { data: [{ id: 'd' }], has_more: false, next_cursor: null } },
  ]);
  const c = client(f);
  const ids = [];
  for await (const item of c.invoices({ status: 'paid', limit: 2 })) ids.push(item.id);

  assert.deepEqual(ids, ['a', 'b', 'c', 'd']);
  assert.equal(f.calls.length, 3);
  assert.deepEqual(queryOf(f.calls[0].url), { limit: '2', status: 'paid' });
  assert.deepEqual(queryOf(f.calls[1].url), { limit: '2', status: 'paid', cursor: 'c2' });
  assert.deepEqual(queryOf(f.calls[2].url), { limit: '2', status: 'paid', cursor: 'c3' });
});

test('auto-pagination stops immediately on a single complete page', async () => {
  const f = constantFetch({ status: 200, body: { data: [{ id: 'a' }], has_more: false, next_cursor: null } });
  const c = client(f);
  assert.deepEqual(await c.collect(c.addresses()), [{ id: 'a' }]);
  assert.equal(f.calls.length, 1);
});

test('auto-pagination refuses to loop on a repeated cursor', async () => {
  const f = constantFetch({ status: 200, body: { data: [{ id: 'a' }], has_more: true, next_cursor: 'same' } });
  const c = client(f);
  await assert.rejects(() => c.collect(c.invoices()), /cursor repeated/);
});

test('auto-pagination refuses to continue when has_more is true but next_cursor is empty', async () => {
  const f = constantFetch({ status: 200, body: { data: [], has_more: true, next_cursor: null } });
  const c = client(f);
  await assert.rejects(() => c.collect(c.invoices()), /next_cursor is empty/);
});

test('collect() honours a max and stops requesting pages', async () => {
  const f = mockFetch((_c, n) => ({
    status: 200,
    body: { data: [{ id: `i${n}` }], has_more: true, next_cursor: `c${n}` },
  }));
  const c = client(f);
  const items = await c.collect(c.invoices(), 3);
  assert.equal(items.length, 3);
  assert.equal(f.calls.length, 3);
});

test('every list method has an auto-paginating twin', () => {
  const c = client(constantFetch({ status: 200, body: {} }));
  for (const name of [
    'invoices',
    'addresses',
    'forwards',
    'paymentLinks',
    'recurring',
    'apiKeys',
    'webhookEndpoints',
    'webhookDeliveries',
  ]) {
    assert.equal(typeof c[name], 'function', `${name} should exist`);
    assert.equal(typeof c[name]()[Symbol.asyncIterator], 'function', `${name}() should be async-iterable`);
  }
});

/* ------------------------------------------------------------------ *
 * Errors
 * ------------------------------------------------------------------ */

test('a known error code maps to its typed class with code and detail preserved', async () => {
  const f = constantFetch({ status: 404, body: { error: 'not_found', detail: 'No such invoice.' } });
  await assert.rejects(
    () => client(f).getInvoice(INVOICE.id),
    (err) => {
      assert.ok(err instanceof NotFoundError);
      assert.ok(err instanceof ReceivifyApiError);
      assert.equal(err.code, 'not_found');
      assert.equal(err.detail, 'No such invoice.');
      assert.equal(err.status, 404);
      assert.equal(err.request, `GET /api/v1/invoices/${INVOICE.id}`);
      assert.equal(err.retryable, false);
      return true;
    },
  );
});

test('each documented code maps to a distinct class', async () => {
  const cases = [
    [401, 'unauthorized', UnauthorizedError],
    [409, 'not_allowed', NotAllowedError],
    [502, 'upstream_error', UpstreamError],
  ];
  for (const [status, code, Cls] of cases) {
    const f = constantFetch({ status, body: { error: code, detail: 'x' } });
    await assert.rejects(
      () => client(f, { maxRetries: 0 }).cancelInvoice('id-1'),
      (err) => err instanceof Cls && err.code === code,
    );
  }
});

test('an UNKNOWN error code falls back to ReceivifyApiError without losing the code', async () => {
  const f = constantFetch({
    status: 418,
    body: { error: 'quantum_flux_detected', detail: 'A code from a newer API.' },
  });
  await assert.rejects(
    () => client(f).getInvoice(INVOICE.id),
    (err) => {
      assert.ok(err instanceof ReceivifyApiError);
      assert.equal(err.constructor.name, 'ReceivifyApiError');
      assert.equal(err.code, 'quantum_flux_detected');
      assert.equal(err.detail, 'A code from a newer API.');
      assert.equal(err.status, 418);
      return true;
    },
  );
});

test('a body with no error key at all still produces a usable ReceivifyApiError', async () => {
  const f = constantFetch({ status: 500, text: '<html>gateway exploded</html>' });
  await assert.rejects(
    () => client(f, { maxRetries: 0 }).getInvoice(INVOICE.id),
    (err) => err instanceof ReceivifyApiError && err.code === null && err.status === 500 && err.retryable === true,
  );
});

test('a 429 becomes RateLimitedError with the rate-limit headers parsed', async () => {
  const f = constantFetch({
    status: 429,
    body: { error: 'rate_limited', detail: 'Slow down.' },
    headers: { 'Retry-After': '7', 'X-RateLimit-Limit': '60', 'X-RateLimit-Remaining': '0', 'X-RateLimit-Reset': '1800000060' },
  });
  await assert.rejects(
    () => client(f, { maxRetries: 0 }).createInvoice({ amount_sats: '1000' }),
    (err) => {
      assert.ok(err instanceof RateLimitedError);
      assert.equal(err.retryAfter, 7);
      assert.equal(err.rateLimitLimit, 60);
      assert.equal(err.rateLimitRemaining, 0);
      assert.equal(err.rateLimitReset, 1800000060);
      assert.equal(err.retryable, true);
      return true;
    },
  );
});

/* ------------------------------------------------------------------ *
 * Retries — the double-charge guard
 * ------------------------------------------------------------------ */

test('a GET is retried on 500 and then succeeds', async () => {
  const f = sequenceFetch([
    { status: 500, body: { error: 'internal', detail: 'Unexpected error.' } },
    { status: 200, body: INVOICE },
  ]);
  const invoice = await client(f).getInvoice(INVOICE.id);
  assert.equal(invoice.id, INVOICE.id);
  assert.equal(f.calls.length, 2);
});

test('a GET is retried on 429', async () => {
  const f = sequenceFetch([
    { status: 429, body: { error: 'rate_limited', detail: 'x' }, headers: { 'Retry-After': '1' } },
    { status: 200, body: { data: [], has_more: false, next_cursor: null } },
  ]);
  await client(f).listInvoices();
  assert.equal(f.calls.length, 2);
});

test('a mutating call WITHOUT an Idempotency-Key is never retried — that is how you double-charge', async () => {
  const f = constantFetch({ status: 500, body: { error: 'internal', detail: 'Unexpected error.' } });
  await assert.rejects(() => client(f).createInvoice({ amount_sats: '1000' }), ReceivifyApiError);
  assert.equal(f.calls.length, 1);
});

test('a mutating call WITH an Idempotency-Key is retried', async () => {
  const f = sequenceFetch([
    { status: 502, body: { error: 'upstream_error', detail: 'node down' } },
    { status: 201, body: INVOICE },
  ]);
  const invoice = await client(f).createInvoice({ amount_sats: '1000' }, { idempotencyKey: 'k-1' });
  assert.equal(invoice.id, INVOICE.id);
  assert.equal(f.calls.length, 2);
  for (const call of f.calls) assert.equal(call.headers['idempotency-key'], 'k-1');
});

test('an Idempotency-Key on an endpoint that does NOT declare support does not unlock retries', async () => {
  // PATCH /api/v1/address explicitly does not accept Idempotency-Key, so retrying is unsafe.
  const f = constantFetch({ status: 500, body: { error: 'internal', detail: 'Unexpected error.' } });
  await assert.rejects(
    () => client(f).updateAddress(FORWARD.id, { status: 'paused' }, { idempotencyKey: 'k-2' }),
    ReceivifyApiError,
  );
  assert.equal(f.calls.length, 1);
});

test('a 4xx is never retried, even on a GET', async () => {
  const f = constantFetch({ status: 400, body: { error: 'bad_request', detail: 'bad cursor' } });
  await assert.rejects(() => client(f).listInvoices({ cursor: 'junk' }), ReceivifyApiError);
  assert.equal(f.calls.length, 1);
});

test('maxRetries bounds the attempts', async () => {
  const f = constantFetch({ status: 503, body: { error: 'rate_unavailable', detail: 'no fx' } });
  await assert.rejects(() => client(f, { maxRetries: 3 }).getInvoice(INVOICE.id), ReceivifyApiError);
  assert.equal(f.calls.length, 4); // 1 attempt + 3 retries
});

test('a transport failure on a GET is retried, then raises ReceivifyConnectionError', async () => {
  let n = 0;
  const f = async () => {
    n += 1;
    throw new Error('ECONNRESET');
  };
  await assert.rejects(
    () => client(f, { maxRetries: 1 }).getInvoice(INVOICE.id),
    (err) => err instanceof ReceivifyConnectionError && err.timeout === false && /ECONNRESET/.test(err.message),
  );
  assert.equal(n, 2);
});

test('an aborted request surfaces as a timeout-flavoured ReceivifyConnectionError', async () => {
  const f = async () => {
    const err = new Error('The operation was aborted');
    err.name = 'AbortError';
    throw err;
  };
  await assert.rejects(
    () => client(f, { maxRetries: 0, timeout: 5 }).getInvoice(INVOICE.id),
    (err) => err instanceof ReceivifyConnectionError && err.timeout === true && /timed out after 5ms/.test(err.message),
  );
});

/* ------------------------------------------------------------------ *
 * Endpoints with non-standard shapes
 * ------------------------------------------------------------------ */

test('getHealth returns the report body on a 503 instead of throwing (it is not an {error, detail})', async () => {
  const report = { status: 'degraded', time: '2026-08-24T12:00:00.000Z', checks: { mongo: { ok: false } }, alerts: [] };
  const f = constantFetch({ status: 503, body: report });
  const health = await client(f).getHealth();
  assert.deepEqual(health, report);
  assert.equal(f.only().headers.authorization, undefined);
});

test('rotateWebhookSecret returns the once-only secret and the grace window', async () => {
  const f = constantFetch({
    status: 200,
    body: {
      secret: 'whsec_new',
      rotated_at: '2026-08-24T12:00:00.000Z',
      prev_expires_at: '2026-08-25T12:00:00.000Z',
      grace_seconds: 86400,
      replaced_previous: false,
      message: 'Store this now.',
    },
  });
  const res = await client(f).rotateWebhookSecret({ idempotencyKey: 'rotate-1' });
  assert.equal(res.secret, 'whsec_new');
  assert.equal(res.grace_seconds, 86400);
  assert.equal(f.only().headers['idempotency-key'], 'rotate-1');
});

test('createApiKey accepts a bare label string and an object alike', async () => {
  const f = mockFetch(() => ({ status: 201, body: { id: 'k1', api_key: 'rcv_live_x', prefix: 'rcv_live_x' } }));
  const c = client(f);
  await c.createApiKey('ci');
  await c.createApiKey({ label: 'ci' });
  assert.deepEqual(f.calls[0].json, { label: 'ci' });
  assert.deepEqual(f.calls[1].json, { label: 'ci' });
});

test('createWebhookEndpoint and sendTestWebhook accept a bare URL string', async () => {
  const f = mockFetch(() => ({ status: 201, body: {} }));
  const c = client(f);
  await c.createWebhookEndpoint('https://shop.example/hooks');
  await c.sendTestWebhook('https://shop.example/hooks');
  assert.deepEqual(f.calls[0].json, { url: 'https://shop.example/hooks' });
  assert.deepEqual(f.calls[1].json, { url: 'https://shop.example/hooks' });
});

test('bulkCancelInvoices sends the documented { ids, action } body', async () => {
  const f = constantFetch({ status: 200, body: { ok: true, action: 'cancel', requested: 2, cancelled: 1 } });
  const res = await client(f).bulkCancelInvoices(['a', 'b']);
  assert.deepEqual(f.only().json, { ids: ['a', 'b'], action: 'cancel' });
  assert.equal(res.requested, 2);
  assert.equal(res.cancelled, 1);
});

test('stagePayoutChanges wraps the map in { addresses }', async () => {
  const f = constantFetch({ status: 200, body: { ok: true, staged: [], applied: [], pending: [] } });
  await client(f).stagePayoutChanges({ btc: 'bc1qnew', eth: '' });
  assert.deepEqual(f.only().json, { addresses: { btc: 'bc1qnew', eth: '' } });
});

test('a non-JSON success body is returned as text rather than throwing', async () => {
  const f = constantFetch({ status: 200, text: 'plain text' });
  assert.equal(await client(f).request('GET', '/api/v1/whatever'), 'plain text');
});

test('the constructor rejects a missing apiUrl', () => {
  assert.throws(() => new Receivify(''), TypeError);
  assert.throws(() => new Receivify('   '), TypeError);
  assert.throws(() => new Receivify(undefined), TypeError);
});

test('the constructor rejects a non-callable transport', () => {
  assert.throws(() => new Receivify(URL_BASE, KEY, { fetch: 'not a function' }), TypeError);
});

test('a client with no API key still calls public routes', async () => {
  const f = constantFetch({ status: 200, body: { data: [], has_more: false, next_cursor: null } });
  await new Receivify(URL_BASE, null, { fetch: f }).listTickers();
  assert.equal(f.only().headers.authorization, undefined);
});
