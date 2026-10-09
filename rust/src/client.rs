//! The API client.

use std::fmt;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde::de::DeserializeOwned;
use serde::Serialize;
use serde_json::Value;

use crate::error::{ApiError, Error, Result};
use crate::models::{
    AddressListParams, BulkResult, CreateAddress, CreateInvoice, Forward, ForwardDetail, Invoice,
    ListParams, Page, Ticker, UpdateAddress, WebhookSecretRotated, WebhookSecretState,
};
use crate::pagination::Paginator;
use crate::transport::{find_header, HttpRequest, HttpResponse, Method, Transport};
use crate::webhooks::{self, VerifyOptions, WebhookError, WebhookEvent};

/// The SDK version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");

/// The default `User-Agent`: `receivify-rust/<version>`.
pub const USER_AGENT: &str = concat!("receivify-rust/", env!("CARGO_PKG_VERSION"));

/// Default per-request timeout.
pub const DEFAULT_TIMEOUT: Duration = Duration::from_secs(30);

/// Default number of retries for retry-safe requests.
pub const DEFAULT_MAX_RETRIES: u32 = 2;

const DEFAULT_RETRY_BASE: Duration = Duration::from_millis(500);
const MAX_RETRY_DELAY: Duration = Duration::from_secs(20);

type Sleeper = Arc<dyn Fn(Duration) + Send + Sync>;

/// Per-call options.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct RequestOptions {
    /// Sent as `Idempotency-Key`. A replay returns the original response instead of acting twice,
    /// and makes the call safe for the SDK to retry.
    pub idempotency_key: Option<String>,
    /// Overrides the client's timeout for this call.
    pub timeout: Option<Duration>,
    /// Extra request headers.
    pub headers: Vec<(String, String)>,
}

impl RequestOptions {
    /// Options carrying only an `Idempotency-Key`.
    pub fn idempotency_key(key: impl Into<String>) -> Self {
        RequestOptions {
            idempotency_key: Some(key.into()),
            ..Default::default()
        }
    }

    /// Set the per-call timeout.
    pub fn with_timeout(mut self, timeout: Duration) -> Self {
        self.timeout = Some(timeout);
        self
    }

    /// Add a request header.
    pub fn with_header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.headers.push((name.into(), value.into()));
        self
    }
}

/// A successful response from [`Client::request_raw`], with status and headers.
#[derive(Debug, Clone)]
pub struct RawResponse {
    /// HTTP status (2xx).
    pub status: u16,
    /// Response headers, names lower-cased.
    pub headers: Vec<(String, String)>,
    /// The raw body.
    pub body: Vec<u8>,
}

impl RawResponse {
    /// A header value, looked up case-insensitively.
    pub fn header(&self, name: &str) -> Option<&str> {
        find_header(&self.headers, name)
    }

    /// True when the response was replayed from a stored `Idempotency-Key` record.
    pub fn is_idempotent_replay(&self) -> bool {
        self.header("idempotent-replay") == Some("true")
    }

    /// Decode the body. An empty body decodes as JSON `null`.
    pub fn json<T: DeserializeOwned>(&self) -> Result<T> {
        decode_body(&self.body)
    }
}

/// Builds a [`Client`]. Start with [`Client::builder`].
pub struct ClientBuilder {
    base_url: String,
    api_key: Option<String>,
    webhook_secrets: Vec<String>,
    timeout: Duration,
    max_retries: u32,
    retry_base: Duration,
    user_agent_suffix: Option<String>,
    transport: Option<Arc<dyn Transport>>,
    sleeper: Option<Sleeper>,
}

impl fmt::Debug for ClientBuilder {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("ClientBuilder")
            .field("base_url", &self.base_url)
            .field("api_key", &self.api_key.as_ref().map(|_| "[redacted]"))
            .field("webhook_secrets", &self.webhook_secrets.len())
            .field("timeout", &self.timeout)
            .field("max_retries", &self.max_retries)
            .finish_non_exhaustive()
    }
}

impl ClientBuilder {
    /// The merchant API key (`rcv_live_...`). Sent as a bearer header, never in a URL, never logged.
    /// Optional if you only call public routes (`list_tickers`).
    pub fn api_key(mut self, key: impl Into<String>) -> Self {
        let key = key.into();
        self.api_key = if key.trim().is_empty() {
            None
        } else {
            Some(key.trim().to_string())
        };
        self
    }

    /// Webhook secrets for [`Client::verify_webhook`] / [`Client::parse_webhook`]: current first,
    /// previous second during a rotation. Empty entries are dropped.
    pub fn webhook_secrets<I, S>(mut self, secrets: I) -> Self
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        self.webhook_secrets = secrets
            .into_iter()
            .map(Into::into)
            .filter(|s: &String| !s.is_empty())
            .collect();
        self
    }

    /// Append one webhook secret.
    pub fn webhook_secret(mut self, secret: impl Into<String>) -> Self {
        let s = secret.into();
        if !s.is_empty() {
            self.webhook_secrets.push(s);
        }
        self
    }

    /// Per-request timeout. Default 30s.
    pub fn timeout(mut self, timeout: Duration) -> Self {
        self.timeout = timeout;
        self
    }

    /// Retries for retry-safe requests (GET, or a call with an `Idempotency-Key`). Default 2.
    /// `0` disables retries.
    pub fn max_retries(mut self, retries: u32) -> Self {
        self.max_retries = retries;
        self
    }

    /// First backoff step; doubles each attempt. Default 500ms.
    pub fn retry_base_delay(mut self, delay: Duration) -> Self {
        self.retry_base = delay;
        self
    }

    /// Appended to the `User-Agent` (`receivify-rust/<version> <suffix>`), never replacing it.
    pub fn user_agent_suffix(mut self, suffix: impl Into<String>) -> Self {
        let s = suffix.into();
        self.user_agent_suffix = if s.trim().is_empty() { None } else { Some(s) };
        self
    }

    /// Use your own [`Transport`] (another HTTP client, a proxy, a test fake).
    pub fn transport(mut self, transport: impl Transport + 'static) -> Self {
        self.transport = Some(Arc::new(transport));
        self
    }

    /// Use a shared [`Transport`].
    pub fn transport_arc(mut self, transport: Arc<dyn Transport>) -> Self {
        self.transport = Some(transport);
        self
    }

    /// Replace the function used to wait between retries (tests, or an instrumented sleep).
    pub fn sleeper(mut self, sleeper: impl Fn(Duration) + Send + Sync + 'static) -> Self {
        self.sleeper = Some(Arc::new(sleeper));
        self
    }

    /// Build the client.
    pub fn build(self) -> Result<Client> {
        let base_url = self.base_url.trim().trim_end_matches('/').to_string();
        if base_url.is_empty() {
            return Err(Error::Config(
                "base URL is required, e.g. \"https://receivify.io\"".into(),
            ));
        }
        if !(base_url.starts_with("https://") || base_url.starts_with("http://")) {
            return Err(Error::Config(format!(
                "base URL must start with https:// or http://, got {base_url:?}"
            )));
        }
        if let Some(key) = &self.api_key {
            if key.chars().any(|c| c.is_control() || c.is_whitespace()) {
                return Err(Error::Config(
                    "API key contains whitespace or control characters".into(),
                ));
            }
        }
        let transport = match self.transport {
            Some(t) => t,
            None => default_transport()?,
        };
        let user_agent = match self.user_agent_suffix {
            Some(s) => format!("{USER_AGENT} {}", s.trim()),
            None => USER_AGENT.to_string(),
        };
        Ok(Client {
            inner: Arc::new(Inner {
                base_url,
                api_key: self.api_key,
                webhook_secrets: self.webhook_secrets,
                timeout: self.timeout,
                max_retries: self.max_retries,
                retry_base: self.retry_base,
                user_agent,
                transport,
                sleeper: self.sleeper.unwrap_or_else(|| Arc::new(std::thread::sleep)),
            }),
        })
    }
}

#[cfg(feature = "ureq")]
fn default_transport() -> Result<Arc<dyn Transport>> {
    Ok(Arc::new(crate::transport::UreqTransport::new()))
}

#[cfg(not(feature = "ureq"))]
fn default_transport() -> Result<Arc<dyn Transport>> {
    Err(Error::Config(
        "no transport: enable the `ureq` feature or call ClientBuilder::transport(...)".into(),
    ))
}

struct Inner {
    base_url: String,
    api_key: Option<String>,
    webhook_secrets: Vec<String>,
    timeout: Duration,
    max_retries: u32,
    retry_base: Duration,
    user_agent: String,
    transport: Arc<dyn Transport>,
    sleeper: Sleeper,
}

/// The Receivify API client. Cheap to clone (an `Arc` inside) and safe to share across threads.
///
/// ```no_run
/// # fn main() -> Result<(), Box<dyn std::error::Error>> {
/// use receivify::{Client, CreateInvoice, RequestOptions};
///
/// let client = Client::builder("https://receivify.io")
///     .api_key(std::env::var("RECEIVIFY_API_KEY")?)
///     .build()?;
///
/// let invoice = client.create_invoice(
///     &CreateInvoice {
///         amount_fiat: Some("25.00".into()),
///         currency: Some("USD".into()),
///         asset: Some("btc".into()),
///         reference: Some("order-1".into()),
///         ..Default::default()
///     },
///     Some(&RequestOptions::idempotency_key("order-1")),
/// )?;
/// println!("redirect to {}", invoice.checkout_url);
/// # Ok(()) }
/// ```
#[derive(Clone)]
pub struct Client {
    inner: Arc<Inner>,
}

impl fmt::Debug for Client {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Client")
            .field("base_url", &self.inner.base_url)
            .field(
                "api_key",
                &self.inner.api_key.as_ref().map(|_| "[redacted]"),
            )
            .field("webhook_secrets", &self.inner.webhook_secrets.len())
            .field("timeout", &self.inner.timeout)
            .field("max_retries", &self.inner.max_retries)
            .field("user_agent", &self.inner.user_agent)
            .finish()
    }
}

/// Internal per-call settings.
struct Call<'a> {
    method: Method,
    path: String,
    query: Vec<(&'static str, String)>,
    body: Option<Vec<u8>>,
    auth: bool,
    /// Does this endpoint DECLARE `Idempotency-Key` support?
    idempotent: bool,
    opts: Option<&'a RequestOptions>,
}

impl<'a> Call<'a> {
    fn new(method: Method, path: impl Into<String>) -> Self {
        Call {
            method,
            path: path.into(),
            query: Vec::new(),
            body: None,
            auth: true,
            idempotent: false,
            opts: None,
        }
    }
    fn json(mut self, body: &impl Serialize) -> Result<Self> {
        self.body = Some(serde_json::to_vec(body)?);
        Ok(self)
    }
    fn opts(mut self, opts: Option<&'a RequestOptions>) -> Self {
        self.opts = opts;
        self
    }
    fn idempotent(mut self) -> Self {
        self.idempotent = true;
        self
    }
    fn public(mut self) -> Self {
        self.auth = false;
        self
    }
    fn query(mut self, key: &'static str, value: Option<impl ToString>) -> Self {
        if let Some(v) = value {
            self.query.push((key, v.to_string()));
        }
        self
    }
}

impl Client {
    /// Start building a client for a deployment, e.g. `https://receivify.io`.
    pub fn builder(base_url: impl Into<String>) -> ClientBuilder {
        ClientBuilder {
            base_url: base_url.into(),
            api_key: None,
            webhook_secrets: Vec::new(),
            timeout: DEFAULT_TIMEOUT,
            max_retries: DEFAULT_MAX_RETRIES,
            retry_base: DEFAULT_RETRY_BASE,
            user_agent_suffix: None,
            transport: None,
            sleeper: None,
        }
    }

    /// The normalised base URL.
    pub fn base_url(&self) -> &str {
        &self.inner.base_url
    }

    /// The `User-Agent` this client sends.
    pub fn user_agent(&self) -> &str {
        &self.inner.user_agent
    }

    // ------------------------------------------------------------------ invoices

    /// `POST /api/v1/invoices`. Always 201, including on an `Idempotency-Key` replay and when a
    /// `reference` matches an existing invoice with the same terms (which is returned instead).
    pub fn create_invoice(
        &self,
        params: &CreateInvoice,
        opts: Option<&RequestOptions>,
    ) -> Result<Invoice> {
        self.call(
            Call::new(Method::Post, "/api/v1/invoices")
                .json(params)?
                .idempotent()
                .opts(opts),
        )
    }

    /// `GET /api/v1/invoices/{id}`: the authoritative current state.
    pub fn get_invoice(&self, id: &str) -> Result<Invoice> {
        self.call(Call::new(
            Method::Get,
            format!("/api/v1/invoices/{}", enc_id(id)?),
        ))
    }

    /// `GET /api/v1/invoices`: one page.
    pub fn list_invoices(&self, params: &ListParams) -> Result<Page<Invoice>> {
        self.call(
            Call::new(Method::Get, "/api/v1/invoices")
                .query("limit", params.limit)
                .query("cursor", params.cursor.as_deref())
                .query("status", params.status.as_deref()),
        )
    }

    /// Auto-paginating [`list_invoices`](Self::list_invoices): an iterator over every invoice.
    ///
    /// ```no_run
    /// # fn run(client: &receivify::Client) -> receivify::Result<()> {
    /// for invoice in client.invoices(receivify::ListParams::new().status("paid")) {
    ///     let invoice = invoice?;
    ///     println!("{} {}", invoice.id, invoice.amount_received);
    /// }
    /// # Ok(()) }
    /// ```
    pub fn invoices(&self, params: ListParams) -> Paginator<'_, Invoice> {
        let start = params.cursor.clone();
        Paginator::new(start, move |cursor| {
            let mut p = params.clone();
            p.cursor = cursor.map(str::to_string);
            self.list_invoices(&p)
        })
    }

    /// `POST /api/v1/invoices/{id}/cancel`. Only a pending, unpaid invoice can be cancelled.
    /// Not idempotent: a second call is `409 not_allowed`.
    pub fn cancel_invoice(&self, id: &str) -> Result<Invoice> {
        self.call(Call::new(
            Method::Post,
            format!("/api/v1/invoices/{}/cancel", enc_id(id)?),
        ))
    }

    /// `POST /api/v1/invoices/bulk` with `action: "cancel"` (max 500 ids). Naturally idempotent:
    /// only pending, unpaid invoices are cancelled. Compare `requested` with `cancelled`.
    pub fn bulk_cancel_invoices<S: AsRef<str>>(&self, ids: &[S]) -> Result<BulkResult> {
        let ids: Vec<&str> = ids.iter().map(|s| s.as_ref()).collect();
        if ids.is_empty() {
            return Err(Error::InvalidArgument("at least one id is required".into()));
        }
        self.call(
            Call::new(Method::Post, "/api/v1/invoices/bulk")
                .json(&serde_json::json!({ "ids": ids, "action": "cancel" }))?,
        )
    }

    // ------------------------------------------------------------------ forwarding addresses

    /// `POST /api/v1/address`: create, or return, a forwarding address. Deterministic: the same
    /// `(ticker, destination, callback)` returns the existing forward (HTTP 200 instead of 201;
    /// use [`request_raw`](Self::request_raw) if you need to tell them apart).
    pub fn create_address(
        &self,
        params: &CreateAddress,
        opts: Option<&RequestOptions>,
    ) -> Result<Forward> {
        self.call(
            Call::new(Method::Post, "/api/v1/address")
                .json(params)?
                .idempotent()
                .opts(opts),
        )
    }

    /// `GET /api/v1/address/{id}`: the forward plus its capped payment and callback logs.
    pub fn get_address(&self, id: &str) -> Result<ForwardDetail> {
        self.call(Call::new(
            Method::Get,
            format!("/api/v1/address/{}", enc_id(id)?),
        ))
    }

    /// `GET /api/v1/address`: one page.
    pub fn list_addresses(&self, params: &AddressListParams) -> Result<Page<Forward>> {
        self.call(
            Call::new(Method::Get, "/api/v1/address")
                .query("limit", params.limit)
                .query("cursor", params.cursor.as_deref())
                .query("status", params.status.as_deref())
                .query("ticker", params.ticker.as_deref()),
        )
    }

    /// Auto-paginating [`list_addresses`](Self::list_addresses).
    pub fn addresses(&self, params: AddressListParams) -> Paginator<'_, Forward> {
        let start = params.cursor.clone();
        Paginator::new(start, move |cursor| {
            let mut p = params.clone();
            p.cursor = cursor.map(str::to_string);
            self.list_addresses(&p)
        })
    }

    /// `PATCH /api/v1/address/{id}`: pause/resume or change `callback_url`.
    pub fn update_address(&self, id: &str, params: &UpdateAddress) -> Result<Forward> {
        self.call(
            Call::new(Method::Patch, format!("/api/v1/address/{}", enc_id(id)?)).json(params)?,
        )
    }

    /// `DELETE /api/v1/address/{id}`: soft delete (`status: disabled`). Idempotent.
    pub fn disable_address(&self, id: &str) -> Result<Forward> {
        self.call(Call::new(
            Method::Delete,
            format!("/api/v1/address/{}", enc_id(id)?),
        ))
    }

    // ------------------------------------------------------------------ catalogue

    /// `GET /api/v1/tickers`: every `(asset, chain)` offered for forwarding. Public; one page.
    pub fn list_tickers(&self) -> Result<Page<Ticker>> {
        self.call(Call::new(Method::Get, "/api/v1/tickers").public())
    }

    // ------------------------------------------------------------------ webhook secret

    /// `POST /api/v1/webhooks/secret/rotate`. The new secret is returned ONCE; store it now.
    /// Verify against `[new, old]` until `prev_expires_at`.
    pub fn rotate_webhook_secret(
        &self,
        opts: Option<&RequestOptions>,
    ) -> Result<WebhookSecretRotated> {
        self.call(
            Call::new(Method::Post, "/api/v1/webhooks/secret/rotate")
                .idempotent()
                .opts(opts),
        )
    }

    /// `GET /api/v1/webhooks/secret`: whether the previous secret is still in its grace window.
    pub fn get_webhook_secret_state(&self) -> Result<WebhookSecretState> {
        self.call(Call::new(Method::Get, "/api/v1/webhooks/secret"))
    }

    // ------------------------------------------------------------------ receiving webhooks

    /// Verify a delivery against the configured secrets (300s tolerance).
    pub fn verify_webhook(&self, raw_body: &[u8], signature_header: &str) -> bool {
        webhooks::verify(
            raw_body,
            signature_header,
            &self.inner.webhook_secrets,
            VerifyOptions::default(),
        )
    }

    /// Verify and parse a delivery against the configured secrets.
    pub fn parse_webhook(
        &self,
        raw_body: &[u8],
        signature_header: &str,
    ) -> std::result::Result<WebhookEvent, WebhookError> {
        webhooks::parse(
            raw_body,
            signature_header,
            &self.inner.webhook_secrets,
            VerifyOptions::default(),
        )
    }

    // ------------------------------------------------------------------ escape hatch

    /// Call any endpoint and decode the JSON response into `T` (use `serde_json::Value` for an
    /// untyped result). `path_and_query` starts with `/`, e.g. `"/api/v1/keys?limit=10"`.
    ///
    /// Retry-safe when the method is GET or `opts` carries an `Idempotency-Key`.
    pub fn request<T: DeserializeOwned>(
        &self,
        method: Method,
        path_and_query: &str,
        body: Option<&Value>,
        opts: Option<&RequestOptions>,
    ) -> Result<T> {
        self.request_raw(method, path_and_query, body, opts)?.json()
    }

    /// Like [`request`](Self::request) but returns the status, headers and raw body, e.g. to
    /// tell a 200 from a 201 or to read `Idempotent-Replay`. Non-2xx statuses are still errors.
    pub fn request_raw(
        &self,
        method: Method,
        path_and_query: &str,
        body: Option<&Value>,
        opts: Option<&RequestOptions>,
    ) -> Result<RawResponse> {
        if !path_and_query.starts_with('/') {
            return Err(Error::InvalidArgument(format!(
                "path must start with '/', got {path_and_query:?}"
            )));
        }
        let mut call = Call::new(method, path_and_query).opts(opts).idempotent();
        if let Some(b) = body {
            call = call.json(b)?;
        }
        let res = self.execute(call)?;
        Ok(RawResponse {
            status: res.status,
            headers: res.headers,
            body: res.body,
        })
    }

    // ------------------------------------------------------------------ internals

    fn call<T: DeserializeOwned>(&self, call: Call<'_>) -> Result<T> {
        let res = self.execute(call)?;
        decode_body(&res.body)
    }

    fn execute(&self, call: Call<'_>) -> Result<HttpResponse> {
        let inner = &self.inner;
        let url = format!(
            "{}{}{}",
            inner.base_url,
            call.path,
            build_query(&call.query)
        );
        let path_only = call.path.split('?').next().unwrap_or("").to_string();
        let label = format!("{} {}", call.method, path_only);

        let mut headers: Vec<(String, String)> = vec![
            ("accept".into(), "application/json".into()),
            ("user-agent".into(), inner.user_agent.clone()),
        ];
        let mut timeout = inner.timeout;
        let mut has_key = false;
        if let Some(opts) = call.opts {
            for (k, v) in &opts.headers {
                let k = k.to_ascii_lowercase();
                headers.retain(|(name, _)| *name != k);
                headers.push((k, v.clone()));
            }
            if let Some(key) = opts.idempotency_key.as_deref().filter(|k| !k.is_empty()) {
                headers.retain(|(name, _)| name != "idempotency-key");
                headers.push(("idempotency-key".into(), key.to_string()));
                has_key = true;
            }
            if let Some(t) = opts.timeout {
                timeout = t;
            }
        }
        if call.auth {
            if let Some(key) = &inner.api_key {
                headers.retain(|(name, _)| name != "authorization");
                headers.push(("authorization".into(), format!("Bearer {key}")));
            }
        }
        if call.body.is_some() {
            headers.retain(|(name, _)| name != "content-type");
            headers.push(("content-type".into(), "application/json".into()));
        }

        // GET is safe. A mutating call is safe ONLY with an Idempotency-Key on an endpoint that
        // honours it. Blind-retrying a POST is how a customer gets charged twice.
        let safe = call.method == Method::Get || (call.idempotent && has_key);
        let max_attempts = if safe {
            inner.max_retries.saturating_add(1)
        } else {
            1
        };

        let request = HttpRequest {
            method: call.method,
            url,
            headers,
            body: call.body,
            timeout,
        };

        let mut attempt = 0u32;
        loop {
            attempt += 1;
            match inner.transport.send(&request) {
                Err(e) => {
                    if attempt >= max_attempts {
                        return Err(Error::Transport(e));
                    }
                    (inner.sleeper)(backoff(attempt, inner.retry_base, None));
                }
                Ok(res) => {
                    if (200..300).contains(&res.status) {
                        return Ok(res);
                    }
                    let retryable = res.status == 429 || res.status >= 500;
                    if retryable && attempt < max_attempts {
                        (inner.sleeper)(backoff(attempt, inner.retry_base, Some(&res.headers)));
                        continue;
                    }
                    return Err(Error::Api(Box::new(ApiError::from_response(
                        res.status,
                        &res.headers,
                        &res.body,
                        label,
                    ))));
                }
            }
        }
    }
}

fn decode_body<T: DeserializeOwned>(body: &[u8]) -> Result<T> {
    if body.iter().all(|b| b.is_ascii_whitespace()) {
        return Ok(serde_json::from_value(Value::Null)?);
    }
    Ok(serde_json::from_slice(body)?)
}

/// Exponential backoff honouring `Retry-After`, then `X-RateLimit-Reset`.
fn backoff(attempt: u32, base: Duration, headers: Option<&[(String, String)]>) -> Duration {
    if let Some(h) = headers {
        if let Some(secs) = find_header(h, "retry-after").and_then(|v| v.trim().parse::<u64>().ok())
        {
            if secs > 0 {
                return Duration::from_secs(secs).min(MAX_RETRY_DELAY);
            }
        }
        if let Some(reset) =
            find_header(h, "x-ratelimit-reset").and_then(|v| v.trim().parse::<u64>().ok())
        {
            let now = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default();
            let reset = Duration::from_secs(reset);
            if reset > now {
                return (reset - now).min(MAX_RETRY_DELAY);
            }
        }
    }
    let exp = base.saturating_mul(1u32 << (attempt.saturating_sub(1)).min(16));
    let jitter_ms = (base.as_millis() as u64).max(1);
    let jitter = Duration::from_millis(pseudo_random() % jitter_ms);
    (exp + jitter).min(MAX_RETRY_DELAY)
}

/// Jitter source. Not cryptographic; it only spreads retries apart.
fn pseudo_random() -> u64 {
    static COUNTER: AtomicU64 = AtomicU64::new(0x9E37_79B9_7F4A_7C15);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.subsec_nanos() as u64)
        .unwrap_or(0);
    let mut x = COUNTER.fetch_add(0x9E37_79B9_7F4A_7C15, Ordering::Relaxed) ^ nanos;
    x ^= x >> 33;
    x = x.wrapping_mul(0xff51_afd7_ed55_8ccd);
    x ^= x >> 33;
    x
}

fn enc_id(id: &str) -> Result<String> {
    if id.trim().is_empty() {
        return Err(Error::InvalidArgument("an id is required".into()));
    }
    Ok(percent_encode(id))
}

/// Percent-encode everything except RFC 3986 unreserved characters.
pub(crate) fn percent_encode(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for b in s.bytes() {
        match b {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'.' | b'_' | b'~' => {
                out.push(b as char)
            }
            _ => out.push_str(&format!("%{b:02X}")),
        }
    }
    out
}

fn build_query(query: &[(&'static str, String)]) -> String {
    if query.is_empty() {
        return String::new();
    }
    let parts: Vec<String> = query
        .iter()
        .map(|(k, v)| format!("{}={}", percent_encode(k), percent_encode(v)))
        .collect();
    format!("?{}", parts.join("&"))
}
