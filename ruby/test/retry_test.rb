# frozen_string_literal: true

require_relative "test_helper"

class RetryPolicyTest < Minitest::Test
  include TestSupport

  def test_get_is_retried_on_5xx_then_succeeds
    client, transport, slept = make_client([error_response("internal", "boom", status: 500),
                                            error_response("upstream_error", "node", status: 502),
                                            json_response(TestSupport::INVOICE)])
    invoice = client.get_invoice("x")
    assert_equal "order-1001", invoice.reference
    assert_equal 3, transport.requests.size
    assert_equal 2, slept.size
  end

  def test_retries_are_bounded_by_max_retries
    responses = Array.new(5) { error_response("internal", "boom", status: 500) }
    client, transport, = make_client(responses, max_retries: 2)
    err = assert_raises(Receivify::InternalError) { client.get_invoice("x") }
    assert_equal 500, err.status
    assert_equal 3, transport.requests.size
  end

  def test_post_without_idempotency_key_is_never_retried
    client, transport, slept = make_client([error_response("internal", "boom", status: 500)])
    assert_raises(Receivify::InternalError) { client.create_invoice(amount_sats: "1000") }
    assert_equal 1, transport.requests.size
    assert_empty slept
  end

  def test_post_with_idempotency_key_is_retried_with_the_same_key
    client, transport, = make_client([error_response("rate_limited", "slow", status: 429),
                                      json_response(TestSupport::INVOICE, status: 201)])
    client.create_invoice(amount_sats: "1000", idempotency_key: "order-1")
    assert_equal 2, transport.requests.size
    assert_equal(%w[order-1 order-1], transport.requests.map { |r| r.header("Idempotency-Key") })
    assert_equal transport.requests[0].body, transport.requests[1].body
  end

  def test_4xx_other_than_429_is_never_retried
    client, transport, = make_client([error_response("not_found", "nope", status: 404)])
    assert_raises(Receivify::NotFoundError) { client.get_invoice("x") }
    assert_equal 1, transport.requests.size
  end

  def test_transport_errors_on_get_are_retried
    client, transport, = make_client([Receivify::TimeoutError.new("slow"), json_response(TestSupport::INVOICE)])
    client.get_invoice("x")
    assert_equal 2, transport.requests.size
  end

  def test_retry_after_is_honoured
    client, _, slept = make_client([error_response("rate_limited", "slow", status: 429, headers: { "Retry-After" => "7" }),
                                    json_response(TestSupport::INVOICE)])
    client.get_invoice("x")
    assert_operator slept[0], :>=, 7.0
    assert_operator slept[0], :<=, 7.0 * 1.25
  end

  def test_x_ratelimit_reset_is_honoured
    reset = (Time.now.to_i + 10).to_s
    client, _, slept = make_client([error_response("rate_limited", "slow", status: 429, headers: { "X-RateLimit-Reset" => reset }),
                                    json_response(TestSupport::INVOICE)])
    client.get_invoice("x")
    assert_operator slept[0], :>=, 8.0
    assert_operator slept[0], :<=, 10.0 * 1.25 + 1
  end

  def test_server_wait_is_capped
    client, _, slept = make_client([error_response("rate_limited", "slow", status: 429, headers: { "Retry-After" => "99999" }),
                                    json_response(TestSupport::INVOICE)])
    client.get_invoice("x")
    assert_operator slept[0], :<=, 60.0 * 1.25
  end

  def test_backoff_grows_exponentially
    responses = [error_response("internal", "a", status: 500), error_response("internal", "b", status: 500),
                 error_response("internal", "c", status: 500), json_response(TestSupport::INVOICE)]
    client, _, slept = make_client(responses, max_retries: 3)
    client.get_invoice("x")
    assert_equal 3, slept.size
    assert_operator slept[0], :>=, 0.5
    assert_operator slept[1], :>=, 1.0
    assert_operator slept[2], :>=, 2.0
  end

  def test_max_retries_zero_disables_retrying
    client, transport, = make_client([error_response("internal", "boom", status: 500)], max_retries: 0)
    assert_raises(Receivify::InternalError) { client.get_invoice("x") }
    assert_equal 1, transport.requests.size
  end
end
