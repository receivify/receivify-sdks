# frozen_string_literal: true

require_relative "test_helper"

class PaginationTest < Minitest::Test
  include TestSupport

  def inv(id)
    TestSupport::INVOICE.merge("id" => id)
  end

  def test_list_returns_a_page_with_envelope_fields
    client, transport, = make_client([list_response([inv("a"), inv("b")], has_more: true, next_cursor: "cur_1")])
    page = client.list_invoices(limit: 2, status: "pending")
    assert_equal({ "limit" => "2", "status" => "pending" }, transport.query_of)
    assert_instance_of Receivify::Page, page
    assert_equal 2, page.size
    assert page.has_more?
    assert_equal true, page.has_more
    assert_equal "cur_1", page.next_cursor
    assert_equal %w[a b], page.map(&:id)
    assert_instance_of Receivify::Invoice, page[0]
    assert_equal "cur_1", page["next_cursor"]
  end

  def test_empty_page
    client, = make_client([list_response([])])
    page = client.list_invoices
    assert page.empty?
    refute page.has_more?
    assert_nil page.next_cursor
  end

  def test_cursor_is_passed_through
    client, transport, = make_client([list_response([])])
    client.list_invoices(cursor: "abc")
    assert_equal({ "cursor" => "abc" }, transport.query_of)
  end

  def test_each_invoice_follows_next_cursor_across_pages
    client, transport, = make_client([
                                       list_response([inv("a"), inv("b")], has_more: true, next_cursor: "c1"),
                                       list_response([inv("c"), inv("d")], has_more: true, next_cursor: "c2"),
                                       list_response([inv("e")], has_more: false, next_cursor: nil)
                                     ])
    ids = []
    result = client.each_invoice(limit: 2, status: "pending") { |i| ids << i.id }
    assert_nil result
    assert_equal %w[a b c d e], ids
    assert_equal 3, transport.requests.size
    assert_equal({ "limit" => "2", "status" => "pending" }, transport.query_of(0))
    assert_equal({ "limit" => "2", "status" => "pending", "cursor" => "c1" }, transport.query_of(1))
    assert_equal({ "limit" => "2", "status" => "pending", "cursor" => "c2" }, transport.query_of(2))
  end

  def test_each_invoice_without_block_is_a_lazy_enumerator
    client, transport, = make_client([
                                       list_response([inv("a"), inv("b")], has_more: true, next_cursor: "c1"),
                                       list_response([inv("c")], has_more: false)
                                     ])
    enum = client.each_invoice(limit: 2)
    assert_kind_of Enumerator, enum
    assert_empty transport.requests
    assert_equal %w[a b], enum.first(2).map(&:id)
    assert_equal 1, transport.requests.size
  end

  def test_enumerator_supports_take_and_lazy
    client, = make_client([
                            list_response([inv("a"), inv("b")], has_more: true, next_cursor: "c1"),
                            list_response([inv("c"), inv("d")], has_more: false)
                          ])
    assert_equal %w[a b c], client.each_invoice.lazy.map(&:id).first(3)
  end

  def test_has_more_without_cursor_stops
    client, transport, = make_client([list_response([inv("a")], has_more: true, next_cursor: nil)])
    assert_equal ["a"], client.each_invoice.map(&:id)
    assert_equal 1, transport.requests.size
  end

  def test_repeated_cursor_raises_instead_of_looping
    client, = make_client([
                            list_response([inv("a")], has_more: true, next_cursor: "same"),
                            list_response([inv("b")], has_more: true, next_cursor: "same")
                          ])
    assert_raises(Receivify::Error) { client.each_invoice.to_a }
  end

  def test_has_more_is_never_inferred_from_page_size
    client, transport, = make_client([list_response([inv("a"), inv("b")], has_more: false)])
    assert_equal 2, client.each_invoice(limit: 2).to_a.size
    assert_equal 1, transport.requests.size
  end

  def test_other_auto_paginators
    client, transport, = make_client([
                                       list_response([TestSupport::FORWARD], has_more: true, next_cursor: "f1"),
                                       list_response([TestSupport::FORWARD], has_more: false)
                                     ])
    assert_equal 2, client.each_address(ticker: "btc").count
    assert_equal({ "ticker" => "btc", "cursor" => "f1" }, transport.query_of(1))
  end

  def test_tickers_page
    client, = make_client([list_response([{ "ticker" => "usdc_base", "provider" => "evm", "chain" => "base",
                                            "name" => "USDC on Base", "decimals" => 6 }])])
    page = client.list_tickers
    assert_equal "usdc_base", page.first.ticker
    assert_equal 6, page.first.decimals
  end

  def test_malformed_envelope_raises
    client, = make_client([json_response([1, 2, 3])])
    assert_raises(Receivify::Error) { client.list_invoices }
  end
end
