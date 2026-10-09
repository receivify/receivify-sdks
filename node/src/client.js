/**
 * The Receivify API client.
 *
 * Zero runtime dependencies: native `fetch` (Node >= 18) and `node:crypto`, nothing else.
 */

import { USER_AGENT, VERSION } from './version.js';
import { ReceivifyConnectionError, errorFromResponse } from './errors.js';
import {
  DEFAULT_TOLERANCE_SECONDS,
  describeVerification,
  isForwardEvent,
  isInvoiceEvent,
  normaliseSecrets,
  parseWebhook,
  verifyWebhook,
  webhookData,
  webhookSequence,
} from './webhooks.js';

const DEFAULT_TIMEOUT_MS = 30_000;
const DEFAULT_MAX_RETRIES = 2;
const DEFAULT_RETRY_BASE_MS = 500;
const MAX_RETRY_DELAY_MS = 20_000;

export class Receivify {
  /**
   * @param {string} apiUrl e.g. `https://receivify.io`
   * @param {string} [apiKey] the merchant `rcv_live_…` key. Optional if you only call public routes.
   * @param {object} [opts]
   * @param {number} [opts.timeout=30000] per-request timeout in ms
   * @param {string|string[]} [opts.webhookSecrets] current secret first, previous second
   * @param {typeof fetch} [opts.fetch] inject a transport (tests, proxies, instrumentation)
   * @param {number} [opts.maxRetries=2] retries for SAFE requests only — see `shouldRetry`
   * @param {number} [opts.retryBaseMs=500] first backoff step; doubles each attempt
   * @param {string} [opts.userAgent] appended to `receivify-node/<version>`
   * @param {(ms:number)=>Promise<void>} [opts.sleep] inject a clock (tests)
   */
  constructor(apiUrl, apiKey = null, opts = {}) {
    if (typeof apiUrl !== 'string' || apiUrl.trim() === '') {
      throw new TypeError('Receivify: apiUrl is required, e.g. "https://receivify.io"');
    }
    /** @type {string} */
    this.apiUrl = apiUrl.trim().replace(/\/+$/, '');
    // Held privately so it can never be enumerated into a log line or a JSON dump.
    Object.defineProperty(this, '_apiKey', { value: apiKey || null, enumerable: false, writable: false });

    this.timeout = Number.isFinite(opts.timeout) ? opts.timeout : DEFAULT_TIMEOUT_MS;
    this.maxRetries = Number.isInteger(opts.maxRetries) && opts.maxRetries >= 0 ? opts.maxRetries : DEFAULT_MAX_RETRIES;
    this.retryBaseMs = Number.isFinite(opts.retryBaseMs) ? opts.retryBaseMs : DEFAULT_RETRY_BASE_MS;
    /** Current secret first, previous second. Always an array. */
    this.webhookSecrets = normaliseSecrets(opts.webhookSecrets ?? opts.webhookSecret);
    this.userAgent = opts.userAgent ? `${USER_AGENT} ${opts.userAgent}` : USER_AGENT;

    Object.defineProperty(this, '_fetch', {
      value: opts.fetch || globalThis.fetch,
      enumerable: false,
      writable: false,
    });
    Object.defineProperty(this, '_sleep', {
      value: opts.sleep || ((ms) => new Promise((r) => setTimeout(r, ms).unref?.())),
      enumerable: false,
      writable: false,
    });

    if (typeof this._fetch !== 'function') {
      throw new TypeError('Receivify: no fetch available. Use Node >= 18 or pass { fetch }.');
    }
  }

  /** The SDK version, also sent as the User-Agent. */
  static get VERSION() {
    return VERSION;
  }

  /** Never expose the key, not even accidentally through `console.log(client)`. */
  toJSON() {
    return { apiUrl: this.apiUrl, timeout: this.timeout, version: VERSION };
  }

  /* ================================================================ *
   * Invoices
   * ================================================================ */

  /**
   * `POST /api/v1/invoices` — always 201.
   *
   * Exactly one amount field is required; the first present one wins, in this order:
   * `amount_fiat` (+`currency`), `amount`, `amount_sats`, `amount_btc`.
   *
   * Two things return an EXISTING invoice rather than creating: an `Idempotency-Key` replay,
   * and a `reference` this merchant has already used. Both still answer 201.
   */
  createInvoice(params, opts = {}) {
    return this.request('POST', '/api/v1/invoices', { body: params, idempotent: true, ...opts });
  }

  /** `GET /api/v1/invoices/{id}` — the authoritative current state. Poll this, not a webhook replay. */
  getInvoice(id, opts = {}) {
    return this.request('GET', `/api/v1/invoices/${enc(id)}`, opts);
  }

  /** `GET /api/v1/invoices` — one page. `{ data, has_more, next_cursor }`. */
  listInvoices(params = {}, opts = {}) {
    return this.request('GET', '/api/v1/invoices', { query: pick(params, ['limit', 'cursor', 'status']), ...opts });
  }

  /** Auto-paginating `listInvoices`: `for await (const inv of client.invoices({status:'paid'}))`. */
  invoices(params = {}, opts = {}) {
    return this.paginate((p, o) => this.listInvoices(p, o), params, opts);
  }

  /**
   * `POST /api/v1/invoices/{id}/cancel`.
   * NOT idempotent — a second call on an already-cancelled invoice is `409 not_allowed`.
   */
  cancelInvoice(id, opts = {}) {
    return this.request('POST', `/api/v1/invoices/${enc(id)}/cancel`, opts);
  }

  /**
   * `POST /api/v1/invoices/bulk` — bulk cancel, max 500 ids.
   * Naturally idempotent (the filter only matches pending, unpaid invoices), but the endpoint
   * does NOT accept `Idempotency-Key`.
   */
  bulkCancelInvoices(ids, opts = {}) {
    return this.request('POST', '/api/v1/invoices/bulk', { body: { ids, action: 'cancel' }, ...opts });
  }

  /** `GET /api/public/invoices/{id}` — unauthenticated, minimal projection. Safe from a thank-you page. */
  getPublicInvoice(id, opts = {}) {
    return this.request('GET', `/api/public/invoices/${enc(id)}`, { auth: false, ...opts });
  }

  /* ================================================================ *
   * Forwarding addresses
   *
   * The spec calls these "forwards"; the shared SDK surface calls them "addresses". Both names
   * are exported and do the same thing.
   * ================================================================ */

  /**
   * `POST /api/v1/address` — create, or return, a forwarding address.
   *
   * Deterministic: the identity is `(merchant, resolved asset, destination, callback_url)`.
   * **201** = newly derived, **200** = an existing forward with that identity. Repeating the call
   * does not burn a second HD derivation index.
   *
   * Read the canonical id from `.id`. `.forward_id` carries the same value and is deprecated.
   */
  createAddress(params, opts = {}) {
    return this.request('POST', '/api/v1/address', { body: params, idempotent: true, ...opts });
  }

  /** `GET /api/v1/address/{id}` — the forward plus its capped `payments` / `callbacks` log. */
  getAddress(id, opts = {}) {
    return this.request('GET', `/api/v1/address/${enc(id)}`, opts);
  }

  /** `GET /api/v1/address` — one page of plain `Forward` objects (no `payments`/`callbacks`). */
  listAddresses(params = {}, opts = {}) {
    return this.request('GET', '/api/v1/address', {
      query: pick(params, ['limit', 'cursor', 'status', 'ticker']),
      ...opts,
    });
  }

  /** Auto-paginating `listAddresses`. */
  addresses(params = {}, opts = {}) {
    return this.paginate((p, o) => this.listAddresses(p, o), params, opts);
  }

  /**
   * `PATCH /api/v1/address/{id}` — the merchant kill switch.
   * Mutable: `status` (`active` ⇄ `paused`) and `callback_url`. `destination` and `ticker` are
   * immutable and any attempt is `409 not_allowed` before the forward is even looked up.
   */
  updateAddress(id, params, opts = {}) {
    return this.request('PATCH', `/api/v1/address/${enc(id)}`, { body: params, ...opts });
  }

  /** `DELETE /api/v1/address/{id}` — soft delete (`status: "disabled"`). Idempotent; the row is never removed. */
  disableAddress(id, opts = {}) {
    return this.request('DELETE', `/api/v1/address/${enc(id)}`, opts);
  }

  /** Alias of {@link createAddress}, matching the spec's `createForward` operationId. */
  createForward(params, opts) {
    return this.createAddress(params, opts);
  }
  /** Alias of {@link getAddress}. */
  getForward(id, opts) {
    return this.getAddress(id, opts);
  }
  /** Alias of {@link listAddresses}. */
  listForwards(params, opts) {
    return this.listAddresses(params, opts);
  }
  /** Alias of {@link updateAddress}. */
  updateForward(id, params, opts) {
    return this.updateAddress(id, params, opts);
  }
  /** Alias of {@link disableAddress}. */
  disableForward(id, opts) {
    return this.disableAddress(id, opts);
  }
  /** Alias of {@link addresses}. */
  forwards(params, opts) {
    return this.addresses(params, opts);
  }

  /* ================================================================ *
   * Catalogue (public, no key required)
   * ================================================================ */

  /** `GET /api/v1/tickers` — every `(asset, chain)` pair offered for forwarding. One finite page. */
  listTickers(opts = {}) {
    return this.request('GET', '/api/v1/tickers', { auth: false, ...opts });
  }

  /** `GET /api/v1/assets` — invoiceable coins. DISPLAY metadata: `min` and `network_fee` are prose. */
  listAssets(opts = {}) {
    return this.request('GET', '/api/v1/assets', { auth: false, ...opts });
  }

  /* ================================================================ *
   * Webhooks — account endpoints, delivery log, secret rotation
   * ================================================================ */

  /** `GET /api/v1/webhooks/endpoints`. Account endpoints receive `invoice.*` and `sweep.sent`, never `forward.*`. */
  listWebhookEndpoints(params = {}, opts = {}) {
    return this.request('GET', '/api/v1/webhooks/endpoints', { query: pick(params, ['limit', 'cursor']), ...opts });
  }

  /** Auto-paginating `listWebhookEndpoints`. */
  webhookEndpoints(params = {}, opts = {}) {
    return this.paginate((p, o) => this.listWebhookEndpoints(p, o), params, opts);
  }

  /** `POST /api/v1/webhooks/endpoints` — max 20 per account (`limit_exceeded` is a **400**, not a 409). */
  createWebhookEndpoint(url, opts = {}) {
    const body = typeof url === 'string' ? { url } : url;
    return this.request('POST', '/api/v1/webhooks/endpoints', { body, idempotent: true, ...opts });
  }

  /** `PATCH /api/v1/webhooks/endpoints/{id}` — mutable: `active`, `url`. */
  updateWebhookEndpoint(id, params, opts = {}) {
    return this.request('PATCH', `/api/v1/webhooks/endpoints/${enc(id)}`, { body: params, ...opts });
  }

  /** `DELETE /api/v1/webhooks/endpoints/{id}` — a HARD delete. Not idempotent: a second call is 404. */
  deleteWebhookEndpoint(id, opts = {}) {
    return this.request('DELETE', `/api/v1/webhooks/endpoints/${enc(id)}`, opts);
  }

  /** `GET /api/v1/webhooks/deliveries` — both families. Filter with `{event, status}`. */
  listWebhookDeliveries(params = {}, opts = {}) {
    return this.request('GET', '/api/v1/webhooks/deliveries', {
      query: pick(params, ['limit', 'cursor', 'event', 'status']),
      ...opts,
    });
  }

  /** Auto-paginating `listWebhookDeliveries`. */
  webhookDeliveries(params = {}, opts = {}) {
    return this.paginate((p, o) => this.listWebhookDeliveries(p, o), params, opts);
  }

  /**
   * `POST /api/v1/webhooks/deliveries/{id}/replay`.
   * The body is FROZEN — a replay re-sends the original bytes, `sequence` and `created_at`.
   * It does not reflect current state.
   */
  replayWebhookDelivery(id, opts = {}) {
    return this.request('POST', `/api/v1/webhooks/deliveries/${enc(id)}/replay`, opts);
  }

  /**
   * `GET /api/v1/webhooks/secret` — rotation METADATA only, never the secret.
   * `prev_active` answers "must my receiver still try the old secret?".
   */
  getWebhookSecretState(opts = {}) {
    return this.request('GET', '/api/v1/webhooks/secret', opts);
  }

  /**
   * `POST /api/v1/webhooks/secret/rotate` — mints a new secret and returns it **once**.
   *
   * The previous secret stays acceptable until `prev_expires_at`. Until then, pass BOTH to
   * `verifyWebhook`. An `Idempotency-Key` replay deliberately returns `secret: null`.
   */
  rotateWebhookSecret(opts = {}) {
    return this.request('POST', '/api/v1/webhooks/secret/rotate', { idempotent: true, ...opts });
  }

  /**
   * `POST /api/v1/webhooks/test` — send a signed `test` event to your own registered endpoints.
   * SSRF-safe: only already-registered URLs are reachable. Returns 200 even when every delivery
   * failed — compare `delivered` with `total`.
   */
  sendTestWebhook(params = {}, opts = {}) {
    const body = typeof params === 'string' ? { url: params } : params;
    return this.request('POST', '/api/v1/webhooks/test', { body, ...opts });
  }

  /* ================================================================ *
   * Payment links
   * ================================================================ */

  /** `GET /api/v1/payment-links`. `active` must be a real boolean. */
  listPaymentLinks(params = {}, opts = {}) {
    return this.request('GET', '/api/v1/payment-links', { query: pick(params, ['limit', 'cursor', 'active']), ...opts });
  }

  /** Auto-paginating `listPaymentLinks`. */
  paymentLinks(params = {}, opts = {}) {
    return this.paginate((p, o) => this.listPaymentLinks(p, o), params, opts);
  }

  /** `POST /api/v1/payment-links`. Set the amount with `amount_btc` (decimal BTC); read it as `amount_sats`. */
  createPaymentLink(params = {}, opts = {}) {
    return this.request('POST', '/api/v1/payment-links', { body: params, idempotent: true, ...opts });
  }

  /** `GET /api/v1/payment-links/{id}` — merchant-side read; shows deactivated links too. */
  getPaymentLink(id, opts = {}) {
    return this.request('GET', `/api/v1/payment-links/${enc(id)}`, opts);
  }

  /** `PATCH /api/v1/payment-links/{id}` — mutable: `active`, `title`, `amount_btc` (`null` makes it open-amount). */
  updatePaymentLink(id, params, opts = {}) {
    return this.request('PATCH', `/api/v1/payment-links/${enc(id)}`, { body: params, ...opts });
  }

  /** `DELETE /api/v1/payment-links/{id}` — soft deactivate. Invoices already minted stay payable. */
  deactivatePaymentLink(id, opts = {}) {
    return this.request('DELETE', `/api/v1/payment-links/${enc(id)}`, opts);
  }

  /** `GET /api/public/links/{id}` — unauthenticated. A deactivated link answers 404. */
  getPublicPaymentLink(id, opts = {}) {
    return this.request('GET', `/api/public/links/${enc(id)}`, { auth: false, ...opts });
  }

  /* ================================================================ *
   * Recurring schedules
   * ================================================================ */

  /** `GET /api/v1/recurring`. */
  listRecurring(params = {}, opts = {}) {
    return this.request('GET', '/api/v1/recurring', { query: pick(params, ['limit', 'cursor']), ...opts });
  }

  /** Auto-paginating `listRecurring`. */
  recurring(params = {}, opts = {}) {
    return this.paginate((p, o) => this.listRecurring(p, o), params, opts);
  }

  /**
   * `POST /api/v1/recurring` — scheduled billing, not auto-debit: each cycle GENERATES an invoice.
   * Provide either `amount_fiat` (+`currency`) or `amount`.
   */
  createRecurring(params, opts = {}) {
    return this.request('POST', '/api/v1/recurring', { body: params, idempotent: true, ...opts });
  }

  /** `POST /api/v1/recurring/{id}/cancel` — returns an ACK, not the resource. */
  cancelRecurring(id, opts = {}) {
    return this.request('POST', `/api/v1/recurring/${enc(id)}/cancel`, opts);
  }

  /* ================================================================ *
   * API keys
   * ================================================================ */

  /** `GET /api/v1/keys` — prefixes only. Revoked keys are included, flagged by `revoked`. */
  listApiKeys(params = {}, opts = {}) {
    return this.request('GET', '/api/v1/keys', { query: pick(params, ['limit', 'cursor']), ...opts });
  }

  /** Auto-paginating `listApiKeys`. */
  apiKeys(params = {}, opts = {}) {
    return this.paginate((p, o) => this.listApiKeys(p, o), params, opts);
  }

  /**
   * `POST /api/v1/keys` — the raw secret is returned ONCE and never persisted.
   * An `Idempotency-Key` replay returns the same `id` and `prefix` but `api_key: null`.
   */
  createApiKey(params = {}, opts = {}) {
    const body = typeof params === 'string' ? { label: params } : params;
    return this.request('POST', '/api/v1/keys', { body, idempotent: true, ...opts });
  }

  /** `POST /api/v1/keys/{id}/revoke` — pass the `id`, NOT the prefix. */
  revokeApiKey(id, opts = {}) {
    return this.request('POST', `/api/v1/keys/${enc(id)}/revoke`, opts);
  }

  /* ================================================================ *
   * Settings, summary, payouts
   * ================================================================ */

  /** `GET /api/v1/settings`. */
  getSettings(opts = {}) {
    return this.request('GET', '/api/v1/settings', opts);
  }

  /** `PATCH /api/v1/settings` — returns an ACK carrying only what changed, not the resource. */
  updateSettings(params, opts = {}) {
    return this.request('PATCH', '/api/v1/settings', { body: params, ...opts });
  }

  /** `GET /api/v1/summary` — dashboard composite. Its `fx` rates are advisory JSON numbers. */
  getSummary(opts = {}) {
    return this.request('GET', '/api/v1/summary', opts);
  }

  /** `GET /api/v1/payouts` — the LIVE map plus every open staged change (a bare array, not a list envelope). */
  getPayouts(opts = {}) {
    return this.request('GET', '/api/v1/payouts', opts);
  }

  /**
   * `PATCH /api/v1/payouts` — stage changes. Nothing is applied inline unless the deployment runs
   * with the timelock at zero AND email confirmation off.
   */
  stagePayoutChanges(addresses, opts = {}) {
    return this.request('PATCH', '/api/v1/payouts', { body: { addresses }, ...opts });
  }

  /** `POST /api/v1/payouts/changes/{id}/cancel` — returns an ACK. */
  cancelPayoutChange(id, opts = {}) {
    return this.request('POST', `/api/v1/payouts/changes/${enc(id)}/cancel`, opts);
  }

  /* ================================================================ *
   * System
   * ================================================================ */

  /**
   * `GET /api/health` — an OPERATOR report.
   * Its 503 body is `{status, time, checks, alerts}` with NO `error` key, so it does not conform
   * to the error contract; this method returns that body instead of throwing.
   */
  async getHealth(opts = {}) {
    const res = await this.requestRaw('GET', '/api/health', { auth: false, ...opts, throwOnError: false });
    return res.body;
  }

  /* ================================================================ *
   * Webhooks — receiving
   * ================================================================ */

  /**
   * Verify a signed delivery against the configured secret list (or an explicit one).
   * Constant-time, over the RAW body, with a 300s default replay window.
   *
   * @param {string|Buffer} rawBody the EXACT bytes — never `JSON.stringify(req.body)`
   * @param {string} signatureHeader value of `X-Receivify-Signature`
   * @param {string|string[]} [secrets] overrides `opts.webhookSecrets`
   * @param {{toleranceSeconds?: number, now?: number|Date}} [options]
   */
  verifyWebhook(rawBody, signatureHeader, secrets, options) {
    return verifyWebhook(rawBody, signatureHeader, secrets ?? this.webhookSecrets, options);
  }

  /** Verify and parse in one step. Throws `ReceivifyWebhookError` with a machine-readable `reason`. */
  parseWebhook(rawBody, signatureHeader, secrets, options) {
    return parseWebhook(rawBody, signatureHeader, secrets ?? this.webhookSecrets, options);
  }

  /** `{ ok, reason, message }` — the same check as `verifyWebhook`, with a reason for your logs. */
  describeWebhook(rawBody, signatureHeader, secrets, options) {
    return describeVerification(rawBody, signatureHeader, secrets ?? this.webhookSecrets, options);
  }

  static webhookData = webhookData;
  static webhookSequence = webhookSequence;
  static isInvoiceEvent = isInvoiceEvent;
  static isForwardEvent = isForwardEvent;
  static DEFAULT_WEBHOOK_TOLERANCE_SECONDS = DEFAULT_TOLERANCE_SECONDS;

  /* ================================================================ *
   * Pagination
   * ================================================================ */

  /**
   * Follow `next_cursor` until `has_more` is false, yielding each item.
   *
   *   for await (const inv of client.paginate((p) => client.listInvoices(p), { status: 'paid' })) { … }
   *
   * A repeated cursor throws rather than looping forever.
   *
   * @template T
   * @param {(params:object, opts:object)=>Promise<{data:T[],has_more:boolean,next_cursor:string|null}>} listFn
   * @param {object} [params]
   * @param {object} [opts]
   * @returns {AsyncGenerator<T>}
   */
  async *paginate(listFn, params = {}, opts = {}) {
    let cursor = params.cursor ?? undefined;
    const seen = new Set();
    for (;;) {
      const page = await listFn({ ...params, ...(cursor ? { cursor } : {}) }, opts);
      const items = Array.isArray(page?.data) ? page.data : [];
      for (const item of items) yield item;

      if (!page?.has_more) return;
      const next = page.next_cursor;
      // `has_more: true` with no cursor would spin forever; the API promises this cannot happen.
      if (typeof next !== 'string' || next === '') {
        throw new TypeError('Receivify: has_more is true but next_cursor is empty — cannot continue paging');
      }
      if (seen.has(next)) {
        throw new TypeError(`Receivify: pagination cursor repeated (${next}) — refusing to loop`);
      }
      seen.add(next);
      cursor = next;
    }
  }

  /** Drain a paginated iterator into an array. Convenience; be mindful of unbounded result sets. */
  async collect(iterable, max = Infinity) {
    const out = [];
    for await (const item of iterable) {
      out.push(item);
      if (out.length >= max) break;
    }
    return out;
  }

  /* ================================================================ *
   * HTTP
   * ================================================================ */

  /**
   * Issue a request and return the decoded body, throwing a typed error on 4xx/5xx.
   *
   * @param {string} method
   * @param {string} path already-encoded path, starting with `/`
   * @param {object} [options]
   * @param {object} [options.query]
   * @param {object} [options.body] serialised as JSON
   * @param {string} [options.idempotencyKey] sent as `Idempotency-Key`
   * @param {boolean} [options.auth=true] send the bearer key
   * @param {number} [options.timeout]
   * @param {AbortSignal} [options.signal]
   * @param {Record<string,string>} [options.headers]
   */
  async request(method, path, options = {}) {
    const res = await this.requestRaw(method, path, options);
    return res.body;
  }

  /**
   * The same request, but returns `{ status, headers, body }` — use it when you need
   * `Idempotent-Replay`, the `X-RateLimit-*` headers, or to tell a 200 from a 201 on
   * `POST /api/v1/address`.
   */
  async requestRaw(method, path, options = {}) {
    const {
      query,
      body,
      idempotencyKey,
      auth = true,
      timeout = this.timeout,
      signal,
      headers: extraHeaders,
      throwOnError = true,
      // Internal: does this endpoint DECLARE Idempotency-Key support in openapi.yaml?
      idempotent = false,
    } = options;

    // The API key is a header, never a query parameter, and is never logged.
    const url = this.apiUrl + path + buildQuery(query);

    const headers = {
      accept: 'application/json',
      'user-agent': this.userAgent,
      ...lowerKeys(extraHeaders),
    };
    if (auth && this._apiKey) headers.authorization = `Bearer ${this._apiKey}`;

    let payload;
    if (body !== undefined && body !== null) {
      payload = JSON.stringify(body);
      headers['content-type'] = 'application/json';
    }

    const key = idempotencyKey ?? options.idempotency_key;
    if (key != null && key !== '') headers['idempotency-key'] = String(key);

    // Retry-safety. GET is safe. A mutating call is safe ONLY when the endpoint itself declares
    // Idempotency-Key support AND the caller supplied one — blind-retrying a POST is how you
    // double-charge someone.
    const safe = method === 'GET' || (idempotent && headers['idempotency-key'] !== undefined);
    const maxAttempts = safe ? this.maxRetries + 1 : 1;

    let attempt = 0;
    let lastError;
    for (;;) {
      attempt += 1;
      let response;
      try {
        response = await this._fetch(url, {
          method,
          headers,
          body: payload,
          signal: withTimeout(signal, timeout),
        });
      } catch (cause) {
        const isAbort = cause?.name === 'AbortError' || cause?.name === 'TimeoutError';
        lastError = new ReceivifyConnectionError(
          isAbort
            ? `Receivify ${method} ${path} timed out after ${timeout}ms`
            : `Receivify ${method} ${path} failed: ${cause?.message || cause}`,
          { cause, timeout: isAbort },
        );
        if (signal?.aborted || !safe || attempt >= maxAttempts) throw lastError;
        await this._sleep(backoffMs(attempt, this.retryBaseMs, null));
        continue;
      }

      const status = response.status;
      const resHeaders = headersToObject(response.headers);
      const decoded = await decodeBody(response);

      if (status >= 400 && throwOnError) {
        const err = errorFromResponse(status, decoded, {
          headers: resHeaders,
          request: `${method} ${path}`,
        });
        const worthRetrying = status === 429 || status >= 500;
        if (safe && worthRetrying && attempt < maxAttempts) {
          await this._sleep(backoffMs(attempt, this.retryBaseMs, resHeaders));
          continue;
        }
        throw err;
      }

      return { status, headers: resHeaders, body: decoded };
    }
  }
}

/* ------------------------------------------------------------------ */

function enc(id) {
  if (id == null || id === '') throw new TypeError('Receivify: an id is required');
  return encodeURIComponent(String(id));
}

function pick(source, keys) {
  const out = {};
  if (!source || typeof source !== 'object') return out;
  for (const k of keys) if (source[k] !== undefined && source[k] !== null) out[k] = source[k];
  return out;
}

function buildQuery(query) {
  if (!query) return '';
  const usp = new URLSearchParams();
  for (const [k, v] of Object.entries(query)) {
    if (v === undefined || v === null) continue;
    usp.append(k, typeof v === 'boolean' ? String(v) : String(v));
  }
  const s = usp.toString();
  return s ? `?${s}` : '';
}

function lowerKeys(obj) {
  const out = {};
  if (!obj) return out;
  for (const [k, v] of Object.entries(obj)) if (v !== undefined && v !== null) out[k.toLowerCase()] = String(v);
  return out;
}

function headersToObject(headers) {
  const out = {};
  if (!headers) return out;
  if (typeof headers.forEach === 'function') {
    headers.forEach((value, name) => {
      out[String(name).toLowerCase()] = value;
    });
    return out;
  }
  for (const [k, v] of Object.entries(headers)) out[k.toLowerCase()] = v;
  return out;
}

async function decodeBody(response) {
  if (response.status === 204) return null;
  let text;
  try {
    text = await response.text();
  } catch {
    return null;
  }
  if (!text) return null;
  try {
    // JSON.parse leaves every money field as the STRING the API sent. Nothing here coerces.
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/**
 * Exponential backoff, honouring `Retry-After` and then `X-RateLimit-Reset` when the server
 * tells us exactly when to come back.
 */
function backoffMs(attempt, base, resHeaders) {
  const retryAfter = Number(resHeaders?.['retry-after']);
  if (Number.isFinite(retryAfter) && retryAfter > 0) {
    return Math.min(retryAfter * 1000, MAX_RETRY_DELAY_MS);
  }
  const reset = Number(resHeaders?.['x-ratelimit-reset']);
  if (Number.isFinite(reset) && reset > 0) {
    const waitMs = reset * 1000 - Date.now();
    if (waitMs > 0) return Math.min(waitMs, MAX_RETRY_DELAY_MS);
  }
  const exp = base * 2 ** (attempt - 1);
  const jitter = Math.random() * base;
  return Math.min(exp + jitter, MAX_RETRY_DELAY_MS);
}

function withTimeout(signal, timeout) {
  const timeoutSignal = Number.isFinite(timeout) && timeout > 0 ? AbortSignal.timeout(timeout) : null;
  if (signal && timeoutSignal) {
    // AbortSignal.any landed in Node 20.3; on 18.x fall back to a manual bridge.
    if (typeof AbortSignal.any === 'function') return AbortSignal.any([signal, timeoutSignal]);
    const controller = new AbortController();
    const abort = (reason) => controller.abort(reason);
    for (const s of [signal, timeoutSignal]) {
      if (s.aborted) abort(s.reason);
      else s.addEventListener('abort', () => abort(s.reason), { once: true });
    }
    return controller.signal;
  }
  return signal || timeoutSignal || undefined;
}
