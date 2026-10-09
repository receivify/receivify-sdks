// Receivify C++ SDK — version constants.
#pragma once

#include <string>

#define RECEIVIFY_VERSION_MAJOR 1
#define RECEIVIFY_VERSION_MINOR 0
#define RECEIVIFY_VERSION_PATCH 0
#define RECEIVIFY_VERSION_STRING "1.0.0"

namespace receivify {

/// The SDK version. Also the `User-Agent` this client sends.
inline const char* version() noexcept { return RECEIVIFY_VERSION_STRING; }

/// `receivify-cpp/<version>` — the required User-Agent from the shared SDK contract.
inline std::string user_agent() { return std::string("receivify-cpp/") + RECEIVIFY_VERSION_STRING; }

}  // namespace receivify
