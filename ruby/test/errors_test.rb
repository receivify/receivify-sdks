# frozen_string_literal: true

require_relative "test_helper"

class ErrorMappingTest < Minitest::Test
  include TestSupport

  EXPECTED = {
    "bad_request" => [Receivify::BadRequestError, 400],
    "bad_ticker" => [Receivify::BadTickerError, 400],
    "bad_destination" => [Receivify::BadDestinationError, 400],
    "asset_unavailable" => [Receivify::AssetUnavailableError, 400],
    "amount_too_low" => [Receivify::AmountTooLowError, 400],
    "suspended" => [Receivify::SuspendedError, 400],
    "limit_exceeded" => [Receivify::LimitExceededError, 400],
    "invalid_code" => [Receivify::InvalidCodeError, 400],
    "no_setup" => [Receivify::NoSetupError, 400],
    "not_enabled" => [Receivify::NotEnabledError, 400],
    "no_target" => [Receivify::NoTargetError, 400],
    "invalid_token" => [Receivify::InvalidTokenError, 400],
    "unauthorized" => [Receivify::UnauthorizedError, 401],
    "bad_signature" => [Receivify::BadSignatureError, 401],
    "signup_disabled" => [Receivify::SignupDisabledError, 403],
    "not_found" => [Receivify::NotFoundError, 404],
    "already_exists" => [Receivify::AlreadyExistsError, 409],
    "already_enabled" => [Receivify::AlreadyEnabledError, 409],
    "not_allowed" => [Receivify::NotAllowedError, 409],
    "rate_limited" => [Receivify::RateLimitedError, 429],
    "internal" => [Receivify::InternalError, 500],
    "upstream_error" => [Receivify::UpstreamError, 502],
    "rate_unavailable" => [Receivify::RateUnavailableError, 503]
  }.freeze

  def test_registry_covers_every_frozen_code
    assert_equal EXPECTED.keys.sort, Receivify::ERROR_CODES.keys.sort
    EXPECTED.each { |code, (klass, _)| assert_equal klass, Receivify::ERROR_CODES[code] }
  end

  def test_every_code_maps_to_its_class_with_code_detail_status
    EXPECTED.each do |code, (klass, status)|
      client, = make_client([error_response(code, "Detail for #{code}.", status: status)], max_retries: 0)
      err = assert_raises(klass) { client.get_invoice("x") }
      assert_kind_of Receivify::APIError, err
      assert_kind_of Receivify::Error, err
      assert_equal code, err.code
      assert_equal "Detail for #{code}.", err.detail
      assert_equal status, err.status
    end
  end

  def test_unknown_code_falls_back_to_the_generic_api_error
    client, = make_client([error_response("brand_new_code", "Something new.", status: 418)])
    err = assert_raises(Receivify::APIError) { client.get_invoice("x") }
    assert_equal Receivify::APIError, err.class
    assert_equal "brand_new_code", err.code
    assert_equal "Something new.", err.detail
    assert_equal 418, err.status
  end

  def test_non_json_error_body_still_yields_a_usable_error
    client, = make_client([json_response(nil, status: 502, body: "<html>Bad gateway</html>")], max_retries: 0)
    err = assert_raises(Receivify::APIError) { client.get_invoice("x") }
    assert_nil err.code
    assert_equal 502, err.status
    assert_equal "Upstream error.", err.detail
    assert_includes err.body, "Bad gateway"
  end

  def test_empty_error_body
    client, = make_client([json_response(nil, status: 401, body: "")])
    err = assert_raises(Receivify::APIError) { client.get_invoice("x") }
    assert_nil err.code
    assert_equal 401, err.status
  end

  def test_retryable_flags
    assert Receivify::RateLimitedError.new("rate_limited").retryable?
    assert Receivify::InternalError.new("internal").retryable?
    assert Receivify::UpstreamError.new("upstream_error").retryable?
    assert Receivify::RateUnavailableError.new("rate_unavailable").retryable?
    refute Receivify::NotFoundError.new("not_found").retryable?
    refute Receivify::APIError.new("x").retryable?
  end

  def test_rate_limit_headers_are_exposed
    client, = make_client([error_response("rate_limited", "Slow down.", status: 429,
                                          headers: { "Retry-After" => "3", "X-RateLimit-Reset" => "1767225600" })],
                          max_retries: 0)
    err = assert_raises(Receivify::RateLimitedError) { client.get_invoice("x") }
    assert_equal 3, err.retry_after
    assert_equal 1_767_225_600, err.rate_limit_reset
  end

  def test_error_message_never_contains_the_api_key
    client, = make_client([error_response("unauthorized", "Bad key.", status: 401)])
    err = assert_raises(Receivify::UnauthorizedError) { client.get_invoice("x") }
    refute_includes err.message, "secretkey"
    refute_includes err.inspect, "secretkey"
  end

  def test_error_class_for_code
    assert_equal Receivify::NotFoundError, Receivify.error_class_for_code("not_found")
    assert_equal Receivify::APIError, Receivify.error_class_for_code("nope")
    assert_equal Receivify::APIError, Receivify.error_class_for_code(nil)
  end
end
