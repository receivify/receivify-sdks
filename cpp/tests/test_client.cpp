// Client behaviour, entirely offline: the transport is a test double, the clock and the sleeper
// are injected. `ctest` never opens a socket.
#include <memory>
#include <string>
#include <type_traits>
#include <vector>

#include "receivify/client.hpp"
#include "fake_transport.hpp"
#include "test_harness.hpp"

using receivify::AddressListParams;
using receivify::ApiError;
using receivify::ClientOptions;
using receivify::Client;
using receivify::ConfigurationError;
using receivify::InvoiceListParams;
using receivify::Json;
using receivify::RequestOptions;
using receivify::RequestSpec;

namespace {

/// A client wired to a `FakeTransport`, with no real sleeping and a frozen clock.
struct Harness {
  std::shared_ptr<rtest::FakeTransport> transport = std::make_shared<rtest::FakeTransport>();
  std::shared_ptr<const receivify::Hmac> hmac = std::make_shared<rtest::StubHmac>();
  std::vector<std::chrono::milliseconds> slept;

  std::unique_ptr<Client> make(std::vector<std::string> secrets = {}) {
    ClientOptions opts;
    opts.transport = transport;
    opts.hmac = hmac;
    opts.webhook_secrets = std::move(secrets);
    opts.retry_base = std::chrono::milliseconds(10);
    opts.sleep = [this](std::chrono::milliseconds ms) { slept.push_back(ms); };
    opts.now_seconds = []() -> std::int64_t { return 1767225600; };
    return std::unique_ptr<Client>(new Client("https://receivify.io/", "rcv_live_secretkey", std::move(opts)));
  }
};

const char* kInvoiceJson = R"({
  "id":"11111111-2222-3333-4444-555555555555",
  "reference":"order-1",
  "asset":"btc",
  "amount_expected":"2100000000000000000000000",
  "amount_received":"0",
  "amount_fiat":"25.00",
  "fiat_currency":"USD",
  "chain":null,
  "provider":"bitcoind",
  "address":"bc1qexample",
  "status":"pending",
  "expires_at":"2026-01-01T01:00:00.000Z",
  "paid_at":null,
  "created_at":"2026-01-01T00:00:00.000Z",
  "metadata":{"customer":"alice"},
  "checkout_url":"https://receivify.io/pay/11111111"
})";

std::string list_page(const std::string& item, bool has_more, const std::string& cursor) {
  return "{\"data\":[" + item + "],\"has_more\":" + (has_more ? "true" : "false") +
         ",\"next_cursor\":" + (cursor.empty() ? "null" : "\"" + cursor + "\"") + "}";
}

std::string error_body(const std::string& code, const std::string& detail) {
  return "{\"error\":\"" + code + "\",\"detail\":\"" + detail + "\"}";
}

}  // namespace

/* ------------------------------------------------------------------ *
 * Construction
 * ------------------------------------------------------------------ */

TEST(a_missing_transport_is_a_clear_configuration_error_not_a_null_deref) {
  ClientOptions opts;  // no transport
  CHECK_THROWS_AS(Client("https://receivify.io", "k", opts), ConfigurationError);
}

TEST(an_empty_api_url_is_rejected) {
  Harness h;
  ClientOptions opts;
  opts.transport = h.transport;
  CHECK_THROWS_AS(Client("   ", "k", opts), ConfigurationError);
}

TEST(trailing_slashes_are_trimmed_so_paths_never_double_up) {
  Harness h;
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  client->get_invoice("abc");
  CHECK_EQ(h.transport->last().url, "https://receivify.io/api/v1/invoices/abc");
}

/* ------------------------------------------------------------------ *
 * The exact request built
 * ------------------------------------------------------------------ */

TEST(the_api_key_goes_in_a_header_and_never_in_the_url) {
  Harness h;
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  client->get_invoice("abc");

  const auto& req = h.transport->last();
  CHECK(req.url.find("rcv_live_secretkey") == std::string::npos);
  const std::string* auth = h.transport->header(req, "authorization");
  CHECK(auth != nullptr);
  CHECK_EQ(*auth, "Bearer rcv_live_secretkey");
}

TEST(the_user_agent_identifies_the_sdk_and_version) {
  Harness h;
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  client->get_invoice("abc");
  const std::string* ua = h.transport->header(h.transport->last(), "user-agent");
  CHECK(ua != nullptr);
  CHECK_EQ(*ua, std::string("receivify-cpp/") + receivify::version());
}

TEST(public_catalogue_endpoints_send_no_credential) {
  Harness h;
  h.transport->queue(200, R"({"data":[],"has_more":false,"next_cursor":null})");
  auto client = h.make();
  client->list_tickers();
  CHECK(h.transport->header(h.transport->last(), "authorization") == nullptr);
}

TEST(ids_are_url_encoded_so_a_hostile_id_cannot_forge_a_path) {
  Harness h;
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  client->get_invoice("../../admin/secrets?x=1");
  CHECK_EQ(h.transport->last().url,
           "https://receivify.io/api/v1/invoices/..%2F..%2Fadmin%2Fsecrets%3Fx%3D1");
}

TEST(an_empty_id_is_rejected_before_a_request_is_made) {
  Harness h;
  auto client = h.make();
  CHECK_THROWS_AS(client->get_invoice(""), ConfigurationError);
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(0));
}

TEST(query_parameters_are_built_and_encoded) {
  Harness h;
  h.transport->queue(200, R"({"data":[],"has_more":false,"next_cursor":null})");
  auto client = h.make();
  AddressListParams params;
  params.limit = 50;
  params.status = "active";
  params.ticker = "usdc_polygon";
  params.cursor = "abc/def+ghi==";
  client->list_addresses(params);
  CHECK_EQ(h.transport->last().url,
           "https://receivify.io/api/v1/address?limit=50&cursor=abc%2Fdef%2Bghi%3D%3D&status=active&ticker=usdc_polygon");
}

TEST(a_post_body_is_json_with_a_content_type) {
  Harness h;
  h.transport->queue(201, kInvoiceJson);
  auto client = h.make();
  client->create_invoice(Json::object({{"asset", Json("btc")}, {"amount_sats", Json("250000")}}));

  const auto& req = h.transport->last();
  CHECK_EQ(req.method, "POST");
  CHECK_EQ(req.url, "https://receivify.io/api/v1/invoices");
  CHECK_EQ(req.body, R"({"asset":"btc","amount_sats":"250000"})");
  const std::string* ct = h.transport->header(req, "content-type");
  CHECK(ct != nullptr);
  CHECK_EQ(*ct, "application/json");
}

TEST(add_fee_ontop_goes_out_as_a_json_bool_and_the_fee_fields_decode) {
  Harness h;
  h.transport->queue(201, R"({"id":"x","asset":"btc","amount_expected":"101010","amount_received":"0",
    "address":"bc1q","status":"pending","expires_at":"t","created_at":"t","checkout_url":"u",
    "decimals":8,"amount_decimal":"0.00101010","payment_uri":null,
    "add_fee_ontop":true,"amount_net_requested":"100000","fee_ontop":"1010"})");
  auto client = h.make();
  receivify::Invoice inv = client->create_invoice(
      Json::object({{"amount_sats", Json("100000")}, {"add_fee_ontop", Json(true)}}));
  CHECK_EQ(h.transport->last().body, R"({"amount_sats":"100000","add_fee_ontop":true})");
  CHECK_EQ(inv.decimals, static_cast<std::int64_t>(8));
  CHECK_EQ(inv.amount_decimal.value_or(""), "0.00101010");
  CHECK(!inv.payment_uri.has_value());
  CHECK(inv.add_fee_ontop);
  CHECK_EQ(inv.amount_net_requested.value_or(""), "100000");
  CHECK_EQ(inv.fee_ontop.value_or(""), "1010");
}

TEST(the_idempotency_key_is_sent_when_supplied) {
  Harness h;
  h.transport->queue(201, kInvoiceJson);
  auto client = h.make();
  RequestOptions opts;
  opts.idempotency_key = "order-1-attempt-1";
  client->create_invoice(Json::object({{"asset", Json("btc")}}), opts);
  const std::string* key = h.transport->header(h.transport->last(), "idempotency-key");
  CHECK(key != nullptr);
  CHECK_EQ(*key, "order-1-attempt-1");
}

TEST(patch_and_delete_use_the_right_verbs) {
  Harness h;
  const char* forward = R"({"id":"f1","forward_id":"f1","ticker":"btc","provider":"bitcoind","chain":null,
    "address":"bc1in","destination":"bc1out","callback_url":null,"confirmations_required":3,
    "min_forward":"0.000100","status":"paused","expires_at":null,"created_at":"2026-01-01T00:00:00.000Z",
    "updated_at":"2026-01-02T00:00:00.000Z"})";
  h.transport->queue(200, forward);
  h.transport->queue(200, forward);
  auto client = h.make();

  client->update_address("f1", Json::object({{"status", Json("paused")}}));
  CHECK_EQ(h.transport->requests[0].method, "PATCH");
  CHECK_EQ(h.transport->requests[0].body, R"({"status":"paused"})");

  client->disable_address("f1");
  CHECK_EQ(h.transport->requests[1].method, "DELETE");
  CHECK(h.transport->requests[1].body.empty());
}

/* ------------------------------------------------------------------ *
 * Money stays a string
 * ------------------------------------------------------------------ */

TEST(money_survives_the_whole_round_trip_as_an_exact_string) {
  Harness h;
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  const auto invoice = client->get_invoice("11111111-2222-3333-4444-555555555555");

  CHECK_EQ(invoice.amount_expected, "2100000000000000000000000");
  CHECK_EQ(invoice.amount_received, "0");
  CHECK(invoice.amount_fiat.has_value());
  CHECK_EQ(*invoice.amount_fiat, "25.00");  // a DECIMAL string, still a string
  // The strongest form of this guarantee is a type check, not a value check: if anyone ever
  // "helpfully" changes these fields to a numeric type, the build stops here.
  static_assert(std::is_same<decltype(invoice.amount_expected), std::string>::value,
                "Invoice::amount_expected must be a std::string, never a numeric type");
  static_assert(std::is_same<decltype(invoice.amount_received), std::string>::value,
                "Invoice::amount_received must be a std::string, never a numeric type");
  static_assert(std::is_same<decltype(receivify::ForwardEventData().value), std::string>::value,
                "ForwardEventData::value must be a std::string, never a numeric type");
  static_assert(std::is_same<decltype(receivify::ForwardPayment().value), std::string>::value,
                "ForwardPayment::value must be a std::string, never a numeric type");
}

TEST(nulls_become_absent_optionals) {
  Harness h;
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  const auto invoice = client->get_invoice("x");
  CHECK(!invoice.paid_at.has_value());
  CHECK(!invoice.chain.has_value());
  CHECK(invoice.provider.has_value());
  CHECK_EQ(*invoice.provider, "bitcoind");
  CHECK_EQ(invoice.metadata["customer"].as_string(), "alice");
}

TEST(unmodelled_fields_are_still_reachable_through_raw) {
  Harness h;
  h.transport->queue(200, R"({"id":"i1","amount_expected":"5","some_future_field":"hello"})");
  auto client = h.make();
  const auto invoice = client->get_invoice("i1");
  CHECK_EQ(invoice.raw["some_future_field"].as_string(), "hello");
}

/* ------------------------------------------------------------------ *
 * The canonical forward id
 * ------------------------------------------------------------------ */

TEST(the_canonical_forward_id_is_id_and_forward_id_is_the_deprecated_alias) {
  Harness h;
  h.transport->queue(201, R"({"id":"canonical-id","forward_id":"canonical-id","ticker":"btc",
    "provider":"bitcoind","chain":null,"address":"bc1in","destination":"bc1out","callback_url":null,
    "confirmations_required":3,"min_forward":"0.000100","status":"active","expires_at":null,
    "created_at":"2026-01-01T00:00:00.000Z","updated_at":null})");
  auto client = h.make();
  const auto forward = client->create_address(
      Json::object({{"ticker", Json("btc")}, {"destination", Json("bc1out")}}));

  CHECK_EQ(forward.id, "canonical-id");
  CHECK_EQ(forward.forward_id, "canonical-id");  // same value, deprecated spelling
  CHECK_EQ(forward.min_forward, "0.000100");     // decimal display units, not base units
}

TEST(get_address_spreads_the_forward_fields_and_adds_the_capped_logs) {
  Harness h;
  h.transport->queue(200, R"({"id":"f1","forward_id":"f1","ticker":"btc","provider":"bitcoind",
    "chain":null,"address":"bc1in","destination":"bc1out","callback_url":"https://x/hook",
    "confirmations_required":3,"min_forward":"0.000100","status":"active","expires_at":null,
    "created_at":"2026-01-01T00:00:00.000Z","updated_at":null,
    "payments":[{"txid_in":"aa","value":"250000","status":"sent","txid_out":"bb","confirmations":6,
      "created_at":"2026-01-01T00:00:00.000Z"}],
    "payments_has_more":true,
    "callbacks":[{"event":"forward.sent","url":"https://x/hook","status":"delivered","attempts":1,
      "last_status":200,"last_error":null,"created_at":"2026-01-01T00:00:00.000Z",
      "delivered_at":"2026-01-01T00:00:01.000Z"}],
    "callbacks_has_more":false})");
  auto client = h.make();
  const auto detail = client->get_address("f1");

  CHECK_EQ(detail.forward.id, "f1");
  CHECK_EQ(detail.payments.size(), static_cast<std::size_t>(1));
  CHECK_EQ(detail.payments[0].value, "250000");
  CHECK(detail.payments_has_more);
  CHECK_EQ(detail.callbacks.size(), static_cast<std::size_t>(1));
  CHECK(detail.callbacks[0].last_status.has_value());
  CHECK_EQ(*detail.callbacks[0].last_status, static_cast<std::int64_t>(200));
  CHECK(!detail.callbacks_has_more);
}

/* ------------------------------------------------------------------ *
 * The list envelope and auto-pagination
 * ------------------------------------------------------------------ */

TEST(the_list_envelope_is_modelled_once) {
  Harness h;
  h.transport->queue(200, list_page(kInvoiceJson, true, "cursor-2"));
  auto client = h.make();
  const auto page = client->list_invoices();

  CHECK_EQ(page.data.size(), static_cast<std::size_t>(1));
  CHECK(page.has_more);
  CHECK(page.next_cursor.has_value());
  CHECK_EQ(*page.next_cursor, "cursor-2");
  CHECK_EQ(page.data[0].amount_expected, "2100000000000000000000000");
}

TEST(an_empty_page_still_carries_has_more_and_next_cursor) {
  Harness h;
  h.transport->queue(200, R"({"data":[],"has_more":false,"next_cursor":null})");
  auto client = h.make();
  const auto page = client->list_invoices();
  CHECK_EQ(page.data.size(), static_cast<std::size_t>(0));
  CHECK(!page.has_more);
  CHECK(!page.next_cursor.has_value());
}

TEST(auto_pagination_follows_next_cursor_to_the_end) {
  Harness h;
  h.transport->queue(200, list_page(kInvoiceJson, true, "cursor-2"));
  h.transport->queue(200, list_page(kInvoiceJson, true, "cursor-3"));
  h.transport->queue(200, list_page(kInvoiceJson, false, ""));
  auto client = h.make();

  int seen = 0;
  for (const auto& invoice : client->invoices()) {
    CHECK_EQ(invoice.amount_expected, "2100000000000000000000000");
    ++seen;
  }
  CHECK_EQ(seen, 3);
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(3));
  CHECK(h.transport->requests[0].url.find("cursor=") == std::string::npos);
  CHECK(h.transport->requests[1].url.find("cursor=cursor-2") != std::string::npos);
  CHECK(h.transport->requests[2].url.find("cursor=cursor-3") != std::string::npos);
}

TEST(auto_pagination_keeps_the_original_filters_on_every_page) {
  Harness h;
  h.transport->queue(200, list_page(kInvoiceJson, true, "cursor-2"));
  h.transport->queue(200, list_page(kInvoiceJson, false, ""));
  auto client = h.make();

  InvoiceListParams params;
  params.status = "paid";
  auto pages = client->invoices(params);
  for (const auto& invoice : pages) (void)invoice;

  CHECK(h.transport->requests[0].url.find("status=paid") != std::string::npos);
  CHECK(h.transport->requests[1].url.find("status=paid") != std::string::npos);
}

TEST(breaking_out_early_stops_fetching) {
  Harness h;
  h.transport->queue(200, list_page(kInvoiceJson, true, "cursor-2"));
  auto client = h.make();
  for (const auto& invoice : client->invoices()) {
    (void)invoice;
    break;
  }
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(1));
}

TEST(a_repeated_cursor_throws_instead_of_looping_forever) {
  Harness h;
  h.transport->queue(200, list_page(kInvoiceJson, true, "same"));
  h.transport->queue(200, list_page(kInvoiceJson, true, "same"));
  auto client = h.make();
  auto pages = client->invoices();
  CHECK_THROWS_AS(pages.collect(), receivify::ReceivifyError);
}

TEST(has_more_with_no_cursor_throws_instead_of_looping_forever) {
  Harness h;
  h.transport->queue(200, R"({"data":[],"has_more":true,"next_cursor":null})");
  auto client = h.make();
  auto pages = client->invoices();
  CHECK_THROWS_AS(pages.collect(), receivify::ReceivifyError);
}

TEST(collect_honours_its_maximum) {
  Harness h;
  h.transport->queue(200, list_page(kInvoiceJson, true, "c2"));
  h.transport->queue(200, list_page(kInvoiceJson, true, "c3"));
  auto client = h.make();
  const auto some = client->invoices().collect(2);
  CHECK_EQ(some.size(), static_cast<std::size_t>(2));
}

TEST(paginate_works_for_endpoints_with_no_typed_model) {
  Harness h;
  h.transport->queue(200, R"({"data":[{"id":"pl_1","amount_sats":"250000"}],"has_more":false,"next_cursor":null})");
  auto client = h.make();
  const auto links = client->paginate<receivify::RawObject>("/api/v1/payment-links").collect();
  CHECK_EQ(links.size(), static_cast<std::size_t>(1));
  CHECK_EQ(links[0].raw["amount_sats"].as_string(), "250000");
}

/* ------------------------------------------------------------------ *
 * Error mapping
 * ------------------------------------------------------------------ */

TEST(each_frozen_code_maps_to_its_own_exception_type) {
  Harness h;
  auto client = h.make();

  h.transport->queue(404, error_body("not_found", "No such invoice."));
  CHECK_THROWS_AS(client->get_invoice("nope"), receivify::NotFoundError);

  h.transport->queue(401, error_body("unauthorized", "Bad key."));
  CHECK_THROWS_AS(client->get_invoice("nope"), receivify::UnauthorizedError);

  h.transport->queue(409, error_body("not_allowed", "Already cancelled."));
  CHECK_THROWS_AS(client->cancel_invoice("nope"), receivify::NotAllowedError);

  h.transport->queue(400, error_body("bad_ticker", "Unknown ticker."));
  CHECK_THROWS_AS(client->create_address(Json::object()), receivify::BadTickerError);

  h.transport->queue(400, error_body("amount_too_low", "Below the floor."));
  CHECK_THROWS_AS(client->create_address(Json::object()), receivify::AmountTooLowError);

  // A 5xx on a GET is retried, so this one needs enough responses to exhaust max_retries.
  for (int i = 0; i < 3; ++i) h.transport->queue(502, error_body("upstream_error", "Node is down."));
  CHECK_THROWS_AS(client->get_invoice("nope"), receivify::UpstreamError);
}

TEST(every_typed_error_is_also_catchable_as_the_generic_one) {
  Harness h;
  h.transport->queue(404, error_body("not_found", "No such invoice."));
  auto client = h.make();
  try {
    client->get_invoice("nope");
    throw rtest::Failure("expected a throw");
  } catch (const ApiError& e) {
    CHECK_EQ(e.code(), "not_found");
    CHECK_EQ(e.detail(), "No such invoice.");
    CHECK_EQ(e.status(), 404);
    CHECK_EQ(std::string(e.what()), "No such invoice.");
    CHECK_EQ(e.request(), "GET /api/v1/invoices/nope");
    CHECK(!e.retryable());
  }
}

TEST(an_unknown_future_error_code_degrades_to_apierror_with_the_code_intact) {
  // This is the test that keeps an old SDK working against a newer deployment.
  Harness h;
  h.transport->queue(400, error_body("quantum_flux_exceeded", "A code from the future."));
  auto client = h.make();
  try {
    client->get_invoice("x");
    throw rtest::Failure("expected a throw");
  } catch (const ApiError& e) {
    CHECK_EQ(e.code(), "quantum_flux_exceeded");
    CHECK_EQ(e.detail(), "A code from the future.");
    CHECK_EQ(e.status(), 400);
  }
}

TEST(a_non_conforming_error_body_still_produces_a_typed_exception) {
  Harness h;
  for (int i = 0; i < 3; ++i) h.transport->queue(502, "<html><body>Bad Gateway</body></html>");
  auto client = h.make();
  try {
    client->get_invoice("x");
    throw rtest::Failure("expected a throw");
  } catch (const ApiError& e) {
    CHECK_EQ(e.code(), "");
    CHECK_EQ(e.status(), 502);
    CHECK_EQ(e.raw_body(), "<html><body>Bad Gateway</body></html>");
    CHECK(e.retryable());
  }
}

TEST(rate_limit_headers_are_parsed_onto_the_exception) {
  Harness h;
  receivify::Headers headers = {{"retry-after", "12"},
                                {"x-ratelimit-limit", "100"},
                                {"x-ratelimit-remaining", "0"},
                                {"x-ratelimit-reset", "1767225660"}};
  h.transport->queue(429, error_body("rate_limited", "Slow down."), headers);
  auto client = h.make();
  // A bare POST is never retried, so the 429 surfaces immediately.
  try {
    client->cancel_invoice("x");
    throw rtest::Failure("expected a throw");
  } catch (const receivify::RateLimitedError& e) {
    CHECK(e.retry_after().has_value());
    CHECK_EQ(*e.retry_after(), static_cast<std::int64_t>(12));
    CHECK_EQ(*e.rate_limit_reset(), static_cast<std::int64_t>(1767225660));
    CHECK_EQ(*e.rate_limit_remaining(), static_cast<std::int64_t>(0));
    CHECK(e.retryable());
  }
}

TEST(an_error_message_never_contains_the_api_key) {
  Harness h;
  h.transport->queue(401, error_body("unauthorized", "Bad key."));
  auto client = h.make();
  try {
    client->get_invoice("x");
  } catch (const ApiError& e) {
    CHECK(std::string(e.what()).find("rcv_live_") == std::string::npos);
    CHECK(e.request().find("rcv_live_") == std::string::npos);
  }
}

/* ------------------------------------------------------------------ *
 * Retry policy — the part that must never double-charge anyone
 * ------------------------------------------------------------------ */

TEST(a_get_is_retried_on_a_500) {
  Harness h;
  h.transport->queue(500, error_body("internal", "Unexpected error."));
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  const auto invoice = client->get_invoice("x");
  CHECK_EQ(invoice.id, "11111111-2222-3333-4444-555555555555");
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(2));
  CHECK_EQ(h.slept.size(), static_cast<std::size_t>(1));
}

TEST(a_get_is_retried_on_a_transport_failure) {
  Harness h;
  h.transport->fail_first = 1;
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  CHECK_NO_THROW(client->get_invoice("x"));
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(2));
}

TEST(retries_stop_at_max_retries_and_then_the_error_surfaces) {
  Harness h;
  for (int i = 0; i < 5; ++i) h.transport->queue(500, error_body("internal", "Unexpected error."));
  auto client = h.make();
  CHECK_THROWS_AS(client->get_invoice("x"), receivify::InternalError);
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(3));  // 1 + 2 retries
}

TEST(a_bare_post_is_NEVER_retried_because_that_is_how_you_charge_someone_twice) {
  Harness h;
  h.transport->queue(500, error_body("internal", "Unexpected error."));
  h.transport->queue(201, kInvoiceJson);  // would succeed on a retry — must not be reached
  auto client = h.make();
  CHECK_THROWS_AS(client->create_invoice(Json::object({{"asset", Json("btc")}})), receivify::InternalError);
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(1));
}

TEST(a_post_WITH_an_idempotency_key_is_retried) {
  Harness h;
  h.transport->queue(500, error_body("internal", "Unexpected error."));
  h.transport->queue(201, kInvoiceJson);
  auto client = h.make();
  RequestOptions opts;
  opts.idempotency_key = "order-1";
  CHECK_NO_THROW(client->create_invoice(Json::object({{"asset", Json("btc")}}), opts));
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(2));
}

TEST(a_post_to_an_endpoint_that_does_NOT_declare_idempotency_is_not_retried_even_with_a_key) {
  // `POST /api/v1/invoices/{id}/cancel` takes no `Idempotency-Key`; a second call is 409. Sending
  // a key we invented would not make the server deduplicate, so we must not repeat the call.
  Harness h;
  h.transport->queue(500, error_body("internal", "Unexpected error."));
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  RequestOptions opts;
  opts.idempotency_key = "wishful-thinking";
  CHECK_THROWS_AS(client->cancel_invoice("x", opts), receivify::InternalError);
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(1));
}

TEST(a_4xx_that_is_not_429_is_never_retried_even_on_a_get) {
  Harness h;
  h.transport->queue(404, error_body("not_found", "Nope."));
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  CHECK_THROWS_AS(client->get_invoice("x"), receivify::NotFoundError);
  CHECK_EQ(h.transport->requests.size(), static_cast<std::size_t>(1));
}

TEST(backoff_honours_retry_after) {
  Harness h;
  h.transport->queue(429, error_body("rate_limited", "Slow down."), {{"retry-after", "7"}});
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  CHECK_NO_THROW(client->get_invoice("x"));
  CHECK_EQ(h.slept.size(), static_cast<std::size_t>(1));
  CHECK_EQ(h.slept[0].count(), static_cast<std::chrono::milliseconds::rep>(7000));
}

TEST(backoff_falls_back_to_x_ratelimit_reset) {
  Harness h;  // the injected clock is frozen at 1767225600
  h.transport->queue(429, error_body("rate_limited", "Slow down."), {{"x-ratelimit-reset", "1767225615"}});
  h.transport->queue(200, kInvoiceJson);
  auto client = h.make();
  CHECK_NO_THROW(client->get_invoice("x"));
  CHECK_EQ(h.slept[0].count(), static_cast<std::chrono::milliseconds::rep>(15000));
}

/* ------------------------------------------------------------------ *
 * Escape hatch and response metadata
 * ------------------------------------------------------------------ */

TEST(the_raw_request_escape_hatch_reaches_unwrapped_endpoints) {
  Harness h;
  h.transport->queue(201, R"({"id":"pl_abc","url":"https://receivify.io/l/pl_abc"})");
  auto client = h.make();

  RequestSpec spec;
  spec.method = "POST";
  spec.path = "/api/v1/payment-links";
  spec.body = Json::object({{"title", Json("Coffee")}, {"amount_btc", Json("0.0005")}});
  spec.declares_idempotency = true;

  const auto response = client->request(spec);
  CHECK_EQ(response.status, 201);
  CHECK_EQ(response.body["id"].as_string(), "pl_abc");
  CHECK_EQ(h.transport->last().body, R"({"title":"Coffee","amount_btc":"0.0005"})");
}

TEST(idempotent_replay_is_visible_on_the_response) {
  Harness h;
  h.transport->queue(201, kInvoiceJson, {{"idempotent-replay", "true"}});
  auto client = h.make();
  RequestSpec spec;
  spec.method = "POST";
  spec.path = "/api/v1/invoices";
  spec.body = Json::object({{"asset", Json("btc")}});
  spec.declares_idempotency = true;
  RequestOptions opts;
  opts.idempotency_key = "k";
  CHECK(client->request(spec, opts).idempotent_replay());
}

TEST(a_201_and_a_200_are_distinguishable_on_create_address) {
  // `POST /api/v1/address` answers 201 for a newly derived address and 200 for an existing one.
  Harness h;
  h.transport->queue(200, R"({"id":"f1","forward_id":"f1"})");
  auto client = h.make();
  RequestSpec spec;
  spec.method = "POST";
  spec.path = "/api/v1/address";
  spec.body = Json::object({{"ticker", Json("btc")}, {"destination", Json("bc1")}});
  CHECK_EQ(client->request(spec).status, 200);
}

TEST(a_204_with_no_body_does_not_break_anything) {
  Harness h;
  h.transport->queue(204, "");
  auto client = h.make();
  RequestSpec spec;
  spec.method = "DELETE";
  spec.path = "/api/v1/webhooks/endpoints/e1";
  const auto response = client->request(spec);
  CHECK_EQ(response.status, 204);
  CHECK(response.body.is_null());
}

/* ------------------------------------------------------------------ *
 * Webhook methods on the client
 * ------------------------------------------------------------------ */

TEST(the_client_verifies_against_its_configured_secret_list) {
  Harness h;
  auto client = h.make({"whsec_current", "whsec_previous"});
  const std::string body = R"({"event":"invoice.paid","sequence":1,"created_at":"x","data":{}})";
  rtest::StubHmac stub;

  receivify::VerifyOptions options;
  options.now_seconds = 1767225600;

  CHECK(client->verify_webhook(body, receivify::sign_webhook(body, "whsec_current", stub, 1767225600), options));
  CHECK(client->verify_webhook(body, receivify::sign_webhook(body, "whsec_previous", stub, 1767225600), options));
  CHECK(!client->verify_webhook(body, receivify::sign_webhook(body, "whsec_other", stub, 1767225600), options));
}

TEST(rotate_webhook_secret_returns_the_secret_once) {
  Harness h;
  h.transport->queue(200, R"({"secret":"whsec_brand_new","rotated_at":"2026-01-01T00:00:00.000Z",
    "prev_expires_at":"2026-01-02T00:00:00.000Z","grace_seconds":86400,"replaced_previous":false,
    "message":"Rotated."})");
  auto client = h.make();
  const auto rotated = client->rotate_webhook_secret();
  CHECK(rotated.secret.has_value());
  CHECK_EQ(*rotated.secret, "whsec_brand_new");
  CHECK_EQ(rotated.grace_seconds, static_cast<std::int64_t>(86400));
  CHECK_EQ(h.transport->last().method, "POST");
}

TEST(a_replayed_rotation_returns_no_secret_and_that_is_not_an_error) {
  Harness h;
  h.transport->queue(200, R"({"secret":null,"rotated_at":"2026-01-01T00:00:00.000Z",
    "prev_expires_at":"2026-01-02T00:00:00.000Z","grace_seconds":86400,"replaced_previous":false,
    "message":"Replayed."})", {{"idempotent-replay", "true"}});
  auto client = h.make();
  const auto rotated = client->rotate_webhook_secret();
  CHECK(!rotated.secret.has_value());
  CHECK_EQ(rotated.rotated_at, "2026-01-01T00:00:00.000Z");
}

TEST(the_secret_state_endpoint_says_when_the_old_secret_can_be_dropped) {
  Harness h;
  h.transport->queue(200, R"({"rotated_at":"2026-01-01T00:00:00.000Z","prev_expires_at":null,"prev_active":false})");
  auto client = h.make();
  const auto state = client->get_webhook_secret_state();
  CHECK(!state.prev_active);
  CHECK(!state.prev_expires_at.has_value());
}

TEST(verifying_without_an_hmac_provider_is_a_clear_configuration_error) {
  auto transport = std::make_shared<rtest::FakeTransport>();
  ClientOptions opts;
  opts.transport = transport;
  opts.webhook_secrets = {"whsec_x"};
  // opts.hmac deliberately left null
  Client client("https://receivify.io", "k", opts);
  CHECK_THROWS_AS(client.verify_webhook("{}", "t=1,v1=aa"), ConfigurationError);
}

int main() { return rtest::run_all("client"); }
