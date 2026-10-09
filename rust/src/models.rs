//! Request and response models, as `openapi.yaml` defines them.
//!
//! - Money is `String`, always: exact-integer base units (or, where documented, a decimal string).
//! - Nullable fields are `Option`.
//! - Every response model keeps fields this SDK does not know about in `extra`, so a field added
//!   by a newer deployment is still reachable without an SDK upgrade.
//! - Status enums are open: an unrecognised value deserializes to `Other(String)`.

use std::fmt;
use std::ops::Deref;

use serde::{Deserialize, Deserializer, Serialize, Serializer};
use serde_json::{Map, Value};

use crate::money::de::{money, opt_money};

/// A JSON object, used for `metadata` and for unknown fields.
pub type JsonObject = Map<String, Value>;

macro_rules! open_enum {
    (
        $(#[$meta:meta])*
        pub enum $name:ident { $( $(#[$vmeta:meta])* $variant:ident => $wire:literal ),+ $(,)? }
    ) => {
        $(#[$meta])*
        #[derive(Debug, Clone, PartialEq, Eq, Hash)]
        #[non_exhaustive]
        pub enum $name {
            $( $(#[$vmeta])* $variant, )+
            /// A value this SDK version does not know. Keep a default branch.
            Other(String),
        }

        impl $name {
            /// The wire string.
            pub fn as_str(&self) -> &str {
                match self {
                    $( $name::$variant => $wire, )+
                    $name::Other(s) => s.as_str(),
                }
            }
        }

        impl From<&str> for $name {
            fn from(s: &str) -> Self {
                match s {
                    $( $wire => $name::$variant, )+
                    other => $name::Other(other.to_string()),
                }
            }
        }

        impl From<String> for $name {
            fn from(s: String) -> Self {
                $name::from(s.as_str())
            }
        }

        impl fmt::Display for $name {
            fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                f.write_str(self.as_str())
            }
        }

        impl Serialize for $name {
            fn serialize<S: Serializer>(&self, s: S) -> Result<S::Ok, S::Error> {
                s.serialize_str(self.as_str())
            }
        }

        impl<'de> Deserialize<'de> for $name {
            fn deserialize<D: Deserializer<'de>>(d: D) -> Result<Self, D::Error> {
                Ok($name::from(String::deserialize(d)?))
            }
        }
    };
}

open_enum! {
    /// The invoice lifecycle. Open: a new value is an additive change.
    pub enum InvoiceStatus {
        /// The creation state.
        Pending => "pending",
        /// Less than expected arrived.
        Partial => "partial",
        /// Seen on-chain, not yet final.
        Confirming => "confirming",
        /// Paid in full.
        Paid => "paid",
        /// Funds left the deposit address for the payout wallet.
        Swept => "swept",
        /// The payment window closed with nothing received.
        Expired => "expired",
        /// The window closed on an under-payment.
        PartialExpired => "partial_expired",
        /// Cancelled by the merchant.
        Cancelled => "cancelled",
        /// Failed.
        Failed => "failed",
    }
}

open_enum! {
    /// A forwarding address's state.
    pub enum ForwardStatus {
        /// Watching for deposits and forwarding everything credited.
        Active => "active",
        /// The merchant kill switch: no NEW deposit is credited.
        Paused => "paused",
        /// Soft-deleted.
        Disabled => "disabled",
        /// A single-session deposit window closed.
        Expired => "expired",
    }
}

/// An invoice. Identical on create, retrieve, list and cancel.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Invoice {
    /// UUID.
    pub id: String,
    /// Your business key. Also a de-duplication key.
    #[serde(default)]
    pub reference: Option<String>,
    /// Lower-case symbol, e.g. `btc`, `usdc`.
    pub asset: String,
    /// Exact-integer base units, as a string.
    #[serde(deserialize_with = "money")]
    pub amount_expected: String,
    /// Exact-integer base units, as a string. `"0"` until something arrives.
    #[serde(deserialize_with = "money")]
    pub amount_received: String,
    /// A DECIMAL fiat string such as `"25.00"`. Display only; never settle against it.
    #[serde(default, deserialize_with = "opt_money")]
    pub amount_fiat: Option<String>,
    /// ISO-4217 code for `amount_fiat`.
    #[serde(default)]
    pub fiat_currency: Option<String>,
    /// The EVM chain, `solana`, `tron`, or `None` for BTC and BTCPay-settled coins.
    #[serde(default)]
    pub chain: Option<String>,
    /// The settlement backend that derived the address.
    #[serde(default)]
    pub provider: Option<String>,
    /// The deposit address.
    pub address: String,
    /// Lifecycle state.
    pub status: InvoiceStatus,
    /// ISO-8601.
    pub expires_at: String,
    /// ISO-8601, once paid.
    #[serde(default)]
    pub paid_at: Option<String>,
    /// ISO-8601.
    pub created_at: String,
    /// Whatever you stored. Always an object.
    #[serde(default)]
    pub metadata: JsonObject,
    /// The hosted checkout page. Redirect the payer here.
    #[serde(default)]
    pub checkout_url: String,
    /// Decimal places of the asset: the divisor for `amount_expected`.
    #[serde(default)]
    pub decimals: Option<u32>,
    /// `amount_expected` already divided by `10^decimals`, as a decimal string. Display only.
    #[serde(default, deserialize_with = "opt_money")]
    pub amount_decimal: Option<String>,
    /// A wallet-scannable payment URI for a QR code, or `None` when the chain has no safe one.
    #[serde(default)]
    pub payment_uri: Option<String>,
    /// Whether the commission was added on top of the requested amount.
    #[serde(default)]
    pub add_fee_ontop: bool,
    /// Base units: what you asked to RECEIVE. Equals `amount_expected` unless the fee is on top.
    #[serde(default, deserialize_with = "opt_money")]
    pub amount_net_requested: Option<String>,
    /// Base units: `amount_expected - amount_net_requested`. `"0"` when the fee is not on top.
    #[serde(default, deserialize_with = "opt_money")]
    pub fee_ontop: Option<String>,
    /// Fields this SDK version does not model.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `POST /api/v1/invoices` body. Set exactly one amount field.
///
/// Amount precedence (first present wins): `amount_fiat` (+`currency`), `amount`, `amount_sats`,
/// `amount_btc`.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
pub struct CreateInvoice {
    /// Lower-case asset symbol. Server default: `btc`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub asset: Option<String>,
    /// EVM chain. Only consulted for EVM assets. Server default: `ethereum`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub chain: Option<String>,
    /// A fiat price as a decimal string, e.g. `"25.00"`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub amount_fiat: Option<String>,
    /// ISO-4217 code for `amount_fiat`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub currency: Option<String>,
    /// A decimal amount in the coin's own units, e.g. `"0.001"`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub amount: Option<String>,
    /// Exact base units as an integer string (applies to every asset despite the name).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub amount_sats: Option<String>,
    /// Legacy. Only honoured when `asset` is `btc`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub amount_btc: Option<String>,
    /// Your business key; also de-duplicates.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reference: Option<String>,
    /// Per-invoice webhook target, in addition to account endpoints.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub callback_url: Option<String>,
    /// `None` (omitted) defers to the account default. `Some(false)` forces the fee to come out
    /// of the amount; `Some(true)` adds it on top so you receive the amount in full.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub add_fee_ontop: Option<bool>,
    /// Arbitrary metadata, returned on the invoice.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub metadata: Option<JsonObject>,
    /// Extra body fields this SDK version does not model.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// Query parameters for `GET /api/v1/invoices`.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ListParams {
    /// Page size, 1..=200 (server default 50).
    pub limit: Option<u32>,
    /// An opaque cursor from a previous page's `next_cursor`.
    pub cursor: Option<String>,
    /// Exact-match status filter.
    pub status: Option<String>,
}

impl ListParams {
    /// Empty parameters.
    pub fn new() -> Self {
        Self::default()
    }
    /// Set the page size.
    pub fn limit(mut self, limit: u32) -> Self {
        self.limit = Some(limit);
        self
    }
    /// Set the cursor.
    pub fn cursor(mut self, cursor: impl Into<String>) -> Self {
        self.cursor = Some(cursor.into());
        self
    }
    /// Filter by status.
    pub fn status(mut self, status: impl Into<String>) -> Self {
        self.status = Some(status.into());
        self
    }
}

/// Query parameters for `GET /api/v1/address`.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct AddressListParams {
    /// Page size, 1..=200.
    pub limit: Option<u32>,
    /// An opaque cursor.
    pub cursor: Option<String>,
    /// `active`, `paused`, `disabled` or `expired` (anything else is a 400).
    pub status: Option<String>,
    /// Exact match on the ticker as supplied at create time.
    pub ticker: Option<String>,
}

impl AddressListParams {
    /// Empty parameters.
    pub fn new() -> Self {
        Self::default()
    }
    /// Set the page size.
    pub fn limit(mut self, limit: u32) -> Self {
        self.limit = Some(limit);
        self
    }
    /// Set the cursor.
    pub fn cursor(mut self, cursor: impl Into<String>) -> Self {
        self.cursor = Some(cursor.into());
        self
    }
    /// Filter by status.
    pub fn status(mut self, status: impl Into<String>) -> Self {
        self.status = Some(status.into());
        self
    }
    /// Filter by ticker.
    pub fn ticker(mut self, ticker: impl Into<String>) -> Self {
        self.ticker = Some(ticker.into());
        self
    }
}

/// The list envelope every list endpoint returns.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Page<T> {
    /// The page, newest first.
    pub data: Vec<T>,
    /// A fact, not a guess. Never infer it from `data.len()`.
    pub has_more: bool,
    /// Pass back as `cursor` for the next page. `None` exactly when `has_more` is false.
    #[serde(default)]
    pub next_cursor: Option<String>,
}

/// A forwarding address: a standing deposit address that sweeps to your wallet.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Forward {
    /// The canonical id. Read this one.
    pub id: String,
    /// Deprecated alias of `id`, same value.
    #[serde(default)]
    pub forward_id: Option<String>,
    /// The ticker exactly as supplied at create time.
    pub ticker: String,
    /// Settlement backend.
    #[serde(default)]
    pub provider: Option<String>,
    /// `None` for BTC and BTCPay-settled coins.
    #[serde(default)]
    pub chain: Option<String>,
    /// The deposit address to hand to payers.
    pub address: String,
    /// Your wallet. Immutable.
    pub destination: String,
    /// Receives `forward.*` events.
    #[serde(default)]
    pub callback_url: Option<String>,
    /// Confirmations before a payment is credited.
    #[serde(default)]
    pub confirmations_required: Option<u32>,
    /// A DISPLAY-unit decimal string (not base units), e.g. `"0.500000"`.
    #[serde(default, deserialize_with = "opt_money")]
    pub min_forward: Option<String>,
    /// State.
    pub status: ForwardStatus,
    /// Only set for single-session BTCPay coins.
    #[serde(default)]
    pub expires_at: Option<String>,
    /// ISO-8601.
    pub created_at: String,
    /// ISO-8601, or `None` if never patched.
    #[serde(default)]
    pub updated_at: Option<String>,
    /// Fields this SDK version does not model.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `GET /api/v1/address/{id}`: the forward plus its capped payment and callback logs.
/// Derefs to [`Forward`].
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ForwardDetail {
    /// Newest first, capped at 200.
    #[serde(default)]
    pub payments: Vec<ForwardPayment>,
    /// True when the 200 cap truncated `payments`.
    #[serde(default)]
    pub payments_has_more: bool,
    /// Newest first, capped at 50.
    #[serde(default)]
    pub callbacks: Vec<ForwardCallback>,
    /// True when the 50 cap truncated `callbacks`.
    #[serde(default)]
    pub callbacks_has_more: bool,
    /// The forward's own fields.
    #[serde(flatten)]
    pub forward: Forward,
}

impl Deref for ForwardDetail {
    type Target = Forward;
    fn deref(&self) -> &Forward {
        &self.forward
    }
}

/// One deposit credited to a forwarding address.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ForwardPayment {
    /// Inbound transaction.
    pub txid_in: String,
    /// Gross received, base units.
    #[serde(deserialize_with = "money")]
    pub value: String,
    /// `seen`, `confirmed`, `forwarding`, `sent`, `failed`, `needs_review`, `orphaned`, `invalidated`.
    pub status: String,
    /// Outbound transaction, once broadcast.
    #[serde(default)]
    pub txid_out: Option<String>,
    /// Confirmations on the inbound transaction.
    #[serde(default)]
    pub confirmations: u64,
    /// ISO-8601.
    pub created_at: String,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// One webhook delivery attempt for a forward.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ForwardCallback {
    /// Event name.
    pub event: String,
    /// Target URL.
    pub url: String,
    /// `pending`, `delivered`, `failed`.
    pub status: String,
    /// Attempts so far.
    #[serde(default)]
    pub attempts: u64,
    /// The receiver's last HTTP status.
    #[serde(default)]
    pub last_status: Option<u16>,
    /// Last error, truncated.
    #[serde(default)]
    pub last_error: Option<String>,
    /// ISO-8601.
    pub created_at: String,
    /// ISO-8601.
    #[serde(default)]
    pub delivered_at: Option<String>,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `POST /api/v1/address` body.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
pub struct CreateAddress {
    /// e.g. `btc`, or `usdc_base` for a non-default chain. See [`Ticker::ticker`].
    pub ticker: String,
    /// Your wallet.
    pub destination: String,
    /// Receives `forward.*` events. Part of the forward's identity.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub callback: Option<String>,
    /// Confirmations before crediting.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub confirmations: Option<u32>,
    /// Arbitrary metadata.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub metadata: Option<JsonObject>,
    /// Extra body fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `PATCH /api/v1/address/{id}` body. Only `status` and `callback_url` are mutable.
#[derive(Debug, Clone, Default, PartialEq, Serialize)]
pub struct UpdateAddress {
    /// `Active` or `Paused`.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub status: Option<ForwardStatus>,
    /// `None` leaves it alone; `Some(None)` clears it; `Some(Some(url))` sets it.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub callback_url: Option<Option<String>>,
}

/// One `(asset, chain)` pair offered for forwarding.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Ticker {
    /// The string to pass as `ticker` when creating a forward.
    pub ticker: String,
    /// Settlement backend.
    #[serde(default)]
    pub provider: Option<String>,
    /// Chain, or `None`.
    #[serde(default)]
    pub chain: Option<String>,
    /// Display name.
    #[serde(default)]
    pub name: String,
    /// Base units per whole coin, as a power of ten.
    pub decimals: u32,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `POST /api/v1/webhooks/secret/rotate` result. `Debug` masks `secret`.
#[derive(Clone, PartialEq, Serialize, Deserialize)]
pub struct WebhookSecretRotated {
    /// The new `whsec_...` secret, shown ONCE. `None` on an `Idempotency-Key` replay.
    #[serde(default)]
    pub secret: Option<String>,
    /// ISO-8601.
    pub rotated_at: String,
    /// Until this instant, verify against both the new and the previous secret.
    #[serde(default)]
    pub prev_expires_at: Option<String>,
    /// Grace window length in seconds (86400 by default).
    #[serde(default)]
    pub grace_seconds: u64,
    /// True when this rotation retired a previous secret that was still in its grace window.
    #[serde(default)]
    pub replaced_previous: bool,
    /// Human note. Do not branch on it.
    #[serde(default)]
    pub message: String,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

impl fmt::Debug for WebhookSecretRotated {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("WebhookSecretRotated")
            .field("secret", &self.secret.as_ref().map(|_| "[redacted]"))
            .field("rotated_at", &self.rotated_at)
            .field("prev_expires_at", &self.prev_expires_at)
            .field("grace_seconds", &self.grace_seconds)
            .field("replaced_previous", &self.replaced_previous)
            .field("message", &self.message)
            .finish()
    }
}

/// `GET /api/v1/webhooks/secret`: rotation metadata, never the secret.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct WebhookSecretState {
    /// When the secret was last rotated.
    #[serde(default)]
    pub rotated_at: Option<String>,
    /// When the previous secret stops being acceptable.
    #[serde(default)]
    pub prev_expires_at: Option<String>,
    /// True while the previous secret is still inside its grace window.
    #[serde(default)]
    pub prev_active: bool,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `POST /api/v1/invoices/bulk` result.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BulkResult {
    /// Always true on success.
    #[serde(default)]
    pub ok: bool,
    /// `cancel`.
    #[serde(default)]
    pub action: String,
    /// How many ids were submitted.
    pub requested: u64,
    /// How many were actually cancelled.
    pub cancelled: u64,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `data` of `invoice.*`, `sweep.sent` and `test` events. A narrow projection: no asset or chain.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct InvoiceEventData {
    /// Invoice id.
    pub id: String,
    /// Your reference.
    #[serde(default)]
    pub reference: Option<String>,
    /// Status at the moment of the event.
    pub status: InvoiceStatus,
    /// Base units.
    #[serde(deserialize_with = "money")]
    pub amount_expected: String,
    /// Base units.
    #[serde(deserialize_with = "money")]
    pub amount_received: String,
    /// Deposit address.
    #[serde(default)]
    pub address: String,
    /// ISO-8601.
    #[serde(default)]
    pub paid_at: Option<String>,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}

/// `data` of every `forward.*` event (CryptAPI-shaped field names).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ForwardEventData {
    /// The forward this payment belongs to.
    pub forward_id: String,
    /// Ticker.
    pub ticker: String,
    /// Deposit address.
    pub address_in: String,
    /// Destination.
    pub address_out: String,
    /// Inbound transaction.
    pub txid_in: String,
    /// Outbound transaction, on `forward.sent`.
    #[serde(default)]
    pub txid_out: Option<String>,
    /// Gross received, base units.
    #[serde(deserialize_with = "money")]
    pub value: String,
    /// Net sent, base units.
    #[serde(deserialize_with = "money")]
    pub value_forwarded: String,
    /// Fee retained, base units.
    #[serde(deserialize_with = "money")]
    pub fee: String,
    /// Confirmations.
    #[serde(default)]
    pub confirmations: u64,
    /// True only on `forward.detected`.
    #[serde(default)]
    pub pending: bool,
    /// Machine-readable cause on `forward.deferred` / `reversed` / `failed`.
    #[serde(default)]
    pub reason: Option<String>,
    /// Human sentence for `reason`. Never branch on it.
    #[serde(default)]
    pub detail: Option<String>,
    /// Unmodelled fields.
    #[serde(flatten)]
    pub extra: JsonObject,
}
