// Money conversion. Every one of these numbers is chosen to be a value a double would ruin.
#include <string>

#include "receivify/units.hpp"
#include "test_harness.hpp"

using receivify::ReceivifyError;
using receivify::units::amount_at_least;
using receivify::units::compare_amounts;
using receivify::units::format_units;
using receivify::units::parse_units;

TEST(format_units_satoshis) {
  CHECK_EQ(format_units("123456", 8), "0.00123456");
  CHECK_EQ(format_units("100000000", 8), "1.00000000");
  CHECK_EQ(format_units("0", 8), "0.00000000");
  CHECK_EQ(format_units("1", 8), "0.00000001");
  CHECK_EQ(format_units("2100000000000000", 8), "21000000.00000000");
}

TEST(format_units_wei_which_no_integer_type_could_hold) {
  // 1 ETH, and then a value larger than a uint64 can represent.
  CHECK_EQ(format_units("1000000000000000000", 18), "1.000000000000000000");
  // 2^256 - 1, the largest value an EVM `uint256` can hold: 78 digits, so 60 before the point
  // and 18 after. No integer type in C++ can hold this; string arithmetic does not care.
  CHECK_EQ(format_units("115792089237316195423570985008687907853269984665640564039457584007913129639935", 18),
           "115792089237316195423570985008687907853269984665640564039457.584007913129639935");
}

TEST(format_units_zero_decimals_and_negatives) {
  CHECK_EQ(format_units("500", 0), "500");
  CHECK_EQ(format_units("-500", 0), "-500");
  CHECK_EQ(format_units("-123456", 8), "-0.00123456");
  CHECK_EQ(format_units("-0", 8), "0.00000000");  // negative zero is just zero
}

TEST(format_units_rejects_non_integers) {
  CHECK_THROWS_AS(format_units("1.5", 8), ReceivifyError);
  CHECK_THROWS_AS(format_units("", 8), ReceivifyError);
  CHECK_THROWS_AS(format_units("abc", 8), ReceivifyError);
  CHECK_THROWS_AS(format_units("-", 8), ReceivifyError);
  CHECK_THROWS_AS(format_units("100", -1), ReceivifyError);
}

TEST(parse_units_round_trips) {
  CHECK_EQ(parse_units("0.00123456", 8), "123456");
  CHECK_EQ(parse_units("1", 8), "100000000");
  CHECK_EQ(parse_units("1.0", 8), "100000000");
  CHECK_EQ(parse_units(".5", 8), "50000000");
  CHECK_EQ(parse_units("0", 8), "0");
  CHECK_EQ(parse_units("0.0", 8), "0");
  CHECK_EQ(parse_units("-0.00123456", 8), "-123456");
  CHECK_EQ(parse_units("21000000", 8), "2100000000000000");
}

TEST(parse_units_refuses_to_round_a_monetary_value) {
  // Nine fraction digits for an eight-decimal asset. Silently truncating this is exactly the bug
  // class this SDK exists to prevent, so it is an exception rather than a rounded answer.
  CHECK_THROWS_AS(parse_units("0.000000001", 8), ReceivifyError);
  CHECK_THROWS_AS(parse_units("1.123456789", 8), ReceivifyError);
}

TEST(parse_units_rejects_junk) {
  CHECK_THROWS_AS(parse_units("", 8), ReceivifyError);
  CHECK_THROWS_AS(parse_units(".", 8), ReceivifyError);
  CHECK_THROWS_AS(parse_units("-", 8), ReceivifyError);
  CHECK_THROWS_AS(parse_units("1.2.3", 8), ReceivifyError);
  CHECK_THROWS_AS(parse_units("1e5", 8), ReceivifyError);
  CHECK_THROWS_AS(parse_units("abc", 8), ReceivifyError);
}

TEST(format_then_parse_is_the_identity) {
  const char* values[] = {"0", "1", "123456", "2100000000000000", "999999999999999999999999999"};
  for (const char* v : values) {
    CHECK_EQ(parse_units(format_units(v, 18), 18), v);
  }
}

TEST(compare_amounts_beyond_every_integer_type) {
  // These two differ in the last digit and are far past 2^64. A double would call them equal.
  const std::string a = "115792089237316195423570985008687907853269984665640564039457584007913129639934";
  const std::string b = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
  CHECK_EQ(compare_amounts(a, b), -1);
  CHECK_EQ(compare_amounts(b, a), 1);
  CHECK_EQ(compare_amounts(a, a), 0);
}

TEST(compare_amounts_handles_zeros_and_signs) {
  CHECK_EQ(compare_amounts("0", "-0"), 0);
  CHECK_EQ(compare_amounts("0", "0000"), 0);
  CHECK_EQ(compare_amounts("-1", "1"), -1);
  CHECK_EQ(compare_amounts("-5", "-3"), -1);
  CHECK_EQ(compare_amounts("10", "9"), 1);
  CHECK_EQ(compare_amounts("0009", "9"), 0);
}

TEST(amount_at_least_is_the_underpayment_check) {
  CHECK(amount_at_least("100000", "100000"));
  CHECK(amount_at_least("100001", "100000"));
  CHECK(!amount_at_least("99999", "100000"));
  // 2^53 + 1 vs 2^53: identical as doubles, correctly distinguished here.
  CHECK(!amount_at_least("9007199254740992", "9007199254740993"));
  CHECK(amount_at_least("9007199254740993", "9007199254740992"));
}

int main() { return rtest::run_all("units"); }
