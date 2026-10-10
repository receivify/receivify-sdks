# Receivify PHP SDK

The reusable core every PHP integration in this repo wraps — WooCommerce,
OpenCart, PrestaShop, Magento, and any custom store.

- **One file, no dependencies.** PHP 7.4+ and ext-curl. No Composer, no
  framework, no autoloader.
- Implements the frozen v1 surface in [`openapi.yaml`](https://receivify.io/openapi.yaml):
  invoices, forwarding addresses (including the pause/disable kill switch),
  tickers/assets, webhook-secret rotation, list auto-pagination and typed errors.

## Install

```bash
composer require receivify/receivify
```

Composer installs it from [receivify/receivify-php](https://github.com/receivify/receivify-php)
(`require __DIR__ . '/vendor/autoload.php';`). Without Composer, copy `Receivify.php` next to
your code and require it:

```php
require_once __DIR__ . '/Receivify.php';
```

If another extension might already have loaded it, guard the include:

```php
if (!class_exists('Receivify')) {
    require_once __DIR__ . '/Receivify.php';
}
```

## Quickstart

```php
require_once __DIR__ . '/Receivify.php';

$rcv = new Receivify('https://receivify.io', getenv('RECEIVIFY_API_KEY'), [
    // Current secret FIRST, the previous one second while a rotation is in its
    // grace window. See "Secret rotation" below.
    'webhook_secrets' => array_filter([
        getenv('RECEIVIFY_WEBHOOK_SECRET'),
        getenv('RECEIVIFY_WEBHOOK_SECRET_PREVIOUS'),
    ]),
    'timeout' => 30,
]);
```

### Create an invoice (hosted checkout)

```php
$invoice = $rcv->createInvoice([
    'amount_fiat'  => '25.00',                 // a STRING, not a float
    'currency'     => 'USD',
    'asset'        => 'btc',
    'reference'    => (string) $orderId,       // your business key
    'callback_url' => 'https://store.example/receivify-webhook',
    'metadata'     => ['order_id' => $orderId],
], ['idempotency_key' => 'order-' . $orderId]);

header('Location: ' . $invoice['checkout_url']);
```

`reference` also de-duplicates: creating a second invoice under a reference you
have already used returns the **existing** invoice with a 201, never a second
one. Combined with `Idempotency-Key`, a retried checkout can never mint two
invoices for one order.

### Create a forwarding address (standing deposit address)

```php
$fwd = $rcv->createAddress([
    'ticker'      => 'btc',
    'destination' => 'bc1qyour-own-wallet',
    'callback'    => 'https://store.example/receivify-forward',
], ['idempotency_key' => 'fwd-btc-main']);

$depositAddress = $fwd['address'];          // hand this to payers
$id             = Receivify::forwardId($fwd); // canonical `id`
```

Creation is deterministic — `(merchant, asset, destination, callback)` is a
unique identity, so repeating the call returns the same address (200) instead of
burning another HD derivation index (201).

### The kill switch

```php
$rcv->pauseAddress($id);    // PATCH status=paused — stop crediting NEW deposits
$rcv->resumeAddress($id);   // PATCH status=active
$rcv->disableAddress($id);  // DELETE — soft delete, idempotent
```

`destination` and `ticker` are immutable. Repointing a live deposit address is
exactly what a leaked key would be used for, so the API refuses it with 409 and
this SDK refuses it before the request is even sent.

### Receive and verify a webhook

```php
require_once __DIR__ . '/Receivify.php';

$rcv  = new Receivify('https://receivify.io', '', ['webhook_secrets' => $secrets]);

// THE RAW BODY. Not $_POST, not json_encode(json_decode($body)).
$raw    = file_get_contents('php://input');
$header = $_SERVER['HTTP_X_RECEIVIFY_SIGNATURE'] ?? '';

$event = $rcv->readWebhook($raw, $header);   // null when the signature fails
if ($event === null) {
    http_response_code(401);
    exit('bad signature');
}

// Drop a repeat delivery BEFORE touching the order.
$key = Receivify::dedupeKey($event);          // "invoice.paid:42"
if (already_processed($key)) { http_response_code(200); exit('ok'); }

$data    = Receivify::webhookData($event);    // the invoice / forward payload
$orderId = $data['reference'] ?? null;

switch (Receivify::classifyEvent(Receivify::eventName($event))) {
    case 'paid':              mark_paid($orderId);       break;
    case 'confirming':        note($orderId, 'seen on-chain, awaiting confirmations'); break;
    case 'underpaid':         note($orderId, 'under-paid');  break;
    case 'underpaid_expired': note($orderId, 'under-paid, window closed'); break;
    case 'expired':
    case 'cancelled':         mark_cancelled($orderId);  break;
    case 'failed':            flag_for_review($orderId); break;
    // 'test' and 'ignore' (forward.* and any future event name): do nothing.
}

remember_processed($key);
http_response_code(200);      // any 2xx marks the delivery delivered
echo 'ok';
```

## ⚠️ Verification uses the RAW body

`X-Receivify-Signature: t=<unix seconds>,v1=<hex>` where
`v1 = HMAC-SHA256(secret, "<t>.<rawBody>")`.

**You must HMAC the exact bytes that arrived** — `file_get_contents('php://input')`.

```php
$body = file_get_contents('php://input');            // ✅ correct
$body = json_encode(json_decode($raw, true));        // ❌ ALWAYS FAILS
$body = json_encode($_POST);                         // ❌ ALWAYS FAILS
```

Re-encoding changes key order, whitespace and slash escaping. The JSON is
semantically identical and the HMAC is completely different. The test suite
asserts this explicitly.

Two more things that will bite you if you host on a framework:

- Some stacks consume `php://input` before your handler runs. Read the raw body
  first and pass it around. (WordPress, PrestaShop and Magento are all fine;
  read the body yourself rather than trusting a parsed array.)
- Do not let a proxy or a `.htaccess` rewrite strip the
  `X-Receivify-Signature` header. If `$_SERVER['HTTP_X_RECEIVIFY_SIGNATURE']`
  is empty on a real delivery, that is where to look.

## Secret rotation — why `verifyWebhook` takes a LIST

`POST /api/v1/webhooks/secret/rotate` mints a new signing secret and keeps the
previous one acceptable until `prev_expires_at` (24 hours by default). Receivify
always **signs with the current secret only** — the grace window is an
instruction to *your* receiver. During a rotation two secrets are legitimately
valid at once, so verification takes an array:

```php
$result = $rcv->rotateWebhookSecret(['idempotency_key' => 'rotate-2026-08']);

if ($result['secret'] !== null) {          // null on an Idempotency-Key replay
    store_secrets([$result['secret'], $currentlyDeployedSecret]);
}
// … after $result['prev_expires_at'], drop the old one:
store_secrets([$result['secret']]);
```

`GET /api/v1/webhooks/secret` (`getWebhookSecretState()`) tells a running
process whether the fallback is still needed — `prev_active` is the live answer.

A single string is still accepted and normalised to a one-element list, so
`new Receivify($url, $key, 'whsec_…')` keeps working.

## Money is always a string

Every on-chain amount is **exact-integer base units as a string** — satoshis for
BTC, wei for ETH. A 256-bit wei value does not survive a PHP float, and neither
does a large satoshi value on a 32-bit build. Never cast one.

```php
$sats = $invoice['amount_expected'];                 // "1000000000000000000000"
echo Receivify::fromBaseUnits($sats, 18) . ' ETH';   // "1000.000000000000000000"
echo Receivify::toBaseUnits('0.001', 8);             // "100000"
```

`amount_fiat` is also a string, but a **decimal** one (`"25.00"`), not base
units. It is the price captured at create time and is display information only —
the authoritative amount is `amount_expected`. Never settle against it.

`Forward.min_forward` is the one money field that is *not* base units: it is a
display-unit decimal string. Multiply by `10 ** decimals` before comparing it
with a payment `value`.

## Lists and auto-pagination

Every list endpoint returns `{ data, has_more, next_cursor }` — never a bare
array — modelled as `ReceivifyList` (countable and iterable).

```php
$page = $rcv->listInvoices(['limit' => 100, 'status' => 'paid']);
foreach ($page as $invoice) { … }
$page->has_more;      // a fact from the server; never infer it from count()
$page->next_cursor;   // opaque — pass it straight back

foreach ($rcv->autoPagingInvoices(['status' => 'paid']) as $invoice) { … }
foreach ($rcv->autoPagingAddresses(['ticker' => 'btc']) as $forward) { … }
```

The auto-paginator follows `next_cursor` until `has_more` is false, and aborts
with an exception if the server ever repeats a cursor rather than looping
forever.

## Errors

Every failure is `{ error: "<code>", detail: "<sentence>" }`. Codes map to typed
exceptions; an **unknown** code falls back to `ReceivifyApiError` with the raw
code intact, so a future code can never crash an older SDK.

```php
try {
    $invoice = $rcv->createInvoice([...]);
} catch (ReceivifyRateUnavailableError $e) {   // 503, retryable
    // no fresh FX rate — try again shortly
} catch (ReceivifyAmountTooLowError $e) {      // 400
    // below the economic floor
} catch (ReceivifyApiError $e) {
    error_log($e->getErrorCode() . ': ' . $e->getDetail());   // raw code preserved
    if ($e->isRetryable()) { /* internal / upstream_error / rate_limited / rate_unavailable */ }
} catch (ReceivifyConnectionError $e) {
    // never reached the server
}
```

| code | exception |
|---|---|
| `bad_request` | `ReceivifyBadRequestError` |
| `bad_ticker` | `ReceivifyBadTickerError` |
| `bad_destination` | `ReceivifyBadDestinationError` |
| `asset_unavailable` | `ReceivifyAssetUnavailableError` |
| `amount_too_low` | `ReceivifyAmountTooLowError` |
| `suspended` | `ReceivifySuspendedError` |
| `limit_exceeded` | `ReceivifyLimitExceededError` |
| `invalid_code` | `ReceivifyInvalidCodeError` |
| `no_setup` | `ReceivifyNoSetupError` |
| `not_enabled` | `ReceivifyNotEnabledError` |
| `no_target` | `ReceivifyNoTargetError` |
| `invalid_token` | `ReceivifyInvalidTokenError` |
| `unauthorized` | `ReceivifyUnauthorizedError` |
| `bad_signature` | `ReceivifyBadSignatureError` |
| `signup_disabled` | `ReceivifySignupDisabledError` |
| `not_found` | `ReceivifyNotFoundError` |
| `already_exists` | `ReceivifyAlreadyExistsError` |
| `already_enabled` | `ReceivifyAlreadyEnabledError` |
| `not_allowed` | `ReceivifyNotAllowedError` |
| `rate_limited` | `ReceivifyRateLimitedError` (`getRetryAfter()`) |
| `internal` | `ReceivifyInternalError` |
| `upstream_error` | `ReceivifyUpstreamError` |
| `rate_unavailable` | `ReceivifyRateUnavailableError` |
| *anything else* | `ReceivifyApiError` |

All of them extend `ReceivifyException`, which extends `Exception`.

## Retries, timeouts and idempotency

- Retried automatically on 429/5xx with exponential backoff + jitter, honouring
  `Retry-After` / `X-RateLimit-Reset`: `GET`, the idempotent `DELETE`, and any
  request carrying an `Idempotency-Key`.
- **Never** retried: a bare mutating call. Blind-retrying a create is how you
  charge someone twice. Pass `idempotency_key` and it becomes retryable.
- 30 second timeout by default (`'timeout' => N`), 10 second connect timeout.
- Redirects are never followed — that would replay your `Authorization` header
  at whatever host the redirect names.
- The API key travels in the `Authorization` header only. It is never put in a
  URL, never logged, and `var_dump()` shows it redacted.

`Idempotency-Key` is accepted on every mutating call that supports it server-side
(`createInvoice`, `createAddress`, `rotateWebhookSecret`) via
`['idempotency_key' => '…']`. It is scoped to `(merchant, endpoint, key)` and
retained for 24 hours; a replay returns the original body and status plus
`Idempotent-Replay: true`.

## API surface

```
new Receivify($apiUrl, $apiKey, $opts)
  $opts: webhook_secrets[], webhook_secret, timeout, connect_timeout,
         max_retries, max_retry_delay, tolerance_seconds, user_agent, transport

Invoices
  createInvoice($params, $opts)            POST   /api/v1/invoices
  getInvoice($id)                          GET    /api/v1/invoices/{id}
  listInvoices($params)                    GET    /api/v1/invoices        -> ReceivifyList
  autoPagingInvoices($params)                                             -> Generator
  cancelInvoice($id)                       POST   /api/v1/invoices/{id}/cancel
  bulkCancelInvoices($ids)                 POST   /api/v1/invoices/bulk
  getPublicInvoice($id)                    GET    /api/public/invoices/{id}   (no key)

Forwarding
  createAddress($params, $opts)            POST   /api/v1/address
  getAddress($id)                          GET    /api/v1/address/{id}
  listAddresses($params)                   GET    /api/v1/address         -> ReceivifyList
  autoPagingAddresses($params)                                            -> Generator
  updateAddress($id, $params)              PATCH  /api/v1/address/{id}
  pauseAddress($id) / resumeAddress($id)   PATCH  (the kill switch)
  disableAddress($id)                      DELETE /api/v1/address/{id}

Catalogue (public)
  listTickers()                            GET    /api/v1/tickers         -> ReceivifyList
  listAssets()                             GET    /api/v1/assets          -> ReceivifyList

Webhooks
  getWebhookSecretState()                  GET    /api/v1/webhooks/secret
  rotateWebhookSecret($opts)               POST   /api/v1/webhooks/secret/rotate
  verifyWebhook($raw, $header, $secrets = null, $toleranceSeconds = null)  -> bool
  parseWebhook($raw, $header, …)           -> envelope, throws on failure
  readWebhook($raw = null, $header = null, …) -> envelope or null

Static helpers
  Receivify::eventName($event)             the discriminator
  Receivify::webhookData($event)           the `data` payload
  Receivify::webhookSequence($event)       ordering / dedupe
  Receivify::dedupeKey($event)             "<event>:<sequence>"
  Receivify::classifyEvent($name)          paid|confirming|underpaid|underpaid_expired|
                                           expired|cancelled|failed|test|ignore
  Receivify::forwardId($forward)           canonical `id` (not the deprecated `forward_id`)
  Receivify::fromBaseUnits($units, $dec)   exact base units  -> decimal string
  Receivify::toBaseUnits($dec, $decimals)  decimal string    -> exact base units
```

## The webhook envelope

One shape for **every** event family:

```json
{
  "event": "invoice.paid",
  "sequence": 42,
  "created_at": "2026-08-24T12:05:00.000Z",
  "data": { "id": "…", "reference": "1001", "status": "paid",
            "amount_expected": "100000", "amount_received": "100000",
            "address": "bc1q…", "paid_at": "2026-08-24T12:05:00.000Z" }
}
```

- `data` is the invoice projection for `invoice.*`, `sweep.sent` and `test`, and
  the (CryptAPI-shaped) forward payment for `forward.*`.
- Delivery is at-least-once and can arrive out of order. `sequence` is a
  globally monotonic counter allocated at enqueue time — **not** per resource, so
  consecutive events for one invoice are not consecutive numbers. De-duplicate on
  `(event, sequence)`; there is no separate event id.
- A retry and a manual replay re-send the **same bytes** with the same
  `created_at` and `sequence`. A webhook states what happened at a moment; poll
  `getInvoice()` / `getAddress()` for what is true now.
- **Ignore unknown `event` values and unknown fields inside `data`.** Adding
  either is an additive change. `classifyEvent()` returns `ignore` for anything it
  does not recognise.
- Answer any 2xx to mark the delivery delivered. Anything else is retried with
  exponential backoff (0s, 10s, 60s, 5m, 30m, 2h, 6h, 24h) and then marked failed.

The old pre-envelope shape (`{event, sequence, invoice: {...}}`) is gone. Read
`data`, not `invoice`.

## Tests

```
php tests/run-tests.php
```

Stock PHP, no Composer, no PHPUnit, **no network** — the HTTP layer is injected
and every response is canned. Covers the list envelope and auto-pagination,
error-code mapping and the unknown-code fallback, money staying a string,
verification with the current *and* the previous secret, a tampered body, a
stale timestamp, re-encoded JSON failing, and that a bare mutating call is never
blind-retried.

Inject your own transport to test your integration, or to route requests through
a platform HTTP stack:

```php
$rcv->setTransport(function (array $req) {
    // $req: method, url, headers[name => value], body, timeout
    return ['status' => 200, 'headers' => [], 'body' => '{"…":"…"}'];
});
```

## Version

`Receivify::VERSION` — `2.0.0`. Sent as `User-Agent: receivify-php/2.0.0`.

### Upgrading from 1.x

- `new Receivify($url, $key, $secret)` still works; prefer
  `['webhook_secrets' => [$current, $previous]]`.
- `createInvoice($amountFiat, $currency, $opts)` still works; prefer the array
  form.
- `verifyWebhook()` now enforces a 300-second timestamp tolerance by default.
  Pass `0` (or `['tolerance_seconds' => 0]`) if your server clock cannot be
  trusted — but fix the clock instead.
- `Receivify::webhookData()` no longer falls back to the pre-envelope
  `['invoice' => …]` shape. Read `data`.
- Errors are now typed. `catch (Exception $e)` still catches everything.

MIT.
