# Receivify .NET SDK

Official C# / .NET SDK for the [Receivify](https://receivify.io) crypto payment gateway — invoices,
forwarding addresses, payment links and signed webhooks.

- **.NET 8**, nullable enabled, `async`/`await` throughout.
- **Zero third-party packages.** `HttpClient`, `System.Text.Json` and `System.Security.Cryptography`,
  nothing else. There is not one `PackageReference` in the library.
- **Money is always a `string`.** Never a `double`, never a `decimal`. See
  [Money is a string](#money-is-a-string-and-that-is-deliberate).
- **`IAsyncEnumerable<T>`** auto-pagination on every list.
- **A typed exception per error code**, with unknown codes degrading gracefully.

---

## Install

The SDK is a plain class library. Reference the project directly, or pack it:

```bash
dotnet add reference path/to/receivify-sdks/dotnet/src/Receivify/Receivify.csproj
```

---

## Quickstart

### 1. Create a client

Create **one** client and keep it. It is thread-safe and holds a single `HttpClient`.

```csharp
using Receivify;

var client = new ReceivifyClient(
    apiUrl: "https://receivify.io",
    apiKey: Environment.GetEnvironmentVariable("RECEIVIFY_API_KEY"),
    options: new ReceivifyClientOptions
    {
        Timeout = TimeSpan.FromSeconds(30),
        // Current secret first, previous second — see "Rotating the webhook secret" below.
        WebhookSecrets = new[] { Environment.GetEnvironmentVariable("RECEIVIFY_WEBHOOK_SECRET")! },
    });
```

From ASP.NET Core, hand it the `HttpClient` your DI container manages instead:

```csharp
builder.Services.AddHttpClient("receivify");

builder.Services.AddSingleton<ReceivifyClient>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("receivify");
    var config = sp.GetRequiredService<IConfiguration>();

    return new ReceivifyClient(
        http,
        config["Receivify:ApiUrl"]!,
        config["Receivify:ApiKey"],
        new ReceivifyClientOptions
        {
            WebhookSecrets = config.GetSection("Receivify:WebhookSecrets").Get<string[]>() ?? Array.Empty<string>(),
        });
});
```

> **Never construct an `HttpClient` per request.** That exhausts sockets under load. The SDK never
> does it, and neither should the code around it.

### 2. Create a forwarding address

A forwarding address is a standing deposit address that sweeps everything it receives to your
wallet.

```csharp
Forward forward = await client.CreateAddressAsync(new ForwardCreateRequest
{
    Ticker = "btc",
    Destination = "bc1qyourcoldwallet...",
    Callback = "https://your-app.example/webhooks/receivify",
});

Console.WriteLine($"Send BTC to {forward.Address}");
Console.WriteLine($"Forward id: {forward.Id}");   // read Id, never ForwardId
```

Creation is **deterministic**: the identity of a forward is
`(merchant, resolved asset, destination, callback_url)`. Calling this again with the same three
values returns the same forward rather than deriving a new address, so a retry costs nothing. A
**201** means a new address was derived and a **200** means an existing one came back — use
`CreateAddressWithStatusAsync` when you need to tell them apart:

```csharp
ReceivifyResponse<Forward> response = await client.CreateAddressWithStatusAsync(request);
bool newlyDerived = response.StatusCode == 201;
```

### 3. Create an invoice

```csharp
Invoice invoice = await client.CreateInvoiceAsync(
    new InvoiceCreateRequest
    {
        Asset = "btc",
        AmountFiat = "25.00",
        Currency = "USD",
        Reference = "order-1042",
        CallbackUrl = "https://your-app.example/webhooks/receivify",
        Metadata = new Dictionary<string, object?> { ["cart_id"] = "abc123" },
    },
    new RequestOptions { IdempotencyKey = Guid.NewGuid().ToString() });

Console.WriteLine($"Pay {invoice.AmountExpected} base units to {invoice.Address}");
Console.WriteLine($"Or send the payer to {invoice.CheckoutUrl}");
```

`Reference` is also a de-duplication key: reusing one returns the **existing** invoice with a 201,
never a 409. There is no way to ask for a second invoice under one reference.

### 4. Receive and verify a webhook

```csharp
app.MapPost("/webhooks/receivify", async (HttpRequest request, ReceivifyClient client) =>
{
    // The RAW bytes. Read them before anything else touches the body.
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    byte[] rawBody = buffer.ToArray();

    string? signature = request.Headers["X-Receivify-Signature"];

    if (!client.VerifyWebhook(rawBody, signature))
    {
        return Results.Unauthorized();
    }

    WebhookEvent evt = client.ParseWebhook(rawBody, signature);

    switch (evt.Event)
    {
        case EventNames.InvoicePaid:
        {
            InvoiceEventData data = evt.AsInvoiceData()!;
            // AmountReceived is an exact base-unit STRING. Compare it, do not parse it.
            await FulfilOrder(data.Reference, data.AmountReceived);
            break;
        }

        case EventNames.ForwardSent:
        {
            ForwardEventData data = evt.AsForwardData()!;
            await RecordSweep(data.ForwardId, data.TxidOut, data.ValueForwarded);
            break;
        }

        default:
            // Ignore names you do not recognise: adding one is an additive change.
            break;
    }

    // Any 2xx marks the delivery delivered. Anything else is retried.
    return Results.Ok();
});
```

#### Verification, in three rules

1. **Use the RAW body bytes.** The signature is
   `HMAC-SHA256(secret, "<t>.<rawBody>")` over the exact bytes Receivify sent. Re-serialising a
   parsed object — `JsonSerializer.Serialize(model)`, a model-bound `[FromBody]` parameter,
   anything — changes key order and whitespace and **the HMAC will not match a genuine delivery**.
   In ASP.NET Core read `HttpRequest.Body` yourself before model binding runs.
2. **Verify against a *list* of secrets.** During a rotation two secrets are legitimately valid at
   once. That is why `VerifyWebhook` takes an `IEnumerable<string>` and always has.
3. **Keep the timestamp window.** Comparison is constant-time
   (`CryptographicOperations.FixedTimeEquals`) and the `t=` value must be within
   `WebhookVerifyOptions.ToleranceSeconds` — 300 by default. No window is enforced server-side, so
   this one is what stops a replay.

If you want to know *why* a delivery was rejected, use `DescribeWebhook`:

```csharp
WebhookVerificationResult result = client.DescribeWebhook(rawBody, signature);
if (!result.Ok)
{
    logger.LogWarning("Rejected webhook: {Reason} — {Message}", result.Reason, result.Message);
}
```

`Reason` is one of `NoSecrets`, `BadHeader`, `StaleTimestamp`, `NoMatch`, `BadJson`, `BadEnvelope`.

### 5. Rotating the webhook secret

```csharp
WebhookSecretRotated rotated = await client.RotateWebhookSecretAsync(
    new RequestOptions { IdempotencyKey = Guid.NewGuid().ToString() });

// Shown ONCE. There is no read-back endpoint — if you lose it, rotate again.
// NULL on an Idempotency-Key replay: the secret is never persisted for the replay window.
string? newSecret = rotated.Secret;
```

Until `rotated.PrevExpiresAt`, deliveries may still carry a signature made with the old secret, so
verify against both:

```csharp
var options = new ReceivifyClientOptions
{
    WebhookSecrets = new[] { newSecret!, oldSecret },
};
```

A running process can ask whether the fallback is still needed:

```csharp
WebhookSecretState state = await client.GetWebhookSecretStateAsync();
if (!state.PrevActive)
{
    // The grace window has closed; drop the old secret.
}
```

`rotated.ReplacedPrevious == true` means this rotation retired a secret that was *still* inside its
grace window — any receiver still on it starts failing now.

---

## Money is a string, and that is deliberate

Every monetary quantity on the wire is an **exact-integer base-unit string**: satoshis for BTC, wei
for ETH, the coin's smallest unit otherwise.

```csharp
invoice.AmountExpected   // "1234567890123456789"  — a string, always
```

A `double` cannot represent a 256-bit wei value at all. A `decimal` tops out around 28 significant
digits and would imply an arithmetic model the API does not have. So the SDK hands you the string
and stays out of the way. When you genuinely need arithmetic, `Money` gives you exact operations via
`BigInteger`:

```csharp
Money.Format("150000000", decimals: 8)   // "1.50000000"   for display
Money.Parse("1.5", decimals: 8)          // "150000000"    back to base units
Money.Compare(a, b)                      // exact, no rounding
Money.Add(a, b);  Money.Subtract(a, b)   // exact, base-unit strings in and out
```

Take `decimals` from `ListTickersAsync()` (`Ticker.Decimals`) — never hard-code 8.

`Money.Parse` **throws** rather than silently truncating a value with more precision than the asset
can hold. Rounding money away without saying so is the bug this whole rule exists to prevent.

### The exceptions worth knowing

Three money fields are **decimal** rather than base units, and are still strings:

| Field | What it is |
| --- | --- |
| `Forward.MinForward` | A display-unit decimal, e.g. `"0.50000000"`. The one field on `Forward` that is not base units. |
| `PublicPaymentLink.AmountBtc` | A decimal BTC reference value. |
| `RecurringSchedule.Amount` | A decimal amount in the coin. |

`Invoice.AmountFiat` and `RecurringSchedule.AmountFiat` are decimal display prices like `"25.00"` —
strings, never numbers, and never something to settle against.

`PaymentLink` is asymmetric by design: you **set** the amount with `amount_btc` (decimal BTC) and
**read** it as `AmountSats` (base units).

FX rates in `Summary.Fx` and `PublicPaymentLink.Fx` are the one deliberate exception and stay JSON
`double`s. A rate is not money: it is advisory, published as a float upstream, and nothing is ever
settled against it.

---

## Pagination

Every list returns the same envelope, and every list has an auto-paginating twin.

```csharp
// One page.
ReceivifyList<Invoice> page = await client.ListInvoicesAsync(new InvoiceListParams
{
    Limit = 100,
    Status = InvoiceStatus.Paid,
});

foreach (Invoice invoice in page.Data) { /* ... */ }
if (page.HasMore) { /* pass page.NextCursor back as Cursor */ }
```

```csharp
// Every page, transparently.
await foreach (Invoice invoice in client.EnumerateInvoicesAsync(new() { Status = InvoiceStatus.Paid }))
{
    Console.WriteLine(invoice.Id);
}
```

`HasMore` is a fact — the server reads `limit + 1` rows. Never infer it from `Data.Count == Limit`.
The paginator throws rather than looping forever if a cursor ever repeats or if `has_more` is true
with no cursor.

`EnumerateXAsync` exists for `Invoices`, `Addresses` (and its `Forwards` alias), `PaymentLinks`,
`Recurring`, `ApiKeys`, `WebhookEndpoints` and `WebhookDeliveries`.

**Not every array is a list envelope.** `PayoutState.Pending`, `ForwardDetail.Payments`,
`ForwardDetail.Callbacks`, `Summary.ByAsset`, `Summary.Series` and `WebhookTestResult.Results` are
bare arrays with no cursor. They have no paginator, on purpose.

---

## Errors

Every failure is `{ "error": "<code>", "detail": "<sentence>" }`, and every code maps to a type.

```csharp
try
{
    await client.CreateAddressAsync(request);
}
catch (BadDestinationException ex)
{
    // The payer-facing wallet address is wrong for that chain.
    return BadRequest(ex.Detail);
}
catch (RateLimitedException ex)
{
    await Task.Delay(TimeSpan.FromSeconds(ex.RetryAfterSeconds ?? 5));
}
catch (ReceivifyApiException ex)
{
    // Anything else the API said — including a code this SDK version has never heard of.
    logger.LogError("Receivify {Code} ({Status}): {Detail}", ex.Code, ex.StatusCode, ex.Detail);
}
catch (ReceivifyConnectionException ex)
{
    // No HTTP response at all: DNS, TLS, socket, or our own timeout (ex.IsTimeout).
}
```

| Code | Exception | HTTP |
| --- | --- | --- |
| `bad_request` | `BadRequestException` | 400 |
| `bad_ticker` | `BadTickerException` | 400 |
| `bad_destination` | `BadDestinationException` | 400 |
| `asset_unavailable` | `AssetUnavailableException` | 400 |
| `amount_too_low` | `AmountTooLowException` | 400 |
| `suspended` | `SuspendedException` | 400 |
| `limit_exceeded` | `LimitExceededException` | 400 |
| `invalid_code` | `InvalidCodeException` | 400 |
| `no_setup` | `NoSetupException` | 400 |
| `not_enabled` | `NotEnabledException` | 400 |
| `no_target` | `NoTargetException` | 400 |
| `invalid_token` | `InvalidTokenException` | 400 |
| `unauthorized` | `UnauthorizedException` | 401 |
| `bad_signature` | `BadSignatureException` | 401 |
| `signup_disabled` | `SignupDisabledException` | 403 |
| `not_found` | `NotFoundException` | 404 |
| `already_exists` | `AlreadyExistsException` | 409 |
| `already_enabled` | `AlreadyEnabledException` | 409 |
| `not_allowed` | `NotAllowedException` | 409 |
| `rate_limited` | `RateLimitedException` | 429 |
| `internal` | `InternalServerException` | 500 |
| `upstream_error` | `UpstreamException` | 502 |
| `rate_unavailable` | `RateUnavailableException` | 503 |

**An unknown code never crashes an older SDK.** It arrives as a plain `ReceivifyApiException` with
`Code` intact, because adding a code is an additive change. Branch on `Code` or on the type — never
on `Detail`, whose wording is explicitly not part of the frozen contract.

`GET /api/health` is the one documented non-conformer: its 503 returns the same
`{status, time, checks, alerts}` object as its 200, with no `error` key. `GetHealthAsync()`
therefore returns the report on both statuses instead of throwing.

---

## Idempotency and retries

`Idempotency-Key` is accepted on exactly eight endpoints — the creates, plus the webhook-secret
rotation:

`CreateInvoiceAsync`, `CreateAddressAsync`, `CreateApiKeyAsync`, `CreatePaymentLinkAsync`,
`CreateRecurringAsync`, `CreateWebhookEndpointAsync`, `RotateWebhookSecretAsync` (and the
query-parameter forwarding create, which this SDK does not model because you should use the POST).

```csharp
await client.CreateInvoiceAsync(request, new RequestOptions { IdempotencyKey = orderId });
```

A replay returns the **original body and the original status code**, plus `Idempotent-Replay: true`
(readable via `ReceivifyResponse<T>.IdempotentReplay`). Two shown-once fields come back **null** on a
replay, because they are deliberately never persisted: `ApiKeyCreated.ApiKeyValue` and
`WebhookSecretRotated.Secret`.

**The retry rule.** A request is retried on 429/5xx only when it is safe:

- a `GET`, or
- a mutating call that carries an idempotency key **and** targets an endpoint that declares support.

A key on `PATCH /api/v1/address` is still sent but does **not** unlock retries, because the server
would not deduplicate it. Blind-retrying a mutating call is how you double-charge someone. Backoff
is exponential with jitter, honouring `Retry-After` first and then `X-RateLimit-Reset`.

---

## The full surface

| Area | Methods |
| --- | --- |
| Invoices | `CreateInvoiceAsync`, `GetInvoiceAsync`, `ListInvoicesAsync`, `EnumerateInvoicesAsync`, `CancelInvoiceAsync`, `BulkCancelInvoicesAsync`, `GetPublicInvoiceAsync` |
| Forwarding | `CreateAddressAsync`, `CreateAddressWithStatusAsync`, `GetAddressAsync`, `ListAddressesAsync`, `EnumerateAddressesAsync`, `UpdateAddressAsync`, `DisableAddressAsync` (+ `…ForwardAsync` aliases) |
| Catalogue | `ListTickersAsync`, `ListAssetsAsync` (public — no key needed) |
| Webhooks | `ListWebhookEndpointsAsync`, `EnumerateWebhookEndpointsAsync`, `CreateWebhookEndpointAsync`, `UpdateWebhookEndpointAsync`, `DeleteWebhookEndpointAsync`, `ListWebhookDeliveriesAsync`, `EnumerateWebhookDeliveriesAsync`, `ReplayWebhookDeliveryAsync`, `GetWebhookSecretStateAsync`, `RotateWebhookSecretAsync`, `SendTestWebhookAsync` |
| Payment links | `ListPaymentLinksAsync`, `EnumeratePaymentLinksAsync`, `CreatePaymentLinkAsync`, `GetPaymentLinkAsync`, `UpdatePaymentLinkAsync`, `DeactivatePaymentLinkAsync`, `GetPublicPaymentLinkAsync` |
| Recurring | `ListRecurringAsync`, `EnumerateRecurringAsync`, `CreateRecurringAsync`, `CancelRecurringAsync` |
| API keys | `ListApiKeysAsync`, `EnumerateApiKeysAsync`, `CreateApiKeyAsync`, `RevokeApiKeyAsync` |
| Account | `GetSettingsAsync`, `UpdateSettingsAsync`, `GetSummaryAsync`, `GetPayoutsAsync`, `StagePayoutChangesAsync`, `CancelPayoutChangeAsync` |
| System | `GetHealthAsync` |
| Escape hatch | `RequestAsync<T>`, `SendAsync<T>` for any endpoint this version does not model |

Deliberately **not** modelled: `/api/admin/**` and `/api/auth/**` (outside the SDK surface),
`POST /api/v1/btcpay/webhook` (an inbound provider webhook), `/api/v1/2fa/*` and
`/api/public/payouts/confirm` (browser session flows), and `POST /api/public/signup` — so no SDK
code path can ever mint credentials.

---

## Naming: addresses and forwards

The shared SDK contract names these `CreateAddress`/`GetAddress`/…; the OpenAPI operation ids name
them `createForward`/`getForward`/…. Both names are exposed and do the same thing. Pick one and be
consistent.

## `Id`, not `ForwardId`

`Forward.Id` is the canonical identifier. `Forward.ForwardId` carries the identical value, is marked
`[Obsolete]`, and is not going away — removing a response field would be a breaking change, so the
API emits it for the whole life of v1.

Note that `ForwardEventData.ForwardId` inside a `forward.*` **webhook** is *not* deprecated: there it
names a relationship to a forward rather than the payload's own identity.

## Clearing a field

Two update requests distinguish "leave this alone" from "set this to null", because for them null is
a real instruction. `ForwardUpdateRequest` and `PaymentLinkUpdateRequest` track which properties you
assigned:

```csharp
// Sends {"status":"paused"} — callback_url is untouched.
await client.UpdateAddressAsync(id, new ForwardUpdateRequest { Status = ForwardStatus.Paused });

// Sends {"callback_url":null} — which CLEARS the callback.
await client.UpdateAddressAsync(id, new ForwardUpdateRequest { CallbackUrl = null });

// Sends {"amount_btc":null} — which converts the link to open-amount.
await client.UpdatePaymentLinkAsync(id, new PaymentLinkUpdateRequest { AmountBtc = null });
```

## Statuses are open sets

`InvoiceStatus`, `ForwardStatus`, `Providers` and `EventNames` are classes of `const string`, not
C# `enum`s. Adding a value is an additive change under the API's versioning policy, and an `enum`
would turn a new value into a decoding failure. Always keep a default branch.

---

## Building and testing

```bash
cd dotnet

dotnet build                                   # library + tests
dotnet run --project tests/Receivify.Tests     # the suite; exits non-zero on failure
```

The tests use no test framework at all — a small console runner, so there is nothing to restore. They
are entirely offline: the HTTP layer is injected as a fake `HttpMessageHandler` and no socket is ever
opened. Coverage includes the list envelope and auto-pagination, error-code mapping, an unknown code
degrading gracefully, money staying an exact string end to end, signature verification with the
current *and* the previous secret, a tampered body, a stale timestamp, and the retry gate.

## License

MIT. See [LICENSE](LICENSE).
