// Receivify C++ SDK — quickstart.
//
//   RECEIVIFY_API_URL=https://receivify.io RECEIVIFY_API_KEY=rcv_live_... ./receivify_quickstart
//
// Creates a forwarding address, lists a few invoices, and verifies a webhook offline.

// MSVC deprecates std::getenv in favour of a non-portable alternative. This is an example, and
// std::getenv is standard C++.
#ifdef _MSC_VER
#define _CRT_SECURE_NO_WARNINGS
#endif

#include <cstdlib>
#include <iostream>
#include <string>

#include "receivify/receivify.hpp"

namespace {

std::string env(const char* name, const char* fallback = "") {
  const char* value = std::getenv(name);
  return (value != nullptr && *value != '\0') ? value : fallback;
}

}  // namespace

int main() {
  const std::string api_url = env("RECEIVIFY_API_URL", "https://receivify.io");
  const std::string api_key = env("RECEIVIFY_API_KEY");
  const std::string webhook_secret = env("RECEIVIFY_WEBHOOK_SECRET", "whsec_example");

  receivify::ClientOptions options;
  options.webhook_secrets = {webhook_secret};  // current first, previous second
  options.user_agent_suffix = "receivify-quickstart/1.0";

  auto client = receivify::make_client(api_url, api_key, options);

  try {
    // ---------------------------------------------------------------- 1. The catalogue
    std::cout << "Tickers offered for forwarding:\n";
    int shown = 0;
    for (const receivify::Ticker& ticker : client.list_tickers().data) {
      std::cout << "  " << ticker.ticker << "  " << ticker.name << "  (" << ticker.decimals << " decimals)\n";
      if (++shown == 5) break;
    }

    if (api_key.empty()) {
      std::cout << "\nSet RECEIVIFY_API_KEY to run the authenticated part.\n";
      return 0;
    }

    // ---------------------------------------------------------------- 2. A forwarding address
    //
    // Creation is deterministic: the same (ticker, destination, callback) always returns the same
    // forward, so this is safe to call on every startup.
    const receivify::Forward forward = client.create_address(receivify::Json::object({
        {"ticker", receivify::Json("btc")},
        {"destination", receivify::Json("bc1qyourcoldwalletaddressgoeshere")},
        {"callback", receivify::Json("https://example.com/receivify/webhook")},
    }));

    std::cout << "\nForward " << forward.id << "\n"           // `.id` is canonical; `.forward_id` is deprecated
              << "  send " << forward.ticker << " to " << forward.address << "\n"
              << "  forwards to " << forward.destination << "\n"
              << "  status " << forward.status << "\n";

    // ---------------------------------------------------------------- 3. An invoice
    //
    // Amounts are STRINGS. `receivify::Json` refuses to hold a double at compile time.
    receivify::RequestOptions request_options;
    request_options.idempotency_key = "quickstart-invoice-001";  // makes the POST retry-safe

    const receivify::Invoice invoice = client.create_invoice(
        receivify::Json::object({
            {"asset", receivify::Json("btc")},
            {"amount_sats", receivify::Json("250000")},
            {"reference", receivify::Json("order-1001")},
            {"metadata", receivify::Json::object({{"customer", receivify::Json("alice")}})},
        }),
        request_options);

    std::cout << "\nInvoice " << invoice.id << "\n"
              << "  expects " << invoice.amount_expected << " base units"
              << " (" << receivify::units::format_units(invoice.amount_expected, 8) << " BTC)\n"
              << "  pay to  " << invoice.address << "\n"
              << "  checkout " << invoice.checkout_url << "\n";

    // ---------------------------------------------------------------- 4. Auto-pagination
    std::cout << "\nRecent invoices:\n";
    int listed = 0;
    for (const receivify::Invoice& item : client.invoices()) {
      std::cout << "  " << item.id << "  " << item.status << "  " << item.amount_expected << "\n";
      if (++listed == 10) break;  // breaking out stops the HTTP traffic
    }
  } catch (const receivify::RateLimitedError& e) {
    std::cerr << "Rate limited; retry after " << (e.retry_after() ? *e.retry_after() : 0) << "s\n";
    return 1;
  } catch (const receivify::ApiError& e) {
    // Branch on `code()`, never on `detail()` — the wording is not part of the frozen contract.
    std::cerr << "API error [" << e.code() << "] " << e.detail() << " (HTTP " << e.status() << ")\n";
    return 1;
  } catch (const receivify::TransportError& e) {
    std::cerr << "Transport error: " << e.what() << (e.timed_out() ? " (timed out)" : "") << "\n";
    return 1;
  }

  // ------------------------------------------------------------------ 5. Receiving a webhook
  //
  // In your HTTP handler, capture the body as raw BYTES before anything parses it — re-serialising
  // the JSON changes key order and breaks the HMAC. Then:
  {
    const std::string raw_body =
        R"({"event":"invoice.paid","sequence":42,"created_at":"2026-01-01T00:00:00.000Z",)"
        R"("data":{"id":"inv-1","status":"paid","amount_expected":"250000","amount_received":"250000"}})";

    // Only for this demo: normally the header arrives in the request.
    const std::string header =
        receivify::sign_webhook(raw_body, webhook_secret, *receivify::default_hmac());

    try {
      const receivify::WebhookEvent event = client.parse_webhook(raw_body, header);
      std::cout << "\nWebhook " << event.event << " (sequence " << event.sequence << ")\n";

      if (receivify::is_invoice_event(event.event)) {
        const auto data = receivify::InvoiceEventData::from_json(event.data);
        // Compare amounts as strings. Never as doubles.
        const bool fully_paid = receivify::units::amount_at_least(data.amount_received, data.amount_expected);
        std::cout << "  invoice " << data.id << " received " << data.amount_received
                  << " of " << data.amount_expected << (fully_paid ? "  [PAID]\n" : "  [PARTIAL]\n");
      } else if (receivify::is_forward_event(event.event)) {
        const auto data = receivify::ForwardEventData::from_json(event.data);
        std::cout << "  forward " << data.forward_id << " sent " << data.value_forwarded
                  << " (fee " << data.fee << ")\n";
      }
      // Delivery is at-least-once and can arrive out of order: store the highest `sequence` you
      // have applied per resource and ignore anything at or below it.
    } catch (const receivify::WebhookError& e) {
      std::cerr << "Rejected webhook [" << e.reason_string() << "] " << e.what() << "\n";
      return 1;
    }
  }

  return 0;
}
