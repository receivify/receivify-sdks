//! Receivify + axum: a complete, tiny shop.
//!
//!   GET  /                      product page with a "Pay with crypto" form
//!   POST /checkout              creates the order + a Receivify invoice, redirects to checkout
//!   GET  /orders/:id            order status page
//!   POST /webhooks/receivify    signed events from Receivify; the ONLY place an order is marked paid
//!
//! Run: export the variables from .env.example, then `cargo run`.

use std::{
    collections::HashMap,
    env,
    sync::{Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};

use axum::{
    body::Bytes,
    extract::{Form, Path, State},
    http::{HeaderMap, StatusCode},
    response::{Html, IntoResponse, Redirect, Response},
    routing::{get, post},
    Router,
};
use receivify::{Client, CreateInvoice, Error, RequestOptions};
use serde::Deserialize;
use serde_json::json;

/// What we sell. Prices are strings: money never goes through a float.
const PRODUCT: &str = "Receivify T-shirt";
const PRICE: &str = "25.00";
const CURRENCY: &str = "GBP";

/// (key, label, asset, chain). Chain picks the network for tokens that live on several.
const COINS: &[(&str, &str, &str, Option<&str>)] = &[
    ("btc", "Bitcoin", "btc", None),
    ("ltc", "Litecoin", "ltc", None),
    ("eth", "Ethereum", "eth", Some("ethereum")),
    ("usdc-base", "USDC on Base", "usdc", Some("base")),
    ("usdt-tron", "USDT on Tron", "usdt", Some("tron")),
    ("sol", "Solana", "sol", Some("solana")),
];

/// invoice.paid is THE signal: the full amount is confirmed and can't be reversed.
/// invoice.swept follows it (funds forwarded to your wallet), so it also means paid.
fn status_for_event(event: &str) -> Option<&'static str> {
    Some(match event {
        "invoice.confirming" => "confirming", // seen on-chain, not final: don't ship
        "invoice.partial" => "partially_paid",
        "invoice.paid" | "invoice.swept" => "paid",
        "invoice.expired" => "expired",
        "invoice.partial_expired" => "underpaid",
        "invoice.cancelled" => "cancelled",
        "invoice.failed" => "failed",
        _ => return None,
    })
}

/// A deliberately tiny in-memory order store. Use your database in a real shop.
#[derive(Clone)]
struct Order {
    id: String,
    status: &'static str,
    invoice_id: Option<String>,
    /// Highest webhook sequence applied to this order (see the webhook handler).
    last_seq: i64,
}

#[derive(Clone)]
struct AppState {
    receivify: Client,
    orders: Arc<Mutex<HashMap<String, Order>>>,
}

#[tokio::main]
async fn main() {
    let api_key = env::var("RECEIVIFY_API_KEY").unwrap_or_default();
    let secret = env::var("RECEIVIFY_WEBHOOK_SECRET").unwrap_or_default();
    if api_key.is_empty() || secret.is_empty() {
        eprintln!("Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET (see .env.example).");
        std::process::exit(1);
    }
    let api_url = env::var("RECEIVIFY_API_URL").unwrap_or_else(|_| "https://receivify.io".into());

    // One client for the app (cheap to clone, shares one connection pool). The webhook
    // secrets are a LIST: during a rotation, the new secret first and the old one second.
    let receivify = Client::builder(api_url)
        .api_key(api_key)
        .webhook_secrets(secret.split(',').map(str::trim).filter(|s| !s.is_empty()))
        .build()
        .expect("valid Receivify configuration");

    let state = AppState { receivify, orders: Arc::new(Mutex::new(HashMap::new())) };
    let app = Router::new()
        .route("/", get(shop))
        .route("/checkout", post(checkout))
        .route("/orders/:id", get(order_page))
        .route("/webhooks/receivify", post(webhook))
        .with_state(state);

    let port = env::var("PORT").unwrap_or_else(|_| "3000".into());
    // Localhost only by default; set HOST=0.0.0.0 when running in a container.
    let host = env::var("HOST").unwrap_or_else(|_| "127.0.0.1".into());
    let listener = tokio::net::TcpListener::bind(format!("{host}:{port}")).await.unwrap();
    println!("Shop running on http://localhost:{port}");
    axum::serve(listener, app).await.unwrap();
}

async fn shop() -> Html<String> {
    let options: String = COINS.iter().map(|(k, label, _, _)| format!(r#"<option value="{k}">{label}</option>"#)).collect();
    page("Shop", &format!(
        r#"<h1>{PRODUCT}</h1><p class="price">£{PRICE}</p>
        <form method="post" action="/checkout">
          <label>Pay with <select name="coin">{options}</select></label>
          <label><input type="checkbox" name="fee_ontop" value="1"> Customer pays the fee on top</label>
          <button>Pay with crypto</button>
        </form>"#
    ))
}

#[derive(Deserialize)]
struct CheckoutForm {
    coin: Option<String>,
    fee_ontop: Option<String>,
}

async fn checkout(State(state): State<AppState>, Form(form): Form<CheckoutForm>) -> Response {
    let (_, _, asset, chain) = COINS
        .iter()
        .find(|(k, ..)| Some(*k) == form.coin.as_deref())
        .copied()
        .unwrap_or(COINS[0]);

    // Prefixed so it can't collide with another shop on the same Receivify account:
    // `reference` is also a de-duplication key on Receivify's side.
    let nanos = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().subsec_nanos();
    let order_id = format!("rs-{nanos:08x}");
    state.orders.lock().unwrap().insert(
        order_id.clone(),
        Order { id: order_id.clone(), status: "pending", invoice_id: None, last_seq: 0 },
    );

    let mut params = CreateInvoice {
        amount_fiat: Some(PRICE.into()), // converted at today's rate and locked on the invoice
        currency: Some(CURRENCY.into()),
        asset: Some(asset.into()),
        chain: chain.map(Into::into),
        // Comes back on every webhook as data.reference: that's how we find the order.
        reference: Some(order_id.clone()),
        // Some(true): the customer covers the fee. None = your account default.
        add_fee_ontop: form.fee_ontop.map(|_| true),
        ..Default::default()
    };
    if let serde_json::Value::Object(meta) = json!({ "product": PRODUCT, "source": "axum-example" }) {
        params.metadata = Some(meta);
    }

    // The SDK client is blocking (it never touches your async runtime), so run the HTTP
    // call on tokio's blocking pool instead of stalling an async worker thread.
    let client = state.receivify.clone();
    let key = format!("checkout-{order_id}");
    let result = tokio::task::spawn_blocking(move || {
        // Same idempotency key = same invoice: a double submit or a retry can't create two.
        client.create_invoice(&params, Some(&RequestOptions::idempotency_key(key)))
    })
    .await
    .expect("blocking task panicked");

    match result {
        Ok(invoice) => {
            if let Some(o) = state.orders.lock().unwrap().get_mut(&order_id) {
                o.invoice_id = Some(invoice.id.clone());
            }
            // 303: the browser follows with a GET to Receivify's hosted checkout.
            Redirect::to(&invoice.checkout_url).into_response()
        }
        // `raw_code` is stable (amount_too_low, asset_unavailable, ...); `detail` is for humans.
        Err(Error::Api(e)) => (StatusCode::BAD_REQUEST, page("Could not start payment", &format!("<p>{} ({})</p><p><a href=\"/\">Back</a></p>", html_escape(&e.detail), e.raw_code))).into_response(),
        Err(e) => (StatusCode::BAD_GATEWAY, format!("Receivify is unreachable: {e}")).into_response(),
    }
}

/// `Bytes` is the body EXACTLY as sent. The signature is an HMAC over these bytes; extracting
/// `Json<T>` and re-serialising would change them and fail verification.
async fn webhook(State(state): State<AppState>, headers: HeaderMap, body: Bytes) -> StatusCode {
    let signature = headers.get("x-receivify-signature").and_then(|v| v.to_str().ok()).unwrap_or("");

    // Constant-time HMAC check + 5-minute replay window + JSON parse.
    let event = match state.receivify.parse_webhook(&body, signature) {
        Ok(event) => event,
        Err(err) => {
            eprintln!("Rejected webhook: {err}");
            return StatusCode::BAD_REQUEST;
        }
    };

    if event.event == "test" {
        println!("Test webhook received and verified.");
        return StatusCode::OK;
    }
    // Ignore anything that isn't about an invoice, including event names added later.
    let Some(next) = status_for_event(&event.event) else { return StatusCode::OK };
    let Ok(data) = event.invoice_data() else { return StatusCode::OK };
    let Some(reference) = data.reference.as_deref() else { return StatusCode::OK };

    let mut orders = state.orders.lock().unwrap();
    // Unknown (made elsewhere), a duplicate, or an older event arriving late: do nothing.
    // Delivery is at-least-once and may be out of order; `sequence` only increases.
    let Some(order) = orders.get_mut(reference) else { return StatusCode::OK };
    if event.sequence <= order.last_seq {
        return StatusCode::OK;
    }
    order.last_seq = event.sequence;
    if order.status != "paid" {
        // paid is final for fulfilment
        println!("Order {}: {} -> {} ({})", order.id, order.status, next, event.event);
        order.status = next;
        order.invoice_id = Some(data.id.clone());
        // Fulfil here when next == "paid" (email, download, shipping); keep it fast.
    }
    StatusCode::OK // 2xx fast; anything else is retried with backoff
}

async fn order_page(State(state): State<AppState>, Path(id): Path<String>) -> Response {
    let Some(order) = state.orders.lock().unwrap().get(&id).cloned() else {
        return (StatusCode::NOT_FOUND, "No such order").into_response();
    };
    // Display only. Landing on this URL proves nothing; the webhook is the source of truth.
    let refresh = if order.status == "paid" { "" } else { r#"<meta http-equiv="refresh" content="5"><p class="muted">Refreshes every 5 seconds.</p>"# };
    page(&format!("Order {}", order.id), &format!(
        r#"<h1>Order {}</h1><p>{PRODUCT}: £{PRICE}</p><p class="status {}">{}</p>{refresh}<p><a href="/">Back to the shop</a></p>"#,
        html_escape(&order.id), order.status, order.status
    ))
    .into_response()
}

fn html_escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

fn page(title: &str, body: &str) -> Html<String> {
    Html(format!(
        r#"<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>{}</title>
<style>body{{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}}.price{{font-size:2rem;font-weight:700}}
label{{display:block;margin:1rem 0}}button{{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}}
.status{{font-weight:700}}.paid{{color:#4d7c0f}}.muted{{color:#71717a;font-size:.9rem}}</style></head><body>{body}</body></html>"#,
        html_escape(title)
    ))
}
