// The bundled `Hmac` implementation, backed by OpenSSL.
//
// This is the ONLY file in the SDK that includes an OpenSSL header. Build with
// `-DRECEIVIFY_WITH_OPENSSL=OFF` and it is not compiled at all; supply your own `receivify::Hmac`
// instead (BCrypt, CommonCrypto, mbedTLS, libsodium, an HSM — anything that can do HMAC-SHA256).
#pragma once

#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <openssl/hmac.h>

#include <memory>
#include <string>

#include "receivify/errors.hpp"
#include "receivify/hmac.hpp"

namespace receivify {

/// HMAC-SHA256 via OpenSSL's one-shot `HMAC()`, and constant-time comparison via `CRYPTO_memcmp`.
///
/// `CRYPTO_memcmp` is preferred over the portable loop in `detail::constant_time_equals` because
/// it is written specifically to survive optimisation: a compiler is free to turn a hand-written
/// `volatile` loop into an early-exit `memcmp` in principle, and `CRYPTO_memcmp` exists precisely
/// so that it cannot.
///
/// Stateless and therefore trivially thread-safe: one instance can be shared by every `Client`.
class OpenSslHmac : public Hmac {
 public:
  Sha256Digest hmac_sha256(const std::string& key, const std::string& message) const override {
    Sha256Digest digest{};
    unsigned int length = 0;
    const unsigned char* result =
        ::HMAC(::EVP_sha256(), key.data(), static_cast<int>(key.size()),
               reinterpret_cast<const unsigned char*>(message.data()), message.size(), digest.data(), &length);
    if (result == nullptr || length != digest.size()) {
      throw ReceivifyError("OpenSslHmac: HMAC-SHA256 failed inside OpenSSL");
    }
    return digest;
  }

  bool constant_time_equals(const unsigned char* a, const unsigned char* b, std::size_t n) const override {
    return ::CRYPTO_memcmp(a, b, n) == 0;
  }
};

/// A shared `OpenSslHmac`. Stateless, so one instance for the whole process is correct.
inline std::shared_ptr<const Hmac> openssl_hmac() {
  static const std::shared_ptr<const Hmac> kInstance = std::make_shared<OpenSslHmac>();
  return kInstance;
}

}  // namespace receivify
