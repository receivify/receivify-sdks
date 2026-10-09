// Typed models for the resources this SDK wraps.
//
// ## Two rules run through every struct here
//
// 1. **Every monetary field is a `std::string`.** Not a double, not a long long, not a decimal
//    class. `amount_expected`, `amount_received`, `value`, `fee`, `value_forwarded` — all
//    `std::string`, holding the exact-integer base-unit text the API sent. A satoshi count above
//    2^53 does not survive an IEEE-754 double, and a 256-bit wei value is not close to surviving
//    an `int64_t`. `units::format_units()` converts for DISPLAY; nothing converts for logic.
//    Two decimal-string fields (`Forward::min_forward`, `Invoice::amount_fiat`) are called out
//    individually below — they are decimal rather than base-unit, but they are still strings.
//
// 2. **Every enum-ish field is a `std::string`.** `status`, `provider`, `event` are documented as
//    OPEN enums: adding a value is an additive change the API may make at any time. A C++ `enum
//    class` would turn that into a crash or a silent misread in an older SDK. Compare against the
//    constants in `receivify::status`, and keep a default branch.
//
// Every model also carries `raw` — the parsed `Json` it came from — so a field added after this
// SDK shipped is one `model.raw["new_field"]` away and never requires an SDK upgrade.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "receivify/json.hpp"

namespace receivify {

/// Documented status values. Open enums: treat an unrecognised value as "something new", not as
/// an error.
namespace status {
inline constexpr const char* kPending = "pending";
inline constexpr const char* kPartial = "partial";
inline constexpr const char* kConfirming = "confirming";
inline constexpr const char* kPaid = "paid";
inline constexpr const char* kSwept = "swept";
inline constexpr const char* kExpired = "expired";
inline constexpr const char* kPartialExpired = "partial_expired";
inline constexpr const char* kCancelled = "cancelled";
inline constexpr const char* kFailed = "failed";

inline constexpr const char* kActive = "active";
inline constexpr const char* kPaused = "paused";
inline constexpr const char* kDisabled = "disabled";
}  // namespace status

namespace detail {

/// `null` in JSON becomes an absent `optional`, which is exactly what the spec's `[string, null]`
/// unions mean.
inline std::optional<std::string> opt_string(const Json& parent, const std::string& key) {
  const Json& v = parent[key];
  if (v.is_string() || v.is_number()) return v.as_string();
  return std::nullopt;
}

inline std::string req_string(const Json& parent, const std::string& key) {
  return parent[key].as_string_or("");
}

}  // namespace detail

/* ==================================================================== *
 * Invoices
 * ==================================================================== */

/// A one-off payment request. Identical on create, retrieve, list and cancel.
struct Invoice {
  std::string id;
  std::optional<std::string> reference;  ///< Your business key. ALSO a de-duplication key on create.
  std::string asset;                     ///< Lower-case symbol: `btc`, `usdc`.

  /// **Exact-integer base units, as a string.** Satoshis for BTC, wei for ETH.
  std::string amount_expected;
  /// Exact-integer base units, as a string. `"0"` until something arrives.
  std::string amount_received;
  /// **A DECIMAL string** (`"25.00"`), not base units. The fiat price captured at create time —
  /// display information only. Never settle against it; `amount_expected` is authoritative.
  std::optional<std::string> amount_fiat;
  std::optional<std::string> fiat_currency;

  std::optional<std::string> chain;
  std::optional<std::string> provider;  ///< `bitcoind`, `btcpay`, `evm`, `solana`, `tron`, or absent.
  std::string address;                  ///< The deposit address. Exactly this asset, exactly this chain.
  std::string status;                   ///< Open enum — see `receivify::status`.
  std::string expires_at;
  std::optional<std::string> paid_at;
  std::string created_at;
  Json metadata;  ///< Always an object; an omitted `metadata` comes back as `{}`.
  std::string checkout_url;

  /// Decimal places of this asset — the divisor for `amount_expected`. Per-token on EVM. 0 if absent.
  std::int64_t decimals = 0;
  /// `amount_expected` already divided by `10^decimals`, as a decimal STRING. Display only.
  std::optional<std::string> amount_decimal;
  /// Wallet-scannable URI for a QR code; absent when the chain has no safe one (show `address`).
  std::optional<std::string> payment_uri;
  /// Whether the commission was added ON TOP of the requested amount.
  bool add_fee_ontop = false;
  /// Base units, as a string: what you asked to RECEIVE.
  std::optional<std::string> amount_net_requested;
  /// Base units, as a string: what the gross-up added. `"0"` when `add_fee_ontop` is false.
  std::optional<std::string> fee_ontop;

  Json raw;

  static Invoice from_json(const Json& j) {
    Invoice inv;
    inv.id = detail::req_string(j, "id");
    inv.reference = detail::opt_string(j, "reference");
    inv.asset = detail::req_string(j, "asset");
    inv.amount_expected = detail::req_string(j, "amount_expected");
    inv.amount_received = detail::req_string(j, "amount_received");
    inv.amount_fiat = detail::opt_string(j, "amount_fiat");
    inv.fiat_currency = detail::opt_string(j, "fiat_currency");
    inv.chain = detail::opt_string(j, "chain");
    inv.provider = detail::opt_string(j, "provider");
    inv.address = detail::req_string(j, "address");
    inv.status = detail::req_string(j, "status");
    inv.expires_at = detail::req_string(j, "expires_at");
    inv.paid_at = detail::opt_string(j, "paid_at");
    inv.created_at = detail::req_string(j, "created_at");
    inv.metadata = j["metadata"].is_object() ? j["metadata"] : Json::object();
    inv.checkout_url = detail::req_string(j, "checkout_url");
    inv.decimals = j["decimals"].as_int64_or(0);
    inv.amount_decimal = detail::opt_string(j, "amount_decimal");
    inv.payment_uri = detail::opt_string(j, "payment_uri");
    inv.add_fee_ontop = j["add_fee_ontop"].as_bool_or(false);
    inv.amount_net_requested = detail::opt_string(j, "amount_net_requested");
    inv.fee_ontop = detail::opt_string(j, "fee_ontop");
    inv.raw = j;
    return inv;
  }
};

/* ==================================================================== *
 * Forwarding addresses
 * ==================================================================== */

/// A standing deposit address that sweeps everything it receives to `destination`.
struct Forward {
  /// **The canonical identifier.** Read this one.
  std::string id;
  /// Deprecated alias for `id`, carrying the identical value. It is not going away — removing a
  /// response field would be a breaking change — but new code should read `id`. (Note that
  /// `forward_id` inside a `forward.*` webhook payload is a DIFFERENT thing and is not
  /// deprecated: there it names a relationship, not the payload's own identity.)
  std::string forward_id;

  std::string ticker;  ///< As you supplied it at create time, not the canonical form.
  std::string provider;
  std::optional<std::string> chain;
  std::string address;      ///< The deposit address to hand to payers.
  std::string destination;  ///< Your wallet. IMMUTABLE — `PATCH` refuses to change it.
  std::optional<std::string> callback_url;
  std::int64_t confirmations_required = 0;

  /// **The one money field on this object that is NOT base units.** A DISPLAY-UNIT decimal string
  /// (`"0.500000"`). Multiply by `10 ** decimals` — or use `units::parse_units` — before
  /// comparing it with a `ForwardPayment::value`.
  std::string min_forward;

  std::string status;  ///< `active`, `paused`, `disabled`, `expired`. Open enum.
  std::optional<std::string> expires_at;
  std::string created_at;
  std::optional<std::string> updated_at;

  Json raw;

  static Forward from_json(const Json& j) {
    Forward f;
    f.id = detail::req_string(j, "id");
    f.forward_id = detail::req_string(j, "forward_id");
    f.ticker = detail::req_string(j, "ticker");
    f.provider = detail::req_string(j, "provider");
    f.chain = detail::opt_string(j, "chain");
    f.address = detail::req_string(j, "address");
    f.destination = detail::req_string(j, "destination");
    f.callback_url = detail::opt_string(j, "callback_url");
    f.confirmations_required = j["confirmations_required"].as_int64_or(0);
    f.min_forward = detail::req_string(j, "min_forward");
    f.status = detail::req_string(j, "status");
    f.expires_at = detail::opt_string(j, "expires_at");
    f.created_at = detail::req_string(j, "created_at");
    f.updated_at = detail::opt_string(j, "updated_at");
    f.raw = j;
    return f;
  }
};

/// One deposit credited to a forwarding address.
struct ForwardPayment {
  std::string txid_in;
  std::string value;  ///< Gross received, exact-integer base units, as a string.
  std::string status;  ///< `seen`, `confirmed`, `forwarding`, `sent`, `failed`, `needs_review`, `orphaned`, `invalidated`.
  std::optional<std::string> txid_out;
  std::int64_t confirmations = 0;
  std::string created_at;
  Json raw;

  static ForwardPayment from_json(const Json& j) {
    ForwardPayment p;
    p.txid_in = detail::req_string(j, "txid_in");
    p.value = detail::req_string(j, "value");
    p.status = detail::req_string(j, "status");
    p.txid_out = detail::opt_string(j, "txid_out");
    p.confirmations = j["confirmations"].as_int64_or(0);
    p.created_at = detail::req_string(j, "created_at");
    p.raw = j;
    return p;
  }
};

/// One webhook delivery attempt for a forward. Delivery status only — no signature material and
/// no body is ever exposed here.
struct ForwardCallback {
  std::string event;
  std::string url;
  std::string status;  ///< `pending`, `delivered`, `failed`.
  std::int64_t attempts = 0;
  std::optional<std::int64_t> last_status;
  std::optional<std::string> last_error;
  std::string created_at;
  std::optional<std::string> delivered_at;
  Json raw;

  static ForwardCallback from_json(const Json& j) {
    ForwardCallback c;
    c.event = detail::req_string(j, "event");
    c.url = detail::req_string(j, "url");
    c.status = detail::req_string(j, "status");
    c.attempts = j["attempts"].as_int64_or(0);
    if (j["last_status"].is_number()) c.last_status = j["last_status"].as_int64();
    c.last_error = detail::opt_string(j, "last_error");
    c.created_at = detail::req_string(j, "created_at");
    c.delivered_at = detail::opt_string(j, "delivered_at");
    c.raw = j;
    return c;
  }
};

/// `GET /api/v1/address/{id}` only. The `Forward` fields are SPREAD at the top level with four
/// extra keys, so this contains a `Forward` rather than sitting beside one.
struct ForwardDetail {
  Forward forward;
  /// Newest first, capped at 200. Not paginated — there is no cursor to reach the rest.
  std::vector<ForwardPayment> payments;
  bool payments_has_more = false;
  /// Newest first, capped at 50. Not paginated.
  std::vector<ForwardCallback> callbacks;
  bool callbacks_has_more = false;
  Json raw;

  static ForwardDetail from_json(const Json& j) {
    ForwardDetail d;
    d.forward = Forward::from_json(j);
    for (const Json& p : j["payments"].values()) d.payments.push_back(ForwardPayment::from_json(p));
    d.payments_has_more = j["payments_has_more"].as_bool_or(false);
    for (const Json& c : j["callbacks"].values()) d.callbacks.push_back(ForwardCallback::from_json(c));
    d.callbacks_has_more = j["callbacks_has_more"].as_bool_or(false);
    d.raw = j;
    return d;
  }
};

/* ==================================================================== *
 * Catalogue
 * ==================================================================== */

/// One `(asset, chain)` pair offered for forwarding.
struct Ticker {
  /// The string to pass as `ticker` when creating a forward. A bare symbol resolves to that
  /// asset's default chain; the `<symbol>_<chain>` form selects another one.
  std::string ticker;
  std::string provider;
  std::optional<std::string> chain;
  std::string name;
  /// Base units per whole coin, as a power of ten. This is the number to hand `format_units`.
  std::int64_t decimals = 0;
  Json raw;

  static Ticker from_json(const Json& j) {
    Ticker t;
    t.ticker = detail::req_string(j, "ticker");
    t.provider = detail::req_string(j, "provider");
    t.chain = detail::opt_string(j, "chain");
    t.name = detail::req_string(j, "name");
    t.decimals = j["decimals"].as_int64_or(0);
    t.raw = j;
    return t;
  }
};

/* ==================================================================== *
 * Webhook secret rotation
 * ==================================================================== */

/// Rotation metadata. Carries NO secret material — readable with a plain API key.
struct WebhookSecretState {
  std::optional<std::string> rotated_at;
  std::optional<std::string> prev_expires_at;
  /// True while a previous secret is still inside its grace window. **When true, your receiver
  /// must accept EITHER secret** — this is the live answer to "can I drop the old one yet?".
  bool prev_active = false;
  Json raw;

  static WebhookSecretState from_json(const Json& j) {
    WebhookSecretState s;
    s.rotated_at = detail::opt_string(j, "rotated_at");
    s.prev_expires_at = detail::opt_string(j, "prev_expires_at");
    s.prev_active = j["prev_active"].as_bool_or(false);
    s.raw = j;
    return s;
  }
};

/// The result of a rotation.
struct WebhookSecretRotated {
  /// The raw `whsec_…` signing secret, shown ONCE. **Absent on an `Idempotency-Key` replay** —
  /// the replay still carries the same `rotated_at` and `prev_expires_at`, so a client that timed
  /// out can tell what happened; if the secret itself was lost, rotate again.
  std::optional<std::string> secret;
  std::string rotated_at;
  /// Until this instant, verify against the NEW secret and fall back to the previous one.
  std::optional<std::string> prev_expires_at;
  std::int64_t grace_seconds = 0;
  /// True when this rotation retired a previous secret that was STILL inside its grace window.
  /// At most two secrets are ever valid at once, so any receiver still on that older one starts
  /// failing now.
  bool replaced_previous = false;
  std::string message;  ///< Informational. Do not branch on it.
  Json raw;

  static WebhookSecretRotated from_json(const Json& j) {
    WebhookSecretRotated r;
    r.secret = detail::opt_string(j, "secret");
    r.rotated_at = detail::req_string(j, "rotated_at");
    r.prev_expires_at = detail::opt_string(j, "prev_expires_at");
    r.grace_seconds = j["grace_seconds"].as_int64_or(0);
    r.replaced_previous = j["replaced_previous"].as_bool_or(false);
    r.message = detail::req_string(j, "message");
    r.raw = j;
    return r;
  }
};

/* ==================================================================== *
 * Webhook payloads
 * ==================================================================== */

/// `data` for `invoice.*`, `sweep.sent` and `test`.
///
/// A deliberately narrow projection: it carries NO `asset` and NO `chain`, so a multi-coin
/// merchant must call `get_invoice(id)` to learn which coin the event is about.
struct InvoiceEventData {
  std::string id;
  std::optional<std::string> reference;
  std::string status;
  std::string amount_expected;  ///< Exact-integer base units, as a string.
  std::string amount_received;  ///< Exact-integer base units, as a string.
  std::string address;
  std::optional<std::string> paid_at;
  Json raw;

  static InvoiceEventData from_json(const Json& j) {
    InvoiceEventData d;
    d.id = detail::req_string(j, "id");
    d.reference = detail::opt_string(j, "reference");
    d.status = detail::req_string(j, "status");
    d.amount_expected = detail::req_string(j, "amount_expected");
    d.amount_received = detail::req_string(j, "amount_received");
    d.address = detail::req_string(j, "address");
    d.paid_at = detail::opt_string(j, "paid_at");
    d.raw = j;
    return d;
  }
};

/// `data` for `forward.confirmed` and `forward.sent`.
///
/// CryptAPI-shaped: the field names are inherited from that convention and differ from the REST
/// resource — `address_in` is the REST `address`, `address_out` is the REST `destination`.
struct ForwardEventData {
  /// The forward this payment belongs to. NOT deprecated here: inside a webhook payload this
  /// names a relationship rather than the payload's own identity.
  std::string forward_id;
  std::string ticker;
  std::string address_in;
  std::string address_out;
  std::string txid_in;
  std::optional<std::string> txid_out;  ///< Absent on `forward.confirmed`, present on `forward.sent`.
  std::string value;            ///< Gross received, exact-integer base units, as a string.
  std::string value_forwarded;  ///< Net sent to the destination, exact-integer base units.
  std::string fee;              ///< Fee retained, exact-integer base units.
  std::int64_t confirmations = 0;
  /// **Dead field.** Hard-coded `false` on every forward event ever sent. Present only because
  /// removing it would be a breaking change. Do not branch on it.
  bool pending = false;
  Json raw;

  static ForwardEventData from_json(const Json& j) {
    ForwardEventData d;
    d.forward_id = detail::req_string(j, "forward_id");
    d.ticker = detail::req_string(j, "ticker");
    d.address_in = detail::req_string(j, "address_in");
    d.address_out = detail::req_string(j, "address_out");
    d.txid_in = detail::req_string(j, "txid_in");
    d.txid_out = detail::opt_string(j, "txid_out");
    d.value = detail::req_string(j, "value");
    d.value_forwarded = detail::req_string(j, "value_forwarded");
    d.fee = detail::req_string(j, "fee");
    d.confirmations = j["confirmations"].as_int64_or(0);
    d.pending = j["pending"].as_bool_or(false);
    d.raw = j;
    return d;
  }
};

/// A raw `Json` passthrough, for list endpoints this SDK does not model as a struct.
struct RawObject {
  Json raw;
  static RawObject from_json(const Json& j) { return RawObject{j}; }
};

}  // namespace receivify
