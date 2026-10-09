/**
 * Webhook signature verification and envelope parsing.
 *
 * ## Use the RAW body. Always.
 *
 * The signature is `HMAC-SHA256(secret, "<t>.<rawBody>")` over the EXACT bytes we sent. If you
 * hand this function `JSON.stringify(req.body)` you are handing it a re-serialisation — key
 * order, whitespace and number formatting all change — and verification will fail even for a
 * perfectly genuine delivery. In Express, mount `express.raw({ type: 'application/json' })` on
 * the webhook route; in a Next.js route handler, `await req.text()`.
 *
 * ## Verify against a LIST of secrets.
 *
 * `POST /api/v1/webhooks/secret/rotate` mints a new secret and keeps the previous one acceptable
 * until `prev_expires_at` (24h by default), so during a rotation TWO secrets are legitimately
 * valid at once. Receivify always signs with the current one; the grace window is an instruction
 * to your receiver. Pass both and delete the old one after the window closes — you can ask
 * `GET /api/v1/webhooks/secret` whether the fallback is still needed.
 */

import { createHmac, timingSafeEqual } from 'node:crypto';
import { ReceivifyWebhookError } from './errors.js';

/** The frozen `EventName` set. Receivers MUST ignore names they do not recognise. */
export const EVENTS = Object.freeze([
  'invoice.confirming',
  'invoice.partial',
  'invoice.paid',
  'invoice.swept',
  'invoice.expired',
  'invoice.partial_expired',
  'invoice.cancelled',
  'invoice.failed',
  'sweep.sent',
  'forward.detected',
  'forward.confirmed',
  'forward.deferred',
  'forward.sent',
  'forward.reversed',
  'forward.failed',
  'test',
]);

/** The header Receivify signs every delivery with. */
export const SIGNATURE_HEADER = 'X-Receivify-Signature';

/** Default replay window, in seconds. */
export const DEFAULT_TOLERANCE_SECONDS = 300;

/**
 * Verify a signed delivery. Returns a boolean and never throws on a bad signature.
 *
 * @param {string|Buffer|Uint8Array} rawBody the EXACT bytes of the request body
 * @param {string|string[]|null|undefined} signatureHeader value of `X-Receivify-Signature`
 * @param {string|string[]|null|undefined} secrets one secret, or the rotation list
 * @param {{toleranceSeconds?: number, now?: number|Date}} [options]
 *        `toleranceSeconds` defaults to 300; pass `0` or `Infinity` to disable the replay window.
 *        `now` is epoch SECONDS (or a Date) and exists for tests.
 * @returns {boolean}
 */
export function verifyWebhook(rawBody, signatureHeader, secrets, options = {}) {
  return describeVerification(rawBody, signatureHeader, secrets, options).ok;
}

/**
 * Verify and JSON-parse a delivery in one step.
 * Throws {@link ReceivifyWebhookError} — with a machine-readable `reason` — on any failure.
 *
 * @returns {{event: string, sequence: number, created_at: string, data: object}}
 */
export function parseWebhook(rawBody, signatureHeader, secrets, options = {}) {
  const result = describeVerification(rawBody, signatureHeader, secrets, options);
  if (!result.ok) throw new ReceivifyWebhookError(result.message, result.reason);

  let parsed;
  try {
    parsed = JSON.parse(toBuffer(rawBody).toString('utf8'));
  } catch (cause) {
    throw new ReceivifyWebhookError(`Webhook body is not valid JSON: ${cause.message}`, 'bad_json');
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed) || typeof parsed.event !== 'string') {
    throw new ReceivifyWebhookError(
      'Webhook body is not a { event, sequence, created_at, data } envelope',
      'bad_envelope',
    );
  }
  return parsed;
}

/**
 * The verification result with a reason, for logging why a delivery was rejected.
 * `reason` is one of `no_secrets`, `bad_header`, `stale_timestamp`, `no_match`.
 */
export function describeVerification(rawBody, signatureHeader, secrets, options = {}) {
  const list = normaliseSecrets(secrets);
  if (list.length === 0) {
    return { ok: false, reason: 'no_secrets', message: 'No webhook secret configured' };
  }

  const header = Array.isArray(signatureHeader) ? signatureHeader[0] : signatureHeader;
  const parsed = parseSignatureHeader(header);
  if (!parsed) {
    return {
      ok: false,
      reason: 'bad_header',
      message: `Missing or malformed ${SIGNATURE_HEADER} header (expected "t=<unix>,v1=<hex>")`,
    };
  }

  const tolerance = options.toleranceSeconds === undefined ? DEFAULT_TOLERANCE_SECONDS : options.toleranceSeconds;
  if (Number.isFinite(tolerance) && tolerance > 0) {
    const now = options.now instanceof Date ? Math.floor(options.now.getTime() / 1000) : (options.now ?? Math.floor(Date.now() / 1000));
    if (Math.abs(now - parsed.timestamp) > tolerance) {
      return {
        ok: false,
        reason: 'stale_timestamp',
        message: `Webhook timestamp ${parsed.timestamp} is outside the ${tolerance}s tolerance window`,
      };
    }
  }

  const body = toBuffer(rawBody);
  const signed = Buffer.concat([Buffer.from(`${parsed.rawTimestamp}.`, 'utf8'), body]);

  // No early exit: every secret is tried and every comparison is constant-time, so neither the
  // number of secrets nor which one matched is observable from timing.
  let matched = false;
  for (const secret of list) {
    const expected = createHmac('sha256', secret).update(signed).digest();
    for (const candidate of parsed.signatures) {
      if (constantTimeHexEqual(expected, candidate)) matched = true;
    }
  }

  return matched
    ? { ok: true, reason: null, message: 'ok', timestamp: parsed.timestamp }
    : { ok: false, reason: 'no_match', message: 'Webhook signature did not match any configured secret' };
}

/**
 * Sign a body the way Receivify does. Exposed for tests and local fixtures — you never need it
 * to RECEIVE a webhook.
 *
 * @returns {string} a full `t=<unix>,v1=<hex>` header value
 */
export function signWebhook(rawBody, secret, timestamp = Math.floor(Date.now() / 1000)) {
  const t = String(Math.trunc(Number(timestamp)));
  const signed = Buffer.concat([Buffer.from(`${t}.`, 'utf8'), toBuffer(rawBody)]);
  return `t=${t},v1=${createHmac('sha256', secret).update(signed).digest('hex')}`;
}

/** The resource a verified event carries. Tolerates the pre-envelope `{invoice: …}` shape. */
export function webhookData(event) {
  if (event && typeof event === 'object') {
    if (event.data && typeof event.data === 'object') return event.data;
    if (event.invoice && typeof event.invoice === 'object') return event.invoice; // legacy
  }
  return {};
}

/**
 * The globally monotonic counter allocated at enqueue time.
 * Delivery is at-least-once and can arrive out of order: store the highest sequence you have
 * applied per resource and ignore anything at or below it.
 */
export function webhookSequence(event) {
  const n = Number(event?.sequence);
  return Number.isFinite(n) ? Math.trunc(n) : 0;
}

/** True for `invoice.*`, `sweep.sent` and `test` — the events whose `data` is invoice-shaped. */
export function isInvoiceEvent(event) {
  const name = typeof event === 'string' ? event : event?.event;
  return typeof name === 'string' && (name.startsWith('invoice.') || name === 'sweep.sent' || name === 'test');
}

/** True for `forward.confirmed` / `forward.sent` — the events whose `data` is forward-payment-shaped. */
export function isForwardEvent(event) {
  const name = typeof event === 'string' ? event : event?.event;
  return typeof name === 'string' && name.startsWith('forward.');
}

/* ------------------------------------------------------------------ */

/** Accepts a string, a single secret, an array, or null. Blank entries are dropped. */
export function normaliseSecrets(secrets) {
  if (secrets == null) return [];
  const arr = Array.isArray(secrets) ? secrets : [secrets];
  return arr.filter((s) => typeof s === 'string' && s.length > 0);
}

function parseSignatureHeader(header) {
  if (typeof header !== 'string' || header.length === 0) return null;
  let rawTimestamp = null;
  const signatures = [];
  for (const part of header.split(',')) {
    const idx = part.indexOf('=');
    if (idx < 0) continue;
    const key = part.slice(0, idx).trim();
    const value = part.slice(idx + 1).trim();
    if (key === 't' && rawTimestamp === null) rawTimestamp = value;
    else if (key === 'v1' && value) signatures.push(value);
  }
  if (rawTimestamp === null || signatures.length === 0) return null;
  if (!/^[0-9]+$/.test(rawTimestamp)) return null;
  const timestamp = Number(rawTimestamp);
  if (!Number.isSafeInteger(timestamp)) return null;
  // `rawTimestamp` (not the parsed number) is what goes into the HMAC — the server signed the
  // exact characters it sent.
  return { rawTimestamp, timestamp, signatures };
}

function constantTimeHexEqual(expected, candidateHex) {
  if (typeof candidateHex !== 'string' || !/^[0-9a-fA-F]*$/.test(candidateHex)) return false;
  let candidate;
  try {
    candidate = Buffer.from(candidateHex, 'hex');
  } catch {
    return false;
  }
  // timingSafeEqual throws on a length mismatch, and a length mismatch is not a secret.
  if (candidate.length !== expected.length) return false;
  return timingSafeEqual(expected, candidate);
}

function toBuffer(rawBody) {
  if (Buffer.isBuffer(rawBody)) return rawBody;
  if (rawBody instanceof Uint8Array) return Buffer.from(rawBody.buffer, rawBody.byteOffset, rawBody.byteLength);
  if (rawBody instanceof ArrayBuffer) return Buffer.from(rawBody);
  if (typeof rawBody === 'string') return Buffer.from(rawBody, 'utf8');
  if (rawBody == null) return Buffer.alloc(0);
  throw new ReceivifyWebhookError(
    'verifyWebhook needs the RAW body as a string or Buffer, not a parsed object — ' +
      're-serialising the JSON changes the bytes and breaks the HMAC',
    'bad_json',
  );
}
