# Receivify Python SDK

The official Python client for [Receivify](https://receivify.io) — invoices, forwarding
addresses, payment links and signed webhooks.

- **Python 3.9+**
- **Zero dependencies.** Standard library only: `urllib.request` for HTTP, `hmac` +
  `hashlib` for signatures. No `requests`, no `pydantic`.
- **Fully typed**, with a `py.typed` marker, so mypy and Pyright see the annotations.
- **Offline-testable**: the HTTP layer is an injected parameter.

```bash
pip install receivify
```

Or, from this repository:

```bash
pip install ./python
```

---

## Quickstart

```python
from receivify import Receivify

rcv = Receivify(
    "https://receivify.io",
    "rcv_live_...",                    # from the dashboard, or POST /api/v1/keys
    webhook_secrets=["whsec_..."],     # a LIST — see "Secret rotation" below
)

# 1. Price an order in fiat and get a hosted checkout page.
invoice = rcv.create_invoice(
    amount_fiat="25.00",               # a STRING, never a float
    currency="USD",
    asset="btc",
    reference="order-1001",            # your business key; also de-duplicates
    callback_url="https://shop.example/hooks/receivify",
    idempotency_key="order-1001",      # safe to retry
)
print(invoice.id, invoice.checkout_url, invoice.address)

# 2. Or take a standing deposit address that forwards everything to your wallet.
forward = rcv.create_forward(
    ticker="btc",
    destination="bc1qmywallet...",
    callback="https://shop.example/hooks/receivify",
)
print(forward.id, forward.address)     # `id` is canonical; `forward_id` is deprecated
```

`Receivify` is a context manager, if you want the connection released deterministically:

```python
with Receivify("https://receivify.io", "rcv_live_...") as rcv:
    for invoice in rcv.iter_invoices(status="paid"):
        ...
```

---

## Receiving a webhook

Every event — `invoice.*`, `sweep.sent` and `forward.*` alike — arrives in one envelope:

```json
{ "event": "invoice.paid", "sequence": 12, "created_at": "2026-08-25T12:05:00.000Z", "data": { } }
```

### Important: verify against the RAW body, not re-serialised JSON

This is the number one way integrations break their own HMAC. The signature is computed
over the exact bytes Receivify sent. If you parse the JSON and re-serialise it, key order
and whitespace change, the bytes change, and the signature can never match — no matter how
correct your secret is.

| Framework | Use this | Never this |
| --- | --- | --- |
| Django | `request.body` | `json.dumps(json.loads(request.body))` |
| Flask | `request.get_data()` | `json.dumps(request.json)` |
| FastAPI | `await request.body()` | `json.dumps(await request.json())` |

### Django

```python
# views.py
import json
from django.http import HttpResponse, HttpResponseForbidden
from django.views.decorators.csrf import csrf_exempt
from receivify import Receivify, SignatureVerificationError

rcv = Receivify(
    settings.RECEIVIFY_URL,
    settings.RECEIVIFY_API_KEY,
    webhook_secrets=settings.RECEIVIFY_WEBHOOK_SECRETS,   # ["current", "previous"]
)


@csrf_exempt
def receivify_webhook(request):
    try:
        event = rcv.parse_webhook(
            request.body,                                  # RAW bytes. Not request.POST.
            request.headers.get("X-Receivify-Signature"),
        )
    except SignatureVerificationError:
        return HttpResponseForbidden("bad signature")

    # Delivery is at-least-once and can arrive out of order. De-duplicate on
    # (event, sequence): store the highest sequence you have applied per order and
    # ignore anything at or below it.
    order = Order.objects.get(reference=event.data.get("reference"))
    if event.sequence <= order.receivify_sequence:
        return HttpResponse(status=200)

    if event.event == "invoice.paid":
        invoice = event.invoice                            # typed InvoiceEventData
        order.mark_paid(amount_base_units=invoice.amount_received)   # a string
    elif event.event == "forward.sent":
        payment = event.forward                            # typed ForwardEventData
        order.record_sweep(payment.txid_out)
    # Ignore any event name you do not recognise: adding one is an additive change.

    order.receivify_sequence = event.sequence
    order.save()

    # Answer 2xx quickly. Anything else is retried with exponential backoff.
    return HttpResponse(status=200)
```

### Flask

```python
from flask import Flask, request, abort
from receivify import Receivify, SignatureVerificationError

app = Flask(__name__)
rcv = Receivify(API_URL, API_KEY, webhook_secrets=[CURRENT_SECRET, PREVIOUS_SECRET])


@app.post("/hooks/receivify")
def receivify_webhook():
    try:
        event = rcv.parse_webhook(
            request.get_data(),                            # RAW bytes. Not request.json.
            request.headers.get("X-Receivify-Signature"),
        )
    except SignatureVerificationError:
        abort(403)

    if event.event == "invoice.paid":
        fulfil(event.data["reference"], event.sequence)
    return "", 200
```

If you would rather have a boolean than an exception, use `rcv.verify_webhook(raw_body,
header)` and parse the JSON yourself.

### A webhook is a statement about a moment

Retries and manual replays re-send the **frozen original body**, with the same `sequence`
and `created_at`. It tells you what happened, not what is true now — poll
`rcv.get_invoice(id)` or `rcv.get_forward(id)` for current state.

### Secret rotation

`rcv.rotate_webhook_secret()` mints a new signing secret and keeps the previous one
acceptable for a grace window (24h by default). Receivify always **signs** with the
current secret; the window is an instruction to your receiver. That is why
`webhook_secrets` and `verify_webhook` take a **list**:

```python
rotated = rcv.rotate_webhook_secret(idempotency_key="rotate-2026-08-25")
print(rotated.secret)            # shown ONCE — store it now, there is no read-back
print(rotated.prev_expires_at)   # until then, accept BOTH secrets

# Deploy the new secret alongside the old one, then drop the old one once:
state = rcv.get_webhook_secret_state()
if not state.prev_active:
    ...  # safe to stop accepting the previous secret
```

At most two secrets are ever valid, so your fallback list is bounded at two. A single
string is accepted for convenience (`webhook_secrets="whsec_..."`) and normalised to a
list.

The replay window defaults to 300 seconds and is enforced client-side only — no window is
enforced by the server. Pass `tolerance_seconds=` to change it, or `None` to disable it.

---

## Money is a string. Always.

Every monetary value on the wire is a string, and this SDK hands it back as a string,
byte for byte.

```python
invoice.amount_expected   # '2100000000000000'  exact-integer base units (satoshis, wei…)
invoice.amount_fiat       # '25.00'             a decimal display price
forward.min_forward       # '0.000100'          a decimal display amount, NOT base units
```

Never `float()` these. An IEEE-754 double carries about 15 significant digits; a wei value
carries up to 78. `float("1000000000000000001")` is wrong *before* you divide it.

Conversion is opt-in and explicit:

```python
from receivify import money

btc   = money.to_decimal(invoice.amount_expected, 8)      # Decimal('21000000.00000000')
sats  = money.to_base_units("0.00042", 8)                 # '42000'
label = money.format_amount(invoice.amount_expected, 8, trim=True)   # '21000000'
```

`decimals` comes from `rcv.list_tickers()` (`Ticker.decimals`). These helpers move the
decimal point in the digit string rather than dividing, so they are exact at any
magnitude, and `to_base_units` **raises** rather than rounding away a fraction of a
satoshi.

Passing a `float` to an amount argument raises a `TypeError` on purpose:

```python
rcv.create_invoice(amount_sats=0.1 + 0.2)   # TypeError: must not be a float
rcv.create_invoice(amount_sats="42000")     # correct
```

FX rates in `rcv.get_summary()["fx"]` are the one deliberate exception and stay JSON
numbers. A rate is not money: nothing is ever settled against it.

---

## Lists and auto-pagination

Every list endpoint returns `{data, has_more, next_cursor}` as a `Page`:

```python
page = rcv.list_invoices(limit=50, status="paid")
page.data          # List[Invoice]
page.has_more      # a fact from the server — never infer it from len(page) == limit
page.next_cursor
```

Each list method has an `iter_*` twin that follows the cursor for you, lazily — one
request per page, made only when the previous page runs out:

```python
for invoice in rcv.iter_invoices(status="paid"):
    ...

for forward in rcv.iter_forwards(ticker="btc"):
    ...
```

Available on invoices, forwards, payment links, recurring schedules, API keys, webhook
endpoints and webhook deliveries.

---

## Errors

Every failure is `{"error": "<code>", "detail": "<sentence>"}`, mapped to a typed
exception:

```python
from receivify import (
    ReceivifyApiError, NotFoundError, RateLimitedError, BadTickerError, NotAllowedError,
)

try:
    forward = rcv.create_forward(ticker="btk", destination="bc1q...")
except BadTickerError as e:
    print(e.detail)        # often carries a "did you mean btc?" suggestion
except RateLimitedError as e:
    time.sleep(e.retry_after or 5)
except ReceivifyApiError as e:
    # Catch-all — and the fallback for any code added to the API after this release.
    log.error("receivify %s (%s): %s", e.code, e.status, e.detail)
```

An `error` code this SDK build has never seen raises the base `ReceivifyApiError` with
`.code` carrying the raw string. An older SDK never crashes on a newer API.

Transport-level failures (DNS, TLS, timeout) raise `ReceivifyTransportError` /
`ReceivifyTimeoutError`, which are *not* `ReceivifyApiError` — nothing reached the API.

`rcv.get_health()` is the documented exception to all of the above: a 503 there carries a
health report rather than an error body, so it is returned rather than raised.

---

## Idempotency and retries

`Idempotency-Key` is accepted on the create-shaped calls (`create_invoice`,
`create_forward`, `create_payment_link`, `create_recurring`, `create_api_key`,
`create_webhook_endpoint`, `rotate_webhook_secret`). A replay returns the original body
and the original status code.

```python
rcv.create_invoice(amount_fiat="25.00", currency="USD", idempotency_key="order-1001")
```

The retry policy is deliberately narrow:

- **GET** — retried on 429/5xx and on transport failure.
- **Mutating calls** — retried **only** when you supplied an `idempotency_key`. Blind
  retries are how a merchant ends up with two invoices or two live API keys.
- Backoff is exponential with jitter, and honours `Retry-After` / `X-RateLimit-Reset`.

Tune with `Receivify(..., max_retries=2, timeout=30.0)`.

Two calls return a shown-once secret that is `None` on an idempotent replay —
`create_api_key().api_key` and `rotate_webhook_secret().secret`. Check `.is_replay`.

---

## Full surface

| Area | Methods |
| --- | --- |
| Invoices | `create_invoice`, `get_invoice`, `list_invoices` / `iter_invoices`, `cancel_invoice`, `bulk_cancel_invoices` |
| Forwarding | `create_forward`, `get_forward`, `list_forwards` / `iter_forwards`, `update_forward`, `disable_forward` (aliased as `*_address`) |
| Catalogue | `list_tickers`, `list_assets` (both public) |
| Payment links | `create_payment_link`, `get_payment_link`, `list_payment_links` / `iter_payment_links`, `update_payment_link`, `deactivate_payment_link` |
| Recurring | `create_recurring`, `list_recurring` / `iter_recurring`, `cancel_recurring` |
| API keys | `create_api_key`, `list_api_keys` / `iter_api_keys`, `revoke_api_key` |
| Webhooks | `create_webhook_endpoint`, `list_webhook_endpoints` / `iter_webhook_endpoints`, `update_webhook_endpoint`, `delete_webhook_endpoint`, `list_webhook_deliveries` / `iter_webhook_deliveries`, `replay_webhook_delivery`, `get_webhook_secret_state`, `rotate_webhook_secret`, `send_test_webhook` |
| Payouts | `get_payouts`, `stage_payout_changes`, `cancel_payout_change` |
| Settings | `get_settings`, `update_settings`, `get_summary` |
| Public | `get_public_invoice`, `get_public_payment_link`, `get_health` |
| Webhooks (local) | `verify_webhook`, `parse_webhook` |
| Escape hatch | `request(method, path, ...)` → raw `Response` |

Some notes the API enforces and the SDK does not hide:

- `create_forward` is **deterministic**: the same `(ticker, destination, callback)` returns
  the same forward instead of deriving a new address. A different `callback` is a
  *different* forward.
- `update_forward` can change only `status` and `callback_url`. `destination` and `ticker`
  are immutable — repointing a live deposit address is what a leaked key would do.
- `disable_forward` is a soft delete. The row is never removed, because money can still
  arrive at an address after you stop using it.
- `cancel_invoice` is not idempotent: cancelling twice raises `NotAllowedError`.
- Endpoints outside the SDK surface (`/api/admin/**`, `/api/auth/**`, 2FA, signup, the
  inbound BTCPay webhook) are deliberately not modelled.

Use `UNSET` semantics on the update methods: omit an argument to leave a field alone, pass
`None` to clear it.

```python
rcv.update_forward(forward.id, callback_url=None)      # clears the callback
rcv.update_forward(forward.id, status="paused")        # leaves the callback alone
```

---

## Testing your integration

The HTTP layer is a constructor parameter, so your tests never touch a network:

```python
from receivify import Receivify, Response

class FakeTransport:
    def __init__(self, responses):
        self.requests, self.responses = [], list(responses)

    def send(self, request):
        self.requests.append(request)
        return self.responses.pop(0)

transport = FakeTransport([Response(201, {}, b'{"id": "inv_1", "amount_expected": "42000"}')])
rcv = Receivify("https://receivify.io", "rcv_live_test", transport=transport)

invoice = rcv.create_invoice(amount_sats="42000", idempotency_key="order-1001")
assert invoice.amount_expected == "42000"
assert transport.requests[0].headers["Idempotency-Key"] == "order-1001"
```

To build a valid signature in your own webhook tests:

```python
from receivify import compute_signature
import time

ts = int(time.time())
header = "t={},v1={}".format(ts, compute_signature(raw_body, ts, "whsec_test"))
```

### Running this SDK's own tests

```bash
cd python
python -m unittest discover
```

Stock `unittest`, no pytest, no network.

---

## Security notes

- The API key is sent as an `Authorization` header and never as a query parameter — URLs
  end up in access logs, headers do not. It is redacted from `repr(client)` and
  `repr(Request)`.
- Redirects are not followed, so a `Location` header can never bounce your key to another
  host.
- Signature comparison uses `hmac.compare_digest`, never `==`.
- `create_api_key()` and `rotate_webhook_secret()` mask their secrets in `repr()`. Print
  them deliberately or not at all.

## Example projects

Complete, runnable shops using this SDK (create an invoice, redirect to checkout, verify the signed webhook, mark the order paid), with comments explaining every step:

- **Flask**: [`examples/python-flask`](../examples/python-flask)
- **FastAPI**: [`examples/python-fastapi`](../examples/python-fastapi)

All examples: [`examples/`](../examples).
