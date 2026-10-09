// A minimal, strict JSON value + parser + serialiser for the Receivify SDK.
//
// ## Why this exists instead of a dependency
//
// The shared SDK contract asks for zero runtime dependencies. C++ has no standard JSON, and
// vendoring a parser would smuggle one in. So this SDK ships a small internal one — see the
// README for the full argument. Two things make that defensible rather than reckless:
//
//   1. **Numbers are never converted.** A JSON number is kept as the EXACT lexeme the server sent
//      and handed back as a `std::string`. There is no path where `21000000000000000000` becomes a
//      double and loses its low bits. Off-the-shelf parsers do the opposite by default, which is
//      precisely the bug class this codebase has already been bitten by. `as_double()` exists, is
//      opt-in, and is documented as being for FX rates only.
//   2. **Depth is bounded.** Parsing is recursive descent; a deeply nested body would blow the
//      stack, so nesting past `ParseOptions::max_depth` (64) is a `JsonError`, not a crash.
//
// ## What this parser is NOT
//
// It is tuned for the well-formed, machine-generated bodies `/api/v1` emits. It is strict rather
// than clever, and it deliberately does not:
//
//   - validate that input bytes are well-formed UTF-8 (bytes outside escapes pass through as-is);
//   - accept any JSON5-ism — no comments, no trailing commas, no single quotes, no unquoted keys,
//     no `NaN`/`Infinity`, no leading `+`, no leading zeros, no hex;
//   - preserve duplicate keys (the LAST occurrence wins, which is what every mainstream parser
//     does, but it is a choice and it is stated here);
//   - guarantee anything about pathological inputs beyond "throws instead of misbehaving".
//
// If you would rather use nlohmann/json, RapidJSON or simdjson, you can: `Client::request()`
// hands back the raw response body as a `std::string` before this parser ever sees it. See the
// README section "Using your own JSON library".
#pragma once

#include <cstddef>
#include <cstdint>
#include <initializer_list>
#include <string>
#include <utility>
#include <vector>

#include "receivify/errors.hpp"

namespace receivify {

/// A JSON value.
///
/// Object key order is the order keys were inserted (or parsed), so `dump()` is deterministic.
class Json {
 public:
  enum class Type { Null, Bool, Number, String, Array, Object };

  Json() = default;
  Json(std::nullptr_t) : type_(Type::Null) {}
  Json(bool v) : type_(Type::Bool), bool_(v) {}
  Json(int v) : type_(Type::Number), text_(std::to_string(static_cast<long long>(v))) {}
  Json(long v) : type_(Type::Number), text_(std::to_string(static_cast<long long>(v))) {}
  Json(long long v) : type_(Type::Number), text_(std::to_string(v)) {}
  Json(unsigned v) : type_(Type::Number), text_(std::to_string(static_cast<unsigned long long>(v))) {}
  Json(unsigned long long v) : type_(Type::Number), text_(std::to_string(v)) {}
  Json(const char* v) : type_(Type::String), text_(v == nullptr ? "" : v) {}
  Json(std::string v) : type_(Type::String), text_(std::move(v)) {}

  /// **Deliberately deleted.** You cannot put a floating-point value into a Receivify request
  /// body, because every amount on this API is an exact-integer base-unit STRING and a double
  /// silently corrupts satoshi counts above 2^53. Pass the string, or `Json::raw_number("1.5")`
  /// if you genuinely mean a non-money number.
  Json(double) = delete;
  Json(float) = delete;

  /// A number written from a pre-formatted lexeme. The text is trusted and emitted verbatim, so
  /// it must already be valid JSON number syntax.
  static Json raw_number(std::string lexeme) {
    Json j;
    j.type_ = Type::Number;
    j.text_ = std::move(lexeme);
    return j;
  }

  static Json array() {
    Json j;
    j.type_ = Type::Array;
    return j;
  }

  static Json array(std::initializer_list<Json> items) {
    Json j = array();
    j.children_.assign(items.begin(), items.end());
    return j;
  }

  static Json object() {
    Json j;
    j.type_ = Type::Object;
    return j;
  }

  static Json object(std::initializer_list<std::pair<const char*, Json>> members) {
    Json j = object();
    for (const auto& m : members) j.set(m.first, m.second);
    return j;
  }

  /* ---------------------------------------------------------------- *
   * Inspection
   * ---------------------------------------------------------------- */

  Type type() const noexcept { return type_; }
  bool is_null() const noexcept { return type_ == Type::Null; }
  bool is_bool() const noexcept { return type_ == Type::Bool; }
  bool is_number() const noexcept { return type_ == Type::Number; }
  bool is_string() const noexcept { return type_ == Type::String; }
  bool is_array() const noexcept { return type_ == Type::Array; }
  bool is_object() const noexcept { return type_ == Type::Object; }

  /// Elements for an array, members for an object, 0 otherwise.
  std::size_t size() const noexcept {
    return (type_ == Type::Array || type_ == Type::Object) ? children_.size() : 0;
  }
  bool empty() const noexcept { return size() == 0; }

  /* ---------------------------------------------------------------- *
   * Reading values
   * ---------------------------------------------------------------- */

  /// The value as a string.
  ///
  /// For a String this is the decoded text. **For a Number this is the raw lexeme** — that is the
  /// whole point: money read through `as_string()` cannot lose precision even if a future
  /// deployment were ever to emit an amount unquoted. Any other type throws.
  const std::string& as_string() const {
    if (type_ == Type::String || type_ == Type::Number) return text_;
    throw JsonError("Json::as_string: value is " + std::string(type_name()) + ", not a string or number");
  }

  /// `as_string()` with a fallback instead of an exception. The idiomatic way to read an optional
  /// or forward-added field: a missing key is Null, and Null yields the fallback.
  std::string as_string_or(const std::string& fallback) const {
    return (type_ == Type::String || type_ == Type::Number) ? text_ : fallback;
  }

  bool as_bool() const {
    if (type_ == Type::Bool) return bool_;
    throw JsonError("Json::as_bool: value is " + std::string(type_name()) + ", not a bool");
  }
  bool as_bool_or(bool fallback) const noexcept { return type_ == Type::Bool ? bool_ : fallback; }

  /// A number as a 64-bit integer. Throws if the value is not a number, is not integral, or does
  /// not fit. Use it for counts, `sequence`, `confirmations`, `decimals` — never for money.
  std::int64_t as_int64() const {
    if (type_ != Type::Number) {
      throw JsonError("Json::as_int64: value is " + std::string(type_name()) + ", not a number");
    }
    std::int64_t out = 0;
    if (!detail::parse_int64(text_, out)) {
      throw JsonError("Json::as_int64: '" + text_ + "' is not an integer that fits in 64 bits");
    }
    return out;
  }
  std::int64_t as_int64_or(std::int64_t fallback) const noexcept {
    std::int64_t out = 0;
    if (type_ == Type::Number && detail::parse_int64(text_, out)) return out;
    return fallback;
  }

  /// A number as a double. **Opt-in, and lossy by construction.**
  ///
  /// The only values in this API that are legitimately doubles are the FX rates on
  /// `GET /api/v1/summary` and `GET /api/public/links/{id}` — advisory rates that nothing is ever
  /// settled against. Calling this on a monetary field is a bug: use `as_string()` and, if you
  /// need arithmetic, `receivify::units`.
  double as_double() const {
    if (type_ != Type::Number) {
      throw JsonError("Json::as_double: value is " + std::string(type_name()) + ", not a number");
    }
    try {
      return std::stod(text_);
    } catch (const std::exception&) {
      throw JsonError("Json::as_double: '" + text_ + "' is out of range for a double");
    }
  }

  /* ---------------------------------------------------------------- *
   * Navigation. Missing keys and out-of-range indices yield Null rather than throwing, so
   * reading a field a future API version has not added yet is safe.
   * ---------------------------------------------------------------- */

  const Json& operator[](const std::string& key) const noexcept {
    if (type_ == Type::Object) {
      for (std::size_t i = 0; i < keys_.size(); ++i) {
        if (keys_[i] == key) return children_[i];
      }
    }
    return null_value();
  }

  const Json& operator[](std::size_t index) const noexcept {
    if ((type_ == Type::Array || type_ == Type::Object) && index < children_.size()) return children_[index];
    return null_value();
  }

  /// Like `operator[]` but throws when the key is absent. Use it for fields the spec marks
  /// `required`, so a contract break surfaces immediately instead of as an empty string.
  const Json& at(const std::string& key) const {
    if (type_ != Type::Object) {
      throw JsonError("Json::at('" + key + "'): value is " + std::string(type_name()) + ", not an object");
    }
    for (std::size_t i = 0; i < keys_.size(); ++i) {
      if (keys_[i] == key) return children_[i];
    }
    throw JsonError("Json::at: no such key '" + key + "'");
  }

  bool has(const std::string& key) const noexcept {
    if (type_ != Type::Object) return false;
    for (const auto& k : keys_) {
      if (k == key) return true;
    }
    return false;
  }

  /// Object keys, in insertion order. Parallel to `values()`.
  const std::vector<std::string>& keys() const noexcept { return keys_; }
  /// Array elements, or object values parallel to `keys()`.
  const std::vector<Json>& values() const noexcept { return children_; }

  /* ---------------------------------------------------------------- *
   * Building
   * ---------------------------------------------------------------- */

  /// Set an object member. Replaces in place when the key exists, preserving its position.
  Json& set(const std::string& key, Json value) {
    if (type_ != Type::Object) {
      type_ = Type::Object;
      children_.clear();
      keys_.clear();
    }
    for (std::size_t i = 0; i < keys_.size(); ++i) {
      if (keys_[i] == key) {
        children_[i] = std::move(value);
        return *this;
      }
    }
    keys_.push_back(key);
    children_.push_back(std::move(value));
    return *this;
  }

  /// Set only when `value` is non-empty. Convenient for optional request fields.
  Json& set_if(const std::string& key, const std::string& value) {
    if (!value.empty()) set(key, Json(value));
    return *this;
  }

  Json& push_back(Json value) {
    if (type_ != Type::Array) {
      type_ = Type::Array;
      children_.clear();
      keys_.clear();
    }
    children_.push_back(std::move(value));
    return *this;
  }

  /* ---------------------------------------------------------------- *
   * Serialising
   * ---------------------------------------------------------------- */

  /// Compact JSON. Deterministic: key order is insertion order and numbers are emitted verbatim.
  std::string dump() const {
    std::string out;
    dump_into(out);
    return out;
  }

  const char* type_name() const noexcept {
    switch (type_) {
      case Type::Null: return "null";
      case Type::Bool: return "bool";
      case Type::Number: return "number";
      case Type::String: return "string";
      case Type::Array: return "array";
      case Type::Object: return "object";
    }
    return "unknown";
  }

  friend bool operator==(const Json& a, const Json& b) {
    if (a.type_ != b.type_) return false;
    switch (a.type_) {
      case Type::Null: return true;
      case Type::Bool: return a.bool_ == b.bool_;
      case Type::Number:
      case Type::String: return a.text_ == b.text_;
      case Type::Array: return a.children_ == b.children_;
      case Type::Object: return a.keys_ == b.keys_ && a.children_ == b.children_;
    }
    return false;
  }
  friend bool operator!=(const Json& a, const Json& b) { return !(a == b); }

 private:
  static const Json& null_value() noexcept {
    static const Json kNull;
    return kNull;
  }

  void dump_into(std::string& out) const {
    switch (type_) {
      case Type::Null: out += "null"; return;
      case Type::Bool: out += (bool_ ? "true" : "false"); return;
      case Type::Number: out += (text_.empty() ? "0" : text_); return;
      case Type::String: escape_into(text_, out); return;
      case Type::Array: {
        out += '[';
        for (std::size_t i = 0; i < children_.size(); ++i) {
          if (i != 0) out += ',';
          children_[i].dump_into(out);
        }
        out += ']';
        return;
      }
      case Type::Object: {
        out += '{';
        for (std::size_t i = 0; i < keys_.size(); ++i) {
          if (i != 0) out += ',';
          escape_into(keys_[i], out);
          out += ':';
          children_[i].dump_into(out);
        }
        out += '}';
        return;
      }
    }
  }

  /// Escapes the two mandatory characters plus every C0 control. Bytes >= 0x20 other than `"` and
  /// `\` pass through untouched, so valid UTF-8 stays valid UTF-8 and stays byte-identical.
  static void escape_into(const std::string& s, std::string& out) {
    static const char* kHex = "0123456789abcdef";
    out += '"';
    for (unsigned char c : s) {
      switch (c) {
        case '"': out += "\\\""; break;
        case '\\': out += "\\\\"; break;
        case '\b': out += "\\b"; break;
        case '\f': out += "\\f"; break;
        case '\n': out += "\\n"; break;
        case '\r': out += "\\r"; break;
        case '\t': out += "\\t"; break;
        default:
          if (c < 0x20) {
            out += "\\u00";
            out += kHex[c >> 4];
            out += kHex[c & 0x0F];
          } else {
            out += static_cast<char>(c);
          }
      }
    }
    out += '"';
  }

  Type type_ = Type::Null;
  bool bool_ = false;
  std::string text_;  ///< String contents, or the verbatim Number lexeme.
  // `std::vector<Json>` with `Json` incomplete is explicitly permitted ([vector.overview]); this
  // is why members are two parallel vectors rather than a vector of pairs, which is not.
  std::vector<Json> children_;
  std::vector<std::string> keys_;  ///< Object member names, parallel to `children_`. Empty for arrays.
};

/* ==================================================================== *
 * Parser
 * ==================================================================== */

struct ParseOptions {
  /// Maximum nesting depth. Exceeding it is a `JsonError`, never a stack overflow. Receivify's
  /// own bodies nest three or four deep; 64 is generous.
  std::size_t max_depth = 64;
};

namespace detail {

class JsonParser {
 public:
  JsonParser(const std::string& text, const ParseOptions& opts) : s_(text), opts_(opts) {}

  Json parse_document() {
    skip_ws();
    Json value = parse_value(0);
    skip_ws();
    if (i_ != s_.size()) fail("unexpected trailing content after the top-level value");
    return value;
  }

 private:
  [[noreturn]] void fail(const std::string& why) const {
    throw JsonError("Invalid JSON at byte " + std::to_string(i_) + ": " + why, i_);
  }

  void skip_ws() {
    while (i_ < s_.size()) {
      const char c = s_[i_];
      if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
        ++i_;
      } else {
        break;
      }
    }
  }

  char peek() const {
    if (i_ >= s_.size()) fail("unexpected end of input");
    return s_[i_];
  }

  void expect(char c) {
    if (i_ >= s_.size() || s_[i_] != c) fail(std::string("expected '") + c + "'");
    ++i_;
  }

  Json parse_value(std::size_t depth) {
    if (depth > opts_.max_depth) {
      throw JsonError("Invalid JSON: nesting deeper than the " + std::to_string(opts_.max_depth) +
                          " level limit; refusing to recurse further",
                      i_);
    }
    switch (peek()) {
      case '{': return parse_object(depth);
      case '[': return parse_array(depth);
      case '"': return Json(parse_string());
      case 't': return parse_literal("true", Json(true));
      case 'f': return parse_literal("false", Json(false));
      case 'n': return parse_literal("null", Json(nullptr));
      default: return parse_number();
    }
  }

  Json parse_literal(const char* word, Json value) {
    const std::size_t n = std::char_traits<char>::length(word);
    if (s_.compare(i_, n, word) != 0) fail(std::string("expected '") + word + "'");
    i_ += n;
    return value;
  }

  Json parse_object(std::size_t depth) {
    expect('{');
    Json obj = Json::object();
    skip_ws();
    if (peek() == '}') {
      ++i_;
      return obj;
    }
    for (;;) {
      skip_ws();
      if (peek() != '"') fail("object keys must be double-quoted strings");
      const std::string key = parse_string();
      skip_ws();
      expect(':');
      skip_ws();
      // Last duplicate wins — `set` replaces in place.
      obj.set(key, parse_value(depth + 1));
      skip_ws();
      const char c = peek();
      if (c == ',') {
        ++i_;
        skip_ws();
        if (peek() == '}') fail("trailing comma in object");
        continue;
      }
      if (c == '}') {
        ++i_;
        return obj;
      }
      fail("expected ',' or '}' in object");
    }
  }

  Json parse_array(std::size_t depth) {
    expect('[');
    Json arr = Json::array();
    skip_ws();
    if (peek() == ']') {
      ++i_;
      return arr;
    }
    for (;;) {
      skip_ws();
      arr.push_back(parse_value(depth + 1));
      skip_ws();
      const char c = peek();
      if (c == ',') {
        ++i_;
        skip_ws();
        if (peek() == ']') fail("trailing comma in array");
        continue;
      }
      if (c == ']') {
        ++i_;
        return arr;
      }
      fail("expected ',' or ']' in array");
    }
  }

  /// Numbers are validated against the JSON grammar and then stored as the EXACT source lexeme.
  /// No conversion happens here, ever. That is the precision guarantee.
  Json parse_number() {
    const std::size_t start = i_;
    if (i_ < s_.size() && s_[i_] == '-') ++i_;
    if (i_ >= s_.size()) fail("unexpected end of input in number");
    if (s_[i_] == '0') {
      ++i_;
    } else if (s_[i_] >= '1' && s_[i_] <= '9') {
      while (i_ < s_.size() && s_[i_] >= '0' && s_[i_] <= '9') ++i_;
    } else {
      fail("expected a value");
    }
    if (i_ < s_.size() && s_[i_] == '.') {
      ++i_;
      const std::size_t frac_start = i_;
      while (i_ < s_.size() && s_[i_] >= '0' && s_[i_] <= '9') ++i_;
      if (i_ == frac_start) fail("a decimal point must be followed by at least one digit");
    }
    if (i_ < s_.size() && (s_[i_] == 'e' || s_[i_] == 'E')) {
      ++i_;
      if (i_ < s_.size() && (s_[i_] == '+' || s_[i_] == '-')) ++i_;
      const std::size_t exp_start = i_;
      while (i_ < s_.size() && s_[i_] >= '0' && s_[i_] <= '9') ++i_;
      if (i_ == exp_start) fail("an exponent must have at least one digit");
    }
    return Json::raw_number(s_.substr(start, i_ - start));
  }

  std::string parse_string() {
    expect('"');
    std::string out;
    for (;;) {
      if (i_ >= s_.size()) fail("unterminated string");
      const unsigned char c = static_cast<unsigned char>(s_[i_]);
      if (c == '"') {
        ++i_;
        return out;
      }
      if (c == '\\') {
        ++i_;
        parse_escape(out);
        continue;
      }
      if (c < 0x20) fail("raw control character in string; it must be escaped");
      out.push_back(static_cast<char>(c));
      ++i_;
    }
  }

  void parse_escape(std::string& out) {
    if (i_ >= s_.size()) fail("unterminated escape sequence");
    const char e = s_[i_++];
    switch (e) {
      case '"': out.push_back('"'); return;
      case '\\': out.push_back('\\'); return;
      case '/': out.push_back('/'); return;
      case 'b': out.push_back('\b'); return;
      case 'f': out.push_back('\f'); return;
      case 'n': out.push_back('\n'); return;
      case 'r': out.push_back('\r'); return;
      case 't': out.push_back('\t'); return;
      case 'u': break;
      default: fail(std::string("unknown escape '\\") + e + "'");
    }

    std::uint32_t cp = parse_hex4();
    if (cp >= 0xD800 && cp <= 0xDBFF) {
      // High surrogate: a low surrogate must follow, or the pair is broken.
      if (i_ + 1 < s_.size() && s_[i_] == '\\' && s_[i_ + 1] == 'u') {
        const std::size_t save = i_;
        i_ += 2;
        const std::uint32_t low = parse_hex4();
        if (low >= 0xDC00 && low <= 0xDFFF) {
          cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
        } else {
          // Not a low surrogate after all. Rewind and emit U+FFFD for the lone high surrogate;
          // the following escape is then parsed normally on the next loop.
          i_ = save;
          cp = 0xFFFD;
        }
      } else {
        cp = 0xFFFD;
      }
    } else if (cp >= 0xDC00 && cp <= 0xDFFF) {
      // A low surrogate with no high surrogate before it. Unrepresentable; substitute.
      cp = 0xFFFD;
    }
    encode_utf8(cp, out);
  }

  std::uint32_t parse_hex4() {
    if (i_ + 4 > s_.size()) fail("\\u escape needs four hex digits");
    std::uint32_t v = 0;
    for (int k = 0; k < 4; ++k) {
      const int d = hex_value(s_[i_ + k]);
      if (d < 0) fail("\\u escape needs four hex digits");
      v = (v << 4) | static_cast<std::uint32_t>(d);
    }
    i_ += 4;
    return v;
  }

  static void encode_utf8(std::uint32_t cp, std::string& out) {
    if (cp < 0x80) {
      out.push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
      out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
      out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
      out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
      out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
      out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
      out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
      out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
      out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
      out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
  }

  const std::string& s_;
  const ParseOptions& opts_;
  std::size_t i_ = 0;
};

}  // namespace detail

/// Parse a JSON document. Throws `JsonError` on anything malformed, on trailing content, and on
/// nesting past `opts.max_depth`.
inline Json json_parse(const std::string& text, const ParseOptions& opts = {}) {
  detail::JsonParser parser(text, opts);
  return parser.parse_document();
}

}  // namespace receivify
