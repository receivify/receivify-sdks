//! Errors.
//!
//! Every API failure body is `{ "error": "<code>", "detail": "<sentence>" }` with a code from a
//! frozen registry. The code is mapped to [`ErrorCode`]; a code this SDK has never heard of (one
//! added by a newer deployment) becomes [`ErrorCode::Unknown`] with the original string kept in
//! [`ApiError::raw_code`], so an older SDK never fails to parse an error.
//!
//! Branch on [`ApiError::code`], never on [`ApiError::detail`]: the wording of `detail` is not
//! part of the contract.

use std::fmt;

use crate::money::MoneyError;
use crate::transport::TransportError;
use crate::webhooks::WebhookError;

/// Shorthand for `Result<T, receivify::Error>`.
pub type Result<T, E = Error> = std::result::Result<T, E>;

/// Everything this SDK can fail with.
#[derive(Debug)]
#[non_exhaustive]
pub enum Error {
    /// The API answered with a 4xx/5xx (or an unexpected non-2xx) status.
    /// Boxed to keep `Result<T, Error>` small; fields are reachable through auto-deref.
    Api(Box<ApiError>),
    /// No HTTP response was obtained at all: DNS, TLS, connect, socket, or a timeout.
    Transport(TransportError),
    /// A 2xx response body did not decode into the expected type.
    Json(serde_json::Error),
    /// The client was configured incorrectly (empty base URL, no transport, ...).
    Config(String),
    /// An argument was rejected before any request was made (for example an empty id).
    InvalidArgument(String),
    /// The server broke the pagination contract (a repeated cursor, or `has_more` with no cursor).
    Pagination(String),
    /// A webhook failed verification or parsing.
    Webhook(WebhookError),
    /// A money helper was given something that is not a valid amount.
    Money(MoneyError),
}

impl Error {
    /// The API error, if this is one.
    pub fn as_api(&self) -> Option<&ApiError> {
        match self {
            Error::Api(e) => Some(e.as_ref()),
            _ => None,
        }
    }

    /// The API error code, if this is an API error.
    pub fn code(&self) -> Option<ErrorCode> {
        self.as_api().map(|e| e.code)
    }

    /// The HTTP status, if this is an API error.
    pub fn status(&self) -> Option<u16> {
        self.as_api().map(|e| e.status)
    }
}

impl fmt::Display for Error {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Api(e) => fmt::Display::fmt(e, f),
            Error::Transport(e) => fmt::Display::fmt(e, f),
            Error::Json(e) => write!(f, "receivify: could not decode the response body: {e}"),
            Error::Config(m) => write!(f, "receivify: configuration error: {m}"),
            Error::InvalidArgument(m) => write!(f, "receivify: invalid argument: {m}"),
            Error::Pagination(m) => write!(f, "receivify: pagination error: {m}"),
            Error::Webhook(e) => fmt::Display::fmt(e, f),
            Error::Money(e) => fmt::Display::fmt(e, f),
        }
    }
}

impl std::error::Error for Error {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Error::Api(e) => Some(e.as_ref()),
            Error::Transport(e) => Some(e),
            Error::Json(e) => Some(e),
            Error::Webhook(e) => Some(e),
            Error::Money(e) => Some(e),
            _ => None,
        }
    }
}

impl From<ApiError> for Error {
    fn from(e: ApiError) -> Self {
        Error::Api(Box::new(e))
    }
}
impl From<TransportError> for Error {
    fn from(e: TransportError) -> Self {
        Error::Transport(e)
    }
}
impl From<serde_json::Error> for Error {
    fn from(e: serde_json::Error) -> Self {
        Error::Json(e)
    }
}
impl From<WebhookError> for Error {
    fn from(e: WebhookError) -> Self {
        Error::Webhook(e)
    }
}
impl From<MoneyError> for Error {
    fn from(e: MoneyError) -> Self {
        Error::Money(e)
    }
}

/// The frozen error-code registry from `openapi.yaml`, plus [`ErrorCode::Unknown`].
///
/// `#[non_exhaustive]`: adding a code server-side is an additive change, so keep a `_` arm.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[non_exhaustive]
pub enum ErrorCode {
    /// 400 — malformed request: bad JSON, a missing field, or a value that failed validation.
    BadRequest,
    /// 400 — the forwarding ticker is unknown, misspelled, or not offered by this deployment.
    BadTicker,
    /// 400 — the destination address is not valid for that ticker/chain.
    BadDestination,
    /// 400 — the asset is enabled but its settlement backend cannot accept it right now.
    AssetUnavailable,
    /// 400 — below the economic floor.
    AmountTooLow,
    /// 400 — the merchant account is suspended.
    Suspended,
    /// 400 — a per-account quota is already at its maximum.
    LimitExceeded,
    /// 400 — the supplied two-factor code is not valid.
    InvalidCode,
    /// 400 — two-factor enrolment has not been started yet.
    NoSetup,
    /// 400 — two-factor authentication is not enabled on this account.
    NotEnabled,
    /// 400 — no registered, active webhook endpoint matched the request.
    NoTarget,
    /// 400 — a single-use confirmation token is invalid, expired, or already spent.
    InvalidToken,
    /// 401 — missing, malformed, revoked or unknown API key.
    Unauthorized,
    /// 401 — an inbound provider webhook failed HMAC verification.
    BadSignature,
    /// 403 — self-serve signup is turned off on this deployment.
    SignupDisabled,
    /// 404 — no such resource, or it does not belong to the caller.
    NotFound,
    /// 409 — a resource with that unique identity already exists.
    AlreadyExists,
    /// 409 — the feature being enabled is already on.
    AlreadyEnabled,
    /// 409 — the resource exists but its state forbids this action.
    NotAllowed,
    /// 429 — rate limit exceeded.
    RateLimited,
    /// 500 — an unhandled server error. Safe to retry.
    Internal,
    /// 502 — a node or payment provider we depend on failed. Retryable.
    UpstreamError,
    /// 503 — no fresh FX rate is available to price the request. Retryable.
    RateUnavailable,
    /// A code this SDK version does not know, or a body with no `error` key at all.
    /// The original string is in [`ApiError::raw_code`].
    Unknown,
}

impl ErrorCode {
    /// Every known code, in the spec's order.
    pub const ALL: [ErrorCode; 23] = [
        ErrorCode::BadRequest,
        ErrorCode::BadTicker,
        ErrorCode::BadDestination,
        ErrorCode::AssetUnavailable,
        ErrorCode::AmountTooLow,
        ErrorCode::Suspended,
        ErrorCode::LimitExceeded,
        ErrorCode::InvalidCode,
        ErrorCode::NoSetup,
        ErrorCode::NotEnabled,
        ErrorCode::NoTarget,
        ErrorCode::InvalidToken,
        ErrorCode::Unauthorized,
        ErrorCode::BadSignature,
        ErrorCode::SignupDisabled,
        ErrorCode::NotFound,
        ErrorCode::AlreadyExists,
        ErrorCode::AlreadyEnabled,
        ErrorCode::NotAllowed,
        ErrorCode::RateLimited,
        ErrorCode::Internal,
        ErrorCode::UpstreamError,
        ErrorCode::RateUnavailable,
    ];

    /// Map a wire code to an [`ErrorCode`]. Never fails: unrecognised input is `Unknown`.
    pub fn from_code(code: &str) -> ErrorCode {
        ErrorCode::ALL
            .iter()
            .copied()
            .find(|c| c.as_str() == code)
            .unwrap_or(ErrorCode::Unknown)
    }

    /// The wire string, e.g. `"not_found"`. `Unknown` is `"unknown"`.
    pub fn as_str(&self) -> &'static str {
        match self {
            ErrorCode::BadRequest => "bad_request",
            ErrorCode::BadTicker => "bad_ticker",
            ErrorCode::BadDestination => "bad_destination",
            ErrorCode::AssetUnavailable => "asset_unavailable",
            ErrorCode::AmountTooLow => "amount_too_low",
            ErrorCode::Suspended => "suspended",
            ErrorCode::LimitExceeded => "limit_exceeded",
            ErrorCode::InvalidCode => "invalid_code",
            ErrorCode::NoSetup => "no_setup",
            ErrorCode::NotEnabled => "not_enabled",
            ErrorCode::NoTarget => "no_target",
            ErrorCode::InvalidToken => "invalid_token",
            ErrorCode::Unauthorized => "unauthorized",
            ErrorCode::BadSignature => "bad_signature",
            ErrorCode::SignupDisabled => "signup_disabled",
            ErrorCode::NotFound => "not_found",
            ErrorCode::AlreadyExists => "already_exists",
            ErrorCode::AlreadyEnabled => "already_enabled",
            ErrorCode::NotAllowed => "not_allowed",
            ErrorCode::RateLimited => "rate_limited",
            ErrorCode::Internal => "internal",
            ErrorCode::UpstreamError => "upstream_error",
            ErrorCode::RateUnavailable => "rate_unavailable",
            ErrorCode::Unknown => "unknown",
        }
    }

    /// True for the codes the API documents as worth retrying.
    pub fn is_retryable(&self) -> bool {
        matches!(
            self,
            ErrorCode::RateLimited
                | ErrorCode::Internal
                | ErrorCode::UpstreamError
                | ErrorCode::RateUnavailable
        )
    }
}

impl fmt::Display for ErrorCode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// An `{ error, detail }` body that came back with a non-2xx status.
#[derive(Debug, Clone)]
pub struct ApiError {
    /// The mapped code. `Unknown` for a code this SDK does not recognise.
    pub code: ErrorCode,
    /// The raw `error` string from the body, always preserved. Empty when the body had none.
    pub raw_code: String,
    /// The human sentence from the body. For logs, never for branching.
    pub detail: String,
    /// HTTP status code.
    pub status: u16,
    /// `METHOD /path` of the request, for logs. Never contains the API key or the query string.
    pub request: String,
    /// Response headers, names lower-cased.
    pub headers: Vec<(String, String)>,
    /// The raw response body (UTF-8, lossily decoded).
    pub body: String,
    /// `Retry-After`, in seconds, when present.
    pub retry_after: Option<u64>,
    /// `X-RateLimit-Reset`, unix seconds, when present.
    pub rate_limit_reset: Option<u64>,
    /// `X-RateLimit-Limit`, when present.
    pub rate_limit_limit: Option<u64>,
    /// `X-RateLimit-Remaining`, when present.
    pub rate_limit_remaining: Option<u64>,
}

impl ApiError {
    /// Build an error from a failed response. Never fails, whatever the body looks like.
    pub fn from_response(
        status: u16,
        headers: &[(String, String)],
        body: &[u8],
        request: impl Into<String>,
    ) -> ApiError {
        let parsed: Option<serde_json::Value> = serde_json::from_slice(body).ok();
        let obj = parsed.as_ref().and_then(|v| v.as_object());
        let raw_code = obj
            .and_then(|o| o.get("error"))
            .and_then(|v| v.as_str())
            .unwrap_or("")
            .to_string();
        let detail = obj
            .and_then(|o| o.get("detail"))
            .and_then(|v| v.as_str())
            .map(str::to_string)
            .unwrap_or_else(|| {
                if raw_code.is_empty() {
                    format!("HTTP {status}")
                } else {
                    raw_code.clone()
                }
            });
        let mut code = ErrorCode::from_code(&raw_code);
        if code == ErrorCode::Unknown && raw_code.is_empty() && status == 429 {
            code = ErrorCode::RateLimited;
        }
        let header_u64 = |name: &str| {
            crate::transport::find_header(headers, name).and_then(|v| v.trim().parse::<u64>().ok())
        };
        ApiError {
            code,
            raw_code,
            detail,
            status,
            request: request.into(),
            headers: headers
                .iter()
                .map(|(k, v)| (k.to_ascii_lowercase(), v.clone()))
                .collect(),
            body: String::from_utf8_lossy(body).into_owned(),
            retry_after: header_u64("retry-after"),
            rate_limit_reset: header_u64("x-ratelimit-reset"),
            rate_limit_limit: header_u64("x-ratelimit-limit"),
            rate_limit_remaining: header_u64("x-ratelimit-remaining"),
        }
    }

    /// A response header, looked up case-insensitively.
    pub fn header(&self, name: &str) -> Option<&str> {
        crate::transport::find_header(&self.headers, name)
    }

    /// True when retrying might succeed (a retryable code, or any 5xx).
    pub fn is_retryable(&self) -> bool {
        self.code.is_retryable() || self.status >= 500
    }
}

impl fmt::Display for ApiError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let code = if self.raw_code.is_empty() {
            "(no code)"
        } else {
            &self.raw_code
        };
        write!(
            f,
            "receivify: {} -> {} {}: {}",
            self.request, self.status, code, self.detail
        )
    }
}

impl std::error::Error for ApiError {}
