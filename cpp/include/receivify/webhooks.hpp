// Webhook signature verification and envelope parsing.
//
// ## Use the RAW body. Always.
//
// The signature is `HMAC-SHA256(secret, "<t>." + rawBody)` over the EXACT bytes Receivify sent.
// If you hand these functions a re-serialised body — your framework parsed the JSON and you
// dumped it back out — key order, whitespace and number formatting all change, and verification
// will fail for a perfectly genuine delivery. Capture the body as a `std::string` of bytes
// BEFORE anything parses it.
//
// ## Verify against a LIST of secrets.
//
// `POST /api/v1/webhooks/secret/rotate` mints a new secret and keeps the previous one acceptable
// until `prev_expires_at` (24h by default), so during a rotation TWO secrets are legitimately
// valid at once. Receivify always signs with the current one; the grace window is an instruction
// to your receiver. Pass both, and drop the old one once `GET /api/v1/webhooks/secret` reports
// `prev_active: false`.
#pragma once

#include <chrono>
#include <cstdint>
#include <string>
#include <vector>

#include "receivify/detail/util.hpp"
#include "receivify/errors.hpp"
#include "receivify/hmac.hpp"
#include "receivify/json.hpp"

namespace receivify {

/// The header Receivify signs every delivery with.
inline const char* signature_header_name() noexcept { return "X-Receivify-Signature"; }

/// Default replay window, in seconds.
inline constexpr std::int64_t kDefaultToleranceSeconds = 300;

/// The complete, frozen `EventName` set. Receivers MUST ignore names they do not recognise, which
/// is why the SDK models `event` as a `std::string` and this list is informational only.
inline const std::vector<std::string>& event_names() {
  static const std::vector<std::string> kEvents = {
      "invoice.confirming", "invoice.partial",         "invoice.paid",      "invoice.swept",
      "invoice.expired",    "invoice.partial_expired", "invoice.cancelled", "invoice.failed",
      "sweep.sent",         "forward.confirmed",       "forward.sent",      "test"};
  return kEvents;
}

struct VerifyOptions {
  /// Replay window in seconds. `0` disables the timestamp check entirely — do not do that in
  /// production; it is here for replaying archived fixtures.
  std::int64_t tolerance_seconds = kDefaultToleranceSeconds;

  /// Current time in epoch SECONDS. `0` means "ask the system clock". Exists so tests are
  /// deterministic and so a caller with a trusted clock source can supply it.
  std::int64_t now_seconds = 0;
};

/// The result of a verification attempt, with a machine-readable reason for your logs.
struct VerificationResult {
  bool ok = false;
  WebhookRejection reason = WebhookRejection::kNoMatch;
  std::string message;
  std::int64_t timestamp = 0;  ///< The `t=` value, once the header parsed. 0 otherwise.

  explicit operator bool() const noexcept { return ok; }
};

/// The unified webhook envelope: `{ event, sequence, created_at, data }` for BOTH `invoice.*`
/// and `forward.*`. `event` is the discriminator — switch on it first. `data` is left as `Json`
/// because the two families carry structurally different payloads; `models.hpp` gives you
/// `InvoiceEventData` / `ForwardEventData` when you know which you have.
struct WebhookEvent {
  std::string event;
  std::int64_t sequence = 0;
  std::string created_at;
  Json data;
  Json raw;  ///< The whole envelope, for fields added after this SDK shipped.
};

/// True for `invoice.*`, `sweep.sent` and `test` — the events whose `data` is invoice-shaped.
inline bool is_invoice_event(const std::string& event) noexcept {
  return event.rfind("invoice.", 0) == 0 || event == "sweep.sent" || event == "test";
}

/// True for `forward.confirmed` / `forward.sent` — the events whose `data` is forward-shaped.
inline bool is_forward_event(const std::string& event) noexcept { return event.rfind("forward.", 0) == 0; }

namespace detail {

/// A parsed `t=<unix>,v1=<hex>` header. Multiple `v1=` values are tolerated (the format allows
/// for a future multi-signature rollout); the FIRST `t=` wins.
struct ParsedSignature {
  std::string raw_timestamp;  ///< The exact characters that go into the HMAC, not the parsed number.
  std::int64_t timestamp = 0;
  std::vector<std::string> signatures;
};

inline bool parse_signature_header(const std::string& header, ParsedSignature& out) {
  if (header.empty()) return false;
  bool have_t = false;
  std::size_t pos = 0;
  while (pos <= header.size()) {
    const std::size_t comma = header.find(',', pos);
    const std::string part = header.substr(pos, comma == std::string::npos ? std::string::npos : comma - pos);
    const std::size_t eq = part.find('=');
    if (eq != std::string::npos) {
      const std::string key = trim(part.substr(0, eq));
      const std::string value = trim(part.substr(eq + 1));
      if (key == "t" && !have_t) {
        // Digits only. The signed string uses the RAW characters, so `007` and `7` are different
        // messages and we must not normalise.
        if (value.empty()) return false;
        for (char c : value) {
          if (c < '0' || c > '9') return false;
        }
        std::int64_t ts = 0;
        if (!parse_int64(value, ts)) return false;
        out.raw_timestamp = value;
        out.timestamp = ts;
        have_t = true;
      } else if (key == "v1" && !value.empty()) {
        out.signatures.push_back(value);
      }
    }
    if (comma == std::string::npos) break;
    pos = comma + 1;
  }
  return have_t && !out.signatures.empty();
}

inline std::int64_t system_epoch_seconds() {
  using namespace std::chrono;
  return duration_cast<seconds>(system_clock::now().time_since_epoch()).count();
}

/// Drop empties. Accepts the single-secret case by construction — the caller wraps a lone string
/// in a one-element vector — so the ARRAY signature is the only signature there is.
inline std::vector<std::string> normalise_secrets(const std::vector<std::string>& secrets) {
  std::vector<std::string> out;
  out.reserve(secrets.size());
  for (const auto& s : secrets) {
    if (!s.empty()) out.push_back(s);
  }
  return out;
}

}  // namespace detail

/// Verify a delivery and say why if it failed.
///
/// Timing discipline: once the header and the replay window pass, EVERY secret is tried against
/// EVERY candidate signature with no early exit, and every comparison is constant-time. Neither
/// how many secrets you configured nor which one matched is observable from the outside.
inline VerificationResult describe_verification(const std::string& raw_body,
                                                const std::string& signature_header,
                                                const std::vector<std::string>& secrets,
                                                const Hmac& hmac,
                                                const VerifyOptions& options = {}) {
  VerificationResult result;

  const std::vector<std::string> list = detail::normalise_secrets(secrets);
  if (list.empty()) {
    result.reason = WebhookRejection::kNoSecrets;
    result.message = "No webhook secret configured";
    return result;
  }

  detail::ParsedSignature parsed;
  if (!detail::parse_signature_header(signature_header, parsed)) {
    result.reason = WebhookRejection::kBadHeader;
    result.message = std::string("Missing or malformed ") + signature_header_name() +
                     " header (expected \"t=<unix>,v1=<hex>\")";
    return result;
  }
  result.timestamp = parsed.timestamp;

  if (options.tolerance_seconds > 0) {
    const std::int64_t now =
        options.now_seconds != 0 ? options.now_seconds : detail::system_epoch_seconds();
    const std::int64_t skew = now > parsed.timestamp ? now - parsed.timestamp : parsed.timestamp - now;
    if (skew > options.tolerance_seconds) {
      result.reason = WebhookRejection::kStaleTimestamp;
      result.message = "Webhook timestamp " + parsed.raw_timestamp + " is outside the " +
                       std::to_string(options.tolerance_seconds) + "s tolerance window";
      return result;
    }
  }

  const std::string signed_payload = parsed.raw_timestamp + "." + raw_body;

  bool matched = false;
  for (const std::string& secret : list) {
    const Sha256Digest expected = hmac.hmac_sha256(secret, signed_payload);
    for (const std::string& candidate_hex : parsed.signatures) {
      std::vector<unsigned char> candidate;
      // A malformed or wrong-length hex string is not secret information, so bailing here leaks
      // nothing an attacker does not already know about their own input.
      if (!detail::hex_decode(candidate_hex, candidate)) continue;
      if (candidate.size() != expected.size()) continue;
      if (hmac.constant_time_equals(expected.data(), candidate.data(), expected.size())) matched = true;
    }
  }

  if (matched) {
    result.ok = true;
    result.message = "ok";
  } else {
    result.reason = WebhookRejection::kNoMatch;
    result.message = "Webhook signature did not match any configured secret";
  }
  return result;
}

/// Verify a signed delivery. Returns a bool and never throws on a bad signature.
///
/// @param raw_body         the EXACT request body bytes
/// @param signature_header the value of `X-Receivify-Signature`
/// @param secrets          the rotation list: current secret first, previous second
inline bool verify_webhook(const std::string& raw_body,
                           const std::string& signature_header,
                           const std::vector<std::string>& secrets,
                           const Hmac& hmac,
                           const VerifyOptions& options = {}) {
  return describe_verification(raw_body, signature_header, secrets, hmac, options).ok;
}

/// Verify and parse in one step. Throws `WebhookError` — carrying a `WebhookRejection` — on any
/// failure, so there is no way to read `data` off a delivery that was not authenticated.
inline WebhookEvent parse_webhook(const std::string& raw_body,
                                  const std::string& signature_header,
                                  const std::vector<std::string>& secrets,
                                  const Hmac& hmac,
                                  const VerifyOptions& options = {}) {
  const VerificationResult verified = describe_verification(raw_body, signature_header, secrets, hmac, options);
  if (!verified.ok) throw WebhookError(verified.message, verified.reason);

  Json envelope;
  try {
    envelope = json_parse(raw_body);
  } catch (const JsonError& e) {
    throw WebhookError(std::string("Webhook body is not valid JSON: ") + e.what(), WebhookRejection::kBadJson);
  }
  if (!envelope.is_object() || !envelope["event"].is_string()) {
    throw WebhookError("Webhook body is not a { event, sequence, created_at, data } envelope",
                       WebhookRejection::kBadEnvelope);
  }

  WebhookEvent ev;
  ev.event = envelope["event"].as_string();
  ev.sequence = envelope["sequence"].as_int64_or(0);
  ev.created_at = envelope["created_at"].as_string_or("");
  ev.data = envelope["data"];
  ev.raw = envelope;
  return ev;
}

/// Sign a body the way Receivify does.
///
/// Exposed for tests and local fixtures — you never need it to RECEIVE a webhook. Returns a
/// complete `t=<unix>,v1=<hex>` header value.
inline std::string sign_webhook(const std::string& raw_body,
                                const std::string& secret,
                                const Hmac& hmac,
                                std::int64_t timestamp = 0) {
  const std::int64_t t = timestamp != 0 ? timestamp : detail::system_epoch_seconds();
  const std::string ts = std::to_string(t);
  const Sha256Digest digest = hmac.hmac_sha256(secret, ts + "." + raw_body);
  return "t=" + ts + ",v1=" + detail::hex_encode(digest.data(), digest.size());
}

}  // namespace receivify
