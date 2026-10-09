// A second translation unit that includes the whole SDK.
//
// It exists to catch the classic header-only bug: a free function or a static that is missing
// `inline`, which compiles fine in one TU and fails at link time with "multiple definition" the
// moment a real project includes the SDK from two files. Linking this alongside test_client.cpp
// makes that a build failure here rather than a build failure in a customer's project.
#include <string>

#include "receivify/client.hpp"
#include "receivify/units.hpp"

namespace second_tu {

/// Touch enough of the surface that the interesting symbols are actually emitted.
std::string exercise_everything() {
  std::string out;
  out += receivify::user_agent();
  out += receivify::error_codes().front();
  out += receivify::event_names().front();
  out += receivify::signature_header_name();
  out += receivify::to_string(receivify::WebhookRejection::kNoMatch);
  out += receivify::Json::object({{"a", receivify::Json("b")}}).dump();
  out += receivify::json_parse("{\"n\":1}")["n"].as_string();
  out += receivify::units::format_units("123456", 8);
  out += receivify::units::parse_units("0.00123456", 8);
  out += std::to_string(receivify::units::compare_amounts("2", "1"));
  out += receivify::detail::url_encode("a/b");
  out += receivify::detail::hex_encode(reinterpret_cast<const unsigned char*>("\x01\x02"), 2);
  out += receivify::status::kPaid;
  out += std::to_string(receivify::kDefaultToleranceSeconds);
  out += receivify::Invoice::from_json(receivify::json_parse("{\"id\":\"x\"}")).id;
  out += receivify::Forward::from_json(receivify::json_parse("{\"id\":\"y\"}")).id;
  out += receivify::Ticker::from_json(receivify::json_parse("{\"ticker\":\"btc\"}")).ticker;
  return out;
}

}  // namespace second_tu
