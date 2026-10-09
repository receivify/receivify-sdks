// The JSON parser is the highest-risk part of a C++ SDK that refuses to vendor a dependency, so
// it gets the most tests. The two properties that matter most:
//
//   - a big integer survives parse -> read -> serialise byte-for-byte;
//   - a pathological input throws instead of misbehaving.
#include <string>
#include <type_traits>

#include "receivify/json.hpp"
#include "test_harness.hpp"

using receivify::Json;
using receivify::JsonError;
using receivify::json_parse;

/* ------------------------------------------------------------------ *
 * Precision — the whole point
 * ------------------------------------------------------------------ */

TEST(money_string_survives_verbatim) {
  const Json j = json_parse(R"({"amount_expected":"2100000000000000000000000"})");
  CHECK_EQ(j["amount_expected"].as_string(), "2100000000000000000000000");
  CHECK(j["amount_expected"].is_string());
}

TEST(unquoted_big_integer_is_never_converted) {
  // The API always quotes money. If a deployment ever did not, `as_string()` STILL hands back the
  // exact digits, because a Number is stored as its source lexeme and nothing converts it.
  const std::string huge = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
  const Json j = json_parse("{\"wei\":" + huge + "}");
  CHECK(j["wei"].is_number());
  CHECK_EQ(j["wei"].as_string(), huge);
  CHECK_EQ(j.dump(), "{\"wei\":" + huge + "}");
}

TEST(satoshi_value_above_2_to_53_round_trips) {
  // 2^53 + 1. A double cannot represent this; it becomes 9007199254740992.
  const Json j = json_parse(R"({"v":"9007199254740993"})");
  CHECK_EQ(j["v"].as_string(), "9007199254740993");
  CHECK_EQ(j.dump(), R"({"v":"9007199254740993"})");
}

TEST(as_int64_rejects_values_that_do_not_fit) {
  const Json j = json_parse(R"({"v":99999999999999999999})");
  CHECK_THROWS_AS(j["v"].as_int64(), JsonError);
  CHECK_EQ(j["v"].as_int64_or(-1), static_cast<std::int64_t>(-1));
  CHECK_EQ(j["v"].as_string(), "99999999999999999999");  // still exact
}

TEST(doubles_cannot_be_written_into_a_request_body) {
  // `Json(double)` is deleted, so this is a COMPILE error rather than a runtime surprise:
  //     Json bad(1.5);                       // does not compile
  //     Json::object({{"amount", Json(0.1)}}); // does not compile
  // The only way to emit a non-integer number is to say so explicitly:
  CHECK_EQ(Json::raw_number("1.5").dump(), "1.5");
  static_assert(!std::is_constructible<Json, double>::value, "Json must refuse doubles");
  static_assert(!std::is_constructible<Json, float>::value, "Json must refuse floats");
}

TEST(as_double_works_for_fx_rates_which_are_legitimately_numbers) {
  const Json j = json_parse(R"({"fx":{"usd":64213.5}})");
  CHECK(j["fx"]["usd"].as_double() > 64213.0);
  CHECK(j["fx"]["usd"].as_double() < 64214.0);
}

/* ------------------------------------------------------------------ *
 * Escapes — the other classic parser bug
 * ------------------------------------------------------------------ */

TEST(simple_escapes) {
  const Json j = json_parse(R"({"s":"a\"b\\c\/d\be\ff\ng\rh\ti"})");
  CHECK_EQ(j["s"].as_string(), std::string("a\"b\\c/d\be\ff\ng\rh\ti"));
}

TEST(unicode_escape_becomes_utf8) {
  const Json j = json_parse(R"({"s":"\u00e9\u20ac"})");
  // U+00E9 -> C3 A9, U+20AC -> E2 82 AC
  CHECK_EQ(j["s"].as_string(), std::string("\xC3\xA9\xE2\x82\xAC"));
}

TEST(surrogate_pair_becomes_one_astral_codepoint) {
  const Json j = json_parse(R"({"s":"\ud83d\ude00"})");  // U+1F600
  CHECK_EQ(j["s"].as_string(), std::string("\xF0\x9F\x98\x80"));
  CHECK_EQ(j["s"].as_string().size(), static_cast<std::size_t>(4));
}

TEST(lone_high_surrogate_becomes_replacement_char_not_garbage) {
  const Json j = json_parse(R"({"s":"\ud83dX"})");
  CHECK_EQ(j["s"].as_string(), std::string("\xEF\xBF\xBDX"));  // U+FFFD then 'X'
}

TEST(lone_low_surrogate_becomes_replacement_char) {
  const Json j = json_parse(R"({"s":"\ude00"})");
  CHECK_EQ(j["s"].as_string(), std::string("\xEF\xBF\xBD"));
}

TEST(high_surrogate_followed_by_non_low_surrogate_escape_keeps_both) {
  const Json j = json_parse(R"({"s":"\ud83d\u0041"})");
  CHECK_EQ(j["s"].as_string(), std::string("\xEF\xBF\xBD") + "A");
}

TEST(raw_control_character_in_string_is_rejected) {
  CHECK_THROWS_AS(json_parse(std::string("{\"s\":\"a\nb\"}")), JsonError);
}

TEST(serialiser_escapes_what_it_must_and_nothing_else) {
  const std::string value = "quote:\" back:\\ nl:\n tab:\t ctl:\x01 utf8:\xC3\xA9";
  Json j = Json::object();
  j.set("s", Json(value));
  const std::string dumped = j.dump();

  // Everything that MUST be escaped, is.
  CHECK(dumped.find("quote:\\\"") != std::string::npos);
  CHECK(dumped.find("back:\\\\") != std::string::npos);
  CHECK(dumped.find("nl:\\n") != std::string::npos);
  CHECK(dumped.find("tab:\\t") != std::string::npos);
  CHECK(dumped.find("ctl:\\u0001") != std::string::npos);
  // ...and valid UTF-8 passes through byte-for-byte rather than being escaped, so a body we
  // emit stays byte-identical to what a UTF-8-native caller handed us.
  CHECK(dumped.find("utf8:\xC3\xA9") != std::string::npos);
  // The dump is valid JSON, and it parses back to exactly the original bytes.
  CHECK_EQ(json_parse(dumped)["s"].as_string(), value);
}

TEST(escape_round_trip) {
  const std::string original = R"({"s":"\u00e9 \" \\ \n \ud83d\ude00"})";
  const Json once = json_parse(original);
  const Json twice = json_parse(once.dump());
  CHECK_EQ(twice["s"].as_string(), once["s"].as_string());
}

/* ------------------------------------------------------------------ *
 * Depth — a hand-rolled recursive parser MUST bound this
 * ------------------------------------------------------------------ */

TEST(deep_nesting_throws_instead_of_overflowing_the_stack) {
  std::string deep;
  for (int i = 0; i < 5000; ++i) deep += "[";
  for (int i = 0; i < 5000; ++i) deep += "]";
  CHECK_THROWS_AS(json_parse(deep), JsonError);
}

TEST(nesting_just_under_the_limit_is_fine) {
  receivify::ParseOptions opts;
  opts.max_depth = 10;
  std::string nested;
  for (int i = 0; i < 10; ++i) nested += "[";
  nested += "1";
  for (int i = 0; i < 10; ++i) nested += "]";
  CHECK_NO_THROW(json_parse(nested, opts));
}

TEST(nesting_just_over_the_limit_throws) {
  receivify::ParseOptions opts;
  opts.max_depth = 10;
  std::string nested;
  for (int i = 0; i < 12; ++i) nested += "[";
  nested += "1";
  for (int i = 0; i < 12; ++i) nested += "]";
  CHECK_THROWS_AS(json_parse(nested, opts), JsonError);
}

/* ------------------------------------------------------------------ *
 * Strictness
 * ------------------------------------------------------------------ */

TEST(malformed_documents_are_rejected) {
  const char* bad[] = {
      "",             "{",           "}",            "[1,]",        "{\"a\":1,}",  "{'a':1}",
      "{a:1}",        "{\"a\" 1}",   "[1 2]",        "01",          "+1",          ".5",
      "1.",           "1e",          "1e+",          "tru",         "nul",         "NaN",
      "Infinity",     "\"unterminated",              "{\"a\":}",    "[",           "]",
      "{\"a\":1}x",   "1 2",         "\"a\"\"b\"",   "--1",         "\"\\x\"",     "\"\\u12\"",
  };
  for (const char* text : bad) {
    bool threw = false;
    try {
      json_parse(text);
    } catch (const JsonError&) {
      threw = true;
    }
    if (!threw) throw rtest::Failure(std::string("expected a JsonError for input: ") + text);
  }
}

TEST(valid_edge_cases_are_accepted) {
  CHECK_EQ(json_parse("0").as_string(), "0");
  CHECK_EQ(json_parse("-0").as_string(), "-0");
  CHECK_EQ(json_parse("1e10").as_string(), "1e10");
  CHECK_EQ(json_parse("-1.5E-3").as_string(), "-1.5E-3");
  CHECK(json_parse("null").is_null());
  CHECK_EQ(json_parse("true").as_bool(), true);
  CHECK_EQ(json_parse("[]").size(), static_cast<std::size_t>(0));
  CHECK_EQ(json_parse("{}").size(), static_cast<std::size_t>(0));
  CHECK_EQ(json_parse("  \t\r\n {\"a\" : 1} \n ")["a"].as_int64(), static_cast<std::int64_t>(1));
  CHECK_EQ(json_parse("\"\"").as_string(), "");
}

TEST(duplicate_keys_last_one_wins_and_order_is_preserved) {
  const Json j = json_parse(R"({"a":1,"b":2,"a":3})");
  CHECK_EQ(j["a"].as_string(), "3");
  CHECK_EQ(j.keys().size(), static_cast<std::size_t>(2));
  CHECK_EQ(j.keys()[0], "a");
  CHECK_EQ(j.keys()[1], "b");
}

/* ------------------------------------------------------------------ *
 * Navigation and building
 * ------------------------------------------------------------------ */

TEST(missing_keys_read_as_null_so_new_api_fields_never_crash_an_old_sdk) {
  const Json j = json_parse(R"({"a":1})");
  CHECK(j["nope"].is_null());
  CHECK(j["nope"]["deeper"].is_null());
  CHECK_EQ(j["nope"].as_string_or("fallback"), "fallback");
  CHECK_EQ(j["nope"].as_int64_or(7), static_cast<std::int64_t>(7));
  CHECK_EQ(j["nope"].as_bool_or(true), true);
  CHECK_THROWS_AS(j.at("nope"), JsonError);
  CHECK_THROWS_AS(j["nope"].as_string(), JsonError);
}

TEST(array_indexing_is_bounds_safe) {
  const Json j = json_parse(R"([10,20])");
  CHECK_EQ(j[static_cast<std::size_t>(0)].as_string(), "10");
  CHECK(j[static_cast<std::size_t>(9)].is_null());
}

TEST(object_builder_preserves_insertion_order_and_replaces_in_place) {
  Json j = Json::object();
  j.set("b", Json("second"));
  j.set("a", Json("first"));
  j.set("b", Json("replaced"));
  CHECK_EQ(j.dump(), R"({"b":"replaced","a":"first"})");
}

TEST(object_initialiser_list_builds_a_request_body) {
  const Json body = Json::object({
      {"asset", Json("btc")},
      {"amount_sats", Json("250000")},
      {"reference", Json("order-99")},
      {"metadata", Json::object({{"customer", Json("alice")}})},
  });
  CHECK_EQ(body.dump(),
           R"({"asset":"btc","amount_sats":"250000","reference":"order-99","metadata":{"customer":"alice"}})");
}

TEST(array_builder) {
  Json arr = Json::array({Json("a"), Json(1), Json(true), Json(nullptr)});
  CHECK_EQ(arr.dump(), R"(["a",1,true,null])");
}

int main() { return rtest::run_all("json"); }
