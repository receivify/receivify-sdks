// Receivify C++ SDK — umbrella header.
//
//     #include <receivify/receivify.hpp>
//
//     auto client = receivify::make_client("https://receivify.io", std::getenv("RECEIVIFY_API_KEY"));
//
// This pulls in the bundled libcurl transport and OpenSSL HMAC. To use your own instead, include
// `receivify/client.hpp` directly — neither dependency's headers are reachable from there.
//
// See README.md for the dependency deviation this SDK makes from the shared SDK contract, and
// why.
#pragma once

#include "receivify/client.hpp"
#include "receivify/defaults.hpp"
#include "receivify/errors.hpp"
#include "receivify/hmac.hpp"
#include "receivify/http.hpp"
#include "receivify/json.hpp"
#include "receivify/models.hpp"
#include "receivify/pagination.hpp"
#include "receivify/units.hpp"
#include "receivify/version.hpp"
#include "receivify/webhooks.hpp"
