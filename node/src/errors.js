/**
 * Typed errors for the frozen `ERROR_CODES` registry.
 *
 * Every API failure body is `{ error: "<code>", detail: "<sentence>" }`. Each code maps to a
 * subclass of {@link ReceivifyApiError}, and the raw `code` is ALWAYS preserved — a code this
 * SDK has never heard of falls back to `ReceivifyApiError` rather than crashing, so an old SDK
 * keeps working against a newer deployment.
 *
 * Branch on `err.code`, never on `err.detail`: the wording of `detail` is explicitly not part
 * of the frozen contract.
 */

/** Base class for everything this SDK throws. `catch (e) { if (e instanceof ReceivifyError) ... }` */
export class ReceivifyError extends Error {
  constructor(message, options) {
    super(message, options);
    this.name = new.target.name;
  }
}

/** The request never produced an HTTP response: DNS, TLS, socket, or a client-side timeout. */
export class ReceivifyConnectionError extends ReceivifyError {
  constructor(message, options = {}) {
    super(message, options);
    /** True when the failure was this SDK's own timeout rather than a transport error. */
    this.timeout = options.timeout === true;
  }
}

/** A webhook could not be authenticated or parsed. Never thrown by `verifyWebhook`, which returns a boolean. */
export class ReceivifyWebhookError extends ReceivifyError {
  constructor(message, reason) {
    super(message);
    /** One of: `no_secrets`, `bad_header`, `no_match`, `stale_timestamp`, `bad_json`, `bad_envelope`. */
    this.reason = reason;
  }
}

/**
 * An `{ error, detail }` body came back with a 4xx/5xx status.
 *
 * This is also the fallback for any error code this SDK does not recognise, so it is the class
 * to catch when you want "any API-level failure".
 */
export class ReceivifyApiError extends ReceivifyError {
  constructor(code, detail, meta = {}) {
    super(detail || code || 'Receivify API error');
    /** The raw `error` string from the body. Always preserved, even when unrecognised. */
    this.code = code ?? null;
    /** The human sentence from the body. For logs and developers, never for branching. */
    this.detail = detail ?? null;
    /** HTTP status code. */
    this.status = meta.status ?? null;
    /** Response headers as a plain lower-cased object. */
    this.headers = meta.headers ?? {};
    /** The decoded response body, whatever shape it was. */
    this.body = meta.body ?? null;
    /** The request method + path, for logs. Never contains the API key. */
    this.request = meta.request ?? null;
  }

  /** True for the codes the API documents as worth retrying. */
  get retryable() {
    return RETRYABLE_CODES.has(this.code) || (this.status != null && this.status >= 500);
  }
}

/* ------------------------------------------------------------------ *
 * One subclass per frozen code.
 * ------------------------------------------------------------------ */

/** 400 — malformed request: bad JSON, a missing field, or a value that failed validation. */
export class BadRequestError extends ReceivifyApiError {}
/** 400 — the forwarding ticker is unknown, misspelled, or not offered by this deployment. */
export class BadTickerError extends ReceivifyApiError {}
/** 400 — the destination address is not valid for that ticker/chain. */
export class BadDestinationError extends ReceivifyApiError {}
/** 400 — the asset is enabled but its settlement backend cannot accept it right now. */
export class AssetUnavailableError extends ReceivifyApiError {}
/** 400 — below the economic floor: forwarding gas would exceed the fee earned. */
export class AmountTooLowError extends ReceivifyApiError {}
/** 400 — the merchant account is suspended and cannot create new resources. */
export class SuspendedError extends ReceivifyApiError {}
/** 400 — a per-account quota for this resource is already at its maximum. */
export class LimitExceededError extends ReceivifyApiError {}
/** 400 — the supplied two-factor (TOTP) code is not valid. */
export class InvalidCodeError extends ReceivifyApiError {}
/** 400 — two-factor enrolment has not been started yet. */
export class NoSetupError extends ReceivifyApiError {}
/** 400 — two-factor authentication is not enabled on this account. */
export class NotEnabledError extends ReceivifyApiError {}
/** 400 — no registered, active webhook endpoint matched the request. */
export class NoTargetError extends ReceivifyApiError {}
/** 400 — a single-use confirmation token is invalid, expired, or already spent. */
export class InvalidTokenError extends ReceivifyApiError {}
/** 401 — missing, malformed, revoked or unknown API key / session cookie. */
export class UnauthorizedError extends ReceivifyApiError {}
/** 401 — an inbound provider webhook failed HMAC verification. Never returned to merchant callers. */
export class BadSignatureError extends ReceivifyApiError {}
/** 403 — self-serve signup is turned off on this deployment. */
export class SignupDisabledError extends ReceivifyApiError {}
/** 404 — no such resource, or it does not belong to the caller. The two are indistinguishable. */
export class NotFoundError extends ReceivifyApiError {}
/** 409 — a resource with that unique identity already exists. */
export class AlreadyExistsError extends ReceivifyApiError {}
/** 409 — the feature being enabled is already on. */
export class AlreadyEnabledError extends ReceivifyApiError {}
/** 409 — the resource exists but its state forbids this action. */
export class NotAllowedError extends ReceivifyApiError {}
/** 500 — an unhandled server error. Safe to retry. */
export class InternalError extends ReceivifyApiError {}
/** 502 — a node or payment provider we depend on failed. Retryable. */
export class UpstreamError extends ReceivifyApiError {}
/** 503 — no fresh FX rate is available to price the request. Retryable. */
export class RateUnavailableError extends ReceivifyApiError {}

/** 429 — back off. Carries the parsed `Retry-After` / `X-RateLimit-*` headers. */
export class RateLimitedError extends ReceivifyApiError {
  constructor(code, detail, meta = {}) {
    super(code, detail, meta);
    const h = this.headers || {};
    /** Seconds to wait, from `Retry-After`. `null` when absent. */
    this.retryAfter = intOrNull(h['retry-after']);
    /** Unix seconds at which the window resets, from `X-RateLimit-Reset`. */
    this.rateLimitReset = intOrNull(h['x-ratelimit-reset']);
    /** Requests allowed in the current window. */
    this.rateLimitLimit = intOrNull(h['x-ratelimit-limit']);
    /** Requests left in the current window. */
    this.rateLimitRemaining = intOrNull(h['x-ratelimit-remaining']);
  }
}

function intOrNull(v) {
  if (v == null || v === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? Math.trunc(n) : null;
}

/**
 * The frozen `ERROR_CODES` registry, in the spec's order.
 * Adding a code is an additive change; this SDK degrades to `ReceivifyApiError` for anything new.
 */
export const ERROR_CODES = Object.freeze([
  'bad_request',
  'bad_ticker',
  'bad_destination',
  'asset_unavailable',
  'amount_too_low',
  'suspended',
  'limit_exceeded',
  'invalid_code',
  'no_setup',
  'not_enabled',
  'no_target',
  'invalid_token',
  'unauthorized',
  'bad_signature',
  'signup_disabled',
  'not_found',
  'already_exists',
  'already_enabled',
  'not_allowed',
  'rate_limited',
  'internal',
  'upstream_error',
  'rate_unavailable',
]);

const ERROR_CLASSES = Object.freeze({
  bad_request: BadRequestError,
  bad_ticker: BadTickerError,
  bad_destination: BadDestinationError,
  asset_unavailable: AssetUnavailableError,
  amount_too_low: AmountTooLowError,
  suspended: SuspendedError,
  limit_exceeded: LimitExceededError,
  invalid_code: InvalidCodeError,
  no_setup: NoSetupError,
  not_enabled: NotEnabledError,
  no_target: NoTargetError,
  invalid_token: InvalidTokenError,
  unauthorized: UnauthorizedError,
  bad_signature: BadSignatureError,
  signup_disabled: SignupDisabledError,
  not_found: NotFoundError,
  already_exists: AlreadyExistsError,
  already_enabled: AlreadyEnabledError,
  not_allowed: NotAllowedError,
  rate_limited: RateLimitedError,
  internal: InternalError,
  upstream_error: UpstreamError,
  rate_unavailable: RateUnavailableError,
});

const RETRYABLE_CODES = new Set(['rate_limited', 'internal', 'upstream_error', 'rate_unavailable']);

/**
 * Build the right error for a failed response.
 * An unrecognised `code` — including one from a future API version — yields a plain
 * `ReceivifyApiError` with `code` intact.
 */
export function errorFromResponse(status, body, meta = {}) {
  const isObject = body !== null && typeof body === 'object' && !Array.isArray(body);
  const code = isObject && typeof body.error === 'string' ? body.error : null;
  const detail = isObject && typeof body.detail === 'string' ? body.detail : null;

  // `GET /api/health` is documented as NOT conforming to `{error, detail}` on its 503.
  const message = detail || (code ? `Receivify: ${code}` : `Receivify: HTTP ${status}`);

  const Cls = (code && ERROR_CLASSES[code]) || (status === 429 ? RateLimitedError : ReceivifyApiError);
  return new Cls(code, message, { ...meta, status, body });
}
