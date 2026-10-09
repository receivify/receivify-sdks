// The bundled `HttpTransport`, backed by libcurl.
//
// This is the ONLY file in the SDK that includes a libcurl header. Build with
// `-DRECEIVIFY_WITH_CURL=OFF` and it is not compiled at all; supply your own
// `receivify::HttpTransport` instead.
#pragma once

#include <curl/curl.h>

#include <chrono>
#include <memory>
#include <mutex>
#include <string>
#include <utility>

#include "receivify/errors.hpp"
#include "receivify/http.hpp"

namespace receivify {

struct CurlOptions {
  /// Call `curl_global_init` / `curl_global_cleanup` for you.
  ///
  /// Set this to false if your application already manages libcurl's global state — calling
  /// `curl_global_init` twice is harmless (it refcounts), but if you have your own lifecycle you
  /// probably want to own it. When true, initialisation happens exactly once via `std::call_once`,
  /// which is the standard remedy for `curl_global_init` not being thread-safe on older libcurl.
  bool manage_global_init = true;

  /// Override the CA bundle. Empty means "use libcurl's default", which is what you want almost
  /// everywhere. There is deliberately no option to disable certificate verification.
  std::string ca_bundle_path;

  /// Optional proxy, in libcurl's `CURLOPT_PROXY` syntax.
  std::string proxy;

  /// Connect-phase timeout. The overall timeout comes from each `HttpRequest`.
  std::chrono::milliseconds connect_timeout{10000};
};

/// A libcurl-backed transport using one easy handle per request.
///
/// Thread safety: `send()` creates and destroys its own handle, so concurrent calls on one
/// `CurlTransport` are safe. (It also means no connection reuse between calls — if you are making
/// enough requests for that to matter, implement `HttpTransport` over a shared handle or the
/// multi interface and hand it to `ClientOptions::transport`.)
class CurlTransport : public HttpTransport {
 public:
  explicit CurlTransport(CurlOptions options = CurlOptions()) : options_(std::move(options)) {
    if (options_.manage_global_init) {
      static std::once_flag once;
      static CURLcode init_result = CURLE_OK;
      std::call_once(once, []() { init_result = ::curl_global_init(CURL_GLOBAL_DEFAULT); });
      if (init_result != CURLE_OK) {
        throw TransportError(std::string("curl_global_init failed: ") + ::curl_easy_strerror(init_result));
      }
    }
  }

  HttpResponse send(const HttpRequest& request) override {
    EasyHandle easy;
    if (easy.get() == nullptr) throw TransportError("curl_easy_init returned null");

    HttpResponse response;
    char error_buffer[CURL_ERROR_SIZE] = {0};

    ::curl_easy_setopt(easy.get(), CURLOPT_URL, request.url.c_str());
    ::curl_easy_setopt(easy.get(), CURLOPT_ERRORBUFFER, error_buffer);
    ::curl_easy_setopt(easy.get(), CURLOPT_NOPROGRESS, 1L);
    // Timeouts are non-negotiable — a hung node must not hang the caller's thread forever.
    ::curl_easy_setopt(easy.get(), CURLOPT_TIMEOUT_MS, static_cast<long>(request.timeout.count()));
    ::curl_easy_setopt(easy.get(), CURLOPT_CONNECTTIMEOUT_MS, static_cast<long>(options_.connect_timeout.count()));
    // Required when using timeouts from a thread: without it libcurl may use signals for DNS.
    ::curl_easy_setopt(easy.get(), CURLOPT_NOSIGNAL, 1L);
    // Redirects are NOT followed on purpose. Following one would replay the `Authorization`
    // header — the merchant's API key — to whatever host the redirect names.
    ::curl_easy_setopt(easy.get(), CURLOPT_FOLLOWLOCATION, 0L);
    // Explicit, and never configurable to 0. TLS verification is the whole security model here.
    ::curl_easy_setopt(easy.get(), CURLOPT_SSL_VERIFYPEER, 1L);
    ::curl_easy_setopt(easy.get(), CURLOPT_SSL_VERIFYHOST, 2L);
    ::curl_easy_setopt(easy.get(), CURLOPT_ACCEPT_ENCODING, "");

    if (!options_.ca_bundle_path.empty()) {
      ::curl_easy_setopt(easy.get(), CURLOPT_CAINFO, options_.ca_bundle_path.c_str());
    }
    if (!options_.proxy.empty()) {
      ::curl_easy_setopt(easy.get(), CURLOPT_PROXY, options_.proxy.c_str());
    }

    ::curl_easy_setopt(easy.get(), CURLOPT_WRITEFUNCTION, &write_body);
    ::curl_easy_setopt(easy.get(), CURLOPT_WRITEDATA, &response.body);
    ::curl_easy_setopt(easy.get(), CURLOPT_HEADERFUNCTION, &write_header);
    ::curl_easy_setopt(easy.get(), CURLOPT_HEADERDATA, &response.headers);

    // `CUSTOMREQUEST` sets the method verb for everything, including GET, so there is one code
    // path rather than four.
    ::curl_easy_setopt(easy.get(), CURLOPT_CUSTOMREQUEST, request.method.c_str());
    if (!request.body.empty()) {
      ::curl_easy_setopt(easy.get(), CURLOPT_POSTFIELDS, request.body.data());
      ::curl_easy_setopt(easy.get(), CURLOPT_POSTFIELDSIZE_LARGE,
                         static_cast<curl_off_t>(request.body.size()));
    }

    SList headers;
    for (const auto& kv : request.headers) {
      headers.append(kv.first + ": " + kv.second);
    }
    // Suppress `Expect: 100-continue`, which libcurl adds for larger bodies and which costs a
    // needless round trip against most reverse proxies.
    headers.append("Expect:");
    ::curl_easy_setopt(easy.get(), CURLOPT_HTTPHEADER, headers.get());

    const CURLcode rc = ::curl_easy_perform(easy.get());
    if (rc != CURLE_OK) {
      const std::string message = error_buffer[0] != '\0' ? error_buffer : ::curl_easy_strerror(rc);
      const bool timed_out = rc == CURLE_OPERATION_TIMEDOUT;
      throw TransportError("Receivify " + request.method + " failed: " + message, timed_out);
    }

    long status = 0;
    ::curl_easy_getinfo(easy.get(), CURLINFO_RESPONSE_CODE, &status);
    response.status = static_cast<int>(status);
    return response;
  }

 private:
  /// RAII for `CURL*`.
  class EasyHandle {
   public:
    EasyHandle() : handle_(::curl_easy_init()) {}
    ~EasyHandle() {
      if (handle_ != nullptr) ::curl_easy_cleanup(handle_);
    }
    EasyHandle(const EasyHandle&) = delete;
    EasyHandle& operator=(const EasyHandle&) = delete;
    CURL* get() const { return handle_; }

   private:
    CURL* handle_;
  };

  /// RAII for `curl_slist*`.
  class SList {
   public:
    SList() = default;
    ~SList() {
      if (list_ != nullptr) ::curl_slist_free_all(list_);
    }
    SList(const SList&) = delete;
    SList& operator=(const SList&) = delete;
    void append(const std::string& line) {
      curl_slist* next = ::curl_slist_append(list_, line.c_str());
      if (next == nullptr) throw TransportError("curl_slist_append failed (out of memory)");
      list_ = next;
    }
    curl_slist* get() const { return list_; }

   private:
    curl_slist* list_ = nullptr;
  };

  static std::size_t write_body(char* ptr, std::size_t size, std::size_t nmemb, void* userdata) {
    const std::size_t bytes = size * nmemb;
    static_cast<std::string*>(userdata)->append(ptr, bytes);
    return bytes;
  }

  static std::size_t write_header(char* ptr, std::size_t size, std::size_t nmemb, void* userdata) {
    const std::size_t bytes = size * nmemb;
    Headers* headers = static_cast<Headers*>(userdata);
    std::string line(ptr, bytes);
    while (!line.empty() && (line.back() == '\r' || line.back() == '\n')) line.pop_back();

    const std::size_t colon = line.find(':');
    // The status line ("HTTP/1.1 200 OK") and the blank separator have no colon; skip both.
    if (colon != std::string::npos) {
      const std::string name = detail::to_lower(detail::trim(line.substr(0, colon)));
      const std::string value = detail::trim(line.substr(colon + 1));
      if (!name.empty()) headers->emplace_back(name, value);
    }
    return bytes;
  }

  CurlOptions options_;
};

/// A shared `CurlTransport` with default options.
inline std::shared_ptr<HttpTransport> curl_transport() {
  static const std::shared_ptr<HttpTransport> kInstance = std::make_shared<CurlTransport>();
  return kInstance;
}

}  // namespace receivify
