# frozen_string_literal: true

require_relative "test_helper"

class ResourcesTest < Minitest::Test
  include TestSupport

  def test_invoice_readers_and_hash_access
    inv = Receivify::Invoice.new(TestSupport::INVOICE)
    assert_equal TestSupport::INVOICE["checkout_url"], inv.checkout_url
    assert_equal TestSupport::INVOICE["checkout_url"], inv["checkout_url"]
    assert_equal TestSupport::INVOICE["checkout_url"], inv[:checkout_url]
    assert_equal "abc", inv.metadata["cart"]
    assert_equal "abc", inv.dig(:metadata, :cart)
    assert inv.key?(:status)
    refute inv.settled?
  end

  def test_money_fields_are_exact_strings
    huge = "115792089237316195423570985008687907853269984665640564039457584007913129639935"
    inv = Receivify::Invoice.new(TestSupport::INVOICE.merge("amount_expected" => huge))
    assert_equal huge, inv.amount_expected
    assert_kind_of String, inv.amount_expected
    assert_kind_of String, inv.amount_received
    assert_equal "25.00", inv.amount_fiat
  end

  def test_money_survives_json_round_trip_as_string
    client, = make_client([json_response(nil, body: '{"id":"x","amount_expected":"2100000000000000123","amount_received":"0"}')])
    inv = client.get_invoice("x")
    assert_equal "2100000000000000123", inv.amount_expected
  end

  def test_numeric_money_on_the_wire_is_stringified_without_float_noise
    inv = Receivify::Invoice.new("amount_expected" => 100_000, "amount_fiat" => 0.3, "fee_ontop" => 1.0e-05)
    assert_equal "100000", inv.amount_expected
    assert_equal "0.3", inv.amount_fiat
    assert_equal "0.00001", inv.fee_ontop
  end

  def test_new_invoice_fields
    payload = TestSupport::INVOICE.merge(
      "asset" => "usdc", "chain" => "base", "decimals" => 6, "amount_expected" => "25252526",
      "amount_decimal" => "25.252526", "payment_uri" => nil, "add_fee_ontop" => true,
      "amount_net_requested" => "25000000", "fee_ontop" => "252526"
    )
    client, = make_client([json_response(payload, status: 201)])
    inv = client.create_invoice(amount: "25", asset: "usdc", chain: "base", add_fee_ontop: true)
    assert_equal 6, inv.decimals
    assert_equal "25.252526", inv.amount_decimal
    assert_nil inv.payment_uri
    assert_equal true, inv.add_fee_ontop
    assert inv.add_fee_ontop?
    assert_equal "25000000", inv.amount_net_requested
    assert_equal "252526", inv.fee_ontop
    assert_equal "252526", inv["fee_ontop"]
  end

  def test_new_invoice_fields_defaults_when_absent
    inv = Receivify::Invoice.new("id" => "x")
    assert_nil inv.decimals
    assert_nil inv.amount_decimal
    assert_nil inv.payment_uri
    assert_equal false, inv.add_fee_ontop
    assert_nil inv.amount_net_requested
    assert_nil inv.fee_ontop
  end

  def test_payment_uri_string
    inv = Receivify::Invoice.new(TestSupport::INVOICE)
    assert_equal "bitcoin:bc1qexampleaddress?amount=21000000.00000000", inv.payment_uri
    assert_equal 8, inv.decimals
  end

  def test_missing_fields_never_raise
    inv = Receivify::Invoice.new({})
    assert_equal "", inv.id
    assert_equal "0", inv.amount_expected
    assert_equal({}, inv.metadata)
    assert_nil inv.checkout_url
    inv2 = Receivify::Invoice.new(nil)
    assert_equal "", inv2.status
  end

  def test_unknown_fields_are_kept
    inv = Receivify::Invoice.new(TestSupport::INVOICE.merge("brand_new_field" => { "x" => 1 }))
    assert_equal 1, inv["brand_new_field"]["x"]
    assert_equal 1, inv.raw["brand_new_field"]["x"]
  end

  def test_resources_are_immutable
    inv = Receivify::Invoice.new(TestSupport::INVOICE)
    assert inv.frozen?
    assert inv.raw.frozen?
    assert inv.metadata.frozen?
    assert_raises(FrozenError) { inv.raw["status"] = "paid" }
    assert_raises(FrozenError) { inv.metadata["cart"] = "x" }
    refute_respond_to inv, :status=
  end

  def test_to_h_is_a_mutable_deep_copy
    inv = Receivify::Invoice.new(TestSupport::INVOICE)
    h = inv.to_h
    h["status"] = "paid"
    h["metadata"]["cart"] = "zzz"
    assert_equal "pending", inv.status
    assert_equal "abc", inv.metadata["cart"]
  end

  def test_source_hash_mutation_does_not_leak_in
    src = { "id" => "a", "metadata" => { "k" => "v" } }
    inv = Receivify::Invoice.new(src)
    src["metadata"]["k"] = "changed"
    assert_equal "v", inv.metadata["k"]
  end

  def test_equality_and_to_json
    a = Receivify::Invoice.new(TestSupport::INVOICE)
    b = Receivify::Invoice.new(TestSupport::INVOICE)
    assert_equal a, b
    assert_equal a.hash, b.hash
    assert_equal TestSupport::INVOICE, JSON.parse(a.to_json)
  end

  def test_settled
    assert Receivify::Invoice.new("status" => "paid").settled?
    assert Receivify::Invoice.new("status" => "swept").settled?
    refute Receivify::Invoice.new("status" => "confirming").settled?
  end

  def test_forward_id_alias_fallback
    fwd = Receivify::Forward.new("forward_id" => "legacy-id")
    assert_equal "legacy-id", fwd.id
  end

  def test_api_key_created_redacts_secret
    key = Receivify::ApiKeyCreated.new("id" => "k1", "api_key" => "rcv_live_supersecret", "api_key_prefix" => "rcv_live_sup")
    assert_equal "rcv_live_sup", key.prefix
    refute_includes key.inspect, "supersecret"
    refute key.replay?
    assert Receivify::ApiKeyCreated.new("id" => "k1", "api_key" => nil).replay?
  end

  def test_settings_include_add_fee_ontop
    s = Receivify::Settings.new("display_currency" => "USD", "email" => "a@b.c", "fee_bps" => 100, "add_fee_ontop" => true)
    assert_equal 100, s.fee_bps
    assert s.add_fee_ontop
  end

  def test_status_enums
    assert_includes Receivify::INVOICE_STATUSES, "cancelled"
    assert_includes Receivify::EVENT_NAMES, "invoice.paid"
    assert_includes Receivify::EVENT_NAMES, "forward.sent"
    assert_equal 16, Receivify::EVENT_NAMES.size
  end
end
