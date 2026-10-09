// Typed exceptions for the frozen `ERROR_CODES` registry.
//
// Every API failure body is `{ "error": "<code>", "detail": "<sentence>" }`. Each code maps to a
// subclass of `ApiError`, and the raw code string is ALWAYS preserved — a code this SDK has never
// heard of throws a plain `ApiError` rather than crashing, so an old SDK keeps working against a
// newer deployment.
//
// Branch on `err.code()`, never on `err.detail()`: the wording of `detail` is explicitly not part
// of the frozen contract.
#pragma once

#include <exception>
#include <optional>
#include <stdexcept>
#include <string>
#include <utility>

#include "receivify/detail/util.hpp"

namespace receivify {

/// Base class for everything this SDK throws.
///
///     try { client.get_invoice(id); }
///     catch (const receivify::ReceivifyError& e) { log(e.what()); }
class ReceivifyError : public std::runtime_error {
 public:
  explicit ReceivifyError(const std::string& message) : std::runtime_error(message) {}
};

/// The SDK was assembled wrong: no transport, no HMAC provider, an empty API URL. Always a bug in
/// the calling program, never a runtime condition.
class ConfigurationError : public ReceivifyError {
 public:
  explicit ConfigurationError(const std::string& message) : ReceivifyError(message) {}
};

/// The request never produced an HTTP response: DNS, TLS, socket, or a client-side timeout.
class TransportError : public ReceivifyError {
 public:
  TransportError(const std::string& message, bool timed_out = false)
      : ReceivifyError(message), timed_out_(timed_out) {}

  /// True when the failure was a timeout rather than a connection error.
  bool timed_out() const noexcept { return timed_out_; }

 private:
  bool timed_out_;
};

/// The response body was not the JSON this SDK expected. Carries a byte offset when the failure
/// came from the parser.
class JsonError : public ReceivifyError {
 public:
  explicit JsonError(const std::string& message, std::size_t offset = 0)
      : ReceivifyError(message), offset_(offset) {}

  std::size_t offset() const noexcept { return offset_; }

 private:
  std::size_t offset_;
};

/// Why a webhook was rejected. Machine-readable — log it, alert on it, do not parse `what()`.
enum class WebhookRejection {
  kNoSecrets,       ///< No secret was configured, so nothing could be verified.
  kBadHeader,       ///< `X-Receivify-Signature` was missing or not `t=<unix>,v1=<hex>`.
  kStaleTimestamp,  ///< The `t=` value fell outside the replay tolerance window.
  kNoMatch,         ///< The HMAC matched none of the supplied secrets.
  kBadJson,         ///< The body verified but is not valid JSON.
  kBadEnvelope,     ///< Valid JSON, but not `{ event, sequence, created_at, data }`.
};

inline const char* to_string(WebhookRejection r) noexcept {
  switch (r) {
    case WebhookRejection::kNoSecrets: return "no_secrets";
    case WebhookRejection::kBadHeader: return "bad_header";
    case WebhookRejection::kStaleTimestamp: return "stale_timestamp";
    case WebhookRejection::kNoMatch: return "no_match";
    case WebhookRejection::kBadJson: return "bad_json";
    case WebhookRejection::kBadEnvelope: return "bad_envelope";
  }
  return "unknown";
}

/// A webhook could not be authenticated or parsed. Thrown by `parse_webhook`; never by
/// `verify_webhook`, which answers with a bool.
class WebhookError : public ReceivifyError {
 public:
  WebhookError(const std::string& message, WebhookRejection reason)
      : ReceivifyError(message), reason_(reason) {}

  WebhookRejection reason() const noexcept { return reason_; }
  const char* reason_string() const noexcept { return to_string(reason_); }

 private:
  WebhookRejection reason_;
};

/// Everything an `ApiError` knows about the failed exchange, minus the API key (which is never
/// stored here, never logged, and never placed in a URL).
struct ErrorContext {
  int status = 0;
  Headers headers;
  std::string raw_body;  ///< The undecoded response body, for logs and for unknown shapes.
  std::string request;   ///< `"GET /api/v1/invoices/…"`. Path only — no key, no query secrets.
};

/// An `{ error, detail }` body came back with a 4xx/5xx status.
///
/// This is ALSO the fallback for any error code this SDK does not recognise, so it is the class to
/// catch when you want "any API-level failure".
class ApiError : public ReceivifyError {
 public:
  ApiError(std::string code, std::string detail, ErrorContext ctx)
      : ReceivifyError(detail.empty() ? (code.empty() ? std::string("Receivify API error") : "Receivify: " + code)
                                      : detail),
        code_(std::move(code)),
        detail_(std::move(detail)),
        ctx_(std::move(ctx)) {}

  /// The raw `error` string from the body. Always preserved, even when unrecognised. May be empty
  /// if the body did not conform at all (a proxy's HTML 502, for instance).
  const std::string& code() const noexcept { return code_; }

  /// The human sentence from the body. For logs and developers, never for branching.
  const std::string& detail() const noexcept { return detail_; }

  int status() const noexcept { return ctx_.status; }
  const Headers& headers() const noexcept { return ctx_.headers; }
  const std::string& raw_body() const noexcept { return ctx_.raw_body; }
  const std::string& request() const noexcept { return ctx_.request; }

  /// True for the codes the API documents as worth retrying, plus any 5xx.
  bool retryable() const noexcept {
    return code_ == "rate_limited" || code_ == "internal" || code_ == "upstream_error" ||
           code_ == "rate_unavailable" || ctx_.status >= 500;
  }

 private:
  std::string code_;
  std::string detail_;
  ErrorContext ctx_;
};

// One subclass per frozen code. Written out with a macro so the list stays greppable and so
// adding a code later is a one-line change.
#define RECEIVIFY_DEFINE_API_ERROR(Name)                                                  \
  class Name : public ApiError {                                                          \
   public:                                                                                \
    Name(std::string code, std::string detail, ErrorContext ctx)                          \
        : ApiError(std::move(code), std::move(detail), std::move(ctx)) {}                 \
  }

/// 400 — malformed request: bad JSON, a missing field, or a value that failed validation.
RECEIVIFY_DEFINE_API_ERROR(BadRequestError);
/// 400 — the forwarding ticker is unknown, misspelled, or not offered by this deployment.
RECEIVIFY_DEFINE_API_ERROR(BadTickerError);
/// 400 — the destination address is not valid for that ticker/chain.
RECEIVIFY_DEFINE_API_ERROR(BadDestinationError);
/// 400 — the asset is enabled but its settlement backend cannot accept it right now.
RECEIVIFY_DEFINE_API_ERROR(AssetUnavailableError);
/// 400 — below the economic floor: forwarding gas would exceed the fee earned.
RECEIVIFY_DEFINE_API_ERROR(AmountTooLowError);
/// 400 — the merchant account is suspended and cannot create new resources.
RECEIVIFY_DEFINE_API_ERROR(SuspendedError);
/// 400 — a per-account quota for this resource is already at its maximum.
RECEIVIFY_DEFINE_API_ERROR(LimitExceededError);
/// 400 — the supplied two-factor (TOTP) code is not valid.
RECEIVIFY_DEFINE_API_ERROR(InvalidCodeError);
/// 400 — two-factor enrolment has not been started yet.
RECEIVIFY_DEFINE_API_ERROR(NoSetupError);
/// 400 — two-factor authentication is not enabled on this account.
RECEIVIFY_DEFINE_API_ERROR(NotEnabledError);
/// 400 — no registered, active webhook endpoint matched the request.
RECEIVIFY_DEFINE_API_ERROR(NoTargetError);
/// 400 — a single-use confirmation token is invalid, expired, or already spent.
RECEIVIFY_DEFINE_API_ERROR(InvalidTokenError);
/// 401 — missing, malformed, revoked or unknown API key / session cookie.
RECEIVIFY_DEFINE_API_ERROR(UnauthorizedError);
/// 401 — an inbound provider webhook failed HMAC verification. Never returned to merchant callers.
RECEIVIFY_DEFINE_API_ERROR(BadSignatureError);
/// 403 — self-serve signup is turned off on this deployment.
RECEIVIFY_DEFINE_API_ERROR(SignupDisabledError);
/// 404 — no such resource, or it does not belong to the caller. The two are indistinguishable.
RECEIVIFY_DEFINE_API_ERROR(NotFoundError);
/// 409 — a resource with that unique identity already exists.
RECEIVIFY_DEFINE_API_ERROR(AlreadyExistsError);
/// 409 — the feature being enabled is already on.
RECEIVIFY_DEFINE_API_ERROR(AlreadyEnabledError);
/// 409 — the resource exists but its state forbids this action.
RECEIVIFY_DEFINE_API_ERROR(NotAllowedError);
/// 500 — an unhandled server error. Safe to retry.
RECEIVIFY_DEFINE_API_ERROR(InternalError);
/// 502 — a node or payment provider we depend on failed. Retryable.
RECEIVIFY_DEFINE_API_ERROR(UpstreamError);
/// 503 — no fresh FX rate is available to price the request. Retryable.
RECEIVIFY_DEFINE_API_ERROR(RateUnavailableError);

#undef RECEIVIFY_DEFINE_API_ERROR

/// 429 — back off. Parses the rate-limit headers so you do not have to.
class RateLimitedError : public ApiError {
 public:
  RateLimitedError(std::string code, std::string detail, ErrorContext ctx)
      : ApiError(std::move(code), std::move(detail), std::move(ctx)) {
    retry_after_ = header_int("retry-after");
    rate_limit_reset_ = header_int("x-ratelimit-reset");
    rate_limit_limit_ = header_int("x-ratelimit-limit");
    rate_limit_remaining_ = header_int("x-ratelimit-remaining");
  }

  /// Seconds to wait, from `Retry-After`.
  const std::optional<std::int64_t>& retry_after() const noexcept { return retry_after_; }
  /// Unix seconds at which the window resets, from `X-RateLimit-Reset`.
  const std::optional<std::int64_t>& rate_limit_reset() const noexcept { return rate_limit_reset_; }
  const std::optional<std::int64_t>& rate_limit_limit() const noexcept { return rate_limit_limit_; }
  const std::optional<std::int64_t>& rate_limit_remaining() const noexcept { return rate_limit_remaining_; }

 private:
  std::optional<std::int64_t> header_int(const char* name) const {
    const std::string* v = detail::find_header(headers(), name);
    if (v == nullptr) return std::nullopt;
    std::int64_t out = 0;
    if (!detail::parse_int64(detail::trim(*v), out)) return std::nullopt;
    return out;
  }

  std::optional<std::int64_t> retry_after_;
  std::optional<std::int64_t> rate_limit_reset_;
  std::optional<std::int64_t> rate_limit_limit_;
  std::optional<std::int64_t> rate_limit_remaining_;
};

/// The frozen `ERROR_CODES` registry, in the spec's order. Provided for tooling and tests; the
/// SDK itself never requires a code to be in this list.
inline const std::vector<std::string>& error_codes() {
  static const std::vector<std::string> kCodes = {
      "bad_request",  "bad_ticker", "bad_destination", "asset_unavailable", "amount_too_low",
      "suspended",    "limit_exceeded", "invalid_code", "no_setup",         "not_enabled",
      "no_target",    "invalid_token",  "unauthorized", "bad_signature",    "signup_disabled",
      "not_found",    "already_exists", "already_enabled", "not_allowed",   "rate_limited",
      "internal",     "upstream_error", "rate_unavailable"};
  return kCodes;
}

/// Throw the right exception type for a failed response.
///
/// An unrecognised `code` — including one from a future API version — yields a plain `ApiError`
/// with the code intact, so an old SDK degrades instead of crashing.
[[noreturn]] inline void throw_api_error(const std::string& code, const std::string& detail, ErrorContext ctx) {
  if (code == "bad_request") throw BadRequestError(code, detail, std::move(ctx));
  if (code == "bad_ticker") throw BadTickerError(code, detail, std::move(ctx));
  if (code == "bad_destination") throw BadDestinationError(code, detail, std::move(ctx));
  if (code == "asset_unavailable") throw AssetUnavailableError(code, detail, std::move(ctx));
  if (code == "amount_too_low") throw AmountTooLowError(code, detail, std::move(ctx));
  if (code == "suspended") throw SuspendedError(code, detail, std::move(ctx));
  if (code == "limit_exceeded") throw LimitExceededError(code, detail, std::move(ctx));
  if (code == "invalid_code") throw InvalidCodeError(code, detail, std::move(ctx));
  if (code == "no_setup") throw NoSetupError(code, detail, std::move(ctx));
  if (code == "not_enabled") throw NotEnabledError(code, detail, std::move(ctx));
  if (code == "no_target") throw NoTargetError(code, detail, std::move(ctx));
  if (code == "invalid_token") throw InvalidTokenError(code, detail, std::move(ctx));
  if (code == "unauthorized") throw UnauthorizedError(code, detail, std::move(ctx));
  if (code == "bad_signature") throw BadSignatureError(code, detail, std::move(ctx));
  if (code == "signup_disabled") throw SignupDisabledError(code, detail, std::move(ctx));
  if (code == "not_found") throw NotFoundError(code, detail, std::move(ctx));
  if (code == "already_exists") throw AlreadyExistsError(code, detail, std::move(ctx));
  if (code == "already_enabled") throw AlreadyEnabledError(code, detail, std::move(ctx));
  if (code == "not_allowed") throw NotAllowedError(code, detail, std::move(ctx));
  if (code == "rate_limited") throw RateLimitedError(code, detail, std::move(ctx));
  if (code == "internal") throw InternalError(code, detail, std::move(ctx));
  if (code == "upstream_error") throw UpstreamError(code, detail, std::move(ctx));
  if (code == "rate_unavailable") throw RateUnavailableError(code, detail, std::move(ctx));

  // A 429 without a recognised code still deserves the rate-limit accessors.
  if (ctx.status == 429) throw RateLimitedError(code, detail, std::move(ctx));

  // Unknown code, or a body that did not conform at all. Never fatal.
  throw ApiError(code, detail, std::move(ctx));
}

}  // namespace receivify
