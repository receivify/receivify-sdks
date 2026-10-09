# Receivify Go SDK

The official Go client for [Receivify](https://receivify.io), a crypto payments API. Create invoices, send payers to a hosted checkout, run forwarding addresses, and verify signed webhooks.

- Standard library only, no third-party modules. Go 1.21+.
- Amounts are exact strings, never `float64`.
- Webhook verification is constant-time, works on the raw body, and accepts a list of secrets so you can rotate without downtime.
- Errors are typed (`errors.Is(err, receivify.ErrNotFound)`), and safe requests are retried automatically.

Full API reference: **https://receivify.io/docs**

## Install

```sh
go get github.com/receivify/receivify-sdks/go
```

```go
import receivify "github.com/receivify/receivify-sdks/go"
```

## Quickstart

This is a complete program. `/checkout` creates an invoice and redirects the payer to the hosted checkout page. `/webhooks/receivify` checks the signature before it trusts anything in the body. The same code is in [`examples/quickstart/main.go`](examples/quickstart/main.go).

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
		"https://receivify.io",
		os.Getenv("RECEIVIFY_API_KEY"),
		receivify.WithWebhookSecrets(os.Getenv("RECEIVIFY_WEBHOOK_SECRET")),
	)
	if err != nil {
		log.Fatal(err)
	}

	http.HandleFunc("/checkout", func(w http.ResponseWriter, r *http.Request) {
		order := r.URL.Query().Get("order")
		inv, err := client.CreateInvoice(r.Context(), &receivify.CreateInvoiceParams{
			Asset:      "btc",
			AmountFiat: "25.00",
			Currency:   "USD",
			Reference:  "myshop-order-" + order,
		}, receivify.RequestOptions{IdempotencyKey: "myshop-order-" + order})
		if err != nil {
			log.Printf("create invoice: %v", err)
			http.Error(w, "could not create invoice", http.StatusBadGateway)
			return
		}
		http.Redirect(w, r, inv.CheckoutURL, http.StatusSeeOther)
	})

	http.HandleFunc("/webhooks/receivify", func(w http.ResponseWriter, r *http.Request) {
		// Read the RAW body before anything else touches it.
		raw, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 1<<20))
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
			log.Printf("invoice %s paid (%s base units)", data.ID, data.AmountReceived)
			// Mark the order paid. Deliveries are at-least-once, so make this idempotent.
		}
		w.WriteHeader(http.StatusOK) // Answer 2xx, or the delivery is retried.
	})

	log.Fatal(http.ListenAndServe(":8080", nil))
}
```

```sh
RECEIVIFY_API_KEY=rcv_live_... RECEIVIFY_WEBHOOK_SECRET=whsec_... go run .
```

Every network method takes a `context.Context` as its first argument and an optional `receivify.RequestOptions` as its last.

## Creating invoices

Exactly one amount field is required:

| Field | Meaning | Example |
|---|---|---|
| `Amount` | Decimal amount in the coin itself | `"0.001"` BTC becomes `AmountExpected` `"100000"` |
| `AmountFiat` + `Currency` | Fiat price, converted at today's rate and locked onto the invoice | `"25.00"`, `"USD"` |
| `AmountSats` | Exact base units, for any asset | `"100000"` |

```go
// USDC on Base
inv, err := client.CreateInvoice(ctx, &receivify.CreateInvoiceParams{
	Asset:    "usdc",
	Chain:    "base",
	Amount:   "25",
	Metadata: map[string]any{"customer": "c_123"},
})
// inv.AmountExpected == "25000000", inv.Decimals == 6, inv.AmountDecimal == "25.000000"
```

You can skip the hosted checkout and build your own page instead. Everything it needs is on the invoice: `Address`, `AmountExpected`, `Decimals`, `AmountDecimal`, `PaymentURI` (use it for a QR code; it is `""` on EVM, Solana and Tron, where you show `Address` instead), `ExpiresAt` and `Status`.

To poll status from the browser side without your API key, call `client.GetPublicInvoice(ctx, id)`.

### Who pays the fee: `AddFeeOntop`

`AddFeeOntop` is a `*bool`. Leaving it out is **not** the same as sending `false`:

| Value | Effect |
|---|---|
| `nil` (omitted) | Uses your account default (`GetSettings().AddFeeOntop`) |
| `receivify.Bool(true)` | The payer covers the commission, and you receive the full amount you asked for |
| `receivify.Bool(false)` | The fee comes out of the amount, whatever your account default is |

```go
inv, err := client.CreateInvoice(ctx, &receivify.CreateInvoiceParams{
	Asset: "usdc", Chain: "base", Amount: "25",
	AddFeeOntop: receivify.Bool(true),
})
// inv.AddFeeOntop        == true
// inv.AmountNetRequested == "25000000"   (what you receive)
// inv.AmountExpected     == "25252525"   (what the payer sends)
// inv.FeeOntop           == "252525"     (AmountExpected - AmountNetRequested)
```

To change the account default, call `client.UpdateSettings(ctx, &receivify.UpdateSettingsParams{AddFeeOntop: receivify.Bool(true)})`.

### Idempotency

Pass an `IdempotencyKey` on any create. If a request times out and you retry with the same key, you get back the original invoice instead of a second one. The key also lets the SDK retry that request safely by itself:

```go
inv, err := client.CreateInvoice(ctx, params, receivify.RequestOptions{IdempotencyKey: "order-1001"})
```

`Reference` de-duplicates as well. Reusing a reference with the same terms returns the existing invoice. Reusing it with different terms returns `409 already_exists`. Make references unique per order, for example `"myshop-order-1001"`.

### Get, list and cancel

```go
inv, err := client.GetInvoice(ctx, id)

page, err := client.ListInvoices(ctx, &receivify.ListInvoicesParams{Limit: 50, Status: receivify.InvoiceStatusPaid})
// page.Data, page.HasMore, page.NextCursor

inv, err = client.CancelInvoice(ctx, id)               // only pending + unpaid; a second call is 409 not_allowed
res, err := client.BulkCancelInvoices(ctx, []string{id1, id2}) // res.Requested, res.Cancelled
```

## Listing with auto-paging

Each `List*` method has an `*AutoPaging` twin that follows `next_cursor` for you:

```go
it := client.ListInvoicesAutoPaging(ctx, &receivify.ListInvoicesParams{Status: receivify.InvoiceStatusPaid})
for it.Next() {
	inv := it.Current()
	fmt.Println(inv.ID, inv.AmountReceived)
}
if err := it.Err(); err != nil {
	log.Fatal(err)
}

// Or collect up to N items:
first100, err := client.ListInvoicesAutoPaging(ctx, nil).Collect(100)
```

Auto-paging is also available for addresses, webhook endpoints, webhook deliveries, payment links, recurring schedules and API keys.

## Forwarding addresses

A forwarding address is a permanent deposit address. Anything sent to it is forwarded to your wallet, minus the fee.

```go
fwd, err := client.CreateAddress(ctx, &receivify.CreateAddressParams{
	Ticker:      "usdc_base", // see client.ListTickers(ctx)
	Destination: "0xYourWallet...",
	Callback:    "https://shop.example/webhooks/forward", // receives forward.* events
})
fmt.Println(fwd.Address)  // give this address to payers
fmt.Println(fwd.Created)  // true = new (201), false = existing forward with the same identity (200)

detail, err := client.GetAddress(ctx, fwd.ID) // detail.Payments, detail.Callbacks

_, err = client.UpdateAddress(ctx, fwd.ID, &receivify.UpdateAddressParams{Status: receivify.ForwardStatusPaused})
_, err = client.UpdateAddress(ctx, fwd.ID, &receivify.UpdateAddressParams{CallbackURL: receivify.String("")}) // clear the callback
_, err = client.DisableAddress(ctx, fwd.ID)

tickers, err := client.ListTickers(ctx) // public; tickers.Data[i].Ticker, .Decimals
```

Read the identifier from `ID`. `ForwardID` holds the same value and is deprecated. Note that `MinForward` is a display-unit decimal (for example `"0.500000"`), not base units.

## Webhooks

Each delivery is a `POST` with an `X-Receivify-Signature: t=<unix>,v1=<hex>` header and a body like this:

```json
{"event":"invoice.paid","sequence":42,"created_at":"...","data":{...}}
```

```go
// Package-level: pass the secrets explicitly.
ok := receivify.VerifyWebhook(rawBody, r.Header.Get(receivify.SignatureHeader), []string{current, previous})

// Same check, but returns a *WebhookError whose Reason you can log:
// no_secrets, bad_header, stale_timestamp, no_match.
err := receivify.CheckWebhook(rawBody, header, secrets)

// Verify, then decode the envelope:
event, err := receivify.ParseWebhook(rawBody, header, secrets)
switch {
case event.IsInvoiceEvent(): // invoice.*, sweep.sent, test
	d, _ := event.InvoiceData()
	_ = d.Status
case event.IsForwardEvent(): // forward.*
	d, _ := event.ForwardData()
	_ = d.ValueForwarded
}

// On a client built with WithWebhookSecrets(...), the secrets are filled in for you:
client.VerifyWebhook(rawBody, header)
client.ParseWebhook(rawBody, header)
```

Rules to follow:

- **Verify the raw bytes.** Decoding the body and re-encoding it changes the bytes, and the signature will no longer match.
- The default replay window is 300 seconds. To change it, pass `receivify.WithTolerance(10*time.Minute)`. Passing `0` disables the check.
- Ignore event names you don't recognise. New ones are added over time.
- Deliveries are at-least-once and can arrive out of order. De-duplicate on `(event.Event, event.Sequence)`.
- The webhook `data` has no `asset` or `chain`. Call `client.GetInvoice(ctx, data.ID)` when you need the full invoice.

### Rotating the secret

```go
rot, err := client.RotateWebhookSecret(ctx, receivify.RequestOptions{IdempotencyKey: "rotate-2026-10"})
// rot.Secret is shown ONCE. Store it now.
// Until rot.PrevExpiresAt (24h by default), accept both secrets:
client, _ = receivify.NewClient(apiURL, apiKey, receivify.WithWebhookSecrets(rot.Secret, oldSecret))

state, err := client.GetWebhookSecretState(ctx)
if !state.PrevActive {
	// The grace window has closed. Remove oldSecret from your config.
}
```

Receivify always signs with the current secret. During the grace window your receiver should accept either one.

## Error handling

Every API failure is an `*receivify.APIError`, and it matches the sentinel for its code with `errors.Is`:

```go
inv, err := client.GetInvoice(ctx, id)
switch {
case err == nil:
	// ok
case errors.Is(err, receivify.ErrNotFound): // 404
case errors.Is(err, receivify.ErrBadRequest): // 400, e.g. id is not a UUID
case errors.Is(err, receivify.ErrUnauthorized): // 401, bad or revoked key
case errors.Is(err, receivify.ErrRateLimited): // 429
	var apiErr *receivify.APIError
	errors.As(err, &apiErr)
	time.Sleep(apiErr.RetryAfter())
default:
	var apiErr *receivify.APIError
	var connErr *receivify.ConnectionError
	switch {
	case errors.As(err, &apiErr):
		log.Printf("receivify %s (HTTP %d): %s", apiErr.Code, apiErr.StatusCode, apiErr.Detail)
	case errors.As(err, &connErr):
		log.Printf("network problem (timeout=%v): %v", connErr.Timeout, connErr.Err)
	}
}
```

- Branch on `Code` or the sentinels. Never branch on `Detail`; its wording can change.
- A code added after this SDK version still comes back as an `*APIError` with `Code` set. It just won't match any sentinel.
- There is one sentinel per code: `ErrBadRequest`, `ErrBadTicker`, `ErrBadDestination`, `ErrAssetUnavailable`, `ErrAmountTooLow`, `ErrSuspended`, `ErrLimitExceeded`, `ErrInvalidCode`, `ErrNoSetup`, `ErrNotEnabled`, `ErrNoTarget`, `ErrInvalidToken`, `ErrUnauthorized`, `ErrBadSignature`, `ErrSignupDisabled`, `ErrNotFound`, `ErrAlreadyExists`, `ErrAlreadyEnabled`, `ErrNotAllowed`, `ErrRateLimited`, `ErrInternal`, `ErrUpstream`, `ErrRateUnavailable`.
- `apiErr.Retryable()` is true for `rate_limited`, `internal`, `upstream_error`, `rate_unavailable` and any 5xx.

### Retries and timeouts

- **Retried:** GET requests, and creates that carry an `IdempotencyKey`. They are retried on 429, 5xx and network errors, up to 2 times by default. The delay honours `Retry-After`, then `X-RateLimit-Reset`, then falls back to exponential backoff.
- **Never retried:** any other POST, PATCH or DELETE, so a payment can't be created twice.
- **Timeout:** 30 seconds per attempt by default.

```go
client, err := receivify.NewClient(apiURL, apiKey,
	receivify.WithTimeout(10*time.Second),
	receivify.WithMaxRetries(3),
	receivify.WithRetryBackoff(250*time.Millisecond),
	receivify.WithHTTPClient(&http.Client{Transport: myTransport}),
	receivify.WithUserAgent("my-shop/2.1"), // sent as "receivify-go/1.0.0 my-shop/2.1"
)
```

The API key goes only in the `Authorization` header. It never appears in a URL, an error message, or `fmt` output of the client.

## Money helpers

Every amount is a string of **base units** (satoshis, wei, and so on). The SDK never converts these for you, because a `float64` cannot hold a wei value exactly. When you need to display or compare amounts, use the `math/big`-backed helpers:

```go
s, _ := receivify.FormatUnits("123456", 8)         // "0.00123456"
b, _ := receivify.ParseUnits("0.00123456", 8)      // "123456"
_, err := receivify.ParseUnits("0.123456789", 8)   // error: refuses to round
c, _ := receivify.CompareAmounts(inv.AmountReceived, inv.AmountExpected) // -1, 0, 1
n, _ := receivify.ToBigInt(inv.AmountExpected)     // *big.Int for arithmetic

paidInFull := c >= 0
```

Get `decimals` from `Invoice.Decimals` or `Ticker.Decimals`. `Invoice.AmountDecimal` is already formatted, but use it for display only.

## More endpoints

| Area | Methods |
|---|---|
| Invoices | `CreateInvoice`, `GetInvoice`, `ListInvoices`, `ListInvoicesAutoPaging`, `CancelInvoice`, `BulkCancelInvoices`, `GetPublicInvoice` |
| Forwarding | `CreateAddress`, `GetAddress`, `ListAddresses`, `ListAddressesAutoPaging`, `UpdateAddress`, `DisableAddress` |
| Catalogue (public) | `ListTickers`, `ListAssets` |
| Webhooks | `RotateWebhookSecret`, `GetWebhookSecretState`, `CreateWebhookEndpoint`, `ListWebhookEndpoints`, `UpdateWebhookEndpoint`, `DeleteWebhookEndpoint`, `ListWebhookDeliveries`, `ReplayWebhookDelivery`, `SendTestWebhook` |
| Payment links | `CreatePaymentLink`, `GetPaymentLink`, `ListPaymentLinks`, `UpdatePaymentLink`, `DeactivatePaymentLink`, `GetPublicPaymentLink` |
| Recurring | `CreateRecurring`, `ListRecurring`, `CancelRecurring` |
| Account | `GetSettings`, `UpdateSettings`, `GetSummary`, `ListAPIKeys`, `CreateAPIKey`, `RevokeAPIKey`, `GetPayouts`, `StagePayoutChanges`, `CancelPayoutChange`, `GetHealth` |
| Anything else | `client.Do(ctx, method, path, body, &out)` |

## Running the tests

The tests run fully offline. Each one injects an `http.RoundTripper`, so nothing touches the network:

```sh
go test ./...
go vet ./...
gofmt -l .
```

You can use the same approach in your own tests: pass `receivify.WithHTTPClient(&http.Client{Transport: yourFake})`, and sign fixture webhooks with `receivify.SignWebhook(body, secret, time.Now())`.

## Links

- Website: https://receivify.io
- Docs and API reference: https://receivify.io/docs

## License

MIT. See [LICENSE](LICENSE).
