# receivify — official C++ SDK

C++17, header-only, CMake. Invoices, forwarding addresses, and signed webhooks.

```cpp
#include <receivify/receivify.hpp>

auto client = receivify::make_client("https://receivify.io", std::getenv("RECEIVIFY_API_KEY"));

auto forward = client.create_address(receivify::Json::object({
    {"ticker",      receivify::Json("btc")},
    {"destination", receivify::Json("bc1qyourcoldwallet...")},
    {"callback",    receivify::Json("https://example.com/receivify/webhook")},
}));

std::cout << "Send BTC to " << forward.address << '\n';
```

---

## The dependency deviation, stated up front

The shared SDK contract asks every Receivify SDK for **zero runtime dependencies beyond the
standard library**. Node has `fetch` and `node:crypto`; PHP has cURL and `hash_hmac`; Python has
`urllib` and `hmac`. **C++17 has neither an HTTP client nor an HMAC.** There is no honest way to
satisfy that clause in this language.

Rather than quietly ignore it, or ship a hand-rolled TLS stack and a hand-rolled SHA-256 — both of
which would be *worse* than a dependency, because you would then be trusting our crypto instead of
OpenSSL's — this SDK does three things:

1. **Depends on libcurl and OpenSSL.** The de-facto standard pairing, already present in every C++
   shop, packaged everywhere, and audited by people whose full-time job that is.
2. **Isolates both behind interfaces**, each about twenty lines:
   `receivify::HttpTransport` (`include/receivify/http.hpp`) and `receivify::Hmac`
   (`include/receivify/hmac.hpp`).
3. **Keeps the core genuinely dependency-free.** The client, the retry policy, the error mapping,
   pagination, the JSON parser and all the webhook verification logic are C++17 and nothing else.
   `receivify::core` is a CMake target you can use on its own.

Exactly two headers in the whole SDK include a third-party header:

| File | Includes | Provides |
|---|---|---|
| `include/receivify/transport/curl_transport.hpp` | `<curl/curl.h>` | `CurlTransport` |
| `include/receivify/crypto/openssl_hmac.hpp` | `<openssl/*.h>` | `OpenSslHmac` |

Both are compiled only when their CMake option is `ON`. Turn them off and neither dependency's
headers are reachable from anywhere in the SDK:

```bash
cmake -B build -DRECEIVIFY_WITH_CURL=OFF -DRECEIVIFY_WITH_OPENSSL=OFF
```

That configuration builds and its test suite passes; you then supply your own transport and HMAC.
See [Substituting your own transport or crypto](#substituting-your-own-transport-or-crypto).

### And the JSON parser

C++ has no standard JSON either, and vendoring nlohmann/json or RapidJSON would smuggle in a third
dependency through the back door. This SDK ships **a small, strict internal parser**
(`include/receivify/json.hpp`, about 300 lines). Two properties justify it:

- **Numbers are never converted.** A JSON number is stored as the exact lexeme the server sent and
  handed back as a `std::string`. Every mainstream parser instead produces a `double` by default —
  which is the precise mechanism by which a satoshi count above 2^53 or a 256-bit wei value gets
  silently corrupted. Our parser cannot do that, because it never performs the conversion.
  `as_double()` exists, is opt-in, and is documented as being for FX rates only.
- **Depth is bounded.** Parsing is recursive descent, so nesting past `ParseOptions::max_depth`
  (64) throws `JsonError`. It does not overflow the stack.

**Be clear about what it is not.** It is tuned for the well-formed, machine-generated bodies
`/api/v1` emits. It is strict rather than clever, and it deliberately does not:

- validate that input bytes are well-formed UTF-8 (bytes outside escapes pass through untouched);
- accept any JSON5-ism — no comments, trailing commas, single quotes, unquoted keys, `NaN`,
  `Infinity`, leading `+`, leading zeros or hex;
- preserve duplicate keys — the **last** occurrence wins (what every mainstream parser does, but it
  is a choice and here it is);
- offer a fast path — objects are a flat vector and lookup is O(n). API bodies have a handful of
  keys; if you are parsing megabytes, use a real parser.

Escapes, surrogate pairs, lone surrogates, control characters, depth limits and every malformed
form above are covered by 26 tests in `tests/test_json.cpp`.

**If you would rather use your own parser, you can.** `Client::request()` hands back
`Response::raw_body` — the untouched bytes — before anything else looks at them:

```cpp
receivify::RequestSpec spec;
spec.path = "/api/v1/invoices";
const std::string body = client.request(spec).raw_body;
nlohmann::json j = nlohmann::json::parse(body);   // your parser, your rules
```

Bear in mind that most parsers will turn any unquoted number into a `double`. Receivify quotes
every monetary value precisely so that cannot bite you — but check your parser's number policy
before you trust it with money.

---

## Two rules that will bite you if you skip them

### 1. Money is a `std::string`. Always.

Every monetary field on this API is an exact-integer, base-unit **string** — satoshis for BTC, wei
for ETH. They are strings because an IEEE-754 double cannot represent a 256-bit wei value, and it
cannot represent a satoshi count above 2^53 either. This SDK enforces that at the type level:

```cpp
invoice.amount_expected   // std::string, "2100000000000000000000000"
invoice.amount_received   // std::string
invoice.amount_fiat       // std::optional<std::string>, a DECIMAL string like "25.00"
```

`receivify::Json` goes further: **its `double` and `float` constructors are deleted**, so putting a
floating-point amount into a request body is a compile error, not a runtime surprise.

```cpp
Json::object({{"amount_sats", Json("250000")}});   // fine
Json::object({{"amount_sats", Json(250000.0)}});   // does not compile
```

Convert only for display, and only with `receivify::units`, which is pure string arithmetic:

```cpp
using namespace receivify::units;

format_units("123456", 8);          // "0.00123456"
parse_units("0.00123456", 8);       // "123456"
parse_units("0.000000001", 8);      // throws — refuses to round a monetary value
amount_at_least(received, expected) // the underpayment check, done right

compare_amounts("9007199254740993", "9007199254740992");  // 1 — a double says 0
```

`decimals` comes from `GET /api/v1/tickers` (`Ticker::decimals`).

### 2. Verify webhooks over the RAW body, against a LIST of secrets.

The signature is `HMAC-SHA256(secret, "<t>." + rawBody)` over the exact bytes we sent. If you hand
`verify_webhook` a re-serialised body — your framework parsed the JSON and you dumped it back out —
key order, whitespace and number formatting all change, and verification fails for a perfectly
genuine delivery. **Capture the body as raw bytes before anything parses it.**

And pass **both** secrets during a rotation. `POST /api/v1/webhooks/secret/rotate` keeps the
previous secret acceptable until `prev_expires_at` (24h by default), so two secrets are
legitimately valid at once. `verify_webhook` takes a `std::vector<std::string>` — current first,
previous second — and there is no single-string overload to reach for by accident.

---

## Install

### As a subdirectory

```cmake
add_subdirectory(third_party/receivify-cpp)
target_link_libraries(my_app PRIVATE receivify::receivify)
```

### Installed

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
cmake --install build --prefix /usr/local
```

Then `#include <receivify/receivify.hpp>` and link `libcurl` and `libcrypto`.

### Dependencies

| | |
|---|---|
| C++17 | required |
| CMake | ≥ 3.16 |
| libcurl | optional (`RECEIVIFY_WITH_CURL`, default `ON`) |
| OpenSSL `libcrypto` | optional (`RECEIVIFY_WITH_OPENSSL`, default `ON`) |

```bash
apt install libcurl4-openssl-dev libssl-dev      # Debian / Ubuntu
brew install curl openssl@3                      # macOS
vcpkg install curl openssl                       # Windows / anywhere
```

---

## Quickstart

### Create an invoice

```cpp
#include <receivify/receivify.hpp>

auto client = receivify::make_client("https://receivify.io", api_key);

receivify::RequestOptions options;
options.idempotency_key = "order-1001";      // makes the POST retry-safe. Use one.

auto invoice = client.create_invoice(receivify::Json::object({
    {"asset",       receivify::Json("btc")},
    {"amount_sats", receivify::Json("250000")},     // a STRING
    {"reference",   receivify::Json("order-1001")},
    {"metadata",    receivify::Json::object({{"customer", receivify::Json("alice")}})},
}), options);

std::cout << invoice.id << ' ' << invoice.address << ' ' << invoice.checkout_url << '\n';
```

Exactly one amount field is required; the first present one wins, in this order: `amount_fiat`
(+`currency`), `amount`, `amount_sats`, `amount_btc`. Two things return an **existing** invoice
rather than creating one — an `Idempotency-Key` replay, and a `reference` this merchant has already
used — and both still answer 201.

### Create a forwarding address

```cpp
auto forward = client.create_address(receivify::Json::object({
    {"ticker",      receivify::Json("usdc_polygon")},
    {"destination", receivify::Json("0xYourWallet...")},
    {"callback",    receivify::Json("https://example.com/receivify/webhook")},
}));

forward.id;            // canonical — read this one
forward.forward_id;    // deprecated alias, same value, kept for the life of v1
forward.address;       // hand this to payers
forward.min_forward;   // a DECIMAL display string, not base units
```

Creation is deterministic: the same `(ticker, destination, callback)` always returns the same
forward, and repeating the call does not burn a second HD derivation index. Safe to call on every
startup.

### Receive and verify a webhook

```cpp
// `raw_body` must be the EXACT request bytes. In your framework:
//   - Boost.Beast / cpp-httplib : req.body() — already a raw std::string
//   - Crow                       : req.body
//   - Pistache                   : request.body()
// Never re-serialise a parsed body.

receivify::ClientOptions options;
options.webhook_secrets = {current_secret, previous_secret};   // current FIRST
auto client = receivify::make_client(api_url, api_key, options);

void handle_webhook(const std::string& raw_body, const std::string& signature_header) {
  receivify::WebhookEvent event;
  try {
    event = client.parse_webhook(raw_body, signature_header);   // verifies, then parses
  } catch (const receivify::WebhookError& e) {
    // e.reason() is machine-readable: kNoSecrets, kBadHeader, kStaleTimestamp, kNoMatch,
    // kBadJson, kBadEnvelope. Log it and answer 400.
    return respond(400, e.reason_string());
  }

  // Answer 2xx FIRST, then do the work. Receivify retries anything that is not 2xx, and
  // delivery is at-least-once and can arrive out of order.
  respond(200, "ok");

  if (receivify::is_invoice_event(event.event)) {
    const auto data = receivify::InvoiceEventData::from_json(event.data);
    if (event.event == "invoice.paid" &&
        receivify::units::amount_at_least(data.amount_received, data.amount_expected)) {
      fulfil(data.reference.value_or(data.id));
    }
  } else if (receivify::is_forward_event(event.event)) {
    const auto data = receivify::ForwardEventData::from_json(event.data);
    credit(data.forward_id, data.value_forwarded);
  }
  // else: an event name this SDK has never heard of. Ignore it — that is the contract.
}
```

`verify_webhook` is the boolean version if you only want a yes/no; `describe_webhook` returns a
`VerificationResult` with the same answer plus a reason for your logs.

The timestamp tolerance defaults to **300 seconds**:

```cpp
receivify::VerifyOptions v;
v.tolerance_seconds = 600;    // 0 disables the replay window — do not do that in production
v.now_seconds = my_clock();   // inject a clock; 0 means "ask the system"
client.verify_webhook(raw_body, signature_header, v);
```

---

## The envelope

Both families ship the same four keys; only `data` differs, and `event` decides which shape it is.

```cpp
struct WebhookEvent {
  std::string  event;        // the discriminator — switch on this first
  std::int64_t sequence;     // globally monotonic, allocated at enqueue time
  std::string  created_at;   // when the EVENT happened; frozen across retries and replays
  Json         data;
  Json         raw;          // the whole envelope, for fields added after this SDK shipped
};
```

`(event, sequence)` is the de-duplication key — there is no separate event id. Sequence is
**global**, not per-resource: consecutive events for one invoice are not consecutive numbers. Store
the highest sequence you have applied per resource and ignore anything at or below it.

`invoice.*`, `sweep.sent` and `test` carry `InvoiceEventData`. `forward.*` carries
`ForwardEventData`, which is CryptAPI-shaped — `address_in` is the REST resource's `address`,
`address_out` is its `destination`.

`InvoiceEventData` is a narrow projection with **no `asset` and no `chain`**. A multi-coin merchant
must call `get_invoice(id)` to learn which coin an event is about.

---

## Rotating the webhook secret

```cpp
auto rotated = client.rotate_webhook_secret();
if (rotated.secret) {
  store_current(*rotated.secret);              // shown ONCE, never readable again
  // Keep the old one until rotated.prev_expires_at.
} else {
  // Absent means this was an Idempotency-Key replay. rotated_at and prev_expires_at are still
  // accurate; if the secret itself was lost, rotate again.
}

// Later, to find out whether the old secret can be dropped:
if (!client.get_webhook_secret_state().prev_active) {
  drop_previous();
}
```

At most two secrets are ever valid at once. Rotating twice in quick succession sets
`replaced_previous`, and any receiver still on the older secret starts failing immediately.

---

## Lists and auto-pagination

Every list endpoint returns `{ data, has_more, next_cursor }`, modelled once as `Page<T>`.
`has_more` is a fact from the server — never re-derive it from `data.size() == limit`.

```cpp
receivify::InvoiceListParams params;
params.status = "paid";
params.limit = 100;

auto page = client.list_invoices(params);      // one page
page.data; page.has_more; page.next_cursor;
```

`Paginator<T>` follows `next_cursor` for you, fetching lazily:

```cpp
for (const receivify::Invoice& invoice : client.invoices(params)) {
  std::cout << invoice.id << ' ' << invoice.amount_expected << '\n';
  if (done) break;        // breaking out stops the HTTP traffic
}

auto first_fifty = client.addresses().collect(50);
```

A `Paginator` is single-pass; construct a fresh one to iterate again. Two malformed-server guards
throw rather than spin forever: `has_more: true` with no cursor, and a cursor that repeats.

For an endpoint this SDK does not model, name the item type yourself — `RawObject` gives you the
untouched `Json`:

```cpp
for (const auto& link : client.paginate<receivify::RawObject>("/api/v1/payment-links")) {
  std::cout << link.raw["id"].as_string() << '\n';
}
```

---

## Errors

Everything this SDK throws derives from `receivify::ReceivifyError`.

```
ReceivifyError
├── ConfigurationError   the SDK was assembled wrong (no transport, no HMAC, empty URL)
├── TransportError       no HTTP response at all — DNS, TLS, socket, timeout
├── JsonError            the body was not the JSON we expected
├── WebhookError         a delivery failed verification or parsing; carries reason()
└── ApiError             an { error, detail } body with a 4xx/5xx status
    ├── BadRequestError, BadTickerError, BadDestinationError, AssetUnavailableError,
    │   AmountTooLowError, SuspendedError, LimitExceededError, InvalidCodeError,
    │   NoSetupError, NotEnabledError, NoTargetError, InvalidTokenError,
    │   UnauthorizedError, BadSignatureError, SignupDisabledError, NotFoundError,
    │   AlreadyExistsError, AlreadyEnabledError, NotAllowedError, InternalError,
    │   UpstreamError, RateUnavailableError
    └── RateLimitedError  + retry_after(), rate_limit_reset(), rate_limit_limit(),
                            rate_limit_remaining()
```

Catch specifically, or generically. **Order your catch blocks most-derived first.**

```cpp
try {
  client.cancel_invoice(id);
} catch (const receivify::NotAllowedError&) {
  // 409 — already paid or already cancelled
} catch (const receivify::NotFoundError&) {
  // 404
} catch (const receivify::RateLimitedError& e) {
  sleep_for(seconds(e.retry_after().value_or(5)));
} catch (const receivify::ApiError& e) {
  // Everything else, INCLUDING a code this SDK version has never heard of.
  log("receivify", e.code(), e.detail(), e.status(), e.request());
} catch (const receivify::TransportError& e) {
  if (e.timed_out()) { /* ... */ }
}
```

**Branch on `code()`, never on `detail()`** — the wording of `detail` is explicitly not part of the
frozen contract. An unknown future code throws a plain `ApiError` with `code()` intact, so an old
SDK keeps working against a newer deployment rather than crashing.

The API key is never placed in a URL, never logged, and appears in no exception this SDK throws —
`ApiError::request()` is method plus path only.

---

## Idempotency and retries

`Idempotency-Key` is accepted on every mutating call:

```cpp
receivify::RequestOptions options;
options.idempotency_key = "order-1001";
client.create_invoice(params, options);
```

A replay returns the **original** body and the **original** status, plus `Idempotent-Replay: true`
(`Response::idempotent_replay()`). Keys are scoped to `(merchant, endpoint, key)` and retained 24h.

The retry rule is deliberately conservative. A request is retried on 429/5xx or a transport failure
**only when it is safe**:

- a `GET`, or
- a request whose endpoint declares `Idempotency-Key` support **and** for which you supplied one.

A bare `POST` is **never** repeated automatically. That is how you charge someone twice. If you
want your writes to be retried, give them an idempotency key.

Backoff is exponential with jitter and honours `Retry-After` first, then `X-RateLimit-Reset`.

```cpp
receivify::ClientOptions options;
options.max_retries = 2;                                   // default
options.retry_base = std::chrono::milliseconds(500);
options.max_retry_delay = std::chrono::milliseconds(20000);
```

---

## Escape hatches

Anything this SDK does not wrap as a typed method is one call away:

```cpp
receivify::RequestSpec spec;
spec.method = "POST";
spec.path   = "/api/v1/payment-links";
spec.body   = receivify::Json::object({{"title", receivify::Json("Coffee")},
                                       {"amount_btc", receivify::Json("0.0005")}});
spec.declares_idempotency = true;      // only set this where openapi.yaml says so

auto response = client.request(spec);
response.status;      // tells a 201 from a 200 on POST /api/v1/address
response.headers;
response.body;        // parsed Json
response.raw_body;    // untouched bytes, for your own parser
```

`spec.throw_on_error = false` returns 4xx/5xx bodies instead of throwing — needed for
`GET /api/health`, whose 503 body is documented as not conforming to the `{error, detail}` contract.

Every model also carries `raw`, the `Json` it was built from, so a field added after this SDK
shipped needs no SDK upgrade:

```cpp
invoice.raw["some_field_added_next_year"].as_string_or("");
```

---

## Substituting your own transport or crypto

Both seams are one virtual method each.

```cpp
class MyTransport : public receivify::HttpTransport {
 public:
  receivify::HttpResponse send(const receivify::HttpRequest& request) override {
    // Return the response for ANY status, including 4xx/5xx — the SDK maps statuses itself.
    // Throw receivify::TransportError only when there was no response at all.
    // Honour request.timeout.
  }
};

class MyHmac : public receivify::Hmac {
 public:
  receivify::Sha256Digest hmac_sha256(const std::string& key,
                                      const std::string& message) const override { /* ... */ }
  // Optional: override constant_time_equals if your library has a hardened comparison.
  // The default is a portable accumulate-and-OR loop; the bundled OpenSSL provider uses
  // CRYPTO_memcmp.
};

receivify::ClientOptions options;
options.transport = std::make_shared<MyTransport>();
options.hmac      = std::make_shared<MyHmac>();
receivify::Client client("https://receivify.io", api_key, options);   // note: not make_client
```

Include `<receivify/client.hpp>` rather than `<receivify/receivify.hpp>` and neither libcurl's nor
OpenSSL's headers are pulled in at all.

`Client` is immutable after construction and holds no per-request state, so sharing one across
threads is safe provided your transport and HMAC are. The bundled ones are.

---

## Not modelled on purpose

The typed surface covers the money path — invoices, forwarding addresses, tickers, webhook
verification and secret rotation. These endpoints exist in `openapi.yaml` and are deliberately
**not** wrapped as typed methods in v1:

- payment links, recurring schedules, API key management
- webhook endpoint CRUD, the delivery log, replay, the signed test send
- settings, the dashboard summary, payouts, two-factor enrolment
- `GET /api/health`, `POST /api/v1/invoices/bulk`, the `/api/public/**` checkout routes

Every one of them is reachable through `client.request()` and
`client.paginate<RawObject>()`, in one call, with the same auth, retry, error-mapping and
pagination behaviour as a typed method.

The reason is honesty about cost. Each hand-wrapped method in C++ needs a model struct, a
`from_json`, and its own tests — and the list above is dashboard and administrative surface that a
C++ backend (a payment daemon, a POS server, an exchange integration) does not drive. A shallow,
untested wrapper around thirty more endpoints would look more complete and be worth less than a
tested core plus a first-class escape hatch. If a C++ integration turns out to need one of them
routinely, it is a small, additive change to promote it.

Two things are excluded permanently rather than deferred, because `openapi.yaml` says no SDK should
model them: `/api/admin/**` (operator console, shapes may change without notice) and `/api/auth/**`
(browser session bootstrap — an SDK that already holds an API key has no reason to call it).

---

## Tests

Offline by construction. The transport is injected, the clock and the sleeper are injected, and
`ctest` never opens a socket.

```bash
cmake -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build
ctest --test-dir build --output-on-failure
```

121 tests across four binaries:

| | |
|---|---|
| `test_json` | escapes, surrogate pairs, lone surrogates, depth limits, 29 malformed inputs, big-integer precision |
| `test_units` | base-unit conversion up to 2^256−1, refusal to round, comparison beyond every integer type |
| `test_webhooks` | current secret, previous secret, tampering, the replay window, header grammar, RFC 4231 vectors, **golden signatures generated by the reference Node SDK** |
| `test_client` | the exact request built, the list envelope, auto-pagination, error-code mapping, unknown-code fallback, money staying a string, the retry policy |

The webhook logic tests use a deterministic non-crypto stub for `Hmac`, so they run even with
`RECEIVIFY_WITH_OPENSSL=OFF`; the real-crypto tests are gated on the OpenSSL provider being
compiled in and say so in the output when they are skipped.

`tests/second_translation_unit.cpp` includes the whole SDK a second time and links against
`test_client`, so a missing `inline` in a header-only library fails the build here rather than in
your project.

---

## Example projects

Runnable C++ programs are in [`examples/`](examples). For full web-shop examples in other languages (the same create-invoice, checkout, verify-webhook flow), see [`../examples`](../examples).

## License

MIT. See `LICENSE`.
