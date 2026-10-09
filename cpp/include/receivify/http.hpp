// The transport seam.
//
// C++ has no standard HTTP client, so this SDK defines a two-struct, one-method interface and
// ships a libcurl implementation behind it (`receivify/transport/curl.hpp`, opt-out at build
// time). Everything above this line — the client, the retry policy, the error mapping, the
// pagination — is pure C++17 with no dependencies and no knowledge of curl.
//
// Substitute your own by implementing `HttpTransport`. That is also exactly what the test suite
// does, which is why the tests need no network.
#pragma once

#include <chrono>
#include <memory>
#include <string>

#include "receivify/detail/util.hpp"
#include "receivify/errors.hpp"

namespace receivify {

/// One outbound HTTP request, fully resolved. The API key, when present, is already in
/// `headers` as `authorization` — it is never in `url`.
struct HttpRequest {
  std::string method;   ///< Upper-case: `GET`, `POST`, `PATCH`, `DELETE`.
  std::string url;      ///< Absolute, query string included.
  Headers headers;      ///< Names already lower-cased.
  std::string body;     ///< Empty when there is no body.
  std::chrono::milliseconds timeout{30000};
};

/// One inbound HTTP response.
struct HttpResponse {
  int status = 0;
  Headers headers;   ///< A transport SHOULD lower-case names; the SDK looks them up case-insensitively either way.
  std::string body;  ///< Raw bytes as received. Nothing has parsed them yet.
};

/// Implement this to plug in your own HTTP stack.
///
/// Contract:
///   - Return the response for ANY status code, including 4xx and 5xx. The SDK maps statuses to
///     exceptions itself; a transport that throws on a 404 breaks that.
///   - Throw `TransportError` only when no response was obtained at all (DNS, TLS, connect,
///     socket, timeout). Set `timed_out` when it was a timeout, so the retry policy can tell.
///   - Honour `HttpRequest::timeout`.
///   - `send` may be called concurrently from several threads if the `Client` is shared.
class HttpTransport {
 public:
  virtual ~HttpTransport() = default;
  virtual HttpResponse send(const HttpRequest& request) = 0;
};

}  // namespace receivify
