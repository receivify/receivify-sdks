import test from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';

import {
  Receivify,
  ReceivifyWebhookError,
  EVENTS,
  SIGNATURE_HEADER,
  describeVerification,
  isForwardEvent,
  isInvoiceEvent,
  parseWebhook,
  signWebhook,
  verifyWebhook,
  webhookData,
  webhookSequence,
} from '../src/index.js';

const CURRENT = 'whsec_current_aaaaaaaaaaaaaaaaaaaaaaaa';
const PREVIOUS = 'whsec_previous_bbbbbbbbbbbbbbbbbbbbbb';
const WRONG = 'whsec_wrong_cccccccccccccccccccccccc';

// A real envelope, with the money fields as strings.
const RAW_BODY = JSON.stringify({
  event: 'invoice.paid',
  sequence: 12,
  created_at: '2026-08-24T12:05:00.000Z',
  data: {
    id: '7f1c0e4a-2b3d-4e5f-8a9b-0c1d2e3f4a5b',
    reference: 'order-1001',
    status: 'paid',
    amount_expected: '2100000000000000001',
    amount_received: '2100000000000000001',
    address: 'bc1qexampleexampleexampleexampleexampleex',
    paid_at: '2026-08-24T12:04:58.000Z',
  },
});

const NOW = 1_800_000_000;
const at = () => ({ now: NOW, toleranceSeconds: 300 });

function header(secret, body = RAW_BODY, ts = NOW) {
  return signWebhook(body, secret, ts);
}

test('verifies a delivery signed with the CURRENT secret', () => {
  assert.equal(verifyWebhook(RAW_BODY, header(CURRENT), [CURRENT, PREVIOUS], at()), true);
});

test('verifies a delivery signed with the PREVIOUS secret (rotation grace window)', () => {
  // Receivify signs with the current secret only; during a rotation a receiver may still be
  // holding deliveries signed just before the switch.
  assert.equal(verifyWebhook(RAW_BODY, header(PREVIOUS), [CURRENT, PREVIOUS], at()), true);
});

test('rejects a delivery signed with a secret that is on neither slot', () => {
  assert.equal(verifyWebhook(RAW_BODY, header(WRONG), [CURRENT, PREVIOUS], at()), false);
  assert.equal(describeVerification(RAW_BODY, header(WRONG), [CURRENT, PREVIOUS], at()).reason, 'no_match');
});

test('rejects a tampered body even though the signature header is genuine', () => {
  const sig = header(CURRENT);
  const tampered = RAW_BODY.replace('2100000000000000001', '9900000000000000000');
  assert.notEqual(tampered, RAW_BODY);
  assert.equal(verifyWebhook(tampered, sig, [CURRENT, PREVIOUS], at()), false);
});

test('rejects a timestamp outside the default 300s tolerance', () => {
  const stale = header(CURRENT, RAW_BODY, NOW - 3600);
  const result = describeVerification(RAW_BODY, stale, [CURRENT], { now: NOW });
  assert.equal(result.ok, false);
  assert.equal(result.reason, 'stale_timestamp');
});

test('rejects a timestamp too far in the FUTURE as well', () => {
  const ahead = header(CURRENT, RAW_BODY, NOW + 3600);
  assert.equal(verifyWebhook(RAW_BODY, ahead, [CURRENT], { now: NOW }), false);
});

test('accepts a timestamp just inside the tolerance window', () => {
  const edge = header(CURRENT, RAW_BODY, NOW - 299);
  assert.equal(verifyWebhook(RAW_BODY, edge, [CURRENT], { now: NOW }), true);
});

test('toleranceSeconds: 0 disables the replay window', () => {
  const ancient = header(CURRENT, RAW_BODY, 1);
  assert.equal(verifyWebhook(RAW_BODY, ancient, [CURRENT], { now: NOW, toleranceSeconds: 0 }), true);
});

test('a custom tolerance is honoured', () => {
  const sig = header(CURRENT, RAW_BODY, NOW - 600);
  assert.equal(verifyWebhook(RAW_BODY, sig, [CURRENT], { now: NOW, toleranceSeconds: 900 }), true);
  assert.equal(verifyWebhook(RAW_BODY, sig, [CURRENT], { now: NOW, toleranceSeconds: 60 }), false);
});

test('a single secret string is normalised to a list', () => {
  assert.equal(verifyWebhook(RAW_BODY, header(CURRENT), CURRENT, at()), true);
});

test('no secret configured is a clean false, not a throw', () => {
  const result = describeVerification(RAW_BODY, header(CURRENT), [], at());
  assert.equal(result.ok, false);
  assert.equal(result.reason, 'no_secrets');
  assert.equal(verifyWebhook(RAW_BODY, header(CURRENT), undefined, at()), false);
});

test('a missing or malformed signature header is rejected with reason bad_header', () => {
  for (const bad of ['', null, undefined, 'garbage', 't=', 'v1=abc', 't=notanumber,v1=abc', 't=123']) {
    const r = describeVerification(RAW_BODY, bad, [CURRENT], { toleranceSeconds: 0 });
    assert.equal(r.ok, false, `expected ${JSON.stringify(bad)} to fail`);
    assert.equal(r.reason, 'bad_header');
  }
});

test('accepts a Buffer raw body identically to the string', () => {
  const sig = header(CURRENT);
  assert.equal(verifyWebhook(Buffer.from(RAW_BODY, 'utf8'), sig, [CURRENT], at()), true);
  assert.equal(verifyWebhook(new TextEncoder().encode(RAW_BODY), sig, [CURRENT], at()), true);
});

test('a RE-SERIALISED body fails — this is why the raw bytes matter', () => {
  const sig = header(CURRENT);
  // Exactly what `JSON.stringify(req.body)` does to you: same data, different bytes.
  const reserialised = JSON.stringify(JSON.parse(RAW_BODY), null, 2);
  assert.equal(verifyWebhook(reserialised, sig, [CURRENT], at()), false);
});

test('a non-hex or wrong-length v1 value is rejected without throwing', () => {
  for (const v1 of ['zz', 'abc', 'a'.repeat(63), 'a'.repeat(65), '']) {
    assert.equal(verifyWebhook(RAW_BODY, `t=${NOW},v1=${v1}`, [CURRENT], at()), false);
  }
});

test('a header carrying several v1 values matches if any one is right', () => {
  const good = header(CURRENT).split('v1=')[1];
  assert.equal(verifyWebhook(RAW_BODY, `t=${NOW},v1=${'0'.repeat(64)},v1=${good}`, [CURRENT], at()), true);
});

test('the signature is HMAC-SHA256 over `${t}.${rawBody}` exactly', () => {
  const expected = createHmac('sha256', CURRENT).update(`${NOW}.${RAW_BODY}`).digest('hex');
  assert.equal(header(CURRENT), `t=${NOW},v1=${expected}`);
});

test('parseWebhook returns the { event, sequence, created_at, data } envelope', () => {
  const event = parseWebhook(RAW_BODY, header(CURRENT), [CURRENT], at());
  assert.equal(event.event, 'invoice.paid');
  assert.equal(event.sequence, 12);
  assert.equal(event.created_at, '2026-08-24T12:05:00.000Z');
  assert.equal(event.data.id, '7f1c0e4a-2b3d-4e5f-8a9b-0c1d2e3f4a5b');
});

test('parseWebhook keeps webhook money as an exact string', () => {
  const { data } = parseWebhook(RAW_BODY, header(CURRENT), [CURRENT], at());
  assert.equal(typeof data.amount_expected, 'string');
  assert.equal(data.amount_expected, '2100000000000000001');
  // The whole point: a double cannot hold this.
  assert.notEqual(String(Number(data.amount_expected)), data.amount_expected);
});

test('parseWebhook throws a typed error carrying the reason', () => {
  assert.throws(
    () => parseWebhook(RAW_BODY, header(WRONG), [CURRENT], at()),
    (err) => err instanceof ReceivifyWebhookError && err.reason === 'no_match',
  );
  assert.throws(
    () => parseWebhook(RAW_BODY, header(CURRENT, RAW_BODY, 1), [CURRENT], { now: NOW }),
    (err) => err.reason === 'stale_timestamp',
  );
});

test('parseWebhook rejects a verified body that is not JSON', () => {
  const body = 'not json at all';
  assert.throws(
    () => parseWebhook(body, header(CURRENT, body), [CURRENT], at()),
    (err) => err instanceof ReceivifyWebhookError && err.reason === 'bad_json',
  );
});

test('parseWebhook rejects a verified body that is not an envelope', () => {
  const body = JSON.stringify([1, 2, 3]);
  assert.throws(
    () => parseWebhook(body, header(CURRENT, body), [CURRENT], at()),
    (err) => err.reason === 'bad_envelope',
  );
});

test('webhookData returns `data`, and tolerates the legacy `invoice` shape', () => {
  assert.equal(webhookData({ data: { id: 'a' } }).id, 'a');
  assert.equal(webhookData({ invoice: { id: 'b' } }).id, 'b');
  assert.deepEqual(webhookData(null), {});
  assert.deepEqual(webhookData({}), {});
});

test('webhookSequence is the dedupe key and defaults to 0', () => {
  assert.equal(webhookSequence({ sequence: 42 }), 42);
  assert.equal(webhookSequence({}), 0);
  assert.equal(webhookSequence(null), 0);
});

test('event family guards cover both families and the extras', () => {
  assert.equal(isInvoiceEvent({ event: 'invoice.paid' }), true);
  assert.equal(isInvoiceEvent({ event: 'sweep.sent' }), true);
  assert.equal(isInvoiceEvent({ event: 'test' }), true);
  assert.equal(isInvoiceEvent({ event: 'forward.sent' }), false);
  assert.equal(isForwardEvent({ event: 'forward.confirmed' }), true);
  assert.equal(isForwardEvent('forward.sent'), true);
  assert.equal(isForwardEvent({ event: 'invoice.paid' }), false);
});

test('the frozen EventName registry is exported in full', () => {
  assert.deepEqual(EVENTS, [
    'invoice.confirming',
    'invoice.partial',
    'invoice.paid',
    'invoice.swept',
    'invoice.expired',
    'invoice.partial_expired',
    'invoice.cancelled',
    'invoice.failed',
    'sweep.sent',
    'forward.confirmed',
    'forward.sent',
    'test',
  ]);
  assert.equal(SIGNATURE_HEADER, 'X-Receivify-Signature');
});

test('the client verifies against its configured rotation list', () => {
  const client = new Receivify('https://receivify.io', 'rcv_live_x', {
    webhookSecrets: [CURRENT, PREVIOUS],
    fetch: async () => {
      throw new Error('no network in this test');
    },
  });
  assert.deepEqual(client.webhookSecrets, [CURRENT, PREVIOUS]);
  assert.equal(client.verifyWebhook(RAW_BODY, header(CURRENT), undefined, at()), true);
  assert.equal(client.verifyWebhook(RAW_BODY, header(PREVIOUS), undefined, at()), true);
  assert.equal(client.verifyWebhook(RAW_BODY, header(WRONG), undefined, at()), false);
  // An explicit list overrides the configured one.
  assert.equal(client.verifyWebhook(RAW_BODY, header(PREVIOUS), [CURRENT], at()), false);
  assert.equal(client.parseWebhook(RAW_BODY, header(PREVIOUS), undefined, at()).event, 'invoice.paid');
});

test('a forward event carries the CryptAPI-shaped data', () => {
  const body = JSON.stringify({
    event: 'forward.sent',
    sequence: 88,
    created_at: '2026-08-24T12:05:00.000Z',
    data: {
      forward_id: 'f0000000-0000-4000-8000-000000000000',
      ticker: 'btc',
      address_in: 'bc1qin',
      address_out: 'bc1qout',
      txid_in: 'aa',
      txid_out: 'bb',
      value: '100000000',
      value_forwarded: '99900000',
      fee: '100000',
      confirmations: 3,
      pending: false,
    },
  });
  const event = parseWebhook(body, header(CURRENT, body), [CURRENT], at());
  assert.equal(isForwardEvent(event), true);
  assert.equal(typeof event.data.value_forwarded, 'string');
  // In a forward EVENT, `forward_id` names a relationship — it is not the deprecated alias.
  assert.equal(event.data.forward_id, 'f0000000-0000-4000-8000-000000000000');
});
