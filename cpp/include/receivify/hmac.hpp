// The crypto seam.
//
// C++ has no standard HMAC, so this SDK defines a two-method interface and ships an OpenSSL
// implementation behind it (`receivify/crypto/openssl.hpp`, opt-out at build time). The webhook
// verification logic — header parsing, the multi-secret loop, the replay window, the no-early-exit
// discipline — is all in `webhooks.hpp` and knows nothing about OpenSSL.
//
// Substitute your own by implementing `Hmac`: BCrypt on Windows, CommonCrypto on Apple,
// mbedTLS/libsodium/Botan anywhere, or your HSM.
#pragma once

#include <array>
#include <cstddef>
#include <string>

#include "receivify/detail/util.hpp"

namespace receivify {

/// A SHA-256 digest. 32 bytes, always.
using Sha256Digest = std::array<unsigned char, 32>;

/// HMAC-SHA256 plus a constant-time comparison.
class Hmac {
 public:
  virtual ~Hmac() = default;

  /// `HMAC-SHA256(key, message)`. Both arguments are byte strings and may contain NUL — the key
  /// is a `whsec_…` ASCII secret in practice, but nothing here assumes that.
  virtual Sha256Digest hmac_sha256(const std::string& key, const std::string& message) const = 0;

  /// Compare two equal-length byte ranges in time that does not depend on where they differ.
  ///
  /// The default is a portable accumulate-and-OR loop; the bundled OpenSSL provider overrides it
  /// with `CRYPTO_memcmp`, which is the version the compiler is not allowed to optimise into an
  /// early exit. Override it if your crypto library offers an equivalent.
  virtual bool constant_time_equals(const unsigned char* a, const unsigned char* b, std::size_t n) const {
    return detail::constant_time_equals(a, b, n);
  }
};

}  // namespace receivify
