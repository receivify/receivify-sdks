// A ~100-line test harness.
//
// Bringing in GoogleTest or Catch2 would mean the test suite has a dependency the library does
// not, and the shared SDK contract asks for tests that run with the language's STOCK tooling.
// This runs under plain `ctest`.
#pragma once

#include <exception>
#include <functional>
#include <iostream>
#include <sstream>
#include <string>
#include <vector>

namespace rtest {

struct TestCase {
  std::string name;
  std::function<void()> fn;
};

inline std::vector<TestCase>& registry() {
  static std::vector<TestCase> cases;
  return cases;
}

struct Registrar {
  Registrar(std::string name, std::function<void()> fn) { registry().push_back({std::move(name), std::move(fn)}); }
};

class Failure : public std::exception {
 public:
  explicit Failure(std::string message) : message_(std::move(message)) {}
  const char* what() const noexcept override { return message_.c_str(); }

 private:
  std::string message_;
};

/// Anything streamable prints itself; a bool prints as a word rather than 1/0.
template <typename T>
std::string show(const T& value) {
  std::ostringstream os;
  os << value;
  return os.str();
}
inline std::string show(bool value) { return value ? "true" : "false"; }
inline std::string show(const std::string& value) { return "\"" + value + "\""; }

inline int run_all(const char* suite) {
  int failed = 0;
  std::cout << "== " << suite << " (" << registry().size() << " tests)\n";
  for (const TestCase& test : registry()) {
    try {
      test.fn();
      std::cout << "  ok   " << test.name << "\n";
    } catch (const std::exception& e) {
      std::cout << "  FAIL " << test.name << "\n       " << e.what() << "\n";
      ++failed;
    } catch (...) {
      std::cout << "  FAIL " << test.name << "\n       (non-std exception)\n";
      ++failed;
    }
  }
  if (failed == 0) {
    std::cout << "== all " << registry().size() << " passed\n";
  } else {
    std::cout << "== " << failed << " of " << registry().size() << " FAILED\n";
  }
  return failed == 0 ? 0 : 1;
}

}  // namespace rtest

#define RECEIVIFY_CAT_(a, b) a##b
#define RECEIVIFY_CAT(a, b) RECEIVIFY_CAT_(a, b)

#define TEST(name)                                                    \
  static void name();                                                 \
  static ::rtest::Registrar RECEIVIFY_CAT(reg_, name)(#name, name);   \
  static void name()

#define CHECK(expr)                                                                            \
  do {                                                                                         \
    if (!(expr)) {                                                                             \
      throw ::rtest::Failure(std::string(__FILE__ ":") + std::to_string(__LINE__) +             \
                             " CHECK failed: " #expr);                                          \
    }                                                                                          \
  } while (false)

// Both operands are copied, not bound by reference. `f().g()` where `g()` returns a reference
// INTO the temporary `f()` would otherwise dangle by the time the comparison runs — a trap that
// cost a real debugging session while writing this suite.
#define CHECK_EQ(actual, expected)                                                             \
  do {                                                                                         \
    const auto rtest_a_ = (actual);                                                             \
    const auto rtest_b_ = (expected);                                                           \
    if (!(rtest_a_ == rtest_b_)) {                                                             \
      throw ::rtest::Failure(std::string(__FILE__ ":") + std::to_string(__LINE__) +             \
                             " CHECK_EQ failed: " #actual " == " #expected                      \
                             "\n         actual:   " + ::rtest::show(rtest_a_) +               \
                             "\n         expected: " + ::rtest::show(rtest_b_));               \
    }                                                                                          \
  } while (false)

#define CHECK_THROWS_AS(stmt, ExceptionType)                                                   \
  do {                                                                                         \
    bool rtest_threw_ = false;                                                                 \
    try {                                                                                      \
      stmt;                                                                                    \
    } catch (const ExceptionType&) {                                                           \
      rtest_threw_ = true;                                                                     \
    } catch (const std::exception& rtest_other_) {                                             \
      throw ::rtest::Failure(std::string(__FILE__ ":") + std::to_string(__LINE__) +             \
                             " expected " #ExceptionType " from " #stmt " but got: " +          \
                             rtest_other_.what());                                             \
    }                                                                                          \
    if (!rtest_threw_) {                                                                       \
      throw ::rtest::Failure(std::string(__FILE__ ":") + std::to_string(__LINE__) +             \
                             " expected " #ExceptionType " from " #stmt " but nothing threw");  \
    }                                                                                          \
  } while (false)

#define CHECK_NO_THROW(stmt)                                                                   \
  do {                                                                                         \
    try {                                                                                      \
      stmt;                                                                                    \
    } catch (const std::exception& rtest_e_) {                                                 \
      throw ::rtest::Failure(std::string(__FILE__ ":") + std::to_string(__LINE__) +             \
                             " unexpected exception from " #stmt ": " + rtest_e_.what());       \
    }                                                                                          \
  } while (false)
