// Opt-in money helpers.
//
// **The SDK never calls these for you.** Every monetary field stays exactly the `std::string` the
// API sent, because a double cannot hold a satoshi count above 2^53, let alone a 256-bit wei
// value. Convert only at the edge where you render, and only with these functions — every one of
// them is pure string arithmetic, so no float ever touches a value.
//
// `decimals` comes from `GET /api/v1/tickers` (`Ticker::decimals`).
#pragma once

#include <algorithm>
#include <string>

#include "receivify/errors.hpp"

namespace receivify {
namespace units {

namespace detail_units {

inline bool is_integer_string(const std::string& s) {
  if (s.empty()) return false;
  std::size_t i = (s[0] == '-') ? 1 : 0;
  if (i >= s.size()) return false;
  for (; i < s.size(); ++i) {
    if (s[i] < '0' || s[i] > '9') return false;
  }
  return true;
}

inline std::string strip_leading_zeros(const std::string& s) {
  std::size_t i = 0;
  while (i + 1 < s.size() && s[i] == '0') ++i;
  return s.substr(i);
}

inline void check_decimals(int decimals) {
  if (decimals < 0 || decimals > 100) {
    throw ReceivifyError("units: decimals must be in 0..100, got " + std::to_string(decimals));
  }
}

/// Compare two non-negative digit strings (no sign, no leading zeros beyond one).
inline int compare_magnitude(const std::string& a, const std::string& b) {
  const std::string x = strip_leading_zeros(a);
  const std::string y = strip_leading_zeros(b);
  if (x.size() != y.size()) return x.size() < y.size() ? -1 : 1;
  if (x == y) return 0;
  return x < y ? -1 : 1;
}

}  // namespace detail_units

/// Exact base units (`"123456"`) -> a display decimal string.
///
///     format_units("123456", 8) == "0.00123456"
///     format_units("2100000000000000000000000", 18) == "2100000.000000000000000000"
inline std::string format_units(const std::string& base, int decimals) {
  detail_units::check_decimals(decimals);
  if (!detail_units::is_integer_string(base)) {
    throw ReceivifyError("format_units: expected an integer base-unit string, got '" + base + "'");
  }
  const bool negative = base[0] == '-';
  std::string digits = negative ? base.substr(1) : base;

  if (decimals == 0) {
    const std::string whole = detail_units::strip_leading_zeros(digits);
    return (negative && whole != "0") ? "-" + whole : whole;
  }

  const std::size_t want = static_cast<std::size_t>(decimals) + 1;
  if (digits.size() < want) digits.insert(digits.begin(), want - digits.size(), '0');

  const std::string whole = detail_units::strip_leading_zeros(digits.substr(0, digits.size() - decimals));
  const std::string frac = digits.substr(digits.size() - decimals);

  const bool nonzero = whole != "0" || frac.find_first_not_of('0') != std::string::npos;
  return std::string(negative && nonzero ? "-" : "") + whole + "." + frac;
}

/// A display decimal string -> exact base units.
///
/// Throws rather than rounding when the input carries more fraction digits than the asset has.
/// Silently truncating a monetary value is exactly the bug this SDK exists to prevent.
///
///     parse_units("0.00123456", 8) == "123456"
inline std::string parse_units(const std::string& value, int decimals) {
  detail_units::check_decimals(decimals);
  if (value.empty()) throw ReceivifyError("parse_units: empty string");

  const bool negative = value[0] == '-';
  const std::string body = negative ? value.substr(1) : value;
  if (body.empty() || body == ".") throw ReceivifyError("parse_units: '" + value + "' is not a decimal number");

  const std::size_t dot = body.find('.');
  const std::string whole = (dot == std::string::npos) ? body : body.substr(0, dot);
  const std::string frac = (dot == std::string::npos) ? "" : body.substr(dot + 1);

  if (body.find('.', dot == std::string::npos ? 0 : dot + 1) != std::string::npos) {
    throw ReceivifyError("parse_units: '" + value + "' has more than one decimal point");
  }
  for (const std::string* part : {&whole, &frac}) {
    for (char c : *part) {
      if (c < '0' || c > '9') throw ReceivifyError("parse_units: '" + value + "' is not a decimal number");
    }
  }
  if (whole.empty() && frac.empty()) {
    throw ReceivifyError("parse_units: '" + value + "' is not a decimal number");
  }
  if (frac.size() > static_cast<std::size_t>(decimals)) {
    throw ReceivifyError("parse_units: '" + value + "' has " + std::to_string(frac.size()) +
                         " fraction digits but the asset has " + std::to_string(decimals) +
                         "; refusing to round a monetary value");
  }

  std::string digits = (whole.empty() ? "0" : whole) + frac;
  digits.append(static_cast<std::size_t>(decimals) - frac.size(), '0');
  digits = detail_units::strip_leading_zeros(digits);
  return (digits == "0" || !negative) ? digits : "-" + digits;
}

/// Compare two exact base-unit strings without going anywhere near a number type.
/// Returns -1, 0 or 1.
inline int compare_amounts(const std::string& a, const std::string& b) {
  if (!detail_units::is_integer_string(a) || !detail_units::is_integer_string(b)) {
    throw ReceivifyError("compare_amounts: both operands must be integer base-unit strings");
  }
  const bool na = a[0] == '-';
  const bool nb = b[0] == '-';
  const std::string ma = na ? a.substr(1) : a;
  const std::string mb = nb ? b.substr(1) : b;

  const bool a_zero = detail_units::strip_leading_zeros(ma) == "0";
  const bool b_zero = detail_units::strip_leading_zeros(mb) == "0";
  if (a_zero && b_zero) return 0;
  if (na != nb) return na ? -1 : 1;

  const int mag = detail_units::compare_magnitude(ma, mb);
  return na ? -mag : mag;
}

/// `true` when `received >= expected`, both as exact base-unit strings. The one comparison an
/// integration actually needs, spelled out so nobody reaches for `std::stod`.
inline bool amount_at_least(const std::string& received, const std::string& expected) {
  return compare_amounts(received, expected) >= 0;
}

}  // namespace units
}  // namespace receivify
