//! The transport seam.
//!
//! Rust's standard library has no HTTP client, so the SDK talks to the network through the
//! one-method [`Transport`] trait. A blocking implementation built on `ureq` ships behind the
//! default-on `ureq` cargo feature ([`UreqTransport`]); everything above this trait (the client,
//! the retry policy, the error mapping, pagination) knows nothing about it.
//!
//! Implement [`Transport`] yourself to use another HTTP stack, add a proxy or instrumentation, or
//! feed canned responses in tests.

use std::fmt;
use std::time::Duration;

/// An HTTP method.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Method {
    /// `GET`
    Get,
    /// `POST`
    Post,
    /// `PUT`
    Put,
    /// `PATCH`
    Patch,
    /// `DELETE`
    Delete,
}

impl Method {
    /// The upper-case method name.
    pub fn as_str(&self) -> &'static str {
        match self {
            Method::Get => "GET",
            Method::Post => "POST",
            Method::Put => "PUT",
            Method::Patch => "PATCH",
            Method::Delete => "DELETE",
        }
    }
}

impl fmt::Display for Method {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

/// One outbound request, fully resolved.
///
/// The API key, when present, is already in `headers` as `authorization`. It is never in `url`.
/// `Debug` masks that header.
#[derive(Clone)]
pub struct HttpRequest {
    /// The method.
    pub method: Method,
    /// Absolute URL, query string included.
    pub url: String,
    /// Header names are lower-case.
    pub headers: Vec<(String, String)>,
    /// The JSON body, when there is one.
    pub body: Option<Vec<u8>>,
    /// Per-request timeout. A transport must honour it.
    pub timeout: Duration,
}

impl HttpRequest {
    /// A header value, looked up case-insensitively.
    pub fn header(&self, name: &str) -> Option<&str> {
        find_header(&self.headers, name)
    }

    /// The body as UTF-8 text (lossy), or `""`.
    pub fn body_str(&self) -> String {
        self.body
            .as_deref()
            .map(|b| String::from_utf8_lossy(b).into_owned())
            .unwrap_or_default()
    }
}

impl fmt::Debug for HttpRequest {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let headers: Vec<(&str, &str)> = self
            .headers
            .iter()
            .map(|(k, v)| {
                if k.eq_ignore_ascii_case("authorization") {
                    (k.as_str(), "[redacted]")
                } else {
                    (k.as_str(), v.as_str())
                }
            })
            .collect();
        f.debug_struct("HttpRequest")
            .field("method", &self.method)
            .field("url", &self.url)
            .field("headers", &headers)
            .field("body_len", &self.body.as_ref().map(|b| b.len()))
            .field("timeout", &self.timeout)
            .finish()
    }
}

/// One inbound response.
#[derive(Debug, Clone, Default)]
pub struct HttpResponse {
    /// The status code.
    pub status: u16,
    /// Response headers. Names should be lower-case; lookups are case-insensitive regardless.
    pub headers: Vec<(String, String)>,
    /// The raw body bytes, unparsed.
    pub body: Vec<u8>,
}

impl HttpResponse {
    /// A header value, looked up case-insensitively.
    pub fn header(&self, name: &str) -> Option<&str> {
        find_header(&self.headers, name)
    }
}

/// The request never produced an HTTP response.
#[derive(Debug)]
pub struct TransportError {
    message: String,
    timed_out: bool,
    source: Option<Box<dyn std::error::Error + Send + Sync + 'static>>,
}

impl TransportError {
    /// A transport failure with a message.
    pub fn new(message: impl Into<String>) -> Self {
        TransportError {
            message: message.into(),
            timed_out: false,
            source: None,
        }
    }

    /// A timeout.
    pub fn timeout(message: impl Into<String>) -> Self {
        TransportError {
            message: message.into(),
            timed_out: true,
            source: None,
        }
    }

    /// Attach the underlying error.
    pub fn with_source(mut self, source: impl std::error::Error + Send + Sync + 'static) -> Self {
        self.source = Some(Box::new(source));
        self
    }

    /// True when the failure was a timeout.
    pub fn is_timeout(&self) -> bool {
        self.timed_out
    }

    /// The message.
    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for TransportError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if self.timed_out {
            write!(f, "receivify: request timed out: {}", self.message)
        } else {
            write!(f, "receivify: transport error: {}", self.message)
        }
    }
}

impl std::error::Error for TransportError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        self.source
            .as_ref()
            .map(|e| e.as_ref() as &(dyn std::error::Error + 'static))
    }
}

/// Plug in your own HTTP stack.
///
/// Contract:
/// - Return the response for ANY status, including 4xx and 5xx. The SDK maps statuses to errors
///   itself; a transport that turns a 404 into an `Err` breaks that.
/// - Return `Err(TransportError)` only when no response was obtained at all, using
///   [`TransportError::timeout`] for a timeout.
/// - Honour [`HttpRequest::timeout`].
/// - Do not follow redirects (a redirect must not carry the `authorization` header elsewhere).
/// - `send` may be called from several threads at once if the `Client` is shared.
pub trait Transport: Send + Sync {
    /// Send one request and return the response.
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, TransportError>;
}

impl<F> Transport for F
where
    F: Fn(&HttpRequest) -> Result<HttpResponse, TransportError> + Send + Sync,
{
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, TransportError> {
        self(request)
    }
}

pub(crate) fn find_header<'a>(headers: &'a [(String, String)], name: &str) -> Option<&'a str> {
    headers
        .iter()
        .find(|(k, _)| k.eq_ignore_ascii_case(name))
        .map(|(_, v)| v.as_str())
}

#[cfg(feature = "ureq")]
pub use self::ureq_impl::UreqTransport;

#[cfg(feature = "ureq")]
mod ureq_impl {
    use super::*;
    use std::io::Read;

    /// Largest response body the bundled transport will read, in bytes.
    const MAX_BODY_BYTES: u64 = 16 * 1024 * 1024;

    /// The bundled blocking transport: `ureq` with rustls, redirects disabled.
    #[derive(Clone)]
    pub struct UreqTransport {
        agent: ureq::Agent,
    }

    impl fmt::Debug for UreqTransport {
        fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
            f.debug_struct("UreqTransport").finish_non_exhaustive()
        }
    }

    impl Default for UreqTransport {
        fn default() -> Self {
            Self::new()
        }
    }

    impl UreqTransport {
        /// A transport with its own connection pool.
        pub fn new() -> Self {
            UreqTransport {
                agent: ureq::AgentBuilder::new().redirects(0).build(),
            }
        }

        /// Wrap an `ureq::Agent` you configured yourself (proxy, custom TLS, ...).
        /// Configure it with `.redirects(0)`.
        pub fn from_agent(agent: ureq::Agent) -> Self {
            UreqTransport { agent }
        }
    }

    fn is_timeout(err: &ureq::Transport) -> bool {
        let mut source = std::error::Error::source(err);
        while let Some(s) = source {
            if let Some(io) = s.downcast_ref::<std::io::Error>() {
                if matches!(
                    io.kind(),
                    std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
                ) {
                    return true;
                }
            }
            source = s.source();
        }
        err.to_string().to_ascii_lowercase().contains("timed out")
    }

    impl Transport for UreqTransport {
        fn send(&self, request: &HttpRequest) -> Result<HttpResponse, TransportError> {
            let mut req = self
                .agent
                .request(request.method.as_str(), &request.url)
                .timeout(request.timeout);
            for (k, v) in &request.headers {
                req = req.set(k, v);
            }
            let result = match &request.body {
                Some(body) => req.send_bytes(body),
                None => req.call(),
            };
            let response = match result {
                Ok(r) => r,
                Err(ureq::Error::Status(_, r)) => r,
                Err(ureq::Error::Transport(t)) => {
                    let timed_out = is_timeout(&t);
                    // ureq's message includes the URL; ours never carries the key, so this is safe.
                    let msg = t.to_string();
                    return Err(if timed_out {
                        TransportError::timeout(msg)
                    } else {
                        TransportError::new(msg)
                    });
                }
            };
            let status = response.status();
            let headers = response
                .headers_names()
                .into_iter()
                .filter_map(|name| {
                    let value = response.header(&name)?.to_string();
                    Some((name.to_ascii_lowercase(), value))
                })
                .collect();
            let mut body = Vec::new();
            response
                .into_reader()
                .take(MAX_BODY_BYTES)
                .read_to_end(&mut body)
                .map_err(|e| {
                    let timed_out = matches!(
                        e.kind(),
                        std::io::ErrorKind::TimedOut | std::io::ErrorKind::WouldBlock
                    );
                    let msg = format!("reading the response body failed: {e}");
                    if timed_out {
                        TransportError::timeout(msg).with_source(e)
                    } else {
                        TransportError::new(msg).with_source(e)
                    }
                })?;
            Ok(HttpResponse {
                status,
                headers,
                body,
            })
        }
    }
}
