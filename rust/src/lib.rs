//! Official Rust SDK for [Receivify](https://receivify.io), the crypto payment gateway:
//! invoices, forwarding addresses, and signed webhooks. API reference:
//! <https://receivify.io/docs>.
//!
//! # Quickstart
//!
//! ```no_run
//! # fn main() -> Result<(), Box<dyn std::error::Error>> {
//! use std::time::Duration;
//! use receivify::{Client, CreateInvoice, RequestOptions};
//!
//! let client = Client::builder("https://receivify.io")
//!     .api_key(std::env::var("RECEIVIFY_API_KEY")?)
//!     .webhook_secrets(vec![std::env::var("RECEIVIFY_WEBHOOK_SECRET")?])
//!     .timeout(Duration::from_secs(30))
//!     .build()?;
//!
//! let invoice = client.create_invoice(
//!     &CreateInvoice {
//!         amount_fiat: Some("25.00".into()),
//!         currency: Some("USD".into()),
//!         asset: Some("btc".into()),
//!         reference: Some("order-1".into()),
//!         add_fee_ontop: Some(true),
//!         ..Default::default()
//!     },
//!     Some(&RequestOptions::idempotency_key("order-1")),
//! )?;
//!
//! // Send the payer to the hosted checkout.
//! println!("{}", invoice.checkout_url);
//! // Money is an exact base-unit string. Never parse it into a float.
//! println!("{} base units", invoice.amount_expected);
//! # Ok(()) }
//! ```
//!
//! # Two rules
//!
//! 1. **Money is a string.** Every amount is an exact-integer base-unit `String` (satoshis, wei).
//!    Use [`money`] to format or compare; never convert to `f64`.
//! 2. **Verify webhooks against the raw body bytes**, with [`webhooks::verify`] or
//!    [`Client::parse_webhook`].
//!
//! # Transport
//!
//! The default `ureq` feature provides a blocking HTTP client. Disable default features and pass
//! your own [`Transport`] to use anything else.

#![forbid(unsafe_code)]
#![warn(missing_docs)]

mod client;
mod error;
pub mod models;
pub mod money;
mod pagination;
pub mod transport;
pub mod webhooks;

pub use client::{
    Client, ClientBuilder, RawResponse, RequestOptions, DEFAULT_MAX_RETRIES, DEFAULT_TIMEOUT,
    USER_AGENT, VERSION,
};
pub use error::{ApiError, Error, ErrorCode, Result};
pub use models::{
    AddressListParams, BulkResult, CreateAddress, CreateInvoice, Forward, ForwardCallback,
    ForwardDetail, ForwardEventData, ForwardPayment, ForwardStatus, Invoice, InvoiceEventData,
    InvoiceStatus, JsonObject, ListParams, Page, Ticker, UpdateAddress, WebhookSecretRotated,
    WebhookSecretState,
};
pub use pagination::Paginator;
pub use transport::{HttpRequest, HttpResponse, Method, Transport, TransportError};
pub use webhooks::{VerifyOptions, WebhookError, WebhookEvent};

#[cfg(feature = "ureq")]
pub use transport::UreqTransport;
