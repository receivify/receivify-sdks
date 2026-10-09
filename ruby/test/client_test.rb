# frozen_string_literal: true

require_relative "test_helper"

class ClientRequestBuildingTest < Minitest::Test
  include TestSupport

  def test_create_invoice_sends_bearer_key_json_body_and_user_agent
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    invoice = client.create_invoice(amount_fiat: "25.00", currency: "usd", reference: "order-1001",
                                    metadata: { cart: "abc" })
    req = transport.last_request
    assert_equal "POST", req.method
    assert_equal "https://receivify.io/api/v1/invoices", req.url
    assert_equal "Bearer rcv_live_secretkey", req.header("Authorization")
    assert_equal "application/json", req.header("Content-Type")
    assert_equal "application/json", req.header("Accept")
    assert_equal "receivify-ruby/#{Receivify::VERSION}", req.header("User-Agent")
    assert_equal({ "amount_fiat" => "25.00", "currency" => "USD", "reference" => "order-1001",
                   "metadata" => { "cart" => "abc" } }, transport.body_of)
    assert_instance_of Receivify::Invoice, invoice
    assert_equal "order-1001", invoice.reference
  end

  def test_create_invoice_passes_asset_and_chain
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount: "25", asset: "usdc", chain: "base", callback_url: "https://shop.example/hook")
    assert_equal({ "amount" => "25", "asset" => "usdc", "chain" => "base",
                   "callback_url" => "https://shop.example/hook" }, transport.body_of)
  end

  def test_default_api_url_and_trailing_slash_normalised
    transport = TestSupport::FakeTransport.new([json_response(INVOICE), json_response(INVOICE)])
    Receivify::Client.new(api_key: "k", transport: transport).get_invoice("x")
    assert_equal "https://receivify.io/api/v1/invoices/x", transport.last_request.url
    Receivify::Client.new(api_url: "https://example.test///", api_key: "k", transport: transport).get_invoice("x")
    assert_equal "https://example.test/api/v1/invoices/x", transport.last_request.url
  end

  def test_api_key_never_appears_in_a_url
    client, transport, = make_client([json_response(INVOICE), list_response([])])
    client.get_invoice(INVOICE["id"])
    client.list_invoices(limit: 10, status: "pending")
    transport.requests.each { |r| refute_includes r.url, "rcv_live" }
  end

  def test_client_inspect_masks_the_api_key
    client, = make_client
    refute_includes client.inspect, "secretkey"
    refute_includes client.to_s, "secretkey"
    assert_includes client.inspect, "rcv_live_sec"
  end

  def test_request_inspect_redacts_authorization
    client, transport, = make_client([json_response(INVOICE)])
    client.get_invoice(INVOICE["id"])
    refute_includes transport.last_request.inspect, "rcv_live_secretkey"
    assert_includes transport.last_request.inspect, "<redacted>"
  end

  def test_idempotency_key_is_sent_as_a_header
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: "42000", idempotency_key: "order-1001-attempt-1")
    assert_equal "order-1001-attempt-1", transport.last_request.header("Idempotency-Key")
  end

  def test_no_idempotency_header_without_a_key
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: "42000")
    assert_nil transport.last_request.header("Idempotency-Key")
  end

  def test_query_params_skip_nil_and_encode_booleans
    client, transport, = make_client([list_response([])])
    client.list_payment_links(limit: 25, cursor: nil, active: true)
    assert_equal({ "limit" => "25", "active" => "true" }, transport.query_of)
  end

  def test_path_ids_are_percent_encoded
    client, transport, = make_client([json_response(INVOICE)])
    client.get_invoice("weird id/../../admin")
    assert transport.last_request.url.end_with?("/api/v1/invoices/weird%20id%2F..%2F..%2Fadmin"), transport.last_request.url
  end

  def test_empty_id_is_refused
    client, transport, = make_client
    assert_raises(ArgumentError) { client.get_invoice("") }
    assert_empty transport.requests
  end

  def test_public_endpoints_send_no_authorization_header
    client, transport, = make_client([list_response([]), list_response([])])
    client.list_tickers
    assert_nil transport.last_request.header("Authorization")
    client.list_assets
    assert_nil transport.last_request.header("Authorization")
  end

  def test_timeout_defaults_to_30_and_is_carried_on_every_request
    client, transport, = make_client([json_response(INVOICE)])
    client.get_invoice("x")
    assert_equal 30, transport.last_request.timeout
    client2, transport2, = make_client([json_response(INVOICE)], timeout: 5.5)
    client2.get_invoice("x")
    assert_equal 5.5, transport2.last_request.timeout
  end

  def test_cancel_invoice_posts_with_no_body
    client, transport, = make_client([json_response(INVOICE.merge("status" => "cancelled"))])
    invoice = client.cancel_invoice(INVOICE["id"])
    assert_equal "POST", transport.last_request.method
    assert transport.last_request.url.end_with?("/api/v1/invoices/#{INVOICE['id']}/cancel")
    assert_nil transport.last_request.body
    assert_equal "cancelled", invoice.status
  end

  def test_rotate_webhook_secret
    payload = { "secret" => "whsec_new", "rotated_at" => "2026-08-25T11:00:00.000Z",
                "prev_expires_at" => "2026-08-26T11:00:00.000Z", "grace_seconds" => 86_400,
                "replaced_previous" => false, "message" => "Store it now." }
    client, transport, = make_client([json_response(payload)])
    rotated = client.rotate_webhook_secret(idempotency_key: "rotate-1")
    assert_equal "https://receivify.io/api/v1/webhooks/secret/rotate", transport.last_request.url
    assert_equal "rotate-1", transport.last_request.header("Idempotency-Key")
    assert_equal "whsec_new", rotated.secret
    assert_equal 86_400, rotated.grace_seconds
    refute rotated.replay?
    refute_includes rotated.inspect, "whsec_new"
  end

  def test_bulk_cancel_enforces_limits_client_side
    client, transport, = make_client
    assert_raises(ArgumentError) { client.bulk_cancel_invoices([]) }
    assert_raises(ArgumentError) { client.bulk_cancel_invoices(["id"] * 501) }
    assert_empty transport.requests
  end

  def test_bulk_cancel_body
    client, transport, = make_client([json_response({ "ok" => true, "action" => "cancel", "requested" => 2, "cancelled" => 1 })])
    result = client.bulk_cancel_invoices(%w[a b])
    assert_equal({ "ids" => %w[a b], "action" => "cancel" }, transport.body_of)
    assert_equal 1, result["cancelled"]
  end

  def test_health_503_is_not_mapped_to_an_api_error
    degraded = { "status" => "degraded", "time" => "2026-08-25T11:00:00.000Z", "checks" => {}, "alerts" => [] }
    client, = make_client([json_response(degraded, status: 503)])
    assert_equal "degraded", client.get_health["status"]
  end

  def test_update_settings_validates_add_fee_ontop
    client, transport, = make_client([json_response({ "ok" => true, "add_fee_ontop" => true })])
    client.update_settings(add_fee_ontop: true)
    assert_equal({ "add_fee_ontop" => true }, transport.body_of)
    assert_raises(TypeError) { client.update_settings(add_fee_ontop: "yes") }
    assert_raises(ArgumentError) { client.update_settings }
  end
end

class ClientAddFeeOntopTest < Minitest::Test
  include TestSupport

  def test_omitted_is_not_sent_at_all
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: "1000")
    refute transport.body_of.key?("add_fee_ontop")
  end

  def test_nil_is_the_same_as_omitted
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: "1000", add_fee_ontop: nil)
    refute transport.body_of.key?("add_fee_ontop")
  end

  def test_false_is_sent_as_a_json_false
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: "1000", add_fee_ontop: false)
    assert_includes transport.last_request.body, '"add_fee_ontop":false'
    assert_equal false, transport.body_of["add_fee_ontop"]
  end

  def test_true_is_sent_as_a_json_true
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: "1000", add_fee_ontop: true)
    assert_includes transport.last_request.body, '"add_fee_ontop":true'
  end

  def test_non_booleans_are_rejected_before_any_request
    client, transport, = make_client
    ["true", 1, 0, "false", :yes, []].each do |bad|
      assert_raises(TypeError, "expected #{bad.inspect} to be rejected") do
        client.create_invoice(amount_sats: "1000", add_fee_ontop: bad)
      end
    end
    assert_empty transport.requests
  end
end

class ClientAmountGuardTest < Minitest::Test
  include TestSupport

  def test_float_amounts_are_refused_loudly
    client, transport, = make_client
    error = assert_raises(TypeError) { client.create_invoice(amount_sats: 0.1 + 0.2) }
    assert_includes error.message, "Float"
    assert_raises(TypeError) { client.create_invoice(amount_fiat: 25.0, currency: "USD") }
    assert_raises(TypeError) { client.create_invoice(amount: 0.001) }
    assert_empty transport.requests
  end

  def test_missing_amount_is_caught_before_the_request
    client, transport, = make_client
    assert_raises(ArgumentError) { client.create_invoice(reference: "order-1") }
    assert_empty transport.requests
  end

  def test_empty_amount_string_is_refused
    client, = make_client
    assert_raises(ArgumentError) { client.create_invoice(amount: "  ") }
  end

  def test_integer_base_units_are_stringified
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: 42_000)
    assert_equal "42000", transport.body_of["amount_sats"]
  end

  def test_bigdecimal_amount_is_serialised_without_exponent
    require "bigdecimal"
    client, transport, = make_client([json_response(INVOICE, status: 201), json_response(INVOICE, status: 201)])
    client.create_invoice(amount_fiat: BigDecimal("2.5E+1"), currency: "USD")
    assert_equal "25", transport.body_of["amount_fiat"]
    client.create_invoice(amount: BigDecimal("0.00042"))
    assert_equal "0.00042", transport.body_of["amount"]
  end

  def test_money_is_sent_as_json_strings_never_numbers
    client, transport, = make_client([json_response(INVOICE, status: 201)])
    client.create_invoice(amount_sats: 42_000)
    assert_includes transport.last_request.body, '"amount_sats":"42000"'
  end
end

class ClientForwardingTest < Minitest::Test
  include TestSupport

  def test_create_address_posts_the_body
    client, transport, = make_client([json_response(FORWARD, status: 201)])
    forward = client.create_address(ticker: "btc", destination: "bc1qmywallet",
                                    callback: "https://shop.example/hooks/receivify", confirmations: 2)
    assert_equal "/api/v1/address", URI.parse(transport.last_request.url).path
    assert_equal({ "ticker" => "btc", "destination" => "bc1qmywallet",
                   "callback" => "https://shop.example/hooks/receivify", "confirmations" => 2 }, transport.body_of)
    assert_instance_of Receivify::Forward, forward
    assert_equal FORWARD["id"], forward.id
    assert_equal FORWARD["id"], forward.forward_id
    assert_equal "0.000100", forward.min_forward
  end

  def test_forward_aliases_point_at_the_same_methods
    assert_equal Receivify::Client.instance_method(:create_address), Receivify::Client.instance_method(:create_forward)
    assert_equal Receivify::Client.instance_method(:disable_address), Receivify::Client.instance_method(:disable_forward)
    assert_equal Receivify::Client.instance_method(:update_address), Receivify::Client.instance_method(:update_forward)
  end

  def test_update_address_can_clear_callback_but_not_send_an_empty_patch
    client, transport, = make_client([json_response(FORWARD)])
    client.update_address(FORWARD["id"], callback_url: nil)
    assert_equal "PATCH", transport.last_request.method
    assert_equal({ "callback_url" => nil }, transport.body_of)
    assert_raises(ArgumentError) { client.update_address(FORWARD["id"]) }
  end

  def test_update_address_omits_untouched_fields
    client, transport, = make_client([json_response(FORWARD)])
    client.update_address(FORWARD["id"], status: "paused")
    assert_equal({ "status" => "paused" }, transport.body_of)
  end

  def test_disable_address_uses_delete
    client, transport, = make_client([json_response(FORWARD.merge("status" => "disabled"))])
    forward = client.disable_address(FORWARD["id"])
    assert_equal "DELETE", transport.last_request.method
    assert_equal "disabled", forward.status
  end

  def test_get_address_returns_detail_with_sub_collections
    detail = FORWARD.merge(
      "payments" => [{ "txid_in" => "aa" * 32, "value" => "150000", "status" => "sent",
                       "txid_out" => "bb" * 32, "confirmations" => 3, "created_at" => "2026-08-25T11:30:00.000Z" }],
      "payments_has_more" => true, "callbacks" => [], "callbacks_has_more" => false
    )
    client, = make_client([json_response(detail)])
    fwd = client.get_address(FORWARD["id"])
    assert_instance_of Receivify::ForwardDetail, fwd
    assert_equal 1, fwd.payments.size
    assert_equal "150000", fwd.payments[0].value
    assert fwd.payments_has_more
    assert_equal [], fwd.callbacks
  end

  def test_list_addresses_passes_filters
    client, transport, = make_client([list_response([FORWARD])])
    page = client.list_addresses(limit: 5, status: "active", ticker: "btc")
    assert_equal({ "limit" => "5", "status" => "active", "ticker" => "btc" }, transport.query_of)
    assert_equal FORWARD["id"], page.first.id
  end
end

class ClientTransportErrorTest < Minitest::Test
  include TestSupport

  def test_non_json_body_raises_a_receivify_error_not_a_json_error
    client, = make_client([json_response(nil, body: "<html>proxy</html>")])
    assert_raises(Receivify::Error) { client.get_invoice(INVOICE["id"]) }
  end

  def test_transport_failure_on_a_post_without_idempotency_key_is_not_retried
    client, transport, = make_client([Receivify::TransportError.new("connection reset")])
    assert_raises(Receivify::TransportError) { client.create_invoice(amount_sats: "1000") }
    assert_equal 1, transport.requests.size
  end

  def test_timeout_error_is_a_transport_error
    assert Receivify::TimeoutError < Receivify::TransportError
    assert Receivify::TransportError < Receivify::Error
  end
end
