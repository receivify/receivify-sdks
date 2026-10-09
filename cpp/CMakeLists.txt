cmake_minimum_required(VERSION 3.16)

project(receivify-cpp
  VERSION 1.0.0
  DESCRIPTION "Official Receivify C++ SDK"
  LANGUAGES CXX)

# --------------------------------------------------------------------------------------------
# Options
#
# The core library is header-only and depends on nothing but C++17. The two optional pieces are
# the bundled implementations of the transport and crypto interfaces — see README.md, section
# "The dependency deviation".
# --------------------------------------------------------------------------------------------
option(RECEIVIFY_WITH_CURL    "Build the bundled libcurl HttpTransport" ON)
option(RECEIVIFY_WITH_OPENSSL "Build the bundled OpenSSL Hmac provider" ON)
option(RECEIVIFY_BUILD_TESTS  "Build the offline test suite" ${PROJECT_IS_TOP_LEVEL})
option(RECEIVIFY_BUILD_EXAMPLES "Build the example programs" ${PROJECT_IS_TOP_LEVEL})

# `PROJECT_IS_TOP_LEVEL` only exists from CMake 3.21; fall back for older ones.
if(NOT DEFINED PROJECT_IS_TOP_LEVEL)
  if(CMAKE_SOURCE_DIR STREQUAL PROJECT_SOURCE_DIR)
    set(RECEIVIFY_BUILD_TESTS ON)
    set(RECEIVIFY_BUILD_EXAMPLES ON)
  else()
    set(RECEIVIFY_BUILD_TESTS OFF)
    set(RECEIVIFY_BUILD_EXAMPLES OFF)
  endif()
endif()

# --------------------------------------------------------------------------------------------
# receivify::core — header-only, ZERO dependencies.
#
# Everything above the transport and crypto seams lives here: the client, the retry policy, the
# error mapping, pagination, the JSON parser, the webhook verification logic. If you are bringing
# your own HTTP stack and your own HMAC, this is the only target you need.
# --------------------------------------------------------------------------------------------
add_library(receivify_core INTERFACE)
add_library(receivify::core ALIAS receivify_core)

target_include_directories(receivify_core INTERFACE
  $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
  $<INSTALL_INTERFACE:include>)

target_compile_features(receivify_core INTERFACE cxx_std_17)

# --------------------------------------------------------------------------------------------
# receivify::receivify — core plus whichever bundled providers were enabled.
# --------------------------------------------------------------------------------------------
add_library(receivify INTERFACE)
add_library(receivify::receivify ALIAS receivify)
target_link_libraries(receivify INTERFACE receivify::core)

if(RECEIVIFY_WITH_CURL)
  find_package(CURL REQUIRED)
  target_link_libraries(receivify INTERFACE CURL::libcurl)
  target_compile_definitions(receivify INTERFACE RECEIVIFY_WITH_CURL)
  message(STATUS "receivify: bundled libcurl transport ENABLED (${CURL_VERSION_STRING})")
else()
  message(STATUS "receivify: bundled libcurl transport disabled — supply ClientOptions::transport")
endif()

if(RECEIVIFY_WITH_OPENSSL)
  find_package(OpenSSL REQUIRED)
  target_link_libraries(receivify INTERFACE OpenSSL::Crypto)
  target_compile_definitions(receivify INTERFACE RECEIVIFY_WITH_OPENSSL)
  message(STATUS "receivify: bundled OpenSSL HMAC ENABLED (${OPENSSL_VERSION})")
else()
  message(STATUS "receivify: bundled OpenSSL HMAC disabled — supply ClientOptions::hmac")
endif()

# --------------------------------------------------------------------------------------------
# Tests. Offline by construction: they inject a fake HttpTransport, so `ctest` never touches the
# network. The HMAC tests DO need a real HMAC implementation — they use the OpenSSL one when it
# is available and are skipped otherwise, which is stated in the test output rather than hidden.
# --------------------------------------------------------------------------------------------
if(RECEIVIFY_BUILD_TESTS)
  enable_testing()
  add_subdirectory(tests)
endif()

if(RECEIVIFY_BUILD_EXAMPLES)
  add_subdirectory(examples)
endif()

# --------------------------------------------------------------------------------------------
# Install: headers only.
# --------------------------------------------------------------------------------------------
include(GNUInstallDirs)
install(DIRECTORY include/receivify DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})
