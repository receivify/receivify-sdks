//! Webhook signature verification and envelope parsing.
//!
//! # Use the RAW body. Always.
//!
//! The `X-Receivify-Signature: t=<unix>,v1=<hex>` header is HMAC-SHA256 over `"<t>." + rawBody`,
//! the EXACT bytes Receivify sent. If your framework parses the JSON and you serialize it again,
//! key order, whitespace and number formatting change and a genuine delivery fails to verify.
//! Take the body as bytes (axum's `Bytes`, actix's `web::Bytes`) before anything parses it.
//!
//! # Verify against a LIST of secrets.
//!
//! Rotating the secret keeps the previous one valid for a grace window (24h by default), so
//! during a rotation two secrets are legitimately valid. Pass `[current, previous]`.
//!
//! ```
//! use receivify::webhooks::{self, VerifyOptions};
//!
//! # let secret = "whsec_test";
//! # let body = br#"{"event":"invoice.paid","sequence":1,"created_at":"2026-01-01T00:00:00.000Z","data":{}}"#;
//! # let header = webhooks::sign(body, secret, webhooks::now_unix());
//! let ok = webhooks::verify(body, &header, &[secret], VerifyOptions::default());
//! assert!(ok);
//!
//! let event = webhooks::parse(body, &header, &[secret], VerifyOptions::default()).unwrap();
//! assert_eq!(event.event, "invoice.paid");
//! ```

use std::fmt;
use std::time::{SystemTime, UNIX_EPOCH};

use hmac::{Hmac, Mac};
use serde::de::DeserializeOwned;
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::Sha256;

use crate::models::JsonObject;

type HmacSha256 = Hmac<Sha256>;

/// The header Receivify signs every delivery with.
pub const SIGNATURE_HEADER: &str = "X-Receivify-Signature";

/// Default replay window, in seconds.
pub const DEFAULT_TOLERANCE_SECONDS: u64 = 300;

/// The complete `EventName` set from `openapi.yaml`. Informational: receivers MUST ignore names
/// they do not recognise, which is why [`WebhookEvent::event`] is a `String`.
pub const EVENTS: &[&str] = &[
    "invoice.confirming",
    "invoice.partial",
    "invoice.paid",
    "invoice.swept",
    "invoice.expired",
    "invoice.partial_expired",
    "invoice.cancelled",
    "invoice.failed",
    "sweep.sent",
    "forward.detected",
    "forward.confirmed",
    "forward.deferred",
    "forward.sent",
    "forward.reversed",
    "forward.failed",
    "test",
];

/// Options for [`verify`] and [`parse`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct VerifyOptions {
    /// Replay window in seconds, applied in both directions. `0` disables the timestamp check
    /// (only for replaying archived fixtures; do not do that in production).
    pub tolerance_seconds: u64,
    /// "Now" in unix seconds. `None` reads the system clock. For tests.
    pub now: Option<i64>,
}

impl Default for VerifyOptions {
    fn default() -> Self {
        VerifyOptions {
            tolerance_seconds: DEFAULT_TOLERANCE_SECONDS,
            now: None,
        }
    }
}

impl VerifyOptions {
    /// Set the tolerance.
    pub fn tolerance_seconds(mut self, seconds: u64) -> Self {
        self.tolerance_seconds = seconds;
        self
    }
    /// Pin "now" (unix seconds).
    pub fn now(mut self, unix_seconds: i64) -> Self {
        self.now = Some(unix_seconds);
        self
    }
}

/// Why a delivery was rejected.
#[derive(Debug, Clone, PartialEq, Eq)]
#[non_exhaustive]
pub enum WebhookError {
    /// No (non-empty) secret was supplied.
    NoSecrets,
    /// The signature header is missing or not `t=<unix>,v1=<hex>`.
    BadHeader,
    /// The timestamp is outside the tolerance window.
    StaleTimestamp {
        /// The header's `t`.
        timestamp: i64,
        /// The tolerance that was applied.
        tolerance_seconds: u64,
    },
    /// No signature matched any secret.
    NoMatch,
    /// The verified body is not valid JSON.
    BadJson(String),
    /// The verified body is JSON but not a `{event, sequence, created_at, data}` envelope.
    BadEnvelope,
}

impl WebhookError {
    /// A stable machine-readable reason: `no_secrets`, `bad_header`, `stale_timestamp`,
    /// `no_match`, `bad_json`, `bad_envelope`.
    pub fn reason(&self) -> &'static str {
        match self {
            WebhookError::NoSecrets => "no_secrets",
            WebhookError::BadHeader => "bad_header",
            WebhookError::StaleTimestamp { .. } => "stale_timestamp",
            WebhookError::NoMatch => "no_match",
            WebhookError::BadJson(_) => "bad_json",
            WebhookError::BadEnvelope => "bad_envelope",
        }
    }
}

impl fmt::Display for WebhookError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            WebhookError::NoSecrets => f.write_str("receivify webhook: no webhook secret configured"),
            WebhookError::BadHeader => write!(
                f,
                "receivify webhook: missing or malformed {SIGNATURE_HEADER} header (expected \"t=<unix>,v1=<hex>\")"
            ),
            WebhookError::StaleTimestamp {
                timestamp,
                tolerance_seconds,
            } => write!(
                f,
                "receivify webhook: timestamp {timestamp} is outside the {tolerance_seconds}s tolerance window"
            ),
            WebhookError::NoMatch => {
                f.write_str("receivify webhook: signature did not match any configured secret")
            }
            WebhookError::BadJson(m) => write!(f, "receivify webhook: body is not valid JSON: {m}"),
            WebhookError::BadEnvelope => f.write_str(
                "receivify webhook: body is not a { event, sequence, created_at, data } envelope",
            ),
        }
    }
}

impl std::error::Error for WebhookError {}

/// The unified envelope: `{ event, sequence, created_at, data }` for every event family.
///
/// Switch on `event` first; `data` is invoice-shaped for `invoice.*`, `sweep.sent` and `test`, and
/// forward-payment-shaped for `forward.*`. `(event, sequence)` is the de-duplication key.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct WebhookEvent {
    /// The event name. Ignore names you do not recognise.
    pub event: String,
    /// Globally monotonic counter; with `event`, the dedupe key.
    #[serde(default)]
    pub sequence: i64,
    /// When the event happened (frozen across retries and replays).
    #[serde(default)]
    pub created_at: String,
    /// The resource. Numbers inside keep their exact text.
    #[serde(default)]
    pub data: Value,
    /// Unmodelled envelope fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

impl WebhookEvent {
    /// True for `invoice.*`, `sweep.sent` and `test`.
    pub fn is_invoice_event(&self) -> bool {
        is_invoice_event(&self.event)
    }

    /// True for `forward.*`.
    pub fn is_forward_event(&self) -> bool {
        is_forward_event(&self.event)
    }

    /// Decode `data` into a typed model, e.g. [`InvoiceEventData`](crate::InvoiceEventData).
    pub fn data_as<T: DeserializeOwned>(&self) -> Result<T, serde_json::Error> {
        serde_json::from_value(self.data.clone())
    }

    /// `data` as [`InvoiceEventData`](crate::InvoiceEventData).
    pub fn invoice_data(&self) -> Result<crate::InvoiceEventData, serde_json::Error> {
        self.data_as()
    }

    /// `data` as [`ForwardEventData`](crate::ForwardEventData).
    pub fn forward_data(&self) -> Result<crate::ForwardEventData, serde_json::Error> {
        self.data_as()
    }
}

/// True for `invoice.*`, `sweep.sent` and `test`, the events whose `data` is invoice-shaped.
pub fn is_invoice_event(event: &str) -> bool {
    event.starts_with("invoice.") || event == "sweep.sent" || event == "test"
}

/// True for `forward.*`, the events whose `data` is forward-payment-shaped.
pub fn is_forward_event(event: &str) -> bool {
    event.starts_with("forward.")
}

/// The current unix time in seconds.
pub fn now_unix() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

struct ParsedHeader<'a> {
    raw_timestamp: &'a str,
    timestamp: i64,
    signatures: Vec<&'a str>,
}

fn parse_header(header: &str) -> Option<ParsedHeader<'_>> {
    let mut raw_timestamp = None;
    let mut signatures = Vec::new();
    for part in header.split(',') {
        let Some((key, value)) = part.split_once('=') else {
            continue;
        };
        let (key, value) = (key.trim(), value.trim());
        if key == "t" && raw_timestamp.is_none() {
            raw_timestamp = Some(value);
        } else if key == "v1" && !value.is_empty() {
            signatures.push(value);
        }
    }
    let raw_timestamp = raw_timestamp?;
    if signatures.is_empty()
        || raw_timestamp.is_empty()
        || !raw_timestamp.bytes().all(|b| b.is_ascii_digit())
    {
        return None;
    }
    let timestamp = raw_timestamp.parse::<i64>().ok()?;
    Some(ParsedHeader {
        raw_timestamp,
        timestamp,
        signatures,
    })
}

fn decode_hex(s: &str) -> Option<Vec<u8>> {
    if s.len() % 2 != 0 {
        return None;
    }
    let nibble = |c: u8| -> Option<u8> {
        match c {
            b'0'..=b'9' => Some(c - b'0'),
            b'a'..=b'f' => Some(c - b'a' + 10),
            b'A'..=b'F' => Some(c - b'A' + 10),
            _ => None,
        }
    };
    s.as_bytes()
        .chunks(2)
        .map(|p| Some((nibble(p[0])? << 4) | nibble(p[1])?))
        .collect()
}

fn encode_hex(bytes: &[u8]) -> String {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        out.push(HEX[(b >> 4) as usize] as char);
        out.push(HEX[(b & 0x0f) as usize] as char);
    }
    out
}

fn mac_for(secret: &str, raw_timestamp: &str, body: &[u8]) -> HmacSha256 {
    // HMAC accepts keys of any length, so this cannot fail.
    let mut mac = HmacSha256::new_from_slice(secret.as_bytes()).expect("HMAC takes any key length");
    mac.update(raw_timestamp.as_bytes());
    mac.update(b".");
    mac.update(body);
    mac
}

/// Verify a delivery and say why it failed. On success, returns the header's timestamp.
///
/// Constant-time: every secret and every `v1` candidate is checked, with no early exit, using
/// HMAC's constant-time comparison.
pub fn verify_detailed<S: AsRef<str>>(
    raw_body: &[u8],
    header: &str,
    secrets: &[S],
    opts: VerifyOptions,
) -> Result<i64, WebhookError> {
    let secrets: Vec<&str> = secrets
        .iter()
        .map(|s| s.as_ref())
        .filter(|s| !s.is_empty())
        .collect();
    if secrets.is_empty() {
        return Err(WebhookError::NoSecrets);
    }
    let parsed = parse_header(header).ok_or(WebhookError::BadHeader)?;

    if opts.tolerance_seconds > 0 {
        let now = opts.now.unwrap_or_else(now_unix);
        let skew = (now as i128 - parsed.timestamp as i128).unsigned_abs();
        if skew > opts.tolerance_seconds as u128 {
            return Err(WebhookError::StaleTimestamp {
                timestamp: parsed.timestamp,
                tolerance_seconds: opts.tolerance_seconds,
            });
        }
    }

    let candidates: Vec<Vec<u8>> = parsed
        .signatures
        .iter()
        .filter_map(|s| decode_hex(s))
        .collect();
    let mut matched = false;
    for secret in &secrets {
        let mac = mac_for(secret, parsed.raw_timestamp, raw_body);
        for candidate in &candidates {
            // `verify_slice` compares in constant time (and rejects a wrong length up front;
            // the length of a signature is not a secret).
            matched |= mac.clone().verify_slice(candidate).is_ok();
        }
    }
    if matched {
        Ok(parsed.timestamp)
    } else {
        Err(WebhookError::NoMatch)
    }
}

/// Verify a delivery. Returns `false` (never panics) for any failure.
///
/// `raw_body` must be the exact request bytes. `secrets` is `[current]` or, during a rotation,
/// `[current, previous]`.
pub fn verify<S: AsRef<str>>(
    raw_body: &[u8],
    header: &str,
    secrets: &[S],
    opts: VerifyOptions,
) -> bool {
    verify_detailed(raw_body, header, secrets, opts).is_ok()
}

/// Verify and parse a delivery in one step.
pub fn parse<S: AsRef<str>>(
    raw_body: &[u8],
    header: &str,
    secrets: &[S],
    opts: VerifyOptions,
) -> Result<WebhookEvent, WebhookError> {
    verify_detailed(raw_body, header, secrets, opts)?;
    parse_unverified(raw_body)
}

/// Parse an envelope WITHOUT checking the signature. Only for bodies you have already verified
/// (or trust for another reason, such as a local fixture).
pub fn parse_unverified(raw_body: &[u8]) -> Result<WebhookEvent, WebhookError> {
    let value: Value =
        serde_json::from_slice(raw_body).map_err(|e| WebhookError::BadJson(e.to_string()))?;
    let mut obj = match value {
        Value::Object(o) => o,
        _ => return Err(WebhookError::BadEnvelope),
    };
    let event = match obj.remove("event") {
        Some(Value::String(s)) => s,
        _ => return Err(WebhookError::BadEnvelope),
    };
    let sequence = match obj.remove("sequence") {
        None | Some(Value::Null) => 0,
        Some(Value::Number(n)) => n
            .as_i64()
            .or_else(|| n.to_string().parse().ok())
            .ok_or(WebhookError::BadEnvelope)?,
        Some(_) => return Err(WebhookError::BadEnvelope),
    };
    let created_at = match obj.remove("created_at") {
        Some(Value::String(s)) => s,
        _ => String::new(),
    };
    // Tolerate the pre-envelope `{ invoice: {...} }` shape.
    let data = match obj.remove("data") {
        Some(v) => v,
        None => obj.get("invoice").cloned().unwrap_or(Value::Null),
    };
    Ok(WebhookEvent {
        event,
        sequence,
        created_at,
        data,
        extra: obj,
    })
}

/// Sign a body the way Receivify does and return the full header value `t=<t>,v1=<hex>`.
///
/// You never need this to RECEIVE webhooks; it exists for tests and local fixtures.
pub fn sign(raw_body: &[u8], secret: &str, timestamp: i64) -> String {
    let t = timestamp.to_string();
    let sig = mac_for(secret, &t, raw_body).finalize().into_bytes();
    format!("t={t},v1={}", encode_hex(&sig))
}
