// The Receivify API client.
//
// Depends on nothing but C++17 and the two interfaces in `http.hpp` and `hmac.hpp`. The libcurl
// and OpenSSL implementations of those interfaces live in `transport/` and `crypto/` and are
// wired up by `defaults.hpp`; this file never mentions them.
#pragma once

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <random>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include "receivify/detail/util.hpp"
#include "receivify/errors.hpp"
#include "receivify/hmac.hpp"
#include "receivify/http.hpp"
#include "receivify/json.hpp"
#include "receivify/models.hpp"
#include "receivify/pagination.hpp"
#include "receivify/version.hpp"
#include "receivify/webhooks.hpp"

namespace receivify {

/// Per-client configuration.
struct ClientOptions {
  /// Per-request timeout. Applies to the whole exchange, not just the connect.
  std::chrono::milliseconds timeout{30000};

  /// Webhook signing secrets, **current first, previous second**. During a rotation two secrets
  /// are legitimately valid at once; see `webhooks.hpp`.
  std::vector<std::string> webhook_secrets;

  /// The HTTP stack. Leave null to get the bundled libcurl transport (when compiled in).
  std::shared_ptr<HttpTransport> transport;

  /// The HMAC provider. Leave null to get the bundled OpenSSL provider (when compiled in).
  std::shared_ptr<const Hmac> hmac;

  /// Retries for SAFE requests only. See `Client::request` for what "safe" means — it is not
  /// "any request that failed".
  int max_retries = 2;
  std::chrono::milliseconds retry_base{500};
  std::chrono::milliseconds max_retry_delay{20000};

  /// Appended to `receivify-cpp/<version>`. Name your application here.
  std::string user_agent_suffix;

  /// Injectable clock and sleeper, so tests run instantly and deterministically.
  std::function<void(std::chrono::milliseconds)> sleep;
  std::function<std::int64_t()> now_seconds;
};

/// Per-call options. Every mutating method accepts these.
struct RequestOptions {
  /// `Idempotency-Key`. Scoped to `(merchant, endpoint, key)` and retained 24h; a replay returns
  /// the ORIGINAL body and status plus `Idempotent-Replay: true`.
  ///
  /// It is also what makes a mutating call retry-safe: without one, this SDK will never
  /// automatically repeat a POST, because that is how you charge someone twice.
  std::optional<std::string> idempotency_key;

  std::optional<std::chrono::milliseconds> timeout;
  Headers headers;  ///< Extra request headers. Names are lower-cased before sending.
};

/// A fully decoded response. Returned by the low-level `request()` escape hatch when you need the
/// status or the headers — the typed methods hand you the model instead.
struct Response {
  int status = 0;
  Headers headers;
  std::string raw_body;  ///< Exactly what came off the wire, before parsing.
  Json body;             ///< The parsed body. Null when the body was empty or not JSON.

  /// True when the API replayed a stored `Idempotency-Key` record rather than doing the work.
  bool idempotent_replay() const {
    const std::string* v = detail::find_header(headers, "idempotent-replay");
    return v != nullptr && detail::to_lower(detail::trim(*v)) == "true";
  }
};

/// A request to build. Public so `Client::request()` can serve as an escape hatch to any endpoint
/// this SDK does not wrap as a typed method.
struct RequestSpec {
  std::string method = "GET";
  std::string path;  ///< Starts with `/`. Path segments must already be encoded.
  std::vector<std::pair<std::string, std::string>> query;
  std::optional<Json> body;

  /// Send the bearer key. False only for the two public catalogue endpoints.
  bool auth = true;

  /// Does this endpoint DECLARE `Idempotency-Key` support in openapi.yaml? Only endpoints where
  /// this is true can ever be retried after a mutation.
  bool declares_idempotency = false;

  /// Throw a typed `ApiError` on 4xx/5xx. Turn it off for `GET /api/health`, whose 503 body is
  /// documented as NOT conforming to the `{error, detail}` contract.
  bool throw_on_error = true;
};

/// Query parameters for `list_invoices`.
struct InvoiceListParams {
  std::optional<int> limit;            ///< 1..200. Larger is clamped; zero or negative is a 400.
  std::optional<std::string> cursor;   ///< Opaque. From a previous `next_cursor`.
  std::optional<std::string> status;
};

/// Query parameters for `list_addresses`.
struct AddressListParams {
  std::optional<int> limit;
  std::optional<std::string> cursor;
  std::optional<std::string> status;
  std::optional<std::string> ticker;
};

/// The Receivify API client.
///
/// Thread safety: a `Client` is immutable after construction and holds no per-request state, so
/// sharing one across threads is safe provided the `HttpTransport` and `Hmac` you gave it are.
/// The bundled ones are.
class Client {
 public:
  /// @param api_url  e.g. `https://receivify.io`. Trailing slashes are trimmed.
  /// @param api_key  the merchant `rcv_live_…` key. Empty is allowed if you only call the public
  ///                 catalogue endpoints. It is sent as a header, never as a query parameter, and
  ///                 is never written to any message this SDK produces.
  Client(std::string api_url, std::string api_key = std::string(), ClientOptions options = ClientOptions());

  /* ================================================================ *
   * Invoices
   * ================================================================ */

  /// `POST /api/v1/invoices` — always 201.
  ///
  /// Exactly one amount field is required; the first present one wins, in this order:
  /// `amount_fiat` (+`currency`), `amount`, `amount_sats`, `amount_btc`. Pass amounts as STRINGS —
  /// `Json` refuses to hold a double, so this is enforced at compile time.
  ///
  /// Two things return an EXISTING invoice rather than creating one: an `Idempotency-Key` replay,
  /// and a `reference` this merchant has already used. Both still answer 201.
  Invoice create_invoice(const Json& params, const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.method = "POST";
    spec.path = "/api/v1/invoices";
    spec.body = params;
    spec.declares_idempotency = true;
    return Invoice::from_json(request(spec, opts).body);
  }

  /// `GET /api/v1/invoices/{id}` — the authoritative current state. Poll this, not a webhook replay.
  Invoice get_invoice(const std::string& id, const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.path = "/api/v1/invoices/" + encode_id(id);
    return Invoice::from_json(request(spec, opts).body);
  }

  /// `GET /api/v1/invoices` — one page.
  Page<Invoice> list_invoices(const InvoiceListParams& params = {}, const RequestOptions& opts = {}) {
    return Page<Invoice>::from_json(request(invoice_list_spec(params), opts).body);
  }

  /// Auto-paginating `list_invoices`. Follows `next_cursor` until `has_more` is false.
  Paginator<Invoice> invoices(InvoiceListParams params = {}, RequestOptions opts = {}) {
    Client* self = this;
    return Paginator<Invoice>([self, params, opts](const std::optional<std::string>& cursor) {
      InvoiceListParams p = params;
      if (cursor.has_value()) p.cursor = cursor;
      return Page<Invoice>::from_json(self->request(self->invoice_list_spec(p), opts).body);
    }, params.cursor);
  }

  /// `POST /api/v1/invoices/{id}/cancel`.
  ///
  /// NOT idempotent — a second call on an already-cancelled invoice is `409 not_allowed`, which
  /// is why this SDK never retries it.
  Invoice cancel_invoice(const std::string& id, const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.method = "POST";
    spec.path = "/api/v1/invoices/" + encode_id(id) + "/cancel";
    return Invoice::from_json(request(spec, opts).body);
  }

  /* ================================================================ *
   * Forwarding addresses
   *
   * The spec calls these "forwards"; the shared SDK surface calls them "addresses". Both names
   * work and do the same thing.
   * ================================================================ */

  /// `POST /api/v1/address` — create, or return, a forwarding address.
  ///
  /// Deterministic: the identity is `(merchant, resolved asset, destination, callback_url)`.
  /// **201** = newly derived, **200** = an existing forward with that identity — repeating the
  /// call does not burn a second HD derivation index. Use `request()` if you need to tell which.
  ///
  /// Read the id from `.id`. `.forward_id` carries the same value and is deprecated.
  Forward create_address(const Json& params, const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.method = "POST";
    spec.path = "/api/v1/address";
    spec.body = params;
    spec.declares_idempotency = true;
    return Forward::from_json(request(spec, opts).body);
  }

  /// `GET /api/v1/address/{id}` — the forward plus its capped `payments` / `callbacks` log.
  ForwardDetail get_address(const std::string& id, const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.path = "/api/v1/address/" + encode_id(id);
    return ForwardDetail::from_json(request(spec, opts).body);
  }

  /// `GET /api/v1/address` — one page of plain `Forward` objects (no `payments` / `callbacks`).
  Page<Forward> list_addresses(const AddressListParams& params = {}, const RequestOptions& opts = {}) {
    return Page<Forward>::from_json(request(address_list_spec(params), opts).body);
  }

  /// Auto-paginating `list_addresses`.
  Paginator<Forward> addresses(AddressListParams params = {}, RequestOptions opts = {}) {
    Client* self = this;
    return Paginator<Forward>([self, params, opts](const std::optional<std::string>& cursor) {
      AddressListParams p = params;
      if (cursor.has_value()) p.cursor = cursor;
      return Page<Forward>::from_json(self->request(self->address_list_spec(p), opts).body);
    }, params.cursor);
  }

  /// `PATCH /api/v1/address/{id}` — the merchant kill switch.
  ///
  /// Mutable: `status` (`active` ⇄ `paused`) and `callback_url`. `destination` and `ticker` are
  /// IMMUTABLE and any attempt is `409 not_allowed` BEFORE the forward is even looked up — so a
  /// patch carrying an illegal field alongside a legal one can never be half-applied.
  Forward update_address(const std::string& id, const Json& params, const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.method = "PATCH";
    spec.path = "/api/v1/address/" + encode_id(id);
    spec.body = params;
    return Forward::from_json(request(spec, opts).body);
  }

  /// `DELETE /api/v1/address/{id}` — soft delete (`status: "disabled"`).
  ///
  /// The row is NEVER removed: money can still arrive at a deposit address after a merchant stops
  /// using it, and a deleted row would make that money unattributable. Idempotent.
  Forward disable_address(const std::string& id, const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.method = "DELETE";
    spec.path = "/api/v1/address/" + encode_id(id);
    return Forward::from_json(request(spec, opts).body);
  }

  // Aliases matching the spec's operationIds.
  Forward create_forward(const Json& p, const RequestOptions& o = {}) { return create_address(p, o); }
  ForwardDetail get_forward(const std::string& id, const RequestOptions& o = {}) { return get_address(id, o); }
  Page<Forward> list_forwards(const AddressListParams& p = {}, const RequestOptions& o = {}) {
    return list_addresses(p, o);
  }
  Paginator<Forward> forwards(AddressListParams p = {}, RequestOptions o = {}) { return addresses(p, o); }
  Forward update_forward(const std::string& id, const Json& p, const RequestOptions& o = {}) {
    return update_address(id, p, o);
  }
  Forward disable_forward(const std::string& id, const RequestOptions& o = {}) { return disable_address(id, o); }

  /* ================================================================ *
   * Catalogue (public — no API key required)
   * ================================================================ */

  /// `GET /api/v1/tickers` — every `(asset, chain)` pair offered for forwarding.
  ///
  /// A finite catalogue that is never actually paginated, but it still comes back in the list
  /// envelope, so it is still a `Page`. `Ticker::decimals` is what `units::format_units` wants.
  Page<Ticker> list_tickers(const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.path = "/api/v1/tickers";
    spec.auth = false;
    return Page<Ticker>::from_json(request(spec, opts).body);
  }

  /* ================================================================ *
   * Webhook secret rotation
   * ================================================================ */

  /// `POST /api/v1/webhooks/secret/rotate` — mints a new secret and returns it **once**.
  ///
  /// The previous secret stays acceptable until `prev_expires_at`. Until then, pass BOTH to
  /// `verify_webhook`, newest first. An `Idempotency-Key` replay deliberately returns no secret.
  WebhookSecretRotated rotate_webhook_secret(const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.method = "POST";
    spec.path = "/api/v1/webhooks/secret/rotate";
    spec.declares_idempotency = true;
    return WebhookSecretRotated::from_json(request(spec, opts).body);
  }

  /// `GET /api/v1/webhooks/secret` — rotation METADATA only, never the secret.
  ///
  /// `prev_active` answers "must my receiver still try the old secret?". Poll it to know when it
  /// is safe to drop the second entry from `webhook_secrets`.
  WebhookSecretState get_webhook_secret_state(const RequestOptions& opts = {}) {
    RequestSpec spec;
    spec.path = "/api/v1/webhooks/secret";
    return WebhookSecretState::from_json(request(spec, opts).body);
  }

  /* ================================================================ *
   * Webhooks — receiving
   * ================================================================ */

  /// Verify a signed delivery against the configured secret list. Constant-time, over the RAW
  /// body, with a 300s default replay window.
  ///
  /// @param raw_body the EXACT request bytes — never a re-serialised JSON body
  bool verify_webhook(const std::string& raw_body,
                      const std::string& signature_header,
                      const VerifyOptions& options = {}) const {
    return receivify::verify_webhook(raw_body, signature_header, webhook_secrets_, require_hmac(), options);
  }

  /// Verify against an explicit secret list instead of the configured one.
  bool verify_webhook(const std::string& raw_body,
                      const std::string& signature_header,
                      const std::vector<std::string>& secrets,
                      const VerifyOptions& options = {}) const {
    return receivify::verify_webhook(raw_body, signature_header, secrets, require_hmac(), options);
  }

  /// Verify and parse in one step. Throws `WebhookError` on any failure.
  WebhookEvent parse_webhook(const std::string& raw_body,
                             const std::string& signature_header,
                             const VerifyOptions& options = {}) const {
    return receivify::parse_webhook(raw_body, signature_header, webhook_secrets_, require_hmac(), options);
  }

  WebhookEvent parse_webhook(const std::string& raw_body,
                             const std::string& signature_header,
                             const std::vector<std::string>& secrets,
                             const VerifyOptions& options = {}) const {
    return receivify::parse_webhook(raw_body, signature_header, secrets, require_hmac(), options);
  }

  /// The same check as `verify_webhook`, but with a machine-readable reason for your logs.
  VerificationResult describe_webhook(const std::string& raw_body,
                                      const std::string& signature_header,
                                      const VerifyOptions& options = {}) const {
    return receivify::describe_verification(raw_body, signature_header, webhook_secrets_, require_hmac(), options);
  }

  /* ================================================================ *
   * Escape hatch
   * ================================================================ */

  /// Issue any request against the API and get the decoded response.
  ///
  /// This is how you reach an endpoint this SDK does not wrap: payment links, recurring
  /// schedules, API keys, settings, payouts, webhook endpoints and the delivery log are all one
  /// call away. See the README for why they are not typed methods in v1.
  ///
  ///     receivify::RequestSpec spec;
  ///     spec.method = "POST";
  ///     spec.path   = "/api/v1/payment-links";
  ///     spec.body   = receivify::Json::object({{"title", receivify::Json("Coffee")}});
  ///     auto link = client.request(spec).body;
  ///
  /// ### Retry policy
  /// A request is retried on 429/5xx and on a transport failure ONLY when it is safe: a `GET`, or
  /// a request whose endpoint declares `Idempotency-Key` support AND for which you supplied one.
  /// A bare `POST` is never repeated automatically. Backoff is exponential with jitter and
  /// honours `Retry-After`, then `X-RateLimit-Reset`.
  Response request(const RequestSpec& spec, const RequestOptions& opts = {});

  /// Auto-paginate any list endpoint, including ones with no typed model. Use `RawObject` as `T`
  /// to get the untouched `Json` for each item.
  template <typename T>
  Paginator<T> paginate(std::string path,
                        std::vector<std::pair<std::string, std::string>> query = {},
                        RequestOptions opts = {}) {
    Client* self = this;
    return Paginator<T>([self, path, query, opts](const std::optional<std::string>& cursor) {
      RequestSpec spec;
      spec.path = path;
      spec.query = query;
      if (cursor.has_value()) spec.query.emplace_back("cursor", *cursor);
      return Page<T>::from_json(self->request(spec, opts).body);
    });
  }

  /* ================================================================ */

  const std::string& api_url() const noexcept { return api_url_; }
  /// The configured rotation list. Never contains the API key.
  const std::vector<std::string>& webhook_secrets() const noexcept { return webhook_secrets_; }
  const std::string& user_agent() const noexcept { return user_agent_; }

 private:
  static std::string encode_id(const std::string& id) {
    if (id.empty()) throw ConfigurationError("Receivify: an id is required");
    return detail::url_encode(id);
  }

  const Hmac& require_hmac() const {
    if (!hmac_) {
      throw ConfigurationError(
          "Receivify: no HMAC provider. Build with RECEIVIFY_WITH_OPENSSL=ON for the bundled "
          "OpenSSL one, or set ClientOptions::hmac to your own implementation of receivify::Hmac.");
    }
    return *hmac_;
  }

  RequestSpec invoice_list_spec(const InvoiceListParams& p) const {
    RequestSpec spec;
    spec.path = "/api/v1/invoices";
    if (p.limit) spec.query.emplace_back("limit", std::to_string(*p.limit));
    if (p.cursor) spec.query.emplace_back("cursor", *p.cursor);
    if (p.status) spec.query.emplace_back("status", *p.status);
    return spec;
  }

  RequestSpec address_list_spec(const AddressListParams& p) const {
    RequestSpec spec;
    spec.path = "/api/v1/address";
    if (p.limit) spec.query.emplace_back("limit", std::to_string(*p.limit));
    if (p.cursor) spec.query.emplace_back("cursor", *p.cursor);
    if (p.status) spec.query.emplace_back("status", *p.status);
    if (p.ticker) spec.query.emplace_back("ticker", *p.ticker);
    return spec;
  }

  std::string build_url(const RequestSpec& spec) const {
    std::string url = api_url_ + spec.path;
    bool first = true;
    for (const auto& kv : spec.query) {
      url += first ? '?' : '&';
      first = false;
      url += detail::url_encode(kv.first);
      url += '=';
      url += detail::url_encode(kv.second);
    }
    return url;
  }

  std::chrono::milliseconds backoff(int attempt, const Headers* headers) const;

  std::string api_url_;
  std::string api_key_;  ///< Never logged, never in a URL, never exposed by an accessor.
  std::string user_agent_;
  std::chrono::milliseconds timeout_;
  std::chrono::milliseconds retry_base_;
  std::chrono::milliseconds max_retry_delay_;
  int max_retries_;
  std::vector<std::string> webhook_secrets_;
  std::shared_ptr<HttpTransport> transport_;
  std::shared_ptr<const Hmac> hmac_;
  std::function<void(std::chrono::milliseconds)> sleep_;
  std::function<std::int64_t()> now_seconds_;
};

/* ==================================================================== *
 * Implementation
 * ==================================================================== */

inline Client::Client(std::string api_url, std::string api_key, ClientOptions options) {
  api_url_ = detail::trim(api_url);
  while (!api_url_.empty() && api_url_.back() == '/') api_url_.pop_back();
  if (api_url_.empty()) {
    throw ConfigurationError("Receivify: api_url is required, e.g. \"https://receivify.io\"");
  }

  api_key_ = std::move(api_key);
  user_agent_ = receivify::user_agent();
  if (!options.user_agent_suffix.empty()) user_agent_ += " " + options.user_agent_suffix;

  timeout_ = options.timeout.count() > 0 ? options.timeout : std::chrono::milliseconds(30000);
  retry_base_ = options.retry_base.count() > 0 ? options.retry_base : std::chrono::milliseconds(500);
  max_retry_delay_ = options.max_retry_delay.count() > 0 ? options.max_retry_delay : std::chrono::milliseconds(20000);
  max_retries_ = options.max_retries > 0 ? options.max_retries : 0;

  for (const std::string& s : options.webhook_secrets) {
    if (!s.empty()) webhook_secrets_.push_back(s);
  }

  transport_ = std::move(options.transport);
  hmac_ = std::move(options.hmac);

  sleep_ = options.sleep ? std::move(options.sleep)
                         : [](std::chrono::milliseconds ms) { std::this_thread::sleep_for(ms); };
  now_seconds_ = options.now_seconds ? std::move(options.now_seconds) : []() -> std::int64_t {
    using namespace std::chrono;
    return duration_cast<seconds>(system_clock::now().time_since_epoch()).count();
  };

  if (!transport_) {
    throw ConfigurationError(
        "Receivify: no HTTP transport. Build with RECEIVIFY_WITH_CURL=ON and use "
        "receivify::make_client() for the bundled libcurl transport, or set "
        "ClientOptions::transport to your own implementation of receivify::HttpTransport.");
  }
}

inline std::chrono::milliseconds Client::backoff(int attempt, const Headers* headers) const {
  using ms = std::chrono::milliseconds;

  if (headers != nullptr) {
    // The server told us exactly how long to wait. Believe it.
    if (const std::string* v = detail::find_header(*headers, "retry-after")) {
      std::int64_t secs = 0;
      if (detail::parse_int64(detail::trim(*v), secs) && secs > 0) {
        return std::min(ms(secs * 1000), max_retry_delay_);
      }
    }
    if (const std::string* v = detail::find_header(*headers, "x-ratelimit-reset")) {
      std::int64_t reset = 0;
      if (detail::parse_int64(detail::trim(*v), reset) && reset > 0) {
        const std::int64_t wait = reset - now_seconds_();
        if (wait > 0) return std::min(ms(wait * 1000), max_retry_delay_);
      }
    }
  }

  std::int64_t delay = retry_base_.count();
  for (int i = 1; i < attempt && delay < max_retry_delay_.count(); ++i) delay *= 2;

  // Jitter, so a fleet that all got 429 at once does not all come back at once.
  static thread_local std::mt19937 rng(static_cast<std::mt19937::result_type>(
      std::chrono::steady_clock::now().time_since_epoch().count()));
  std::uniform_int_distribution<std::int64_t> jitter(0, retry_base_.count());
  return std::min(ms(delay + jitter(rng)), max_retry_delay_);
}

inline Response Client::request(const RequestSpec& spec, const RequestOptions& opts) {
  HttpRequest req;
  req.method = spec.method;
  req.url = build_url(spec);
  req.timeout = opts.timeout.value_or(timeout_);

  req.headers.emplace_back("accept", "application/json");
  req.headers.emplace_back("user-agent", user_agent_);
  for (const auto& kv : opts.headers) {
    req.headers.emplace_back(detail::to_lower(kv.first), kv.second);
  }
  // The API key is a header, never a query parameter, and appears in no log line this SDK writes.
  if (spec.auth && !api_key_.empty()) {
    req.headers.emplace_back("authorization", "Bearer " + api_key_);
  }
  if (spec.body.has_value()) {
    req.body = spec.body->dump();
    req.headers.emplace_back("content-type", "application/json");
  }

  const bool has_key = opts.idempotency_key.has_value() && !opts.idempotency_key->empty();
  if (has_key) req.headers.emplace_back("idempotency-key", *opts.idempotency_key);

  // Retry-safety. GET is safe. A mutating call is safe ONLY when the endpoint itself declares
  // Idempotency-Key support AND the caller supplied one. Blind-retrying a POST is how you charge
  // someone twice.
  const bool safe = (spec.method == "GET") || (spec.declares_idempotency && has_key);
  const int max_attempts = safe ? max_retries_ + 1 : 1;

  const std::string request_label = spec.method + " " + spec.path;

  for (int attempt = 1;; ++attempt) {
    HttpResponse http;
    try {
      http = transport_->send(req);
    } catch (const TransportError&) {
      if (!safe || attempt >= max_attempts) throw;
      sleep_(backoff(attempt, nullptr));
      continue;
    }

    Response response;
    response.status = http.status;
    response.headers = std::move(http.headers);
    response.raw_body = std::move(http.body);
    if (!response.raw_body.empty()) {
      try {
        response.body = json_parse(response.raw_body);
      } catch (const JsonError&) {
        // A non-JSON body is not automatically fatal — a proxy's HTML 502 still has to become a
        // typed error below, with the bytes preserved in `raw_body`.
        response.body = Json();
      }
    }

    if (response.status >= 400 && spec.throw_on_error) {
      const bool worth_retrying = response.status == 429 || response.status >= 500;
      if (safe && worth_retrying && attempt < max_attempts) {
        sleep_(backoff(attempt, &response.headers));
        continue;
      }
      ErrorContext ctx;
      ctx.status = response.status;
      ctx.headers = response.headers;
      ctx.raw_body = response.raw_body;
      ctx.request = request_label;

      const std::string code = response.body["error"].as_string_or("");
      std::string detail = response.body["detail"].as_string_or("");
      if (detail.empty()) {
        detail = code.empty() ? ("Receivify: HTTP " + std::to_string(response.status))
                              : ("Receivify: " + code);
      }
      throw_api_error(code, detail, std::move(ctx));
    }

    return response;
  }
}

}  // namespace receivify
