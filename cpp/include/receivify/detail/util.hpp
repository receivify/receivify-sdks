// Small internal helpers. No dependencies beyond the C++17 standard library.
//
// Everything in `receivify::detail` is an implementation detail and is NOT part of the SDK's
// stable surface — the one exception is `Headers`, which appears in public types.
#pragma once

#include <cctype>
#include <cstddef>
#include <cstdint>
#include <string>
#include <utility>
#include <vector>

namespace receivify {

/// An HTTP header list. Header NAMES are lower-cased everywhere in this SDK, on the way out and
/// on the way back in, so lookups never have to worry about the wire casing.
using Headers = std::vector<std::pair<std::string, std::string>>;

namespace detail {

inline std::string to_lower(std::string s) {
  for (char& c : s) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
  return s;
}

inline std::string trim(const std::string& s) {
  std::size_t b = 0, e = s.size();
  while (b < e && std::isspace(static_cast<unsigned char>(s[b]))) ++b;
  while (e > b && std::isspace(static_cast<unsigned char>(s[e - 1]))) --e;
  return s.substr(b, e - b);
}

/// Case-insensitive header lookup. Returns nullptr when absent.
inline const std::string* find_header(const Headers& headers, const std::string& name) {
  const std::string want = to_lower(name);
  for (const auto& kv : headers) {
    if (to_lower(kv.first) == want) return &kv.second;
  }
  return nullptr;
}

/// Strict decimal integer parse: optional `-`, then digits only, nothing else. Returns false on
/// anything malformed or out of range. We never use `atoi`/`stoll` directly on wire data.
inline bool parse_int64(const std::string& s, std::int64_t& out) {
  if (s.empty()) return false;
  std::size_t i = 0;
  bool neg = false;
  if (s[0] == '-') {
    neg = true;
    i = 1;
    if (s.size() == 1) return false;
  }
  std::uint64_t acc = 0;
  for (; i < s.size(); ++i) {
    if (s[i] < '0' || s[i] > '9') return false;
    const std::uint64_t digit = static_cast<std::uint64_t>(s[i] - '0');
    if (acc > (UINT64_C(0xFFFFFFFFFFFFFFFF) - digit) / 10) return false;  // overflow
    acc = acc * 10 + digit;
  }
  if (neg) {
    if (acc > UINT64_C(9223372036854775808)) return false;
    out = (acc == UINT64_C(9223372036854775808)) ? INT64_MIN : -static_cast<std::int64_t>(acc);
  } else {
    if (acc > UINT64_C(9223372036854775807)) return false;
    out = static_cast<std::int64_t>(acc);
  }
  return true;
}

/// Portable constant-time byte comparison.
///
/// This is the fallback used when a caller supplies their own `Hmac` implementation and does not
/// override the comparison. The bundled OpenSSL provider overrides it with `CRYPTO_memcmp`, which
/// is the compiler-hardened version. Both accumulate every byte and branch only on the total, so
/// the running time does not depend on WHERE the first difference is.
///
/// `volatile` here is a request, not a guarantee — it is what portable C++ can express, and it is
/// why we prefer `CRYPTO_memcmp` when OpenSSL is available.
inline bool constant_time_equals(const unsigned char* a, const unsigned char* b, std::size_t n) {
  volatile unsigned char acc = 0;
  for (std::size_t i = 0; i < n; ++i) {
    acc = static_cast<unsigned char>(acc | (a[i] ^ b[i]));
  }
  return acc == 0;
}

inline int hex_value(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  if (c >= 'A' && c <= 'F') return c - 'A' + 10;
  return -1;
}

/// Decode a hex string. Returns false on odd length or a non-hex character.
inline bool hex_decode(const std::string& hex, std::vector<unsigned char>& out) {
  if (hex.size() % 2 != 0) return false;
  out.clear();
  out.reserve(hex.size() / 2);
  for (std::size_t i = 0; i < hex.size(); i += 2) {
    const int hi = hex_value(hex[i]);
    const int lo = hex_value(hex[i + 1]);
    if (hi < 0 || lo < 0) return false;
    out.push_back(static_cast<unsigned char>((hi << 4) | lo));
  }
  return true;
}

inline std::string hex_encode(const unsigned char* p, std::size_t n) {
  static const char* kDigits = "0123456789abcdef";
  std::string out;
  out.reserve(n * 2);
  for (std::size_t i = 0; i < n; ++i) {
    out.push_back(kDigits[p[i] >> 4]);
    out.push_back(kDigits[p[i] & 0x0F]);
  }
  return out;
}

/// RFC 3986 percent-encoding, unreserved set only. Used for both path segments and query values,
/// which is why space becomes `%20` and not `+`.
inline std::string url_encode(const std::string& s) {
  static const char* kDigits = "0123456789ABCDEF";
  std::string out;
  out.reserve(s.size());
  for (unsigned char c : s) {
    const bool unreserved = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                            c == '-' || c == '_' || c == '.' || c == '~';
    if (unreserved) {
      out.push_back(static_cast<char>(c));
    } else {
      out.push_back('%');
      out.push_back(kDigits[c >> 4]);
      out.push_back(kDigits[c & 0x0F]);
    }
  }
  return out;
}

}  // namespace detail
}  // namespace receivify
