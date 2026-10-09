/**
 * receivify — the official Node.js SDK for the Receivify API.
 *
 * Zero runtime dependencies. Node >= 18 (native `fetch`, native `node:crypto`).
 *
 *   import { Receivify } from 'receivify';
 *   const receivify = new Receivify(process.env.RECEIVIFY_URL, process.env.RECEIVIFY_API_KEY, {
 *     webhookSecrets: [process.env.RECEIVIFY_WEBHOOK_SECRET],
 *   });
 *
 * Two rules that the type system cannot enforce for you:
 *
 *  1. **Money is a string.** `amount_expected`, `amount_received`, `value`, `amount_fiat` — all
 *     strings, all left untouched by this SDK. `Number()` on a satoshi or wei value silently
 *     corrupts it. Use `formatUnits` / `parseUnits` (BigInt-backed) at the display edge.
 *  2. **Verify webhooks against the RAW body.** `JSON.stringify(req.body)` is a different byte
 *     sequence and will not reproduce the HMAC.
 */

export { Receivify } from './client.js';
export { VERSION } from './version.js';

export {
  ReceivifyError,
  ReceivifyApiError,
  ReceivifyConnectionError,
  ReceivifyWebhookError,
  ERROR_CODES,
  errorFromResponse,
  // one class per frozen error code
  BadRequestError,
  BadTickerError,
  BadDestinationError,
  AssetUnavailableError,
  AmountTooLowError,
  SuspendedError,
  LimitExceededError,
  InvalidCodeError,
  NoSetupError,
  NotEnabledError,
  NoTargetError,
  InvalidTokenError,
  UnauthorizedError,
  BadSignatureError,
  SignupDisabledError,
  NotFoundError,
  AlreadyExistsError,
  AlreadyEnabledError,
  NotAllowedError,
  RateLimitedError,
  InternalError,
  UpstreamError,
  RateUnavailableError,
} from './errors.js';

export {
  EVENTS,
  SIGNATURE_HEADER,
  DEFAULT_TOLERANCE_SECONDS,
  verifyWebhook,
  parseWebhook,
  describeVerification,
  signWebhook,
  webhookData,
  webhookSequence,
  isInvoiceEvent,
  isForwardEvent,
} from './webhooks.js';

export { formatUnits, parseUnits, compareAmounts, toBigInt } from './units.js';

export { Receivify as default } from './client.js';
