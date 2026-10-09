// Wiring for the bundled libcurl + OpenSSL providers.
//
// This is the ONLY header that knows both that `client.hpp` exists and that `curl_transport.hpp`
// and `openssl_hmac.hpp` exist. Include it (or the umbrella `receivify/receivify.hpp`) for the
// batteries-included path:
//
//     auto client = receivify::make_client("https://receivify.io", key);
//
// Include `receivify/client.hpp` directly instead, and neither libcurl's nor OpenSSL's headers
// are ever pulled in — you then supply `ClientOptions::transport` and `ClientOptions::hmac`
// yourself. That is the whole point of the two interfaces.
#pragma once

#include <memory>
#include <string>
#include <utility>

#include "receivify/client.hpp"

#ifdef RECEIVIFY_WITH_CURL
#include "receivify/transport/curl_transport.hpp"
#endif

#ifdef RECEIVIFY_WITH_OPENSSL
#include "receivify/crypto/openssl_hmac.hpp"
#endif

namespace receivify {

/// The bundled HTTP transport, or nullptr when the SDK was built without libcurl.
inline std::shared_ptr<HttpTransport> default_transport() {
#ifdef RECEIVIFY_WITH_CURL
  return curl_transport();
#else
  return nullptr;
#endif
}

/// The bundled HMAC provider, or nullptr when the SDK was built without OpenSSL.
inline std::shared_ptr<const Hmac> default_hmac() {
#ifdef RECEIVIFY_WITH_OPENSSL
  return openssl_hmac();
#else
  return nullptr;
#endif
}

/// True when this build has both bundled providers compiled in.
inline bool has_bundled_providers() { return default_transport() != nullptr && default_hmac() != nullptr; }

/// Build a `Client`, filling in the bundled libcurl transport and OpenSSL HMAC for anything you
/// left unset. Anything you DID set is untouched, so this is also the right entry point when you
/// are substituting only one of the two.
inline Client make_client(std::string api_url, std::string api_key = std::string(), ClientOptions options = ClientOptions()) {
  if (!options.transport) options.transport = default_transport();
  if (!options.hmac) options.hmac = default_hmac();
  return Client(std::move(api_url), std::move(api_key), std::move(options));
}

}  // namespace receivify
