# Offline test suite. Runs with `ctest`; needs no network and no API key.

set(RECEIVIFY_TEST_WARNINGS "")
if(MSVC)
  set(RECEIVIFY_TEST_WARNINGS /W4 /permissive-)
else()
  set(RECEIVIFY_TEST_WARNINGS -Wall -Wextra -Wpedantic)
endif()

function(receivify_add_test name)
  add_executable(${name} ${ARGN})
  target_link_libraries(${name} PRIVATE receivify::receivify)
  target_include_directories(${name} PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
  target_compile_options(${name} PRIVATE ${RECEIVIFY_TEST_WARNINGS})
  if(MSVC)
    # /EHsc     — the SDK throws and catches across the whole surface.
    # /utf-8    — the test fixtures contain UTF-8 byte sequences.
    # /Zc:preprocessor — the conforming preprocessor. Without it MSVC's legacy one mangles raw
    #             string literals passed as macro arguments, which is most of this suite.
    target_compile_options(${name} PRIVATE /EHsc /utf-8)
    if(MSVC_VERSION GREATER_EQUAL 1925)
      target_compile_options(${name} PRIVATE /Zc:preprocessor)
    endif()
  endif()
  add_test(NAME ${name} COMMAND ${name})
endfunction()

receivify_add_test(receivify_test_json     test_json.cpp)
receivify_add_test(receivify_test_units    test_units.cpp)
receivify_add_test(receivify_test_webhooks test_webhooks.cpp)

# Two translation units on purpose — see second_translation_unit.cpp.
receivify_add_test(receivify_test_client   test_client.cpp second_translation_unit.cpp)
