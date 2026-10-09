// Offline test doubles.
//
// The whole reason `HttpTransport` and `Hmac` are interfaces is so the test suite can replace
// them. Nothing here touches the network or a real clock.
#pragma once

#include <deque>
#include <functional>
#include <stdexcept>
#include <string>
#include <vector>

#include "receivify/errors.hpp"
#include "receivify/hmac.hpp"
#include "receivify/http.hpp"

namespace rtest {

/// Records every request the SDK built, and replays canned responses in order.
class FakeTransport : public receivify::HttpTransport {
 public:
  /// Every request the client made, in order. Assert against these.
  std::vector<receivify::HttpRequest> requests;

  /// Canned responses, consumed front-to-back. Running out is a test bug and throws loudly.
  std::deque<receivify::HttpResponse> responses;

  /// Number of leading attempts that should fail at the transport layer (connect/timeout)
  /// before the queue is consulted. For exercising the retry path.
  int fail_first = 0;
  bool fail_as_timeout = true;

  receivify::HttpResponse send(const receivify::HttpRequest& request) override {
    requests.push_back(request);
    if (fail_first > 0) {
      --fail_first;
      throw receivify::TransportError("fake transport: simulated connection failure", fail_as_timeout);
    }
    if (responses.empty()) {
      throw std::logic_error("FakeTransport: the client made more requests than the test queued responses for (" +
                             request.method + " " + request.url + ")");
    }
    receivify::HttpResponse response = responses.front();
    responses.pop_front();
    return response;
  }

  void queue(int status, std::string body, receivify::Headers headers = {}) {
    receivify::HttpResponse r;
    r.status = status;
    r.body = std::move(body);
    r.headers = std::move(headers);
    if (!r.body.empty()) r.headers.emplace_back("content-type", "application/json");
    responses.push_back(std::move(r));
  }

  const receivify::HttpRequest& last() const {
    if (requests.empty()) throw std::logic_error("FakeTransport: no requests were made");
    return requests.back();
  }

  const std::string* header(const receivify::HttpRequest& request, const std::string& name) const {
    return receivify::detail::find_header(request.headers, name);
  }
};

/// A deterministic, **NON-CRYPTOGRAPHIC** stand-in for HMAC-SHA256.
///
/// It exists to test the logic AROUND the crypto — the multi-secret loop, the signature header
/// grammar, the replay window, tamper detection — without requiring OpenSSL to be present. It is
/// a mixing function, not a MAC, and it must never be used for anything but tests. The real
/// provider is separately checked against RFC 4231 vectors in test_webhooks.cpp.
class StubHmac : public receivify::Hmac {
 public:
  receivify::Sha256Digest hmac_sha256(const std::string& key, const std::string& message) const override {
    receivify::Sha256Digest out{};
    // FNV-1a per output byte, salted by the byte's index, over key + '\0' + message.
    for (std::size_t i = 0; i < out.size(); ++i) {
      std::uint64_t h = 1469598103934665603ULL ^ (static_cast<std::uint64_t>(i) * 31u);
      auto mix = [&h](unsigned char c) {
        h ^= c;
        h *= 1099511628211ULL;
      };
      for (unsigned char c : key) mix(c);
      mix(0);
      for (unsigned char c : message) mix(c);
      out[i] = static_cast<unsigned char>((h >> 24) & 0xFF);
    }
    return out;
  }
};

}  // namespace rtest
