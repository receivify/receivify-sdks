# Receivify SDKs

Official client libraries for **[Receivify](https://receivify.io)**, the crypto payments API.

Receivify lets you accept crypto with one API call. You create an invoice priced in your own
currency, send your customer to a hosted checkout (or build your own), and get a signed webhook
when the payment lands. Funds are forwarded straight to your own wallet: there is no balance to
withdraw, and every payment is tracked with reorg-safe, double-entry accounting.

- **Coins and chains:** Bitcoin, Litecoin, Dogecoin, Bitcoin Cash, Monero, Zcash, Ether and
  stablecoins such as USDC and USDT on Ethereum, Base, Arbitrum, Optimism, Polygon, BNB Chain,
  Avalanche, Linea, Solana and Tron. The live list is at `GET /api/v1/tickers`.
- **Invoices:** priced in USD, EUR, GBP and more, rate-locked, with a hosted checkout page.
- **Forwarding addresses:** standing deposit addresses that sweep everything to your wallet.
- **Signed webhooks:** HMAC-SHA256, with zero-downtime secret rotation.

**[Website](https://receivify.io)** · **[Docs](https://receivify.io/docs)** ·
**[API reference (OpenAPI)](https://receivify.io/openapi.yaml)** ·
**[Sign up](https://receivify.io/signup)** ·
**[Shop plugins](https://receivify.io/integrations)**

---

## Contents

- [Pick your language](#pick-your-language)
- [How a payment works](#how-a-payment-works)
- [Before you start](#before-you-start)
- Examples: [Node.js](#nodejs--typescript) · [Python](#python) · [PHP](#php) · [.NET](#net-c) ·
  [C++](#c) · [Go](#go) · [Ruby](#ruby) · [Java / Kotlin](#java--kotlin) · [Rust](#rust) · [No SDK (curl)](#no-sdk-curl)
- [Reference](#reference): [invoices](#invoices) · [amounts](#amounts-and-money) ·
  [statuses](#invoice-statuses) · [webhooks](#webhooks) · [errors](#errors) ·
  [idempotency](#idempotency-and-references) · [fees](#who-pays-the-fee) ·
  [rate limits](#rate-limits)
- [Running the tests](#running-the-tests)

## Pick your language

| Language | Folder | Install | Needs | Runtime dependencies |
|---|---|---|---|---|
| Node.js / TypeScript | [`node/`](node) | `npm install receivify` | Node 18+ | none |
| Python | [`python/`](python) | `pip install receivify` | Python 3.9+ | none |
| PHP | [`receivify-php`](https://github.com/receivify/receivify-php) | `composer require receivify/receivify` | PHP 7.4+, ext-curl | none |
| .NET (C#, F#, VB) | [`dotnet/`](dotnet) | from source (NuGet soon) | .NET 8 | none |
| C++ | [`cpp/`](cpp) | CMake, header-only | C++17 | libcurl, OpenSSL (optional) |
| Go | [`go/`](go) | `go get github.com/receivify/receivify-sdks/go@v1.0.0` | Go 1.21+ | none |
| Ruby | [`ruby/`](ruby) | from source (RubyGems soon) | Ruby 2.7+ | none |
| Java / Kotlin | [`java/`](java) | from source (Maven Central soon) | Java 11+ | none |
| Rust | [`rust/`](rust) | `cargo add receivify --git https://github.com/receivify/receivify-sdks` | Rust 1.70+ | serde, hmac, sha2, ureq |
| No SDK (plain PHP) | [`examples/plain-php/`](examples/plain-php) | copy the folder | PHP 7.4+ | none |

> **Package registries:** npm, PyPI, Packagist (Composer) and the Go module proxy are live. NuGet,
> RubyGems, Maven Central and crates.io are on their way; until then the install lines below build
> from this repository, or download a zip from [receivify.io/sdks](https://receivify.io/sdks).

All nine SDKs have the same surface (invoices, forwarding addresses, tickers, webhook verification
and secret rotation) and follows the same rules, so moving between languages is painless.

## How a payment works

```
 your server                         Receivify                         your customer
 ───────────                         ─────────                         ─────────────
 1. createInvoice(25.00 USD)  ───▶   locks the rate, makes a
                              ◀───   deposit address + checkout_url
 2. redirect to checkout_url  ─────────────────────────────────────▶  pays in crypto
                                     3. sees the payment on-chain,
                                        waits for confirmations,
                                        forwards it to your wallet
 4. your webhook URL          ◀───   POST invoice.paid (signed)
    verify, then mark the order paid
```

1. **Create an invoice** for the order, priced in your currency. You get back a `checkout_url`.
2. **Redirect** the customer there (or show the `address` and amount on your own page).
3. Receivify **detects the payment** and forwards it to your payout wallet.
4. Receivify **calls your webhook**. You verify the signature, then fulfil the order.

## Before you start

1. [Sign up](https://receivify.io/signup) and set your payout wallet address.
2. In the [dashboard](https://receivify.io/dashboard), copy your **API key** (`rcv_live_…`) and
   your **webhook secret** (`whsec_…`).
3. Put them in environment variables. Never commit them, and never send the API key to a browser.

```bash
export RECEIVIFY_URL="https://receivify.io"
export RECEIVIFY_API_KEY="rcv_live_..."
export RECEIVIFY_WEBHOOK_SECRET="whsec_..."
```

Your webhook URL must be **https** and publicly reachable. Register it in the dashboard, or pass
`callback_url` on each invoice.

---

## Node.js / TypeScript

Zero dependencies, ESM, with TypeScript types included. [Full README](node/README.md).

```bash
npm install receivify
```

**Set up the client** (`lib/receivify.js`):

```js
import { Receivify } from 'receivify';

export const receivify = new Receivify(process.env.RECEIVIFY_URL, process.env.RECEIVIFY_API_KEY, {
  webhookSecrets: [process.env.RECEIVIFY_WEBHOOK_SECRET],
});
```

**Create an invoice and send the customer to checkout** (Express):

```js
import express from 'express';
import { receivify } from './lib/receivify.js';

const app = express();

app.post('/checkout/:orderId', async (req, res) => {
  const orderId = req.params.orderId;

  const invoice = await receivify.createInvoice(
    {
      amount_fiat: '25.00',          // a STRING, in your currency
      currency: 'USD',
      asset: 'btc',                  // or 'usdc' with chain: 'base', 'eth', 'ltc', ...
      reference: `order-${orderId}`, // your order id; must be unique per order
      metadata: { orderId },
    },
    { idempotencyKey: `order-${orderId}` }, // a retry never creates a second invoice
  );

  res.redirect(303, invoice.checkout_url);
});
```

**Receive the webhook.** The signature covers the exact bytes, so mount `express.raw()` on this
route, before any global `express.json()`:

```js
import { SIGNATURE_HEADER, ReceivifyWebhookError } from 'receivify';

app.post('/webhooks/receivify', express.raw({ type: 'application/json' }), (req, res) => {
  let event;
  try {
    event = receivify.parseWebhook(req.body, req.get(SIGNATURE_HEADER)); // verifies, then parses
  } catch (err) {
    if (err instanceof ReceivifyWebhookError) return res.status(400).send('bad signature');
    throw err;
  }

  switch (event.event) {
    case 'invoice.paid':
      markOrderPaid(event.data.reference, event.data.id);
      break;
    case 'invoice.expired':
      cancelOrder(event.data.reference);
      break;
  }
  res.sendStatus(200); // any 2xx marks the delivery done; anything else is retried
});

app.listen(3000);
```

**Next.js (App Router)** route handler:

```js
// app/api/webhooks/receivify/route.js
import { SIGNATURE_HEADER } from 'receivify';
import { receivify } from '@/lib/receivify';

export async function POST(request) {
  const raw = await request.text(); // the raw body, untouched
  let event;
  try {
    event = receivify.parseWebhook(raw, request.headers.get(SIGNATURE_HEADER));
  } catch {
    return new Response('bad signature', { status: 400 });
  }
  if (event.event === 'invoice.paid') await markOrderPaid(event.data.reference);
  return new Response('ok');
}
```

**Build your own checkout page** instead of redirecting:

```js
import { formatUnits } from 'receivify';

const invoice = await receivify.getInvoice(invoiceId);
invoice.address;         // send exactly this asset, on exactly this chain
invoice.amount_decimal;  // '0.00123456' for display
invoice.payment_uri;     // put this in a QR code (null on EVM chains: show the address)
invoice.expires_at;      // the rate is locked until then
formatUnits(invoice.amount_expected, invoice.decimals); // same as amount_decimal
```

**List invoices** (the iterator follows pages for you):

```js
for await (const invoice of receivify.invoices({ status: 'paid', limit: 100 })) {
  console.log(invoice.id, invoice.reference, invoice.amount_expected);
}
```

**Handle errors:**

```js
import { ReceivifyApiError, UnauthorizedError, AmountTooLowError } from 'receivify';

try {
  await receivify.createInvoice({ amount: '0.0001', asset: 'eth' });
} catch (err) {
  if (err instanceof AmountTooLowError) console.log('Too small:', err.detail);
  else if (err instanceof UnauthorizedError) console.log('Check RECEIVIFY_API_KEY');
  else if (err instanceof ReceivifyApiError) console.log(err.status, err.code, err.detail);
  else throw err; // network problem, timeout, ...
}
```

**Create a forwarding address** (a permanent deposit address that sweeps to your wallet):

```js
const forward = await receivify.createAddress({
  ticker: 'btc',
  destination: 'bc1q...your-wallet...',
  callback: 'https://shop.example/webhooks/receivify',
});
console.log(forward.id, forward.address); // give forward.address to the payer
```

---

## Python

Zero dependencies (standard library only), fully typed. [Full README](python/README.md).

```bash
pip install receivify
```

**Set up the client:**

```python
import os
from receivify import Receivify

rcv = Receivify(
    os.environ["RECEIVIFY_URL"],
    os.environ["RECEIVIFY_API_KEY"],
    webhook_secrets=[os.environ["RECEIVIFY_WEBHOOK_SECRET"]],
)
```

**Create an invoice and redirect** (Flask):

```python
from flask import Flask, redirect, request

app = Flask(__name__)

@app.post("/checkout/<order_id>")
def checkout(order_id):
    invoice = rcv.create_invoice(
        amount_fiat="25.00",              # a STRING (or Decimal), never a float
        currency="USD",
        asset="btc",                      # or asset="usdc", chain="base"
        reference=f"order-{order_id}",    # unique per order
        metadata={"order_id": order_id},
        idempotency_key=f"order-{order_id}",
    )
    return redirect(invoice.checkout_url, code=303)
```

**Receive the webhook** (Flask). Pass the raw bytes, never `request.json`:

```python
from receivify import SIGNATURE_HEADER, SignatureVerificationError, WebhookPayloadError

@app.post("/webhooks/receivify")
def receivify_webhook():
    try:
        event = rcv.parse_webhook(request.get_data(), request.headers.get(SIGNATURE_HEADER))
    except (SignatureVerificationError, WebhookPayloadError):
        return "bad signature", 400

    if event.event == "invoice.paid":
        mark_order_paid(event.data["reference"], event.data["id"])
    elif event.event == "invoice.expired":
        cancel_order(event.data["reference"])
    return "ok", 200
```

**Django:**

```python
from django.http import HttpResponse, HttpResponseBadRequest
from django.views.decorators.csrf import csrf_exempt
from django.views.decorators.http import require_POST
from receivify import SignatureVerificationError

@csrf_exempt
@require_POST
def receivify_webhook(request):
    try:
        event = rcv.parse_webhook(request.body, request.headers.get("X-Receivify-Signature"))
    except SignatureVerificationError:
        return HttpResponseBadRequest("bad signature")
    if event.event == "invoice.paid":
        mark_order_paid(event.data["reference"])
    return HttpResponse("ok")
```

**Build your own checkout page:**

```python
invoice = rcv.get_invoice(invoice_id)
invoice.address          # send exactly this asset, on exactly this chain
invoice.amount_decimal   # '0.00123456' for display
invoice.payment_uri      # QR code content (None on EVM chains: show the address)
invoice.expires_at
```

**List invoices** (follows pages for you):

```python
for invoice in rcv.iter_invoices(status="paid", limit=100):
    print(invoice.id, invoice.reference, invoice.amount_expected)
```

**Handle errors:**

```python
from receivify import ReceivifyApiError, UnauthorizedError, AmountTooLowError

try:
    rcv.create_invoice(amount="0.0001", asset="eth")
except AmountTooLowError as e:
    print("Too small:", e.detail)
except UnauthorizedError:
    print("Check RECEIVIFY_API_KEY")
except ReceivifyApiError as e:
    print(e.status, e.code, e.detail)
```

**Create a forwarding address:**

```python
forward = rcv.create_address(
    ticker="btc",
    destination="bc1q...your-wallet...",
    callback="https://shop.example/webhooks/receivify",
)
print(forward.id, forward.address)
```

---

## PHP

One file, no dependencies, PHP 7.4+. Lives in its own repo for Composer:
[receivify/receivify-php](https://github.com/receivify/receivify-php) ([full README](php/README.md)).

```bash
composer require receivify/receivify
# then: require __DIR__ . '/vendor/autoload.php';
# no Composer? copy php/Receivify.php into your project and require_once it
```

**Set up the client:**

```php
<?php
require_once __DIR__ . '/Receivify.php';

$rcv = new Receivify(getenv('RECEIVIFY_URL'), getenv('RECEIVIFY_API_KEY'), [
    'webhook_secrets' => [getenv('RECEIVIFY_WEBHOOK_SECRET')],
]);
```

**Create an invoice and redirect** (`checkout.php`):

```php
<?php
require_once __DIR__ . '/Receivify.php';
$rcv = new Receivify(getenv('RECEIVIFY_URL'), getenv('RECEIVIFY_API_KEY'));

$orderId = (string) $_POST['order_id'];

try {
    $invoice = $rcv->createInvoice([
        'amount_fiat' => '25.00',            // a STRING, in your currency
        'currency'    => 'USD',
        'asset'       => 'btc',              // or 'usdc' with 'chain' => 'base'
        'reference'   => 'order-' . $orderId, // unique per order
        'metadata'    => ['order_id' => $orderId],
    ], ['idempotency_key' => 'order-' . $orderId]);

    header('Location: ' . $invoice['checkout_url'], true, 303);
    exit;
} catch (ReceivifyApiError $e) {
    http_response_code($e->isRetryable() ? 503 : 400);
    echo 'Payment error: ' . htmlspecialchars($e->getDetail());
}
```

**Receive the webhook** (`webhook.php`). `readWebhook()` reads the raw body and the signature
header for you:

```php
<?php
require_once __DIR__ . '/Receivify.php';
$rcv = new Receivify(getenv('RECEIVIFY_URL'), getenv('RECEIVIFY_API_KEY'), [
    'webhook_secrets' => [getenv('RECEIVIFY_WEBHOOK_SECRET')],
]);

$event = $rcv->readWebhook(); // null if the signature or timestamp is bad
if ($event === null) {
    http_response_code(401);
    exit('bad signature');
}

$data = Receivify::webhookData($event);
switch (Receivify::eventName($event)) {
    case 'invoice.paid':
        mark_order_paid($data['reference'], $data['id']);
        break;
    case 'invoice.expired':
        cancel_order($data['reference']);
        break;
}
http_response_code(200);
echo 'ok';
```

**Laravel** route (exclude it from CSRF in `bootstrap/app.php` or `VerifyCsrfToken`):

```php
use Illuminate\Http\Request;

Route::post('/webhooks/receivify', function (Request $request) {
    $rcv = new \Receivify(env('RECEIVIFY_URL'), env('RECEIVIFY_API_KEY'), [
        'webhook_secrets' => [env('RECEIVIFY_WEBHOOK_SECRET')],
    ]);
    $event = $rcv->readWebhook($request->getContent(), (string) $request->header('X-Receivify-Signature'));
    if ($event === null) {
        return response('bad signature', 401);
    }
    if (\Receivify::eventName($event) === 'invoice.paid') {
        mark_order_paid(\Receivify::webhookData($event)['reference']);
    }
    return response('ok');
});
```

**Build your own checkout page:**

```php
$invoice = $rcv->getInvoice($invoiceId);
echo $invoice['address'];        // send exactly this asset, on exactly this chain
echo $invoice['amount_decimal']; // '0.00123456' for display
echo $invoice['payment_uri'];    // QR code content (null on EVM chains)
```

**List invoices:**

```php
foreach ($rcv->autoPagingInvoices(['status' => 'paid', 'limit' => 100]) as $invoice) {
    echo $invoice['id'], ' ', $invoice['reference'], PHP_EOL;
}
```

**Handle errors:**

```php
try {
    $rcv->createInvoice(['amount' => '0.0001', 'asset' => 'eth']);
} catch (ReceivifyAmountTooLowError $e) {
    echo 'Too small: ', $e->getDetail();
} catch (ReceivifyUnauthorizedError $e) {
    echo 'Check RECEIVIFY_API_KEY';
} catch (ReceivifyApiError $e) {
    echo $e->getStatusCode(), ' ', $e->getErrorCode(), ' ', $e->getDetail();
}
```

**Create a forwarding address:**

```php
$forward = $rcv->createAddress([
    'ticker'      => 'btc',
    'destination' => 'bc1q...your-wallet...',
    'callback'    => 'https://shop.example/webhook.php',
]);
echo $forward['id'], ' ', $forward['address'];
```

---

## .NET (C#)

Zero third-party dependencies, async throughout. [Full README](dotnet/README.md).

```bash
# NuGet package coming soon. Until then, reference the project from source:
git clone https://github.com/receivify/receivify-sdks.git
dotnet add reference receivify-sdks/dotnet/src/Receivify/Receivify.csproj
```

**Set up the client** (register one instance, e.g. as a singleton):

```csharp
using Receivify;

var client = new ReceivifyClient(
    Environment.GetEnvironmentVariable("RECEIVIFY_URL")!,
    Environment.GetEnvironmentVariable("RECEIVIFY_API_KEY"),
    new ReceivifyClientOptions
    {
        WebhookSecrets = new[] { Environment.GetEnvironmentVariable("RECEIVIFY_WEBHOOK_SECRET")! },
    });
```

**Create an invoice and redirect** (ASP.NET Core minimal API):

```csharp
app.MapPost("/checkout/{orderId}", async (string orderId) =>
{
    Invoice invoice = await client.CreateInvoiceAsync(
        new InvoiceCreateRequest
        {
            AmountFiat = "25.00",          // a STRING, in your currency
            Currency = "USD",
            Asset = "btc",                 // or Asset = "usdc", Chain = "base"
            Reference = $"order-{orderId}",
        },
        new RequestOptions { IdempotencyKey = $"order-{orderId}" });

    return Results.Redirect(invoice.CheckoutUrl!);
});
```

**Receive the webhook.** Read the raw body, never a model-bound object:

```csharp
app.MapPost("/webhooks/receivify", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    string raw = await reader.ReadToEndAsync();

    WebhookEvent evt;
    try
    {
        evt = client.ParseWebhook(raw, request.Headers["X-Receivify-Signature"]);
    }
    catch (ReceivifyWebhookException)
    {
        return Results.BadRequest("bad signature");
    }

    if (evt.Event == "invoice.paid")
    {
        string? reference = evt.Data.GetProperty("reference").GetString();
        // mark the order paid
    }
    return Results.Ok();
});
```

**Build your own checkout page:**

```csharp
Invoice invoice = await client.GetInvoiceAsync(invoiceId);
Console.WriteLine(invoice.Address);       // send exactly this asset, on exactly this chain
Console.WriteLine(invoice.AmountDecimal); // "0.00123456" for display
Console.WriteLine(invoice.PaymentUri);    // QR code content (null on EVM chains)
Console.WriteLine(Money.Format(invoice.AmountExpected, invoice.Decimals ?? 8));
```

**List invoices:**

```csharp
await foreach (Invoice invoice in client.EnumerateInvoicesAsync(new InvoiceListParams { Status = InvoiceStatus.Paid }))
{
    Console.WriteLine($"{invoice.Id} {invoice.Reference} {invoice.AmountExpected}");
}
```

**Handle errors:**

```csharp
try
{
    await client.CreateInvoiceAsync(new InvoiceCreateRequest { Amount = "0.0001", Asset = "eth" });
}
catch (UnauthorizedException)
{
    Console.WriteLine("Check RECEIVIFY_API_KEY");
}
catch (ReceivifyApiException e)
{
    Console.WriteLine($"{e.StatusCode} {e.Code} {e.Detail}");
}
```

**Create a forwarding address:**

```csharp
Forward forward = await client.CreateAddressAsync(new ForwardCreateRequest
{
    Ticker = "btc",
    Destination = "bc1q...your-wallet...",
    Callback = "https://shop.example/webhooks/receivify",
});
Console.WriteLine($"{forward.Id} {forward.Address}");
```

---

## C++

Header-only C++17, with libcurl and OpenSSL for the bundled HTTP and HMAC (both swappable).
[Full README](cpp/README.md).

```cmake
# CMakeLists.txt
add_subdirectory(third_party/receivify-sdks/cpp)   # or FetchContent
target_link_libraries(your_app PRIVATE receivify::receivify)
```

**Create an invoice:**

```cpp
#include <cstdlib>
#include <iostream>
#include "receivify/receivify.hpp"

using receivify::Json;

int main() {
  receivify::ClientOptions options;
  options.webhook_secrets = {std::getenv("RECEIVIFY_WEBHOOK_SECRET")};
  auto client = receivify::make_client(std::getenv("RECEIVIFY_URL"), std::getenv("RECEIVIFY_API_KEY"), options);

  receivify::RequestOptions once;
  once.idempotency_key = "order-1001";

  receivify::Invoice invoice = client.create_invoice(Json::object({
      {"amount_fiat", Json("25.00")},   // strings: Json refuses doubles on purpose
      {"currency", Json("USD")},
      {"asset", Json("btc")},
      {"reference", Json("order-1001")},
  }), once);

  std::cout << "Send the customer to " << invoice.checkout_url << "\n";
}
```

**Verify a webhook** (with any HTTP server; pass the raw body and the header):

```cpp
bool handle_webhook(const std::string& raw_body, const std::string& signature_header) {
  if (!client.verify_webhook(raw_body, signature_header)) return false;  // answer 400

  receivify::WebhookEvent event = client.parse_webhook(raw_body, signature_header);
  if (event.event == "invoice.paid") {
    std::string reference = event.data["reference"].as_string_or("");
    // mark the order paid
  }
  return true;  // answer 200
}
```

**List invoices and handle errors:**

```cpp
try {
  receivify::InvoiceListParams params;
  params.status = "paid";
  for (const receivify::Invoice& invoice : client.invoices(params)) {
    std::cout << invoice.id << " " << invoice.amount_expected << "\n";
  }
} catch (const receivify::UnauthorizedError&) {
  std::cerr << "Check RECEIVIFY_API_KEY\n";
} catch (const receivify::ApiError& e) {
  std::cerr << e.status() << " " << e.code() << " " << e.detail() << "\n";
}
```

---

## Go

Standard library only, Go 1.21+. [Full README](go/README.md).

```bash
go get github.com/receivify/receivify-sdks/go@v1.0.0
```

**A complete program** (`main.go`): `/checkout` creates an invoice and redirects; `/webhooks/receivify`
verifies the signature before trusting anything.

```go
package main

import (
	"io"
	"log"
	"net/http"
	"os"

	receivify "github.com/receivify/receivify-sdks/go"
)

func main() {
	client, err := receivify.NewClient(
		os.Getenv("RECEIVIFY_URL"),
		os.Getenv("RECEIVIFY_API_KEY"),
		receivify.WithWebhookSecrets(os.Getenv("RECEIVIFY_WEBHOOK_SECRET")),
	)
	if err != nil {
		log.Fatal(err)
	}

	http.HandleFunc("/checkout", func(w http.ResponseWriter, r *http.Request) {
		order := r.URL.Query().Get("order")
		inv, err := client.CreateInvoice(r.Context(), &receivify.CreateInvoiceParams{
			AmountFiat: "25.00", // a string, never a float
			Currency:   "USD",
			Asset:      "btc", // or Asset: "usdc", Chain: "base"
			Reference:  "order-" + order,
		}, receivify.RequestOptions{IdempotencyKey: "order-" + order})
		if err != nil {
			log.Printf("create invoice: %v", err)
			http.Error(w, "could not create invoice", http.StatusBadGateway)
			return
		}
		http.Redirect(w, r, inv.CheckoutURL, http.StatusSeeOther)
	})

	http.HandleFunc("/webhooks/receivify", func(w http.ResponseWriter, r *http.Request) {
		raw, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 1<<20)) // the RAW body
		if err != nil {
			http.Error(w, "bad body", http.StatusBadRequest)
			return
		}
		event, err := client.ParseWebhook(raw, r.Header.Get(receivify.SignatureHeader))
		if err != nil {
			http.Error(w, "invalid signature", http.StatusBadRequest)
			return
		}
		if event.Event == receivify.EventInvoicePaid {
			data, _ := event.InvoiceData()
			log.Printf("order %s paid", data.Reference) // mark the order paid here
		}
		w.WriteHeader(http.StatusOK) // any 2xx = delivered
	})

	log.Fatal(http.ListenAndServe(":8080", nil))
}
```

**List invoices** (the iterator follows pages for you):

```go
it := client.ListInvoicesAutoPaging(ctx, &receivify.ListInvoicesParams{Status: receivify.InvoiceStatusPaid})
for it.Next() {
	inv := it.Current()
	fmt.Println(inv.ID, inv.Reference, inv.AmountExpected)
}
if err := it.Err(); err != nil {
	log.Fatal(err)
}
```

**Handle errors** with `errors.Is`:

```go
_, err := client.CreateInvoice(ctx, &receivify.CreateInvoiceParams{Asset: "eth", Amount: "0.0001"})
var apiErr *receivify.APIError
switch {
case errors.Is(err, receivify.ErrAmountTooLow):
	log.Println("too small")
case errors.Is(err, receivify.ErrUnauthorized):
	log.Println("check RECEIVIFY_API_KEY")
case errors.As(err, &apiErr):
	log.Printf("%d %s: %s", apiErr.StatusCode, apiErr.Code, apiErr.Detail)
}
```

**Create a forwarding address:**

```go
fwd, err := client.CreateAddress(ctx, &receivify.CreateAddressParams{
	Ticker:      "btc",
	Destination: "bc1q...your-wallet...",
	Callback:    "https://shop.example/webhooks/receivify",
})
if err != nil {
	log.Fatal(err)
}
fmt.Println(fwd.ID, fwd.Address) // give fwd.Address to the payer
```

---

## Ruby

No runtime dependencies, Ruby 2.7+. [Full README](ruby/README.md).

```bash
# RubyGems package coming soon. Until then, build the gem from source:
git clone https://github.com/receivify/receivify-sdks.git
cd receivify-sdks/ruby && gem build receivify.gemspec && gem install ./receivify-1.0.0.gem
```

**Set up the client** (`config/initializers/receivify.rb` in Rails):

```ruby
require "receivify"

RECEIVIFY = Receivify::Client.new(
  api_url: ENV.fetch("RECEIVIFY_URL", "https://receivify.io"),
  api_key: ENV.fetch("RECEIVIFY_API_KEY"),
  webhook_secrets: [ENV.fetch("RECEIVIFY_WEBHOOK_SECRET")]
)
```

**Create an invoice and redirect** (Rails controller):

```ruby
class CheckoutsController < ApplicationController
  def create
    order = current_user.orders.find(params[:order_id])

    invoice = RECEIVIFY.create_invoice(
      amount_fiat: "25.00",             # a String or BigDecimal, never a Float
      currency: "USD",
      asset: "btc",                     # or asset: "usdc", chain: "base"
      reference: "order-#{order.id}",
      metadata: { order_id: order.id },
      idempotency_key: "order-#{order.id}"
    )
    redirect_to invoice.checkout_url, allow_other_host: true
  rescue Receivify::APIError => e
    redirect_to order_path(order), alert: "Crypto checkout is unavailable (#{e.code})."
  end
end
```

**Receive the webhook** (Rails). Use `request.raw_post`, never `params`:

```ruby
# config/routes.rb:  post "/webhooks/receivify", to: "receivify_webhooks#create"
class ReceivifyWebhooksController < ActionController::Base
  skip_forgery_protection

  def create
    event = RECEIVIFY.parse_webhook(request.raw_post, request.headers["X-Receivify-Signature"])

    case event.event
    when "invoice.paid"    then mark_order_paid(event.data["reference"])
    when "invoice.expired" then cancel_order(event.data["reference"])
    end
    head :ok
  rescue Receivify::SignatureVerificationError
    head :bad_request
  end
end
```

**Sinatra:**

```ruby
post "/webhooks/receivify" do
  event = Receivify::Webhook.parse(request.body.read, request.env["HTTP_X_RECEIVIFY_SIGNATURE"],
                                   [ENV.fetch("RECEIVIFY_WEBHOOK_SECRET")])
  mark_order_paid(event.data["reference"]) if event.event == "invoice.paid"
  200
rescue Receivify::SignatureVerificationError
  400
end
```

**List invoices and handle errors:**

```ruby
RECEIVIFY.each_invoice(status: "paid") { |inv| puts "#{inv.id} #{inv.reference} #{inv.amount_expected}" }

begin
  RECEIVIFY.create_invoice(amount: "0.0001", asset: "eth")
rescue Receivify::AmountTooLowError => e
  puts "Too small: #{e.detail}"
rescue Receivify::APIError => e
  puts "#{e.status} #{e.code}: #{e.detail}"
end
```

---

## Java / Kotlin

Java 11+, zero dependencies. [Full README](java/README.md).

Maven Central package coming soon. Until then, build it into your local Maven repository:

```bash
git clone https://github.com/receivify/receivify-sdks.git
cd receivify-sdks/java && mvn -q install
```

Then depend on it as usual:

```xml
<dependency>
  <groupId>io.receivify</groupId>
  <artifactId>receivify-java</artifactId>
  <version>1.0.0</version>
</dependency>
```

Gradle (with `mavenLocal()` in your repositories): `implementation("io.receivify:receivify-java:1.0.0")`

**Set up the client:**

```java
import io.receivify.*;
import java.util.List;

ReceivifyClient client = ReceivifyClient.builder()
    .apiUrl(System.getenv("RECEIVIFY_URL"))
    .apiKey(System.getenv("RECEIVIFY_API_KEY"))
    .webhookSecrets(List.of(System.getenv("RECEIVIFY_WEBHOOK_SECRET")))
    .build();
```

**Create an invoice and redirect:**

```java
Invoice invoice = client.createInvoice(
    CreateInvoiceParams.builder()
        .amountFiat("25.00").currency("USD")   // strings, never doubles
        .asset("btc")                          // or .asset("usdc").chain("base")
        .reference("order-" + orderId)
        .build(),
    RequestOptions.idempotencyKey("order-" + orderId));

response.sendRedirect(invoice.getCheckoutUrl());
```

**Receive the webhook** (Spring Boot). Take the raw bytes, never a DTO:

```java
@RestController
public class ReceivifyWebhookController {
    private final List<String> secrets = List.of(System.getenv("RECEIVIFY_WEBHOOK_SECRET"));

    @PostMapping(value = "/webhooks/receivify", consumes = "application/json")
    public ResponseEntity<String> handle(
            @RequestBody byte[] rawBody,
            @RequestHeader(value = "X-Receivify-Signature", required = false) String signature) {
        WebhookEvent event;
        try {
            event = Webhooks.parse(rawBody, signature, secrets);
        } catch (SignatureVerificationException e) {
            return ResponseEntity.status(400).body("bad signature");
        }
        if (EventNames.INVOICE_PAID.equals(event.getEvent())) {
            markOrderPaid(event.getInvoiceData().getReference());
        }
        return ResponseEntity.ok("ok");
    }
}
```

**List invoices and handle errors:**

```java
for (Invoice inv : client.autoPagingInvoices(ListInvoicesParams.builder().status(InvoiceStatus.PAID).build())) {
    System.out.println(inv.getId() + " " + inv.getReference() + " " + inv.getAmountExpected());
}

try {
    client.createInvoice(CreateInvoiceParams.builder().amount("0.0001").asset("eth").build());
} catch (AmountTooLowException e) {
    System.out.println("Too small: " + e.getDetail());
} catch (ReceivifyApiException e) {
    System.out.println(e.getStatusCode() + " " + e.getCode() + ": " + e.getDetail());
}
```

**Kotlin** uses the same library:

```kotlin
val invoice = client.createInvoice(
    CreateInvoiceParams.builder().amount("25").asset("usdc").chain("base").reference("order-42").build(),
    RequestOptions.idempotencyKey("order-42"),
)
println("Pay ${invoice.amountDecimal} USDC to ${invoice.address}")
```

---

## Rust

Blocking client with a pluggable transport. [Full README](rust/README.md).

```bash
cargo add receivify --git https://github.com/receivify/receivify-sdks   # crates.io package coming soon
```

**Create an invoice:**

```rust
use receivify::{Client, CreateInvoice, RequestOptions};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let client = Client::builder(std::env::var("RECEIVIFY_URL")?)
        .api_key(std::env::var("RECEIVIFY_API_KEY")?)
        .webhook_secrets(vec![std::env::var("RECEIVIFY_WEBHOOK_SECRET")?])
        .build()?;

    let invoice = client.create_invoice(
        &CreateInvoice {
            amount_fiat: Some("25.00".into()),
            currency: Some("USD".into()),
            asset: Some("btc".into()), // or asset "usdc" with chain: Some("base".into())
            reference: Some("order-1001".into()),
            ..Default::default()
        },
        Some(&RequestOptions::idempotency_key("order-1001")),
    )?;

    println!("Send the customer to {}", invoice.checkout_url);
    Ok(())
}
```

**Receive the webhook** (axum; take the body as `Bytes`):

```rust
use axum::{body::Bytes, extract::State, http::{HeaderMap, StatusCode}};
use receivify::Client;

async fn receivify_webhook(State(client): State<Client>, headers: HeaderMap, body: Bytes) -> StatusCode {
    let signature = headers.get("x-receivify-signature").and_then(|v| v.to_str().ok()).unwrap_or("");
    let event = match client.parse_webhook(&body, signature) {
        Ok(event) => event,
        Err(_) => return StatusCode::BAD_REQUEST,
    };
    if event.event == "invoice.paid" {
        if let Ok(data) = event.invoice_data() {
            println!("order {:?} paid", data.reference); // mark the order paid here
        }
    }
    StatusCode::OK
}
```

**List invoices and handle errors:**

```rust
use receivify::{ErrorCode, ListParams};

for invoice in client.invoices(ListParams::new().status("paid")) {
    let invoice = invoice?;
    println!("{} {}", invoice.id, invoice.amount_expected);
}

match client.get_invoice("00000000-0000-4000-8000-000000000000") {
    Ok(inv) => println!("{}", inv.status),
    Err(e) if e.code() == Some(ErrorCode::NotFound) => println!("no such invoice"),
    Err(e) => return Err(e.into()),
}
```

---

## No SDK (curl)

Every SDK is a thin wrapper over plain HTTPS and JSON, so you can call the API from anything.

**Create an invoice:**

```bash
curl -X POST "$RECEIVIFY_URL/api/v1/invoices" \
  -H "Authorization: Bearer $RECEIVIFY_API_KEY" \
  -H "Content-Type: application/json" \
  -H "Idempotency-Key: order-1001" \
  -d '{"amount_fiat":"25.00","currency":"USD","asset":"btc","reference":"order-1001"}'
```

**Fetch it again, or list paid invoices:**

```bash
curl "$RECEIVIFY_URL/api/v1/invoices/<id>"            -H "Authorization: Bearer $RECEIVIFY_API_KEY"
curl "$RECEIVIFY_URL/api/v1/invoices?status=paid&limit=50" -H "Authorization: Bearer $RECEIVIFY_API_KEY"
```

**Verify a webhook by hand.** Take `t` and `v1` from the `X-Receivify-Signature` header, then:

```bash
EXPECTED=$(printf '%s' "$T.$RAW_BODY" | openssl dgst -sha256 -hmac "$RECEIVIFY_WEBHOOK_SECRET" | sed 's/^.* //')
[ "$EXPECTED" = "$V1" ] && echo "valid"   # use a constant-time compare in real code
```

A complete plain-PHP checkout and webhook receiver is in
[`examples/plain-php/`](examples/plain-php).

---

## Reference

The full API is described in [openapi.yaml](https://receivify.io/openapi.yaml). These are the
parts you need for a typical integration.

### Invoices

`POST /api/v1/invoices` creates one. Send **exactly one** amount field:

| Field | Meaning |
|---|---|
| `amount_fiat` + `currency` | A price in your currency, e.g. `"25.00"` + `"USD"`. Converted at the current rate and locked. |
| `amount` | A decimal amount in the coin itself, e.g. `"0.001"` BTC. |
| `amount_sats` | Exact base units as an integer string (satoshis, wei, ...). |

Other fields:

| Field | Meaning |
|---|---|
| `asset` | `btc` (default), `eth`, `usdc`, `usdt`, `ltc`, `doge`, `xmr`, ... See `GET /api/v1/tickers`. |
| `chain` | For EVM tokens: `ethereum` (default), `base`, `arbitrum`, `optimism`, `polygon`, `bsc`, ... |
| `reference` | Your order id. Must be unique per order (see [below](#idempotency-and-references)). |
| `callback_url` | A webhook URL for this invoice only, in addition to your account endpoints. https only. |
| `metadata` | Any JSON object. Returned on the invoice. |
| `add_fee_ontop` | `true` makes the customer pay the fee (see [below](#who-pays-the-fee)). |

The response is the invoice. The fields you'll use most:

| Field | Meaning |
|---|---|
| `id` | The invoice id (UUID). |
| `checkout_url` | The hosted payment page. Redirect the customer here. |
| `address` | The deposit address, if you build your own page. |
| `amount_expected` | What must arrive, in base units (string). |
| `amount_decimal` | The same, already divided for display, e.g. `"0.00123456"`. |
| `decimals` | Decimal places of the asset (8 for BTC, 6 for USDC, 18 for ETH). |
| `payment_uri` | QR code content, or `null` on EVM, Solana and Tron (show the address). |
| `status` | See [statuses](#invoice-statuses). |
| `expires_at` | When the rate lock and the invoice expire (30 minutes by default). |

Other endpoints: `GET /api/v1/invoices/{id}`, `GET /api/v1/invoices` (list),
`POST /api/v1/invoices/{id}/cancel` (only while pending and unpaid).

### Amounts and money

- **Every amount is a string** of whole base units: satoshis for BTC, wei for ETH, the token's
  smallest unit otherwise. `"100000"` satoshis is 0.001 BTC.
- **Never convert an amount to a float or double.** A wei amount does not fit in one, and you
  would lose money to rounding. Every SDK ships exact helpers for display and comparison.
- `amount_fiat` (e.g. `"25.00"`) is a display price only. `amount_expected` is what counts.

### Invoice statuses

| Status | Meaning | What to do |
|---|---|---|
| `pending` | Created, waiting for payment. | Wait. |
| `confirming` | Payment seen on-chain, not final yet. | Show "payment detected". **Don't ship yet.** |
| `partial` | Less than the full amount arrived; the window is still open. | Ask the customer to send the rest. |
| `paid` | Paid in full and confirmed. | **Fulfil the order.** |
| `swept` | Paid, and the funds have been forwarded to your wallet. | Already fulfilled at `paid`. |
| `expired` | Nothing arrived in time. | Cancel the order. |
| `partial_expired` | The window closed on an under-payment. | Needs a human: refund or accept. |
| `cancelled` | You cancelled it. | Nothing. |
| `failed` | Settlement failed. | Needs a human. |

Treat the list as open: new statuses may be added, so always keep a default branch.

### Webhooks

Every delivery is a `POST` with a JSON body and a signature header:

```http
POST /webhooks/receivify HTTP/1.1
Content-Type: application/json
X-Receivify-Signature: t=1735689600,v1=5257a869e7ecebeda32affa62cdca3fa51cad7e77a0e56ff536d0ce8e108d8bd

{"event":"invoice.paid","sequence":42,"created_at":"2026-01-01T12:00:00.000Z",
 "data":{"id":"b1f2...","reference":"order-1001","status":"paid",
         "amount_expected":"123456","amount_received":"123456","address":"bc1q...","paid_at":"..."}}
```

**Events:**

| Event | Meaning |
|---|---|
| `invoice.confirming` | Payment seen on-chain; waiting for confirmations. |
| `invoice.partial` | An under-payment arrived. |
| `invoice.paid` | Paid in full. **This is the one to fulfil on.** |
| `invoice.swept` | Funds forwarded to your wallet. |
| `invoice.expired` / `invoice.partial_expired` | The invoice window closed. |
| `invoice.cancelled` / `invoice.failed` | Cancelled, or settlement failed. |
| `forward.detected`, `forward.confirmed`, `forward.sent`, ... | Forwarding-address activity. |
| `test` | A test delivery from the dashboard. |

**Verifying the signature** (every SDK does this for you):

1. Split the header into `t` (Unix seconds) and `v1` (hex).
2. Compute `HMAC-SHA256(webhook_secret, t + "." + raw_body)` as hex.
3. Compare it with `v1` in **constant time**, and reject `t` more than 5 minutes from now.

Rules that trip people up:

- **Use the raw body.** Parsing and re-serialising the JSON changes the bytes and the signature
  will never match.
- **Accept two secrets during a rotation.** After you rotate in the dashboard, the old secret stays
  valid for 24 hours. Pass `[current, previous]` to the SDK during that window.
- **Answer 2xx quickly.** Anything else is retried with backoff: 8 attempts over about 32 hours
  (10s, 1m, 5m, 30m, 2h, 6h, 24h). Do slow work after responding.
- **Expect duplicates.** Delivery is at-least-once. De-duplicate on `event` + `sequence`.
- **The webhook is a snapshot.** For the current state, call `GET /api/v1/invoices/{id}`.

### Errors

Every error has the same shape and an HTTP status:

```json
{ "error": "amount_too_low", "detail": "Amount too low for USDT on tron: minimum ... USDT (gas to forward it would exceed the fee). ..." }
```

Branch on `error` (stable), never on `detail` (wording may change). Common codes:

| Code | Status | Meaning |
|---|---|---|
| `unauthorized` | 401 | Missing or wrong API key. |
| `bad_request` | 400 | A field is missing or invalid; `detail` says which. |
| `asset_unavailable` | 400 | That asset or chain isn't enabled on this deployment. |
| `amount_too_low` | 400 | Below the minimum for that asset and chain (network fees would eat it). |
| `not_found` | 404 | No such invoice (or not yours). |
| `already_exists` | 409 | That `reference` is already used by a different invoice. |
| `not_allowed` | 409 | The action isn't allowed in the current state (e.g. cancelling a paid invoice). |
| `rate_limited` | 429 | Slow down; retry after `X-RateLimit-Reset`. |
| `rate_unavailable` | 503 | No fresh exchange rate right now. Retry shortly. |
| `upstream_error` | 502 | A node or provider failed. Retry. |
| `internal` | 500 | Our fault. Retry, then contact us. |

Each SDK maps these to typed errors (`UnauthorizedError`, `AmountTooLowError`, ...). Unknown
codes still arrive as the generic API error, so an older SDK never breaks.

### Idempotency and references

- Send an **`Idempotency-Key`** header (every SDK has an option for it) on creates. If a request
  times out and you retry with the same key, you get the original invoice back instead of a
  second one.
- **`reference`** is your order id and also de-duplicates: creating another invoice with the same
  reference and the same terms returns the existing invoice. With different terms you get
  `409 already_exists`.
- Make references **unique per order**. If several shops share one Receivify account, prefix
  them, e.g. `shop-a-1001`.

### Who pays the fee

By default the Receivify fee comes out of what you receive. Send `add_fee_ontop: true` and the
customer pays it instead: `amount_expected` is grossed up so you receive exactly the amount you
asked for (shown in `amount_net_requested`; the added part is in `fee_ontop`). Leave the field out
to use your account default from the dashboard.

### Rate limits

Responses carry `X-RateLimit-Limit`, `X-RateLimit-Remaining` and `X-RateLimit-Reset`. Over the
limit you get `429 rate_limited`. The SDKs retry safe requests (reads, and creates that carry an
idempotency key) automatically, honouring `X-RateLimit-Reset`.

---

## Running the tests

Every SDK has an offline test suite that needs no network and no API key.

```bash
cd node   && npm test
cd python && python -m unittest discover -s tests -t .
cd php    && php tests/run-tests.php
cd dotnet && dotnet run --project tests/Receivify.Tests
cmake -S cpp -B build && cmake --build build && ctest --test-dir build
cd go     && go test ./...
cd ruby   && ruby -Ilib -Itest test/run_all.rb
cd java   && mvn -q package
cd rust   && cargo test
```

CI runs all of these on every push (see [`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

## Shop plugins

Ready-made plugins for WooCommerce, Magento 2, PrestaShop and OpenCart are available from
[receivify.io/integrations](https://receivify.io/integrations).

## Support and security

- Questions and bugs: [open an issue](https://github.com/receivify/receivify-sdks/issues).
- Security problems: please don't open a public issue. Contact us through
  [receivify.io](https://receivify.io).

## License

[MIT](LICENSE)
