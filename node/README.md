# receivify — official Node.js SDK

Invoices, forwarding addresses, payment links and signed webhooks for the
[Receivify](https://receivify.io) crypto payment gateway.

- **Zero runtime dependencies.** Native `fetch` and `node:crypto`, nothing else.
- **Node >= 18.** ESM, with hand-written TypeScript declarations.
- **Generated from the frozen `openapi.yaml` contract**, by hand, so the doc comments say what the
  code actually does rather than what it ideally should.

```bash
npm install receivify
```

Or, from this repository (pnpm can install a subfolder straight from GitHub):

```bash
pnpm add github:receivify/receivify-sdks#path:/node
# npm: clone the repo, then
npm install ./receivify-sdks/node
```

---

## Two rules that will bite you if you skip them

**1. Money is a string. Never call `Number()` on it.**

Every monetary field on the wire is an exact-integer base-unit string — satoshis for BTC, wei for
ETH. An IEEE-754 double cannot hold a satoshi count above 2⁵³, let alone a 256-bit wei value, so
`Number("2100000000000000001")` silently gives you `2100000000000000000` and you have just lost a
payment. This SDK never touches those fields. Convert only for display, only with the BigInt-backed
helpers below.

```js
import { formatUnits, compareAmounts } from 'receivify';

formatUnits(invoice.amount_expected, 8);                     // '0.00123456'  (decimals from listTickers())
compareAmounts(invoice.amount_received, invoice.amount_expected) >= 0;  // paid in full?
```

`amount_fiat` is also a string — a decimal one like `"25.00"`. It is a display price captured at
create time; the authoritative amount is always `amount_expected`.

**2. Verify webhooks against the RAW request body.**

The signature is an HMAC over the exact bytes we sent. `JSON.stringify(req.body)` re-serialises —
key order, spacing and number formatting all change — and will not reproduce it, even for a
perfectly genuine delivery.

---

## Quickstart

### Create an invoice

```js
import { Receivify } from 'receivify';

const receivify = new Receivify(process.env.RECEIVIFY_URL, process.env.RECEIVIFY_API_KEY, {
  webhookSecrets: [process.env.RECEIVIFY_WEBHOOK_SECRET],
});

const invoice = await receivify.createInvoice(
  { amount_fiat: '25.00', currency: 'USD', asset: 'btc', reference: `order-${order.id}` },
  { idempotencyKey: `order-${order.id}` }, // a retry after a timeout will NOT create a second invoice
);

redirect(invoice.checkout_url);
```

`reference` is also a de-duplication key: creating a second invoice with a reference you have
already used returns the **existing** one, still with a 201. There is no way to get two invoices
under one reference.

### Create a forwarding address

A standing deposit address that sweeps everything it receives to your wallet.

```js
const forward = await receivify.createAddress({
  ticker: 'btc',
  destination: 'bc1qyourwallet…',
  callback: 'https://shop.example/hooks/receivify',
});

console.log(forward.id);       // ← the canonical id. Read this one.
console.log(forward.address);  // the deposit address to hand to payers
```

Creation is **deterministic**: the identity is `(asset, destination, callback_url)`, so calling it
again returns the same forward (with a 200 instead of a 201) rather than burning another derivation
index. `forward.forward_id` carries the same value and is deprecated — it stays for the life of v1
because removing a response field would be breaking.

### Receive and verify a webhook

Express — note `express.raw`, not `express.json`:

```js
import express from 'express';
import { Receivify } from 'receivify';

const receivify = new Receivify(process.env.RECEIVIFY_URL, process.env.RECEIVIFY_API_KEY, {
  // Current secret first, previous second. See "Rotating the secret" below.
  webhookSecrets: [process.env.RECEIVIFY_WEBHOOK_SECRET, process.env.RECEIVIFY_WEBHOOK_SECRET_PREVIOUS],
});

const app = express();

app.post('/hooks/receivify', express.raw({ type: 'application/json' }), (req, res) => {
  let event;
  try {
    event = receivify.parseWebhook(req.body, req.get('X-Receivify-Signature'));
  } catch (err) {
    console.warn('rejected webhook:', err.reason); // no_match | stale_timestamp | bad_header | …
    return res.sendStatus(400);
  }

  // Delivery is at-least-once and can arrive out of order. `(event, sequence)` is the dedupe key.
  if (alreadyApplied(event.event, event.sequence)) return res.sendStatus(200);

  switch (event.event) {
    case 'invoice.paid':
      markOrderPaid(event.data.reference, event.data.amount_received); // still a string
      break;
    case 'forward.sent':
      recordSweep(event.data.forward_id, event.data.value_forwarded);
      break;
    default:
      break; // MUST ignore unknown events — adding one is an additive change
  }

  res.sendStatus(200); // any 2xx marks it delivered; anything else is retried
});
```

Next.js route handler:

```js
export async function POST(req) {
  const raw = await req.text(); // the raw bytes, before any parsing
  if (!receivify.verifyWebhook(raw, req.headers.get('x-receivify-signature'))) {
    return new Response('bad signature', { status: 400 });
  }
  const event = JSON.parse(raw);
  // …
  return new Response('ok');
}
```

A webhook is a statement about a moment, not about now. It carries a frozen snapshot — a retry or a
manual replay re-sends the **same** bytes with the same `sequence` and `created_at`. For current
state, poll `getInvoice(id)` or `getAddress(id)`.

---

## The envelope

Every event, both families, arrives in one shape:

```json
{
  "event": "invoice.paid",
  "sequence": 12,
  "created_at": "2026-08-24T12:05:00.000Z",
  "data": { "...": "the resource" }
}
```

Switch on `event` first. `data` is invoice-shaped for `invoice.*`, `sweep.sent` and `test`, and
forward-payment-shaped for `forward.*`. Type guards are exported:

```js
import { isInvoiceEvent, isForwardEvent } from 'receivify';
```

Account-level webhook endpoints receive `invoice.*` and `sweep.sent` only. `forward.*` events go
**only** to the forward's own `callback_url`.

---

## Rotating the webhook secret

```js
const { secret, prev_expires_at, grace_seconds } = await receivify.rotateWebhookSecret({
  idempotencyKey: 'rotate-2026-08',
});
// `secret` is shown ONCE. Store it now — there is no read-back endpoint.
```

Receivify always **signs** with the current secret. The grace window is an instruction to your
receiver: until `prev_expires_at` (24h by default), deliveries signed just before the switch may
still be in flight, so verify against both.

```js
receivify.verifyWebhook(raw, header, [newSecret, oldSecret]);
```

This is why `verifyWebhook` takes an array from day one. Ask
`receivify.getWebhookSecretState()` whether the fallback is still needed — `prev_active` is the
live answer — then drop the old secret.

---

## Lists and auto-pagination

Every list endpoint returns `{ data, has_more, next_cursor }`. `has_more` is a fact: the query reads
`limit + 1` rows. Never infer it from `data.length === limit`.

```js
const page = await receivify.listInvoices({ limit: 100, status: 'paid' });
```

Each list method has an auto-paginating twin that follows `next_cursor` until `has_more` is false:

```js
for await (const invoice of receivify.invoices({ status: 'paid' })) {
  console.log(invoice.id, invoice.amount_received);
}

const firstHundred = await receivify.collect(receivify.addresses({ ticker: 'btc' }), 100);
```

| page                        | auto-paginate            |
| --------------------------- | ------------------------ |
| `listInvoices()`            | `invoices()`             |
| `listAddresses()`           | `addresses()`            |
| `listPaymentLinks()`        | `paymentLinks()`         |
| `listRecurring()`           | `recurring()`            |
| `listApiKeys()`             | `apiKeys()`              |
| `listWebhookEndpoints()`    | `webhookEndpoints()`     |
| `listWebhookDeliveries()`   | `webhookDeliveries()`    |

Cursors are opaque and keyset-based on `(created_at, _id)` descending, so a row inserted while you
are paging can never shift a later page or hide a row. A malformed cursor is a `400 bad_request`,
never a silent reset to page one.

---

## Errors

Every failure body is `{ error, detail }` with a code from a frozen registry. Each code maps to a
typed class; an **unknown** code — one from a newer deployment — falls back to `ReceivifyApiError`
with `code` intact, so an older SDK never crashes on it.

```js
import { ReceivifyApiError, NotFoundError, RateLimitedError, AmountTooLowError } from 'receivify';

try {
  await receivify.createInvoice({ amount_sats: '10' });
} catch (err) {
  if (err instanceof AmountTooLowError) return showFloorMessage(err.detail);
  if (err instanceof RateLimitedError) return retryIn(err.retryAfter ?? 5);
  if (err instanceof NotFoundError) return show404();
  if (err instanceof ReceivifyApiError) {
    console.error(err.code, err.status, err.detail); // branch on `code`, never on `detail`
  }
  throw err;
}
```

| class | code | class | code |
| --- | --- | --- | --- |
| `BadRequestError` | `bad_request` | `UnauthorizedError` | `unauthorized` |
| `BadTickerError` | `bad_ticker` | `BadSignatureError` | `bad_signature` |
| `BadDestinationError` | `bad_destination` | `SignupDisabledError` | `signup_disabled` |
| `AssetUnavailableError` | `asset_unavailable` | `NotFoundError` | `not_found` |
| `AmountTooLowError` | `amount_too_low` | `AlreadyExistsError` | `already_exists` |
| `SuspendedError` | `suspended` | `AlreadyEnabledError` | `already_enabled` |
| `LimitExceededError` | `limit_exceeded` | `NotAllowedError` | `not_allowed` |
| `InvalidCodeError` | `invalid_code` | `RateLimitedError` | `rate_limited` |
| `NoSetupError` | `no_setup` | `InternalError` | `internal` |
| `NotEnabledError` | `not_enabled` | `UpstreamError` | `upstream_error` |
| `NoTargetError` | `no_target` | `RateUnavailableError` | `rate_unavailable` |
| `InvalidTokenError` | `invalid_token` | | |

Transport failures and timeouts raise `ReceivifyConnectionError`. Webhook failures raise
`ReceivifyWebhookError` with a machine-readable `reason`. Everything extends `ReceivifyError`.

`GET /api/health` is the documented exception: its 503 body is a health report with no `error` key,
so `getHealth()` returns it rather than throwing.

---

## Idempotency and retries

`Idempotency-Key` is scoped to `(merchant, endpoint, key)` for 24 hours. A replay returns the
**original** body and the **original** status code, plus an `Idempotent-Replay: true` header.

```js
await receivify.createInvoice(params, { idempotencyKey: `order-${id}` });
```

The endpoints that declare support in `openapi.yaml` are `createInvoice`, `createAddress`,
`createApiKey`, `createPaymentLink`, `createRecurring`, `createWebhookEndpoint` and
`rotateWebhookSecret`. Passing the option elsewhere is harmless but does nothing.

**Retry policy.** The SDK retries on 429 and 5xx, with exponential backoff that honours
`Retry-After` and then `X-RateLimit-Reset`, but only for requests that are safe to repeat:

- any `GET`, and
- a mutating call that both carries an `idempotencyKey` **and** hits an endpoint that declares
  support for it.

A mutating call without that guarantee is sent exactly once. Blind-retrying a POST is how you
double-charge someone. Tune with `{ maxRetries, retryBaseMs }`, or set `maxRetries: 0` to disable.

Two calls that look idempotent but are not: `cancelInvoice()` is `409 not_allowed` the second time,
and `deleteWebhookEndpoint()` is a hard delete that 404s on a second call — prefer
`updateWebhookEndpoint(id, { active: false })`.

---

## Escape hatches

`requestRaw()` gives you the status and headers, which you need to tell a 200 (existing forward)
from a 201 (newly derived) or to spot `Idempotent-Replay`:

```js
const { status, headers, body } = await receivify.requestRaw('POST', '/api/v1/address', {
  body: { ticker: 'btc', destination: 'bc1q…' },
  idempotencyKey: 'setup-1',
});
if (status === 200) console.log('reused an existing forward');
```

`request()` calls any endpoint this SDK does not model yet and returns the decoded body.

---

## Configuration

```js
new Receivify(apiUrl, apiKey, {
  timeout: 30_000,            // per request, ms
  webhookSecrets: [],         // current first, previous second
  maxRetries: 2,              // safe requests only
  retryBaseMs: 500,           // first backoff step, doubling
  userAgent: 'my-shop/2.1',   // appended to `receivify-node/<version>`
  fetch: myFetch,             // inject a transport
});
```

The API key is held on a non-enumerable property: it never appears in a URL, in
`JSON.stringify(client)`, or in an error's `request` field.

---

## TypeScript

Types ship with the package — no `@types/receivify`, no build step.

```ts
import { Receivify, type Invoice, type WebhookEvent, type InvoiceEventData } from 'receivify';

const receivify = new Receivify(url, key, { webhookSecrets: [secret] });
const invoice: Invoice = await receivify.createInvoice({ amount_fiat: '25.00', currency: 'USD' });
const sats: string = invoice.amount_expected; // string, as it should be
```

Status and event unions are **open** (`'paid' | (string & {})`), so a value added in a future
additive release still type-checks. Keep a default branch.

### CommonJS

The package is ESM-only, because a dual build would mean shipping the crypto and HTTP code twice.
On Node 22.12+ (and 20.19+) `require()` loads it natively:

```js
const { Receivify } = require('receivify');
```

On older Node, use a dynamic import:

```js
const { Receivify } = await import('receivify');
```

---

## Not modelled on purpose

`/api/admin/**` (operator console, shapes may change without notice), `/api/auth/**` (browser
session bootstrap), `POST /api/v1/btcpay/webhook` (an inbound *provider* webhook),
`POST /api/public/signup` (so no SDK path ever mints credentials), `POST /api/public/links/{id}`
(unauthenticated invoice minting for the hosted checkout page — server-side code should call
`createInvoice`), and the `/api/v1/2fa/*` dashboard flow.

## Tests

Offline, no framework, no network — an injected transport asserts the exact request built and
feeds canned responses.

```bash
npm test    # node --test
```

## Example projects

Complete, runnable shops using this SDK (create an invoice, redirect to checkout, verify the signed webhook, mark the order paid), with comments explaining every step:

- **Express**: [`examples/node-express`](../examples/node-express)
- **Next.js App Router**: [`examples/nextjs-app-router`](../examples/nextjs-app-router)

All examples: [`examples/`](../examples).

## License

MIT.
