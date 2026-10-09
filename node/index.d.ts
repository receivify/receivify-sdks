/**
 * Type declarations for the official Receivify Node.js SDK.
 *
 * Hand-written against `openapi.yaml` (the frozen v1 contract). Two conventions run through
 * every type here and are worth internalising before you read further:
 *
 *  - **Every monetary quantity is a `string`.** Exact-integer base units (satoshis, wei, …), or
 *    a decimal string for the handful of display prices. Never a `number`: an IEEE-754 double
 *    cannot hold a 256-bit wei value or a satoshi count above 2^53, so parsing one silently
 *    corrupts a payment amount. `Ticker.decimals` is what you divide by, and `formatUnits` does
 *    it with BigInt.
 *  - **Open enums.** Statuses and event names are typed as a union `| (string & {})`, so a value
 *    added in a future additive API release still type-checks. Always keep a default branch.
 */

/* ================================================================== *
 * Primitives
 * ================================================================== */

/** A union that still accepts future additions — adding an enum member is an additive change. */
export type Open<T extends string> = T | (string & {});

/**
 * An exact-integer base-unit amount, as a string: satoshis for BTC, wei for ETH, the coin's
 * smallest unit otherwise. Divide by `10 ** decimals` only for display, and only via
 * {@link formatUnits}.
 */
export type BaseUnits = string;

/** A decimal display amount as a string, e.g. `"25.00"` or `"0.00123456"`. Not base units. */
export type DecimalString = string;

/** ISO-8601 UTC with milliseconds, e.g. `"2026-08-24T12:05:00.000Z"`. */
export type Timestamp = string;

/** The one and only list envelope. `has_more` is a fact, never inferred from `data.length`. */
export interface ListEnvelope<T> {
  /** The page, newest first. */
  data: T[];
  /** The query reads `limit + 1` rows, so this is a fact rather than a guess. */
  has_more: boolean;
  /** Pass back as `cursor`. `null` exactly when `has_more` is false. */
  next_cursor: string | null;
}

export interface PageParams {
  /** 1..200. Larger is clamped to 200; zero, negative or non-integer is a `400 bad_request`. */
  limit?: number;
  /** An opaque keyset cursor from a previous `next_cursor`. A malformed cursor is a 400. */
  cursor?: string;
}

/* ================================================================== *
 * Errors
 * ================================================================== */

/** The frozen `ERROR_CODES` registry. Adding a code is additive — always keep a fallback. */
export type ErrorCode =
  | 'bad_request'
  | 'bad_ticker'
  | 'bad_destination'
  | 'asset_unavailable'
  | 'amount_too_low'
  | 'suspended'
  | 'limit_exceeded'
  | 'invalid_code'
  | 'no_setup'
  | 'not_enabled'
  | 'no_target'
  | 'invalid_token'
  | 'unauthorized'
  | 'bad_signature'
  | 'signup_disabled'
  | 'not_found'
  | 'already_exists'
  | 'already_enabled'
  | 'not_allowed'
  | 'rate_limited'
  | 'internal'
  | 'upstream_error'
  | 'rate_unavailable';

export const ERROR_CODES: readonly ErrorCode[];

/** Base class for everything this SDK throws. */
export class ReceivifyError extends Error {}

/** No HTTP response was produced: DNS, TLS, socket, or this SDK's own timeout. */
export class ReceivifyConnectionError extends ReceivifyError {
  /** True when the failure was the configured timeout rather than a transport error. */
  readonly timeout: boolean;
}

/** A webhook failed verification or parsing. */
export class ReceivifyWebhookError extends ReceivifyError {
  readonly reason: 'no_secrets' | 'bad_header' | 'no_match' | 'stale_timestamp' | 'bad_json' | 'bad_envelope';
}

/**
 * An `{ error, detail }` body arrived with a 4xx/5xx status.
 * Also the fallback for any code this SDK does not recognise — `code` is always preserved.
 */
export class ReceivifyApiError extends ReceivifyError {
  /** The raw `error` string. Branch on THIS, never on `detail`. */
  readonly code: Open<ErrorCode> | null;
  /** A human sentence, for logs. Its wording is explicitly not part of the frozen contract. */
  readonly detail: string | null;
  readonly status: number | null;
  /** Response headers, lower-cased. */
  readonly headers: Record<string, string>;
  readonly body: unknown;
  /** `"<METHOD> <path>"`. Never contains the API key. */
  readonly request: string | null;
  /** True for `rate_limited`, `internal`, `upstream_error`, `rate_unavailable`, and any 5xx. */
  readonly retryable: boolean;
}

export class BadRequestError extends ReceivifyApiError {}
export class BadTickerError extends ReceivifyApiError {}
export class BadDestinationError extends ReceivifyApiError {}
export class AssetUnavailableError extends ReceivifyApiError {}
export class AmountTooLowError extends ReceivifyApiError {}
export class SuspendedError extends ReceivifyApiError {}
export class LimitExceededError extends ReceivifyApiError {}
export class InvalidCodeError extends ReceivifyApiError {}
export class NoSetupError extends ReceivifyApiError {}
export class NotEnabledError extends ReceivifyApiError {}
export class NoTargetError extends ReceivifyApiError {}
export class InvalidTokenError extends ReceivifyApiError {}
export class UnauthorizedError extends ReceivifyApiError {}
export class BadSignatureError extends ReceivifyApiError {}
export class SignupDisabledError extends ReceivifyApiError {}
export class NotFoundError extends ReceivifyApiError {}
export class AlreadyExistsError extends ReceivifyApiError {}
export class AlreadyEnabledError extends ReceivifyApiError {}
export class NotAllowedError extends ReceivifyApiError {}
export class InternalError extends ReceivifyApiError {}
export class UpstreamError extends ReceivifyApiError {}
export class RateUnavailableError extends ReceivifyApiError {}

/** 429. Carries the parsed rate-limit headers. */
export class RateLimitedError extends ReceivifyApiError {
  /** Seconds to wait, from `Retry-After`. */
  readonly retryAfter: number | null;
  /** Unix seconds at which the window resets. The only non-ISO time in the API. */
  readonly rateLimitReset: number | null;
  readonly rateLimitLimit: number | null;
  readonly rateLimitRemaining: number | null;
}

export function errorFromResponse(status: number, body: unknown, meta?: Record<string, unknown>): ReceivifyApiError;

/* ================================================================== *
 * Resources
 * ================================================================== */

export type InvoiceStatus = Open<
  'pending' | 'partial' | 'confirming' | 'paid' | 'swept' | 'expired' | 'partial_expired' | 'cancelled' | 'failed'
>;

export type Provider = Open<'bitcoind' | 'btcpay' | 'evm' | 'solana' | 'tron'>;

/** The invoice, identical on create, retrieve, list and cancel. */
export interface Invoice {
  id: string;
  /**
   * Your business key. It is ALSO a de-duplication key: creating a second invoice with a
   * reference you have already used returns the EXISTING invoice, still with a 201.
   */
  reference: string | null;
  /** Lower-case symbol, e.g. `btc`, `usdc`. */
  asset: string;
  /** Exact-integer base units. The authoritative amount. */
  amount_expected: BaseUnits;
  /** Exact-integer base units. `"0"` until something arrives. */
  amount_received: BaseUnits;
  /** A DECIMAL string like `"25.00"` — display information only. Never settle against it. */
  amount_fiat: DecimalString | null;
  fiat_currency: string | null;
  /** The EVM chain, `solana`/`tron`, or `null` for BTC and BTCPay-settled coins. */
  chain: string | null;
  provider: Provider | null;
  /** The deposit address. Send exactly this asset on exactly this chain. */
  address: string;
  status: InvoiceStatus;
  expires_at: Timestamp;
  paid_at: Timestamp | null;
  created_at: Timestamp;
  /** Always an object, never null — an omitted `metadata` comes back as `{}`. */
  metadata: Record<string, unknown>;
  checkout_url: string;
  /** Decimal places of this asset — the divisor for `amount_expected`. Per-token on EVM. */
  decimals?: number;
  /** `amount_expected` already divided by `10 ** decimals`, as a decimal STRING. Display only. */
  amount_decimal?: DecimalString;
  /** Wallet-scannable URI for a QR code, or `null` when the chain has no safe one. */
  payment_uri?: string | null;
  /** Whether the commission was added ON TOP of the requested amount. */
  add_fee_ontop?: boolean;
  /** What you asked to RECEIVE, in base units. Equals `amount_expected` when `add_fee_ontop` is false. */
  amount_net_requested?: BaseUnits;
  /** `amount_expected - amount_net_requested`, in base units. `"0"` when `add_fee_ontop` is false. */
  fee_ontop?: BaseUnits;
  [key: string]: unknown;
}

export interface InvoiceCreateParams {
  /** Lower-cased. Defaults to `btc`. Must be enabled AND settleable on this deployment. */
  asset?: string;
  /** Only consulted for EVM assets, but defaulted to `ethereum` for all. */
  chain?: string;
  /**
   * A fiat price, converted at the current rate and LOCKED onto the invoice. Highest precedence
   * of the four amount fields. Prefer a string: a float here is a float on the wire.
   */
  amount_fiat?: DecimalString | number;
  /** ISO-4217 for `amount_fiat`. Defaults to USD in the pricing layer. */
  currency?: string;
  /** A decimal amount in the coin's own units. */
  amount?: DecimalString;
  /** Exact base units as an integer string. Named `sats` historically; applies to every asset. */
  amount_sats?: BaseUnits;
  /** Legacy. Only honoured when `asset` is `btc`. */
  amount_btc?: DecimalString;
  reference?: string;
  /** Per-invoice webhook target, in addition to every account-level endpoint. https in production. */
  callback_url?: string;
  metadata?: Record<string, unknown>;
  /**
   * `true` grosses the amount up so the payer covers the commission and you receive the amount
   * you asked for. `false` forces fee-out-of-amount. OMIT it to use the account default — omitted
   * is not the same as `false`. Must be a real boolean; a string is `400 bad_request`.
   */
  add_fee_ontop?: boolean;
}

export interface InvoiceListParams extends PageParams {
  /** Exact match. NOT validated — an unknown value simply matches nothing (unlike the forward list). */
  status?: InvoiceStatus;
}

export interface BulkResult {
  ok: boolean;
  action: 'cancel';
  /** How many ids survived the string filter and were submitted. */
  requested: number;
  /** How many were actually eligible and changed. The response does not say which. */
  cancelled: number;
}

/** The checkout-page projection: no reference, metadata, asset, chain or merchant. */
export interface PublicInvoice {
  id: string;
  status: InvoiceStatus;
  address: string;
  amount_expected: BaseUnits;
  amount_received: BaseUnits;
  expires_at: Timestamp;
  [key: string]: unknown;
}

export type ForwardStatus = Open<'active' | 'paused' | 'disabled' | 'expired'>;

/** A standing deposit address that sweeps everything it receives to `destination`. */
export interface Forward {
  /** **The canonical identifier.** Read this one. */
  id: string;
  /**
   * @deprecated Permanent alias of {@link Forward.id}, carrying the identical value. Kept for
   * the whole life of v1 because removing a response field would be breaking. Read `id`.
   */
  forward_id: string;
  /** Exactly as you supplied it at create time, lower-cased — not the canonical form. */
  ticker: string;
  provider: Provider;
  /** `null` for `btc` and the BTCPay-settled coins. */
  chain: string | null;
  /** The deposit address to hand to payers. */
  address: string;
  /** Your wallet. IMMUTABLE — a PATCH that touches it is `409 not_allowed`. */
  destination: string;
  /** Receives `forward.*` events only. Account-level endpoints do NOT get them. */
  callback_url: string | null;
  confirmations_required: number;
  /**
   * **The one money field here that is NOT base units** — a DISPLAY-unit decimal string
   * (`"0.500000"`). Multiply by `10 ** decimals` before comparing with a `ForwardPayment.value`.
   */
  min_forward: DecimalString;
  status: ForwardStatus;
  /** `null` for every permanent address; a timestamp only for single-session BTCPay coins. */
  expires_at: Timestamp | null;
  created_at: Timestamp;
  /** `null` on any forward that has never been patched. */
  updated_at: Timestamp | null;
  [key: string]: unknown;
}

export interface ForwardPayment {
  txid_in: string;
  /** Gross received, exact-integer base units. */
  value: BaseUnits;
  status: Open<'seen' | 'confirmed' | 'forwarding' | 'sent' | 'failed' | 'needs_review' | 'orphaned' | 'invalidated'>;
  /** The forwarding transaction, once broadcast. */
  txid_out: string | null;
  /** `0` when unknown, never null. */
  confirmations: number;
  created_at: Timestamp;
}

export interface ForwardCallback {
  event: EventName;
  url: string;
  status: Open<'pending' | 'delivered' | 'failed'>;
  attempts: number;
  last_status: number | null;
  /** Truncated to 300 characters. */
  last_error: string | null;
  created_at: Timestamp;
  delivered_at: Timestamp | null;
}

/** `GET /api/v1/address/{id}`: the `Forward` fields SPREAD at the top level, plus four keys. */
export interface ForwardDetail extends Forward {
  /** Newest first, capped at 200. Not paginated — there is no cursor for it. */
  payments: ForwardPayment[];
  /** True when the 200 cap truncated `payments`. */
  payments_has_more: boolean;
  /** Newest first, capped at 50. Not paginated. */
  callbacks: ForwardCallback[];
  /** True when the 50 cap truncated `callbacks`. */
  callbacks_has_more: boolean;
}

/**
 * Alias-tolerant. `ticker`/`coin`/`currency`/`asset`, `destination`/`address`/`to`/`payout_address`,
 * `callback`/`callback_url`/`webhook`/`webhook_url`/`url`, `confirmations`/`confs` all work.
 * (The invoice API accepts none of these aliases.)
 */
export interface ForwardCreateParams {
  ticker: string;
  /** Validated against the chain's address format. EVM destinations dedupe case-insensitively. */
  destination: string;
  /** Part of the deterministic identity — a different callback creates a DIFFERENT forward. */
  callback?: string;
  callback_url?: string;
  /** A non-integer or non-positive value falls back to the deployment default rather than erroring. */
  confirmations?: number;
  metadata?: Record<string, unknown>;
  [key: string]: unknown;
}

export interface ForwardUpdateParams {
  /** Only these two are assignable. `expired` is a fact; use `disableAddress()` for `disabled`. */
  status?: 'active' | 'paused';
  /** `null` or `""` clears it. Changing it recomputes the dedupe identity — may be `409 already_exists`. */
  callback_url?: string | null;
}

export interface ForwardListParams extends PageParams {
  /** Validated against the enum — an unknown value is a `400 bad_request`. */
  status?: ForwardStatus;
  /** Exact match on the ticker string as supplied at create time, lower-cased. */
  ticker?: string;
}

export interface Ticker {
  /** A bare symbol resolves to the asset's DEFAULT chain; `<symbol>_<chain>` selects another. */
  ticker: string;
  provider: Provider;
  chain: string | null;
  name: string;
  /** Base units per whole coin, as a power of ten. The number to divide by for display. */
  decimals: number;
}

/** DISPLAY metadata, not a machine contract. */
export interface Asset {
  symbol: string;
  name: string;
  /** A human sentence, e.g. `ETH · Base · Arbitrum`. Not a machine-readable chain list. */
  network: string;
  /** Brand hex colour for UI use. */
  color: string;
  /** Either a decimal string OR the literal word `"network"`. Not unconditionally parseable. */
  min: string;
  /** English prose: `"~0.000001 BTC"`, `"dynamic gas"`. Display only. */
  network_fee: string;
  /** `false` means catalogue-only, not wired end to end. */
  live: boolean;
}

/** ONE shape for every payment-link operation. */
export interface PaymentLink {
  /** 8 base64url characters, not a UUID. */
  id: string;
  title: string | null;
  /**
   * Exact-integer base units for a fixed link; `null` for an open-amount one. Note the asymmetry:
   * you SET the amount with `amount_btc` (decimal BTC) and READ it as `amount_sats`.
   */
  amount_sats: BaseUnits | null;
  open_amount: boolean;
  active: boolean;
  uses: number;
  created_at: Timestamp;
  updated_at: Timestamp | null;
  url: string;
  [key: string]: unknown;
}

export interface PaymentLinkCreateParams {
  /** Truncated to 120 characters. */
  title?: string;
  /** A decimal BTC amount. Omit, or send `""`, for an open-amount link. */
  amount_btc?: DecimalString;
}

export interface PaymentLinkUpdateParams {
  /** Must be a real boolean — a string is a `400 bad_request`. */
  active?: boolean;
  title?: string | null;
  /** `null` or `""` converts a fixed link into an open-amount one. */
  amount_btc?: DecimalString | null;
}

export interface PaymentLinkListParams extends PageParams {
  /** `true` also matches links created before the field existed. Anything but `true`/`false` is a 400. */
  active?: boolean;
}

export interface PublicPaymentLink {
  id: string;
  title: string | null;
  open_amount: boolean;
  /** **A decimal BTC string, not base units.** `null` for an open-amount link. */
  amount_btc: DecimalString | null;
  assets: Array<{ symbol: string; name: string; color: string; decimals: number }>;
  /** Advisory floating-point rates. Display only. */
  fx: { crypto: Record<string, number>; fiat: Record<string, number> };
}

export interface RecurringSchedule {
  id: string;
  title: string | null;
  asset: string;
  /** A DECIMAL string like `"25.00"`. Null when the schedule is priced in the coin. */
  amount_fiat: DecimalString | null;
  currency: string | null;
  /** A DECIMAL amount in the coin — NOT base units. Null when priced in fiat. */
  amount: DecimalString | null;
  interval_days: number;
  active: boolean;
  runs: number;
  /** Set to the creation instant, so the first invoice is generated on the next worker tick. */
  next_run: Timestamp;
  last_invoice_id: string | null;
  created_at: Timestamp;
}

export interface RecurringCreateParams {
  title?: string;
  asset?: string;
  amount_fiat?: DecimalString | number;
  currency?: string;
  /** A decimal amount in the coin. Supply this OR `amount_fiat`; neither is a `400 bad_request`. */
  amount?: DecimalString;
  /** Floored and clamped to at least 1. */
  interval_days: number;
  /** Copied onto every generated invoice. NOT validated at create time, unlike the invoice API. */
  callback_url?: string;
}

export interface ApiKey {
  /** The identifier `revokeApiKey()` matches on — NOT the prefix. */
  id: string;
  prefix: string;
  label: string | null;
  created_at: Timestamp;
  last_used_at: Timestamp | null;
  revoked: boolean;
}

export interface ApiKeyCreated {
  id: string;
  /** The raw secret, shown exactly once. **`null` on an `Idempotency-Key` replay.** */
  api_key: string | null;
  /** Legacy name for `prefix`. Both are emitted. */
  api_key_prefix: string;
  prefix: string;
  label: string | null;
  created_at: Timestamp | null;
  /** Informational only — do not branch on it. */
  message: string;
}

export interface WebhookEndpoint {
  id: string;
  /** Normalised through the WHATWG URL parser, so it may differ from what you sent. */
  url: string;
  /** Inactive endpoints receive nothing. */
  active: boolean;
  created_at: Timestamp;
  updated_at: Timestamp | null;
}

export interface WebhookDelivery {
  /** A 24-character hex ObjectId — the only id in this API that is not a UUID or short token. */
  id: string;
  /** Set on `invoice.*` / `sweep.sent` rows; explicitly `null` (never absent) on a `forward.*` row. */
  invoice_id: string | null;
  /** Set on `forward.*` rows. Exactly one of the two is non-null, so a failure is always attributable. */
  forward_id: string | null;
  event: EventName;
  status: Open<'pending' | 'delivered' | 'failed'>;
  attempts: number;
  url: string;
  last_status: number | null;
  last_error: string | null;
  created_at: Timestamp;
  delivered_at: Timestamp | null;
}

export interface WebhookDeliveryListParams extends PageParams {
  /** Validated against `EventName`; a typo is a `400 bad_request`, not an empty page. */
  event?: EventName;
  status?: 'pending' | 'delivered' | 'failed';
}

/** Rotation metadata. Carries NO secret material — readable with a plain API key. */
export interface WebhookSecretState {
  /** `null` on an account still using the secret it was issued at signup. */
  rotated_at: Timestamp | null;
  /** `null` when there is no previous secret, and also once the grace window has passed. */
  prev_expires_at: Timestamp | null;
  /** **While true, your receiver must accept EITHER secret.** */
  prev_active: boolean;
}

export interface WebhookSecretRotated {
  /** The raw `whsec_…` secret, shown ONCE. **`null` on an `Idempotency-Key` replay.** */
  secret: string | null;
  rotated_at: Timestamp;
  /** Until this instant, verify against the new secret and fall back to the previous one. */
  prev_expires_at: Timestamp | null;
  /** Length of the grace window. 86400 (24h) by default. */
  grace_seconds: number;
  /** True when this rotation retired a previous secret that was still inside its grace window. */
  replaced_previous: boolean;
  message: string;
}

export interface WebhookTestResult {
  event: 'test';
  /** How many targets answered 2xx. This endpoint returns 200 even when every delivery failed. */
  delivered: number;
  total: number;
  results: Array<{ url: string; ok: boolean; status: number | null; error?: string }>;
}

export interface PayoutChange {
  id: string;
  asset: string;
  /** An empty string means "clear this asset's payout address". */
  new_address: string;
  old_address: string | null;
  /** `pending_email` and `confirmed` are the OPEN states that appear in `pending` lists. */
  status: Open<'pending_email' | 'confirmed' | 'applied' | 'cancelled'>;
  requested_at: Timestamp;
  /** When the timelock elapses. Confirming by email does not move this. */
  effective_at: Timestamp;
  confirmed_at: Timestamp | null;
  applied_at: Timestamp | null;
}

export interface PayoutState {
  /** The LIVE map the sweeper reads. Staged changes are not in here until they go live. */
  payout_addresses: Record<string, string>;
  /** Every open change. **NOT a list envelope** and not bounded — no `has_more`, no cursor. */
  pending: PayoutChange[];
  timelock_hours: number;
  email_confirmation: boolean;
  /** True only when the timelock is zero AND email confirmation is off. */
  instant: boolean;
}

export interface PayoutStageAck {
  ok: boolean;
  /** Changes created by THIS call. An address equal to the live one is a no-op and appears nowhere. */
  staged: PayoutChange[];
  /** Non-empty only on an `instant` deployment. */
  applied: PayoutChange[];
  pending: PayoutChange[];
  timelock_hours: number;
  email_confirmation: boolean;
  message: string;
}

export interface Settings {
  display_currency: string;
  email: string;
  /** An INTEGER — it is a rate, not money. The per-invoice rate is resolved and snapshotted separately. */
  fee_bps: number;
}

/** An ACK, not the `Settings` resource: only the fields actually written come back. */
export interface SettingsUpdateAck {
  ok: boolean;
  display_currency?: string;
}

/** **Advisory only.** Every rate is a floating-point NUMBER — a rate is not money. */
export interface FxSnapshot {
  base: string;
  crypto: Record<string, number>;
  fiat: Record<string, number>;
  fetched_at: Timestamp | null;
  source: string | null;
  /** A stale snapshot is still returned. */
  stale: boolean;
}

export interface Summary {
  merchant: {
    id: string;
    email: string;
    payout_address?: string | null;
    payout_addresses: Record<string, string>;
    fee_bps: number;
    api_key_prefix: string | null;
    display_currency: string;
  };
  counts: Record<string, number> & { total?: number };
  by_asset: Array<{
    symbol: string;
    name: string;
    color: string;
    decimals: number;
    count: number;
    paid_count: number;
    /** Exact-integer base units as a string. Paid and swept invoices only. */
    volume_base: BaseUnits;
    fee_base: BaseUnits;
    /** `volume_base - fee_base`, exact-integer base units. */
    net_base: BaseUnits;
  }>;
  by_status: Record<string, number>;
  /** Exactly 30 entries, oldest first, gap-filled. */
  series: Array<{ day: string; assets: Record<string, BaseUnits> }>;
  fx: FxSnapshot;
  currencies: string[];
}

/**
 * The operator health report. **Returned unchanged on both 200 and 503** — a 503 is NOT an
 * `{error, detail}` body, which is why {@link Receivify.getHealth} returns it rather than throwing.
 */
export interface Health {
  status: Open<'ok' | 'warn' | 'degraded'>;
  time: Timestamp;
  checks: {
    mongo: { ok: boolean; error?: string };
    bitcoind?: { ok: boolean; height?: number; processed_height?: number | null; indexer_lag?: number | null; error?: string };
    fx?: { ok: boolean; source?: string | null; age_seconds?: number | null; stale?: boolean; error?: string };
    queues?: {
      ok: boolean;
      webhooks_pending?: number;
      sweeps_queued?: number;
      oldest_queued_age_seconds?: number | null;
      error?: string;
    };
  };
  alerts: Array<{ level: 'warn'; signal: Open<'indexer_lag' | 'fx_stale' | 'sweep_queue_stale'>; value?: number | null; threshold?: number }>;
}

export interface CancelledAck {
  ok: boolean;
  /** Echoed from the path, not re-read from the database. */
  cancelled: string;
}
export interface RevokedAck {
  ok: boolean;
  revoked: string;
}
export interface DeletedAck {
  ok: boolean;
  deleted: string;
}
export interface ReplayedAck {
  ok: boolean;
  replayed: string;
}

/* ================================================================== *
 * Webhook events
 * ================================================================== */

/** The frozen event registry. Receivers MUST ignore names they do not recognise. */
export type EventName = Open<
  | 'invoice.confirming'
  | 'invoice.partial'
  | 'invoice.paid'
  | 'invoice.swept'
  | 'invoice.expired'
  | 'invoice.partial_expired'
  | 'invoice.cancelled'
  | 'invoice.failed'
  | 'sweep.sent'
  | 'forward.confirmed'
  | 'forward.sent'
  | 'test'
>;

export const EVENTS: readonly string[];
export const SIGNATURE_HEADER: 'X-Receivify-Signature';
export const DEFAULT_TOLERANCE_SECONDS: 300;

/**
 * `data` for `invoice.*`, `sweep.sent` and `test`. A deliberately narrow projection — it carries
 * NO `asset` or `chain`, so a multi-coin merchant must call `getInvoice(id)` to learn the coin.
 */
export interface InvoiceEventData {
  id: string;
  reference: string | null;
  status: InvoiceStatus;
  amount_expected: BaseUnits;
  amount_received: BaseUnits;
  address: string;
  paid_at: Timestamp | null;
  [key: string]: unknown;
}

/**
 * `data` for `forward.confirmed` / `forward.sent`. CryptAPI-shaped: `address_in` is the deposit
 * address (the REST resource calls it `address`), `address_out` the destination.
 */
export interface ForwardEventData {
  /** Here this names a RELATIONSHIP to a forward, not a deprecated alias — it is the right field. */
  forward_id: string;
  ticker: string;
  address_in: string;
  address_out: string;
  txid_in: string;
  /** Null on `forward.confirmed`; populated on `forward.sent`. */
  txid_out: string | null;
  /** Gross received, exact-integer base units. */
  value: BaseUnits;
  /** Net sent to the destination, exact-integer base units. */
  value_forwarded: BaseUnits;
  /** Fee retained, exact-integer base units. */
  fee: BaseUnits;
  confirmations: number;
  /** **Dead field.** Hard-coded `false` on every forward event ever sent. */
  pending: boolean;
  [key: string]: unknown;
}

/** The unified envelope. Switch on `event` first; only `data` differs between families. */
export interface WebhookEvent<D = InvoiceEventData | ForwardEventData> {
  event: EventName;
  /**
   * A globally monotonic counter allocated at enqueue time. NOT per-resource. `(event, sequence)`
   * is the de-duplication key — there is no separate event id.
   */
  sequence: number;
  /** When the EVENT happened. Frozen: a retry and a replay carry the original value. */
  created_at: Timestamp;
  data: D;
}

export type InvoiceWebhookEvent = WebhookEvent<InvoiceEventData>;
export type ForwardWebhookEvent = WebhookEvent<ForwardEventData>;

export interface VerifyOptions {
  /** Replay window. Defaults to 300. Pass `0` or `Infinity` to disable. */
  toleranceSeconds?: number;
  /** Epoch SECONDS or a Date. For tests. */
  now?: number | Date;
}

export interface VerificationResult {
  ok: boolean;
  reason: 'no_secrets' | 'bad_header' | 'no_match' | 'stale_timestamp' | null;
  message: string;
  timestamp?: number;
}

/**
 * Verify a signed delivery. Returns a boolean; never throws on a bad signature.
 *
 * **`rawBody` must be the EXACT bytes.** `JSON.stringify(req.body)` re-serialises and will not
 * reproduce the HMAC. Pass a `string`, `Buffer` or `Uint8Array`.
 *
 * **`secrets` is a LIST.** During a rotation two secrets are legitimately valid; pass the current
 * one first and the previous one second until `prev_expires_at`.
 */
export function verifyWebhook(
  rawBody: string | Uint8Array,
  signatureHeader: string | string[] | null | undefined,
  secrets: string | string[] | null | undefined,
  options?: VerifyOptions,
): boolean;

/** Verify and JSON-parse in one step. Throws {@link ReceivifyWebhookError} on any failure. */
export function parseWebhook<D = InvoiceEventData | ForwardEventData>(
  rawBody: string | Uint8Array,
  signatureHeader: string | string[] | null | undefined,
  secrets: string | string[] | null | undefined,
  options?: VerifyOptions,
): WebhookEvent<D>;

/** The same check as {@link verifyWebhook}, with a machine-readable reason for your logs. */
export function describeVerification(
  rawBody: string | Uint8Array,
  signatureHeader: string | string[] | null | undefined,
  secrets: string | string[] | null | undefined,
  options?: VerifyOptions,
): VerificationResult;

/** Produce a `t=…,v1=…` header the way Receivify does. For tests and fixtures only. */
export function signWebhook(rawBody: string | Uint8Array, secret: string, timestamp?: number): string;

/** The resource a verified event carries. Tolerates the pre-envelope `{invoice: …}` shape. */
export function webhookData<D = InvoiceEventData | ForwardEventData>(event: unknown): D;

/** The monotonic counter. Store the highest applied per resource and ignore anything at or below it. */
export function webhookSequence(event: unknown): number;

/** True for `invoice.*`, `sweep.sent` and `test` — the invoice-shaped `data`. */
export function isInvoiceEvent(event: unknown): event is InvoiceWebhookEvent;

/** True for `forward.confirmed` / `forward.sent` — the forward-payment-shaped `data`. */
export function isForwardEvent(event: unknown): event is ForwardWebhookEvent;

/* ================================================================== *
 * Money helpers (opt-in — the SDK never calls them for you)
 * ================================================================== */

/** Exact base units -> a display decimal string. Pure BigInt: `formatUnits('123456', 8) === '0.00123456'`. */
export function formatUnits(base: BaseUnits | bigint, decimals: number): DecimalString;

/** A decimal string -> exact base units. Throws rather than rounding away precision. */
export function parseUnits(value: DecimalString | number, decimals: number): BaseUnits;

/** Compare two base-unit strings without a Number. Returns -1, 0 or 1. */
export function compareAmounts(a: BaseUnits | bigint, b: BaseUnits | bigint): -1 | 0 | 1;

/** Base-unit string -> BigInt. The safe way to do arithmetic on an amount. */
export function toBigInt(value: BaseUnits | bigint, who?: string): bigint;

/* ================================================================== *
 * Client
 * ================================================================== */

/**
 * The transport contract. Global `fetch` satisfies it; so does a hand-rolled stub in a test,
 * which is how the SDK's own suite runs entirely offline.
 */
export type FetchLike = (
  url: string,
  init: {
    method: string;
    headers: Record<string, string>;
    body?: string;
    signal?: AbortSignal;
  },
) => Promise<{
  status: number;
  headers: { forEach(cb: (value: string, name: string) => void): void };
  text(): Promise<string>;
}>;

export interface ReceivifyOptions {
  /** Per-request timeout in ms. Default 30000. */
  timeout?: number;
  /** Current secret first, previous second. A bare string is normalised to a one-element list. */
  webhookSecrets?: string | string[];
  /** Alias of `webhookSecrets` for a single secret. */
  webhookSecret?: string;
  /** Inject a transport — tests, proxies, instrumentation. Defaults to global `fetch`. */
  fetch?: FetchLike;
  /** Retries for SAFE requests only. Default 2. */
  maxRetries?: number;
  /** First backoff step in ms; doubles each attempt. Default 500. */
  retryBaseMs?: number;
  /** Appended to `receivify-node/<version>`. */
  userAgent?: string;
  /** Inject a clock. For tests. */
  sleep?: (ms: number) => Promise<void>;
}

/** Per-call options accepted by every method. */
export interface RequestOptions {
  /**
   * Sent as `Idempotency-Key`. Scoped to `(merchant, endpoint, key)` for 24h; a replay returns the
   * ORIGINAL body and status plus `Idempotent-Replay: true`.
   *
   * Only the endpoints that declare the header in `openapi.yaml` honour it — `createInvoice`,
   * `createAddress`, `createApiKey`, `createPaymentLink`, `createRecurring`,
   * `createWebhookEndpoint`, `rotateWebhookSecret`. Supplying it elsewhere is harmless but does
   * nothing, and this SDK will NOT treat such a call as retry-safe.
   */
  idempotencyKey?: string;
  /** Override the client timeout for this call. */
  timeout?: number;
  /** Your own cancellation signal, combined with the timeout. */
  signal?: AbortSignal;
  /** Extra request headers. */
  headers?: Record<string, string>;
}

/** `{ status, headers, body }` — for when you need `Idempotent-Replay` or a 200-vs-201 distinction. */
export interface RawResponse<T> {
  status: number;
  /** Lower-cased header names. */
  headers: Record<string, string>;
  body: T;
}

export class Receivify {
  /**
   * @param apiUrl e.g. `https://receivify.io`. A trailing slash is trimmed.
   * @param apiKey the merchant `rcv_live_…` key. Optional if you only call public routes.
   *               Never logged, never placed in a URL, and not enumerable on the instance.
   */
  constructor(apiUrl: string, apiKey?: string | null, opts?: ReceivifyOptions);

  readonly apiUrl: string;
  readonly timeout: number;
  readonly maxRetries: number;
  /** Always an array, whatever you passed in. */
  readonly webhookSecrets: string[];
  readonly userAgent: string;
  static readonly VERSION: string;

  /* -- Invoices -- */
  createInvoice(params: InvoiceCreateParams, opts?: RequestOptions): Promise<Invoice>;
  getInvoice(id: string, opts?: RequestOptions): Promise<Invoice>;
  listInvoices(params?: InvoiceListParams, opts?: RequestOptions): Promise<ListEnvelope<Invoice>>;
  /** Auto-paginating: `for await (const inv of client.invoices({ status: 'paid' }))`. */
  invoices(params?: InvoiceListParams, opts?: RequestOptions): AsyncGenerator<Invoice>;
  /** NOT idempotent — a second call on an already-cancelled invoice is `409 not_allowed`. */
  cancelInvoice(id: string, opts?: RequestOptions): Promise<Invoice>;
  /** Max 500 ids. Compare `requested` with `cancelled` to see how many were eligible. */
  bulkCancelInvoices(ids: string[], opts?: RequestOptions): Promise<BulkResult>;
  /** Unauthenticated, minimal projection. Safe to poll from a thank-you page. */
  getPublicInvoice(id: string, opts?: RequestOptions): Promise<PublicInvoice>;

  /* -- Forwarding addresses -- */
  /** **201** = newly derived, **200** = an existing forward with the same identity. Read `.id`. */
  createAddress(params: ForwardCreateParams, opts?: RequestOptions): Promise<Forward>;
  getAddress(id: string, opts?: RequestOptions): Promise<ForwardDetail>;
  listAddresses(params?: ForwardListParams, opts?: RequestOptions): Promise<ListEnvelope<Forward>>;
  /** Auto-paginating `listAddresses`. */
  addresses(params?: ForwardListParams, opts?: RequestOptions): AsyncGenerator<Forward>;
  /** The kill switch. `destination`/`ticker` are immutable — either is `409 not_allowed`. */
  updateAddress(id: string, params: ForwardUpdateParams, opts?: RequestOptions): Promise<Forward>;
  /** Soft delete. Idempotent; the row is never removed because money can still arrive. */
  disableAddress(id: string, opts?: RequestOptions): Promise<Forward>;

  /** Alias of {@link createAddress} (the spec's `createForward`). */
  createForward(params: ForwardCreateParams, opts?: RequestOptions): Promise<Forward>;
  /** Alias of {@link getAddress}. */
  getForward(id: string, opts?: RequestOptions): Promise<ForwardDetail>;
  /** Alias of {@link listAddresses}. */
  listForwards(params?: ForwardListParams, opts?: RequestOptions): Promise<ListEnvelope<Forward>>;
  /** Alias of {@link updateAddress}. */
  updateForward(id: string, params: ForwardUpdateParams, opts?: RequestOptions): Promise<Forward>;
  /** Alias of {@link disableAddress}. */
  disableForward(id: string, opts?: RequestOptions): Promise<Forward>;
  /** Alias of {@link addresses}. */
  forwards(params?: ForwardListParams, opts?: RequestOptions): AsyncGenerator<Forward>;

  /* -- Catalogue (public) -- */
  listTickers(opts?: RequestOptions): Promise<ListEnvelope<Ticker>>;
  listAssets(opts?: RequestOptions): Promise<ListEnvelope<Asset>>;

  /* -- Webhooks -- */
  listWebhookEndpoints(params?: PageParams, opts?: RequestOptions): Promise<ListEnvelope<WebhookEndpoint>>;
  webhookEndpoints(params?: PageParams, opts?: RequestOptions): AsyncGenerator<WebhookEndpoint>;
  /** Max 20 per account; the quota failure is a **400 `limit_exceeded`**, not a 409. */
  createWebhookEndpoint(url: string | { url: string }, opts?: RequestOptions): Promise<WebhookEndpoint>;
  updateWebhookEndpoint(
    id: string,
    params: { active?: boolean; url?: string },
    opts?: RequestOptions,
  ): Promise<WebhookEndpoint>;
  /** A HARD delete. Not idempotent: a second call is 404. Prefer `{active:false}` if unsure. */
  deleteWebhookEndpoint(id: string, opts?: RequestOptions): Promise<DeletedAck>;
  listWebhookDeliveries(
    params?: WebhookDeliveryListParams,
    opts?: RequestOptions,
  ): Promise<ListEnvelope<WebhookDelivery>>;
  webhookDeliveries(params?: WebhookDeliveryListParams, opts?: RequestOptions): AsyncGenerator<WebhookDelivery>;
  /** Re-queues the FROZEN original body, with the original `sequence` and `created_at`. */
  replayWebhookDelivery(id: string, opts?: RequestOptions): Promise<ReplayedAck>;
  getWebhookSecretState(opts?: RequestOptions): Promise<WebhookSecretState>;
  /** Returns the new secret ONCE. Keep verifying with the previous one until `prev_expires_at`. */
  rotateWebhookSecret(opts?: RequestOptions): Promise<WebhookSecretRotated>;
  sendTestWebhook(params?: string | { url?: string }, opts?: RequestOptions): Promise<WebhookTestResult>;

  /* -- Payment links -- */
  listPaymentLinks(params?: PaymentLinkListParams, opts?: RequestOptions): Promise<ListEnvelope<PaymentLink>>;
  paymentLinks(params?: PaymentLinkListParams, opts?: RequestOptions): AsyncGenerator<PaymentLink>;
  createPaymentLink(params?: PaymentLinkCreateParams, opts?: RequestOptions): Promise<PaymentLink>;
  getPaymentLink(id: string, opts?: RequestOptions): Promise<PaymentLink>;
  updatePaymentLink(id: string, params: PaymentLinkUpdateParams, opts?: RequestOptions): Promise<PaymentLink>;
  /** Soft deactivate. Invoices already minted from the link stay payable. */
  deactivatePaymentLink(id: string, opts?: RequestOptions): Promise<PaymentLink>;
  /** Unauthenticated. A deactivated link answers 404, not 409. */
  getPublicPaymentLink(id: string, opts?: RequestOptions): Promise<PublicPaymentLink>;

  /* -- Recurring -- */
  listRecurring(params?: PageParams, opts?: RequestOptions): Promise<ListEnvelope<RecurringSchedule>>;
  recurring(params?: PageParams, opts?: RequestOptions): AsyncGenerator<RecurringSchedule>;
  createRecurring(params: RecurringCreateParams, opts?: RequestOptions): Promise<RecurringSchedule>;
  cancelRecurring(id: string, opts?: RequestOptions): Promise<CancelledAck>;

  /* -- API keys -- */
  listApiKeys(params?: PageParams, opts?: RequestOptions): Promise<ListEnvelope<ApiKey>>;
  apiKeys(params?: PageParams, opts?: RequestOptions): AsyncGenerator<ApiKey>;
  /** The raw secret is returned ONCE. An `Idempotency-Key` replay returns `api_key: null`. */
  createApiKey(params?: string | { label?: string }, opts?: RequestOptions): Promise<ApiKeyCreated>;
  /** Pass the `id`, NOT the prefix. */
  revokeApiKey(id: string, opts?: RequestOptions): Promise<RevokedAck>;

  /* -- Settings, summary, payouts -- */
  getSettings(opts?: RequestOptions): Promise<Settings>;
  /** Returns an ACK carrying only what changed — not interchangeable with `getSettings()`. */
  updateSettings(params: { display_currency?: string }, opts?: RequestOptions): Promise<SettingsUpdateAck>;
  getSummary(opts?: RequestOptions): Promise<Summary>;
  getPayouts(opts?: RequestOptions): Promise<PayoutState>;
  /** Staged and timelocked. Nothing goes live inline unless the deployment is `instant`. */
  stagePayoutChanges(addresses: Record<string, string>, opts?: RequestOptions): Promise<PayoutStageAck>;
  cancelPayoutChange(id: string, opts?: RequestOptions): Promise<CancelledAck>;

  /* -- System -- */
  /** Returns the `Health` body on both 200 and 503 — its 503 is not an `{error, detail}`. */
  getHealth(opts?: RequestOptions): Promise<Health>;

  /* -- Webhooks: receiving -- */
  /** Uses `opts.webhookSecrets` unless you pass an explicit list. */
  verifyWebhook(
    rawBody: string | Uint8Array,
    signatureHeader: string | string[] | null | undefined,
    secrets?: string | string[],
    options?: VerifyOptions,
  ): boolean;
  parseWebhook<D = InvoiceEventData | ForwardEventData>(
    rawBody: string | Uint8Array,
    signatureHeader: string | string[] | null | undefined,
    secrets?: string | string[],
    options?: VerifyOptions,
  ): WebhookEvent<D>;
  describeWebhook(
    rawBody: string | Uint8Array,
    signatureHeader: string | string[] | null | undefined,
    secrets?: string | string[],
    options?: VerifyOptions,
  ): VerificationResult;

  static webhookData: typeof webhookData;
  static webhookSequence: typeof webhookSequence;
  static isInvoiceEvent: typeof isInvoiceEvent;
  static isForwardEvent: typeof isForwardEvent;
  static DEFAULT_WEBHOOK_TOLERANCE_SECONDS: number;

  /* -- Pagination -- */
  /** Follow `next_cursor` until `has_more` is false. A repeated cursor throws rather than looping. */
  paginate<T>(
    listFn: (params: Record<string, unknown>, opts: RequestOptions) => Promise<ListEnvelope<T>>,
    params?: Record<string, unknown>,
    opts?: RequestOptions,
  ): AsyncGenerator<T>;
  /** Drain an async iterable into an array. Mind unbounded result sets. */
  collect<T>(iterable: AsyncIterable<T>, max?: number): Promise<T[]>;

  /* -- Escape hatches -- */
  /** Call an endpoint this SDK does not model yet. Returns the decoded body. */
  request<T = unknown>(
    method: string,
    path: string,
    options?: RequestOptions & { query?: Record<string, unknown>; body?: unknown; auth?: boolean },
  ): Promise<T>;
  /** As {@link request}, but returns status and headers too. */
  requestRaw<T = unknown>(
    method: string,
    path: string,
    options?: RequestOptions & {
      query?: Record<string, unknown>;
      body?: unknown;
      auth?: boolean;
      throwOnError?: boolean;
    },
  ): Promise<RawResponse<T>>;
}

export const VERSION: string;
export default Receivify;
