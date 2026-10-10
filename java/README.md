# receivify-java: the official Java SDK for Receivify

Invoices, forwarding addresses, payment links and signed webhooks for the
[Receivify](https://receivify.io) crypto payment gateway. Full API reference:
[receivify.io/docs](https://receivify.io/docs).

- **Zero runtime dependencies.** It uses only the JDK: `java.net.http`, `javax.crypto` and a small
  built-in JSON reader/writer.
- **Java 11+.** Works from Kotlin too, with no extra code (see [Kotlin](#kotlin)).
- **Money is never a `double`.** Every amount is an exact-integer base-unit `String`, and the JSON
  layer keeps numbers as their raw text.
- **Thread-safe.** Build one `ReceivifyClient` and share it.

---

## Install

> The Maven Central package is coming soon. Until it's published, run the "Build from source"
> step below once; the coordinates then resolve from your local Maven repository.

**Maven**

```xml
<dependency>
  <groupId>io.receivify</groupId>
  <artifactId>receivify-java</artifactId>
  <version>1.0.0</version>
</dependency>
```

**Gradle (Kotlin DSL)**

```kotlin
implementation("io.receivify:receivify-java:1.0.0")
```

**Gradle (Groovy)**

```groovy
implementation 'io.receivify:receivify-java:1.0.0'
```

**Build from source** (puts the jar in your local Maven repository):

```bash
git clone https://github.com/receivify/receivify-sdks.git
cd receivify-sdks/java
mvn -q install          # builds, runs the offline test suite, installs io.receivify:receivify-java:1.0.0
```

Or just copy `target/receivify-java-1.0.0.jar` onto your classpath: it has no dependencies.

---

## Two rules that will bite you if you skip them

**1. Money is a string. Never turn it into a `double`.**

Every amount on the wire is an exact-integer string of base units: satoshis for BTC, wei for ETH,
the smallest unit otherwise. A `double` cannot hold a satoshi count above 2^53, let alone a 256-bit
wei value. This SDK never converts those fields for you. When you need arithmetic, use `Money`
(backed by `BigInteger`/`BigDecimal`):

```java
Money.format(invoice.getAmountExpected(), invoice.getDecimals());        // "0.00100000"
Money.compare(invoice.getAmountReceived(), invoice.getAmountExpected()) >= 0;  // paid in full?
```

**2. Verify webhooks against the RAW request body.**

The signature is an HMAC over the exact bytes we sent. If you let a framework bind the body to a
DTO and re-serialise it, key order and whitespace change and verification fails even for a genuine
delivery. Take the body as `byte[]` (or `String`).

---

## Quickstart

### 1. Create a client

```java
import io.receivify.*;
import java.time.Duration;
import java.util.List;

ReceivifyClient client = ReceivifyClient.builder()
    .apiUrl("https://receivify.io")
    .apiKey(System.getenv("RECEIVIFY_API_KEY"))
    .webhookSecrets(List.of(System.getenv("RECEIVIFY_WEBHOOK_SECRET")))
    .timeout(Duration.ofSeconds(30))
    .build();
```

### 2. Create an invoice and redirect the payer

```java
Invoice invoice = client.createInvoice(
    CreateInvoiceParams.builder()
        .amountFiat("25.00").currency("USD")   // or .amount("0.001") in coin units, or .amountSats("100000")
        .asset("btc")
        .reference("order-" + orderId)
        .build(),
    RequestOptions.idempotencyKey("order-" + orderId)); // a retry after a timeout will NOT create a second invoice

// Redirect the payer to the hosted checkout page...
response.sendRedirect(invoice.getCheckoutUrl());

// ...or render your own checkout from these fields:
invoice.getAddress();        // deposit address
invoice.getAmountExpected(); // "30284"   exact base units (what the payer must send)
invoice.getAmountDecimal();  // "0.00030284"  display only
invoice.getDecimals();       // 8
invoice.getPaymentUri();     // "bitcoin:bc1q...?amount=0.00030284", or null when the chain has no safe URI
invoice.getExpiresAt();      // java.time.Instant
```

Reusing a `reference` also returns the existing invoice instead of creating a new one.

USDC on Base, ETH on Ethereum:

```java
client.createInvoice(CreateInvoiceParams.builder().asset("usdc").chain("base").amount("25").build());
client.createInvoice(CreateInvoiceParams.builder().asset("eth").chain("ethereum").amount("0.05").build());
```

### 3. Receive webhooks: Spring Boot

```java
import io.receivify.*;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.*;

@RestController
public class ReceivifyWebhookController {

    private final List<String> secrets = List.of(System.getenv("RECEIVIFY_WEBHOOK_SECRET"));

    @PostMapping(value = "/webhooks/receivify", consumes = "application/json")
    public ResponseEntity<String> handle(
            @RequestBody byte[] rawBody,                                   // RAW bytes, not a DTO
            @RequestHeader(value = "X-Receivify-Signature", required = false) String signature) {

        WebhookEvent event;
        try {
            event = Webhooks.parse(rawBody, signature, secrets);
        } catch (SignatureVerificationException e) {
            return ResponseEntity.status(400).body("bad signature: " + e.getReason());
        }

        switch (event.getEvent()) {
            case EventNames.INVOICE_PAID: {
                InvoiceEventData data = event.getInvoiceData();
                // De-duplicate on (event, sequence): delivery is at-least-once.
                // For the authoritative state, call client.getInvoice(data.getId()).
                markOrderPaid(data.getReference(), data.getAmountReceived());
                break;
            }
            case EventNames.INVOICE_EXPIRED:
                releaseStock(event.getInvoiceData().getReference());
                break;
            default:
                // Ignore event names you do not recognise: new ones may be added.
        }
        return ResponseEntity.ok("ok");
    }
}
```

`@RequestBody String rawBody` works too. Do **not** bind to a POJO.

### 3b. Receive webhooks: plain JDK `HttpServer`

```java
import com.sun.net.httpserver.HttpServer;
import io.receivify.*;
import java.net.InetSocketAddress;
import java.util.List;

HttpServer server = HttpServer.create(new InetSocketAddress(8080), 0);
List<String> secrets = List.of(System.getenv("RECEIVIFY_WEBHOOK_SECRET"));

server.createContext("/webhooks/receivify", exchange -> {
    byte[] rawBody = exchange.getRequestBody().readAllBytes();
    String signature = exchange.getRequestHeaders().getFirst("X-Receivify-Signature");

    int status;
    try {
        WebhookEvent event = Webhooks.parse(rawBody, signature, secrets);
        if (EventNames.INVOICE_PAID.equals(event.getEvent())) {
            System.out.println("paid: " + event.getInvoiceData().getId());
        }
        status = 200;
    } catch (SignatureVerificationException e) {
        status = 400;
    }
    exchange.sendResponseHeaders(status, -1);
    exchange.close();
});
server.start();
```

Just a boolean? `Webhooks.verify(rawBody, signature, secrets)`. Need a loggable reason?
`Webhooks.describe(rawBody, signature, secrets, null).getReason()`. The default replay tolerance is
300 seconds; change it with `Webhooks.verify(rawBody, signature, secrets, Duration.ofMinutes(10))`.

---

## Kotlin

No Kotlin-specific artifact is needed. Getters documented as nullable come through as platform
types, so use `?.`/`?:` on them; lists are never null.

```kotlin
import io.receivify.*
import java.time.Duration

val client = ReceivifyClient.builder()
    .apiUrl("https://receivify.io")
    .apiKey(System.getenv("RECEIVIFY_API_KEY"))
    .webhookSecrets(listOf(System.getenv("RECEIVIFY_WEBHOOK_SECRET")))
    .timeout(Duration.ofSeconds(30))
    .build()

val invoice = client.createInvoice(
    CreateInvoiceParams.builder()
        .amount("25").asset("usdc").chain("base")
        .reference("order-42")
        .addFeeOntop(true)
        .build(),
    RequestOptions.idempotencyKey("order-42"),
)
println("Pay ${invoice.amountDecimal} USDC to ${invoice.address}")
val qr: String = invoice.paymentUri ?: invoice.address

// Auto-pagination is a plain Iterable; turn it into a lazy Sequence if you like.
client.autoPagingInvoices(ListInvoicesParams.builder().status(InvoiceStatus.PAID).build())
    .asSequence()
    .take(100)
    .forEach { println("${it.id} ${it.amountExpected}") }

// Webhook (Ktor / Spring / anything that gives you the raw bytes)
fun onWebhook(rawBody: ByteArray, signature: String?) {
    val event = try {
        Webhooks.parse(rawBody, signature, client.webhookSecrets)
    } catch (e: SignatureVerificationException) {
        return  // respond 400
    }
    when (event.event) {
        EventNames.INVOICE_PAID -> println("paid ${event.invoiceData?.id}")
        else -> Unit
    }
}
```

---

## Fee on top (`add_fee_ontop`)

By default our commission comes out of the amount: you receive `amount_expected` minus the fee.
With `addFeeOntop(true)` the payer is quoted a grossed-up amount and you receive the amount you
asked for in full.

```java
Invoice inv = client.createInvoice(CreateInvoiceParams.builder()
    .asset("usdc").chain("base").amount("25")
    .addFeeOntop(true)            // true: payer covers the fee
    .build());

inv.isAddFeeOntop();          // true
inv.getAmountNetRequested();  // "25000000"  what you receive
inv.getFeeOntop();            // "252525"    what the gross-up added
inv.getAmountExpected();      // "25252525"  what the payer sends (= net + fee_ontop)
```

There are three states, and they are different:

| call | sent | meaning |
|---|---|---|
| not called, or `addFeeOntop(null)` | field omitted | use the **account default** (`client.getSettings().isAddFeeOntop()`) |
| `addFeeOntop(false)` | `"add_fee_ontop": false` | force fee-out-of-amount for this invoice |
| `addFeeOntop(true)` | `"add_fee_ontop": true` | fee on top for this invoice |

Change the account default with
`client.updateSettings(UpdateSettingsParams.builder().addFeeOntop(true).build())`.

---

## Listing and auto-pagination

Every list returns a `Page<T>`: `getData()`, `hasMore()`, `getNextCursor()`.

```java
Page<Invoice> page = client.listInvoices(ListInvoicesParams.builder().limit(50).status(InvoiceStatus.PAID).build());
for (Invoice i : page) { ... }
if (page.hasMore()) {
    Page<Invoice> next = client.listInvoices(ListInvoicesParams.builder().limit(50).cursor(page.getNextCursor()).build());
}
```

Or let the SDK follow the cursors. Pages are fetched lazily as you iterate, so breaking out early
stops the requests:

```java
for (Invoice i : client.autoPagingInvoices(ListInvoicesParams.builder().status(InvoiceStatus.PAID).build())) {
    System.out.println(i.getId() + " " + Money.format(i.getAmountExpected(), i.getDecimals()));
}

long paid = client.autoPagingInvoices().stream().filter(i -> "paid".equals(i.getStatus())).count();
```

Also available: `autoPagingAddresses`, `autoPagingPaymentLinks`, `autoPagingWebhookEndpoints`,
`autoPagingWebhookDeliveries`, `autoPagingRecurring`, `autoPagingApiKeys`.

---

## Forwarding addresses

A forwarding address is a permanent deposit address that sweeps everything it receives to your
wallet. Creation is deterministic: the same ticker, destination and callback always return the same
address.

```java
Page<Ticker> tickers = client.listTickers();     // public; e.g. "btc", "usdc_base", with decimals

Forward fwd = client.createAddress(
    CreateAddressParams.builder()
        .ticker("usdc_base")
        .destination("0xYourWallet")
        .callback("https://example.com/webhooks/receivify-forward")  // receives forward.* events
        .build(),
    RequestOptions.idempotencyKey("user-123-usdc"));

fwd.getAddress();     // give this to the payer
fwd.getMinForward();  // "0.500000"  NOTE: display units, not base units

ForwardDetail detail = client.getAddress(fwd.getId());   // + payments and callbacks logs
client.updateAddress(fwd.getId(), UpdateAddressParams.pause());   // kill switch
client.updateAddress(fwd.getId(), UpdateAddressParams.resume());
client.disableAddress(fwd.getId());                       // soft delete
```

Use `createAddressWithResponse(...)` to tell a `201` (newly derived) from a `200` (an existing
forward came back). Creating an address whose existing forward is paused or disabled throws
`NotAllowedException` (409); re-enable it with `updateAddress` instead.

---

## Errors

Every API failure throws a subclass of `ReceivifyApiException` (unchecked), with `getCode()`,
`getDetail()`, `getStatusCode()`, `getHeaders()` and `getRawBody()`:

```java
try {
    client.getInvoice(id);
} catch (NotFoundException e) {            // 404 not_found
    ...
} catch (BadRequestException e) {          // 400 bad_request (e.g. not a UUID)
    log.warn("bad request: {}", e.getDetail());
} catch (UnauthorizedException e) {        // 401: wrong or revoked key
    ...
} catch (RateLimitedException e) {         // 429
    Long wait = e.getRetryAfterSeconds();
} catch (ReceivifyApiException e) {        // any other code, including ones newer than this SDK
    log.error("Receivify {} {}: {}", e.getStatusCode(), e.getCode(), e.getDetail());
} catch (ReceivifyConnectionException e) { // no HTTP response at all; e.isTimeout()
    ...
}
```

One class per frozen code: `BadRequestException`, `BadTickerException`, `BadDestinationException`,
`AssetUnavailableException`, `AmountTooLowException`, `SuspendedException`,
`LimitExceededException`, `InvalidCodeException`, `NoSetupException`, `NotEnabledException`,
`NoTargetException`, `InvalidTokenException`, `UnauthorizedException`, `BadSignatureException`,
`SignupDisabledException`, `NotFoundException`, `AlreadyExistsException`,
`AlreadyEnabledException`, `NotAllowedException`, `RateLimitedException`,
`InternalServerException`, `UpstreamException`, `RateUnavailableException`. Branch on the type or
on `getCode()`, never on the detail text.

**Retries.** The client retries `429`/`5xx`/connection failures (2 retries by default, honouring
`Retry-After` and `X-RateLimit-Reset`) only when that is safe: `GET`s, and creates that carry an
`Idempotency-Key`. A `POST` without a key is never retried, so it cannot double-charge anyone.
Tune with `.maxRetries(n)`, `.retryBaseDelay(...)` and `.maxRetryDelay(...)` on the builder.

---

## Rotating the webhook secret

```java
WebhookSecretRotated r = client.rotateWebhookSecret();
String newSecret = r.getSecret();        // shown ONCE; store it now
Instant until = r.getPrevExpiresAt();    // the old secret stays valid until here (24h by default)
```

Until `until`, verify with **both** secrets, current first:

```java
List<String> secrets = List.of(newSecret, oldSecret);
Webhooks.parse(rawBody, signature, secrets);
```

`client.getWebhookSecretState().isPrevActive()` tells a running process whether it still needs the
old one.

---

## Money helpers

```java
Money.format("150000000", 8);          // "1.50000000"
Money.parse("0.001", 8);               // "100000"   refuses to round: Money.parse("0.000000001", 8) throws
Money.toBigInteger("115792089237316195423570985008687907853269984665640564039457584007913129639935");
Money.toBigDecimal("25252525", 6);     // 25.252525
Money.fromBigDecimal(new BigDecimal("0.05"), 18);  // "50000000000000000"
Money.add("25000000", "252525");       // "25252525"
Money.compare(received, expected);     // exact, any size
```

Take `decimals` from `invoice.getDecimals()` or `ticker.getDecimals()`. Never hard-code 8.

---

## More

- **Per-call options:** `RequestOptions.builder().idempotencyKey("k").timeout(Duration.ofSeconds(5)).header("X-Trace", id).build()`.
- **Raw JSON:** every model has `raw()` (an unmodifiable `Map`) and `get("field")`, so a field added
  by a newer API version is never lost.
- **Any endpoint:** `client.request("GET", "/api/v1/fees/tier", null, null, null)` returns a
  `ReceivifyResponse<Object>` with the status, headers and decoded JSON tree.
- **Custom HTTP stack / proxy:** `.transport(new JdkHttpTransport(myHttpClient))`, or implement the
  one-method `Transport` interface.
- **Other endpoints:** payment links (`createPaymentLink`, `listPaymentLinks`, ...), recurring
  schedules, API keys, webhook endpoints and the delivery log, settings, summary, payouts and health.

---

## Running the tests

The suite is offline (an injected fake transport, no network) and uses no test framework:

```bash
mvn -q package                       # compiles, runs io.receivify.RunTests, builds the jar
# or without Maven:
javac -d out $(find src -name '*.java') && java -cp out io.receivify.RunTests
```

---

## Example projects

Complete, runnable shops using this SDK (create an invoice, redirect to checkout, verify the signed webhook, mark the order paid), with comments explaining every step:

- **Spring Boot**: [`examples/java-spring-boot`](../examples/java-spring-boot)

All examples: [`examples/`](../examples).

## License

MIT. See [LICENSE](LICENSE). Docs: [receivify.io/docs](https://receivify.io/docs).
