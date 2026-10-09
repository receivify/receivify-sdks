# frozen_string_literal: true

require_relative "test_helper"
require "openssl"
require "stringio"

class WebhookTest < Minitest::Test
  include TestSupport

  SECRET = "whsec_current_secret"
  PREVIOUS = "whsec_previous_secret"
  NOW = 1_767_225_600
  BODY = '{"event":"invoice.paid","sequence":42,"created_at":"2026-01-01T00:00:00.000Z",' \
         '"data":{"id":"inv_1","reference":"order-1001","status":"paid","amount_expected":"100000",' \
         '"amount_received":"100000","address":"bc1qx","paid_at":"2026-01-01T00:00:00.000Z"}}'

  # Signed INDEPENDENTLY of the SDK, straight from the spec: HMAC-SHA256 hex over "#{t}.#{body}".
  def sign(body, secret, ts = NOW)
    "t=#{ts},v1=#{OpenSSL::HMAC.hexdigest('SHA256', secret, "#{ts}.#{body}")}"
  end

  def test_valid_signature_verifies
    assert Receivify::Webhook.verify(BODY, sign(BODY, SECRET), [SECRET], now: NOW)
  end

  def test_single_string_secret_is_accepted
    assert Receivify::Webhook.verify(BODY, sign(BODY, SECRET), SECRET, now: NOW)
  end

  def test_previous_secret_verifies_during_rotation
    assert Receivify::Webhook.verify(BODY, sign(BODY, PREVIOUS), [SECRET, PREVIOUS], now: NOW)
    assert Receivify::Webhook.verify(BODY, sign(BODY, SECRET), [SECRET, PREVIOUS], now: NOW)
  end

  def test_previous_secret_alone_fails_against_current_only_list
    refute Receivify::Webhook.verify(BODY, sign(BODY, PREVIOUS), [SECRET], now: NOW)
  end

  def test_wrong_secret_fails
    refute Receivify::Webhook.verify(BODY, sign(BODY, "whsec_other"), [SECRET], now: NOW)
  end

  def test_tampered_body_fails
    tampered = BODY.sub('"100000","address"', '"999999","address"')
    refute_equal BODY, tampered
    refute Receivify::Webhook.verify(tampered, sign(BODY, SECRET), [SECRET], now: NOW)
  end

  def test_reserialised_json_fails
    reserialised = JSON.pretty_generate(JSON.parse(BODY))
    refute Receivify::Webhook.verify(reserialised, sign(BODY, SECRET), [SECRET], now: NOW)
  end

  def test_old_timestamp_fails_with_default_tolerance
    header = sign(BODY, SECRET, NOW - 3600)
    refute Receivify::Webhook.verify(BODY, header, [SECRET], now: NOW)
  end

  def test_tolerance_boundary_is_300_seconds
    assert Receivify::Webhook.verify(BODY, sign(BODY, SECRET, NOW - 300), [SECRET], now: NOW)
    refute Receivify::Webhook.verify(BODY, sign(BODY, SECRET, NOW - 301), [SECRET], now: NOW)
    assert_equal 300, Receivify::Webhook::DEFAULT_TOLERANCE
  end

  def test_future_timestamp_outside_tolerance_fails
    refute Receivify::Webhook.verify(BODY, sign(BODY, SECRET, NOW + 3600), [SECRET], now: NOW)
  end

  def test_custom_and_disabled_tolerance
    header = sign(BODY, SECRET, NOW - 3600)
    assert Receivify::Webhook.verify(BODY, header, [SECRET], tolerance: 7200, now: NOW)
    assert Receivify::Webhook.verify(BODY, header, [SECRET], tolerance: nil, now: NOW)
  end

  def test_uses_wall_clock_by_default
    assert Receivify::Webhook.verify(BODY, sign(BODY, SECRET, Time.now.to_i), [SECRET])
  end

  def test_malformed_headers_fail_without_raising
    [nil, "", "garbage", "t=abc,v1=00", "v1=#{'0' * 64}", "t=#{NOW}", "t=#{NOW},v1="].each do |header|
      refute Receivify::Webhook.verify(BODY, header, [SECRET], now: NOW), "header #{header.inspect}"
    end
  end

  def test_no_secrets_fails
    refute Receivify::Webhook.verify(BODY, sign(BODY, SECRET), [], now: NOW)
    refute Receivify::Webhook.verify(BODY, sign(BODY, SECRET), nil, now: NOW)
    refute Receivify::Webhook.verify(BODY, sign(BODY, SECRET), [nil, ""], now: NOW)
  end

  def test_header_with_spaces_uppercase_hex_and_multiple_v1
    sig = OpenSSL::HMAC.hexdigest("SHA256", SECRET, "#{NOW}.#{BODY}")
    assert Receivify::Webhook.verify(BODY, "t=#{NOW}, v1=#{sig.upcase}", [SECRET], now: NOW)
    assert Receivify::Webhook.verify(BODY, "t=#{NOW},v1=#{'0' * 64},v1=#{sig}", [SECRET], now: NOW)
  end

  def test_wrong_length_signature_fails
    refute Receivify::Webhook.verify(BODY, "t=#{NOW},v1=abcd", [SECRET], now: NOW)
  end

  def test_binary_and_utf8_bodies_are_hashed_as_raw_bytes
    body = '{"event":"test","sequence":1,"created_at":null,"data":{"reference":"café"}}'
    header = sign(body, SECRET)
    assert Receivify::Webhook.verify(body, header, [SECRET], now: NOW)
    assert Receivify::Webhook.verify(body.b, header, [SECRET], now: NOW)
  end

  def test_io_body_is_read_and_rewound
    io = StringIO.new(BODY)
    assert Receivify::Webhook.verify(io, sign(BODY, SECRET), [SECRET], now: NOW)
    assert_equal BODY, io.read
  end

  def test_non_string_body_raises_type_error
    assert_raises(TypeError) { Receivify::Webhook.verify({ "a" => 1 }, sign(BODY, SECRET), [SECRET], now: NOW) }
  end

  def test_parse_returns_the_event_envelope
    event = Receivify::Webhook.parse(BODY, sign(BODY, SECRET), [SECRET], now: NOW)
    assert_instance_of Receivify::WebhookEvent, event
    assert_equal "invoice.paid", event.event
    assert_equal 42, event.sequence
    assert_equal "2026-01-01T00:00:00.000Z", event.created_at
    assert_equal "inv_1", event.data["id"]
    assert_equal "inv_1", event["data"]["id"]
    assert event.invoice_event?
    refute event.forward_event?
    assert_equal "100000", event.invoice.amount_received
    assert_equal "order-1001", event.invoice.reference
    assert_nil event.forward
    assert_equal ["invoice.paid", 42], event.dedupe_key
  end

  def test_parse_raises_on_bad_signature
    err = assert_raises(Receivify::SignatureVerificationError) do
      Receivify::Webhook.parse(BODY, sign(BODY, "whsec_wrong"), [SECRET], now: NOW)
    end
    assert_includes err.message, "RAW"
    assert_kind_of Receivify::Error, err
  end

  def test_parse_raises_payload_error_for_non_object_json
    body = "[1,2,3]"
    assert_raises(Receivify::WebhookPayloadError) { Receivify::Webhook.parse(body, sign(body, SECRET), [SECRET], now: NOW) }
    body = "not json"
    assert_raises(Receivify::WebhookPayloadError) { Receivify::Webhook.parse(body, sign(body, SECRET), [SECRET], now: NOW) }
  end

  def test_forward_event_payload
    body = JSON.generate("event" => "forward.sent", "sequence" => 7, "created_at" => "2026-01-01T00:00:00.000Z",
                         "data" => { "forward_id" => "fwd_1", "ticker" => "btc", "address_in" => "bc1qin",
                                     "address_out" => "bc1qout", "txid_in" => "aa", "txid_out" => "bb",
                                     "value" => "150000", "value_forwarded" => "148500", "fee" => "1500",
                                     "confirmations" => 2, "pending" => false })
    event = Receivify::Webhook.parse(body, sign(body, SECRET), SECRET, now: NOW)
    assert event.forward_event?
    assert_nil event.invoice
    assert_equal "148500", event.forward.value_forwarded
    assert_equal "1500", event.forward.fee
    assert_equal 2, event.forward.confirmations
  end

  def test_unknown_event_names_still_parse
    body = JSON.generate("event" => "invoice.something_new", "sequence" => 1, "created_at" => nil, "data" => {})
    event = Receivify::Webhook.parse(body, sign(body, SECRET), SECRET, now: NOW)
    assert_equal "invoice.something_new", event.event
  end

  def test_legacy_invoice_shape_is_tolerated
    body = JSON.generate("event" => "invoice.paid", "invoice" => { "id" => "inv_legacy" })
    event = Receivify::Webhook.parse(body, sign(body, SECRET), SECRET, now: NOW)
    assert_equal "inv_legacy", event.data["id"]
  end

  def test_compute_signature_and_generate_header_match_the_spec
    expected = OpenSSL::HMAC.hexdigest("SHA256", SECRET, "#{NOW}.#{BODY}")
    assert_equal expected, Receivify::Webhook.compute_signature(BODY, NOW, SECRET)
    assert_equal "t=#{NOW},v1=#{expected}", Receivify::Webhook.generate_header(BODY, SECRET, timestamp: NOW)
  end

  def test_constant_time_compare_is_used
    # Both code paths must agree.
    assert Receivify::Webhook.secure_compare("abc", "abc")
    refute Receivify::Webhook.secure_compare("abc", "abd")
    refute Receivify::Webhook.secure_compare("abc", "abcd")
  end

  def test_client_helpers_use_configured_secrets
    client, = make_client([], webhook_secrets: [SECRET, PREVIOUS])
    assert client.verify_webhook(BODY, sign(BODY, PREVIOUS), now: NOW)
    assert_equal "invoice.paid", client.parse_webhook(BODY, sign(BODY, SECRET), now: NOW).event
    refute client.verify_webhook(BODY, sign(BODY, SECRET), ["whsec_other"], now: NOW)
  end

  def test_signature_header_constants
    assert_equal "X-Receivify-Signature", Receivify::Webhook::SIGNATURE_HEADER
    assert_equal "HTTP_X_RECEIVIFY_SIGNATURE", Receivify::Webhook::RACK_HEADER
  end
end
