# receivify: the official Rust SDK

Accept crypto payments from Rust. Create invoices, send customers to a hosted checkout, and
receive signed webhooks when they pay.

- Website: <https://receivify.io>
- API docs: <https://receivify.io/docs>
- Crate docs: <https://docs.rs/receivify>

Blocking API, Rust 1.70+, no async runtime required. Every example below is copy-paste ready.

---

## 1. Install

```sh
# straight from GitHub (works today):
cargo add receivify --git https://github.com/receivify/receivify-sdks
# from crates.io, once published:
cargo add receivify
```

Or in `Cargo.toml`:

```toml
[dependencies]
receivify = "1"

# Straight from GitHub instead:
# receivify = { git = "https://github.com/receivify/receivify-sdks" }
# Or from a local checkout:
# receivify = { path = "../receivify-sdks/rust" }
```

## 2. Configure

Copy [`.env.example`](.env.example) to `.env` and fill in your values from the
[Receivify dashboard](https://receivify.io):

```sh
RECEIVIFY_URL=https://receivify.io
RECEIVIFY_API_KEY=rcv_live_...
RECEIVIFY_WEBHOOK_SECRET=whsec_...
```

The SDK reads nothing by itself: load the file however you normally do (your process manager,
`export`, or a crate such as `dotenvy`) and pass the values to the builder. Keep the API key on
your server. Never put it in a browser, an app, a URL, or a log line.

## 3. Take a payment

```rust
use receivify::{Client, CreateInvoice, RequestOptions};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let client = Client::builder(std::env::var("RECEIVIFY_URL")?)
        .api_key(std::env::var("RECEIVIFY_API_KEY")?)
        .webhook_secrets(vec![std::env::var("RECEIVIFY_WEBHOOK_SECRET")?])
        .build()?;

    let order_id = "order-1001";
    let invoice = client.create_invoice(
        &CreateInvoice {
            amount_fiat: Some("25.00".into()),
            currency: Some("USD".into()),
            asset: Some("btc".into()),
            reference: Some(order_id.into()),
            ..Default::default()
        },
        // If this call times out and you retry it, you still get ONE invoice.
        Some(&RequestOptions::idempotency_key(order_id)),
    )?;

    // Send the customer here to pay.
    println!("{}", invoice.checkout_url);
    Ok(())
}
```

That is the whole integration on the paying side: create the invoice, redirect to
`invoice.checkout_url`. You find out the customer paid through a webhook (next step).

Other ways to set the amount (use exactly one):

```rust
// 0.001 BTC, in coin units
let btc = CreateInvoice { asset: Some("btc".into()), amount: Some("0.001".into()), ..Default::default() };
// 25 USDC on Base
let usdc = CreateInvoice { asset: Some("usdc".into()), chain: Some("base".into()), amount: Some("25".into()), ..Default::default() };
// 0.05 ETH, in base units (wei)
let eth = CreateInvoice { asset: Some("eth".into()), amount_sats: Some("50000000000000000".into()), ..Default::default() };
```

`reference` also de-duplicates: creating another invoice with the same reference and the same
terms returns the existing invoice. Make references unique per order.

## 4. Get told when they pay (webhooks)

Receivify POSTs a signed JSON event to your webhook URL. Verify the signature against the
**raw body bytes** (not re-serialized JSON) and you know it is genuine.

With [axum](https://docs.rs/axum) (take the body as `Bytes`):

```rust
use axum::{body::Bytes, extract::State, http::{HeaderMap, StatusCode}, routing::post, Router};
use receivify::Client;

async fn receivify_webhook(State(client): State<Client>, headers: HeaderMap, body: Bytes) -> StatusCode {
    let signature = headers
        .get("x-receivify-signature")
        .and_then(|v| v.to_str().ok())
        .unwrap_or("");

    let event = match client.parse_webhook(&body, signature) {
        Ok(event) => event,
        Err(e) => {
            eprintln!("rejected webhook: {}", e.reason()); // bad_header, no_match, stale_timestamp...
            return StatusCode::BAD_REQUEST;
        }
    };

    // Deliveries can repeat. (event, sequence) identifies one; skip ones you already handled.
    match event.event.as_str() {
        "invoice.paid" => {
            if let Ok(data) = event.invoice_data() {
                println!("order {:?} paid {} base units", data.reference, data.amount_received);
                // mark the order paid in your database here
            }
        }
        _ => {} // ignore events you don't use; new ones may be added
    }
    StatusCode::OK // any 2xx = delivered; anything else is retried
}

// let app = Router::new().route("/hooks/receivify", post(receivify_webhook)).with_state(client);
```

Not using a client object? The same check is a free function:

```rust
use receivify::webhooks::{self, VerifyOptions};

let genuine: bool = webhooks::verify(raw_body, signature, &[current], VerifyOptions::default());
let event = webhooks::parse(raw_body, signature, &[current], VerifyOptions::default());
```

Timestamps older than 5 minutes are rejected (replay protection). Change it with
`VerifyOptions::default().tolerance_seconds(600)`.

A runnable, offline version is in [`examples/verify_webhook.rs`](examples/verify_webhook.rs):
`cargo run --example verify_webhook`.

---

## Two rules

**Money is a `String`. Never turn it into `f64`.** Every amount (`amount_expected`,
`amount_received`, ...) is an exact whole number of the coin's smallest unit: satoshis for BTC,
wei for ETH. A float cannot hold those exactly. Use the helpers:

```rust
use receivify::money::{compare_amounts, format_units, parse_units};

assert_eq!(format_units("123456", 8).unwrap(), "0.00123456"); // for display
assert_eq!(parse_units("0.001", 8).unwrap(), "100000");        // refuses to round
let paid_in_full = compare_amounts("100000", "100000").unwrap().is_ge();
```

`invoice.decimals` gives the decimal places and `invoice.amount_decimal` is a ready-made display
string. `amount_fiat` is a decimal string such as `"25.00"`, for display only.

**Verify webhooks against the raw body.** See step 4.

---

## Everything else

### Look up, list, cancel invoices

```rust
use receivify::ListParams;

let invoice = client.get_invoice(id)?;                       // current state
let page = client.list_invoices(&ListParams::new().limit(50).status("paid"))?;
println!("{} invoices, more: {}", page.data.len(), page.has_more);

// Every invoice, page by page, automatically:
for invoice in client.invoices(ListParams::new().status("paid")) {
    let invoice = invoice?;
    println!("{} {}", invoice.id, invoice.amount_received);
}

client.cancel_invoice(id)?;  // only pending + unpaid; a second cancel is an error
client.bulk_cancel_invoices(&["id-1", "id-2"])?;
```

### Let the customer pay the fee (`add_fee_ontop`)

```rust
use receivify::CreateInvoice;

let invoice = client.create_invoice(&CreateInvoice {
    asset: Some("usdc".into()),
    chain: Some("base".into()),
    amount: Some("25".into()),
    add_fee_ontop: Some(true), // you receive the full 25 USDC; the payer covers the fee
    ..Default::default()
}, None)?;

assert!(invoice.add_fee_ontop);
println!("you receive {:?}", invoice.amount_net_requested); // Some("25000000")
println!("payer sends {}", invoice.amount_expected);        // a bit more
println!("fee added {:?}", invoice.fee_ontop);
```

| `add_fee_ontop` | meaning                                                  |
| --------------- | -------------------------------------------------------- |
| `None`          | not sent: use your account default (dashboard setting)   |
| `Some(false)`   | fee comes out of the amount, whatever the default is     |
| `Some(true)`    | fee is added on top, whatever the default is             |

### Forwarding addresses

A permanent deposit address that forwards everything it receives to your wallet.

```rust
use receivify::{AddressListParams, CreateAddress, ForwardStatus, UpdateAddress};

let fwd = client.create_address(&CreateAddress {
    ticker: "btc".into(),                         // see client.list_tickers()
    destination: "bc1q...your-wallet".into(),
    callback: Some("https://shop.example/hooks/receivify".into()),
    ..Default::default()
}, None)?;
println!("pay to {} (id {})", fwd.address, fwd.id);

let detail = client.get_address(&fwd.id)?;        // includes recent payments
for f in client.addresses(AddressListParams::new().ticker("btc")) { let _ = f?; }
client.update_address(&fwd.id, &UpdateAddress { status: Some(ForwardStatus::Paused), ..Default::default() })?;
client.disable_address(&fwd.id)?;
```

Calling `create_address` again with the same ticker, destination and callback returns the same
address.

### Supported coins

```rust
for t in client.list_tickers()?.data {
    println!("{} ({}), {} decimals", t.ticker, t.name, t.decimals);
}
```

### Errors

```rust
use receivify::{Error, ErrorCode};

match client.get_invoice(id) {
    Ok(invoice) => println!("{}", invoice.status),
    Err(Error::Api(e)) => match e.code {
        ErrorCode::NotFound => println!("no such invoice"),
        ErrorCode::Unauthorized => println!("check RECEIVIFY_API_KEY"),
        ErrorCode::RateLimited => println!("retry in {:?}s", e.retry_after),
        _ => println!("{} {}: {}", e.status, e.raw_code, e.detail),
    },
    Err(Error::Transport(e)) if e.is_timeout() => println!("timed out"),
    Err(e) => return Err(e.into()),
}
```

Branch on `e.code`, never on the `detail` text. A code this SDK version doesn't know yet arrives
as `ErrorCode::Unknown` with the original string in `e.raw_code`; it never fails to parse.
Shortcuts: `err.code()` and `err.status()` work on any `Error`.

### Retries and timeouts

- 30 second timeout per request (`.timeout(...)` on the builder, or per call with
  `RequestOptions::default().with_timeout(...)`).
- GET requests, and calls that carry an `Idempotency-Key`, are retried up to 2 times on 429 and
  5xx (honouring `Retry-After` / `X-RateLimit-Reset`). Change with `.max_retries(n)`.
- A POST without an `Idempotency-Key` is sent exactly once, so a retry can never charge twice.

### Rotating the webhook secret

```rust
use receivify::RequestOptions;

let rotated = client.rotate_webhook_secret(Some(&RequestOptions::idempotency_key("rotate-2026-10")))?;
let new_secret = rotated.secret.expect("shown once: store it now");
// For the next 24h (until rotated.prev_expires_at), accept both:
//   .webhook_secrets(vec![new_secret, old_secret])
// Then drop the old one. client.get_webhook_secret_state()?.prev_active tells you when.
```

### Endpoints without a typed method

```rust
use receivify::Method;
let keys: serde_json::Value = client.request(Method::Get, "/api/v1/keys", None, None)?;
let raw = client.request_raw(Method::Get, "/api/v1/settings", None, None)?; // status + headers too
```

### Async code (tokio)

The client is blocking. From async code, run calls on the blocking pool:

```rust
let c = client.clone(); // cheap: an Arc inside
let invoice = tokio::task::spawn_blocking(move || c.get_invoice(&id)).await??;
```

Webhook verification does no I/O, so call `parse_webhook` directly in an async handler.

### Your own HTTP client

Turn off the bundled transport and implement one method:

```toml
receivify = { version = "1", default-features = false }
```

```rust
use receivify::{Client, HttpRequest, HttpResponse, Transport, TransportError};

struct ReqwestTransport(reqwest::blocking::Client);

impl Transport for ReqwestTransport {
    fn send(&self, req: &HttpRequest) -> Result<HttpResponse, TransportError> {
        let method = reqwest::Method::from_bytes(req.method.as_str().as_bytes()).unwrap();
        let mut rb = self.0.request(method, &req.url).timeout(req.timeout);
        for (k, v) in &req.headers {
            rb = rb.header(k, v);
        }
        if let Some(body) = &req.body {
            rb = rb.body(body.clone());
        }
        let res = rb.send().map_err(|e| {
            if e.is_timeout() { TransportError::timeout(e.to_string()) } else { TransportError::new(e.to_string()) }
        })?;
        let status = res.status().as_u16();
        let headers = res.headers().iter()
            .map(|(k, v)| (k.as_str().to_string(), v.to_str().unwrap_or("").to_string()))
            .collect();
        let body = res.bytes().map_err(|e| TransportError::new(e.to_string()))?.to_vec();
        Ok(HttpResponse { status, headers, body }) // return 4xx/5xx as Ok too
    }
}

// let client = Client::builder(url).api_key(key)
//     .transport(ReqwestTransport(reqwest::blocking::Client::builder().redirect(reqwest::redirect::Policy::none()).build()?))
//     .build()?;
```

A closure `Fn(&HttpRequest) -> Result<HttpResponse, TransportError>` is also a `Transport`, which
makes faking the network in your own tests a one-liner.

### Builder options

| method                         | default                        |
| ------------------------------ | ------------------------------ |
| `api_key(key)`                 | none (public routes only)      |
| `webhook_secrets(vec![..])`    | none; current first, previous second |
| `timeout(Duration)`            | 30s                            |
| `max_retries(n)`               | 2                              |
| `retry_base_delay(Duration)`   | 500ms, doubling                |
| `user_agent_suffix("my-app/1")`| `receivify-rust/<version>` is always sent |
| `transport(t)`                 | bundled `ureq` transport       |

`format!("{client:?}")` prints `api_key: Some("[redacted]")`; the key is never logged or put in a
URL.

---

## Dependencies

Rust's standard library has no HTTP, JSON or HMAC, so the crate uses a small set of widely used
crates:

| crate        | why                                                                                           |
| ------------ | --------------------------------------------------------------------------------------------- |
| `serde`      | derive (de)serialization for the models                                                       |
| `serde_json` | JSON, with `arbitrary_precision` so a number is kept as its exact text and never becomes `f64` |
| `hmac`, `sha2` | HMAC-SHA256 webhook signatures, with a constant-time comparison                             |
| `ureq`       | blocking HTTPS client (rustls). Optional: the default `ureq` feature; turn it off to bring your own |

No async runtime, no OpenSSL. Note that `arbitrary_precision` is a cargo feature, so it applies
to every `serde_json` user in your build: `serde_json::Value` numbers then keep their exact
text too.

## Minimum Rust version

Rust 1.70, checked by building and running the test suite with 1.70.0. The newest patch releases
of a few dependencies need a newer compiler; on 1.70, pin them with `cargo update --precise`
(the suite was verified with `serde 1.0.219`, `serde_json 1.0.140` and `ureq 2.11.0`). Cargo 1.84
and later does this for you with `CARGO_RESOLVER_INCOMPATIBLE_RUST_VERSIONS=fallback`.

## Running the tests

```sh
cargo test                          # offline: a fake transport, no network
cargo test --no-default-features    # same, without the bundled HTTP client
cargo clippy --all-targets -- -D warnings
cargo fmt --check
```

The suite checks the exact requests built, money staying an exact string, auto-pagination
(including refusing to loop on a repeated cursor), every error code, the retry policy, and
webhook signatures against vectors produced by the Node SDK.

## License

MIT. See [LICENSE](LICENSE).
