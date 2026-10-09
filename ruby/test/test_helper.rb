# frozen_string_literal: true

# Test doubles. No network, no monkeypatching -- just the injected transport seam.

$LOAD_PATH.unshift(File.expand_path("../lib", __dir__))
require "minitest/autorun"
require "json"
require "uri"
require "receivify"

module TestSupport
  # Records every request and replays a scripted queue of responses. A queued entry may be
  # an Exception, which is raised instead -- that is how the retry tests simulate a
  # connection failure.
  class FakeTransport
    attr_reader :requests, :responses

    def initialize(responses = [])
      @requests = []
      @responses = responses.dup
    end

    def call(request)
      @requests << request
      raise "FakeTransport ran out of canned responses at #{request.method} #{request.url}" if @responses.empty?

      item = @responses.shift
      raise item if item.is_a?(Exception)

      item
    end

    def last_request
      @requests.last
    end

    def body_of(index = -1)
      raw = @requests[index].body
      raw.nil? ? nil : JSON.parse(raw)
    end

    def query_of(index = -1)
      query = URI.parse(@requests[index].url).query
      query.nil? ? {} : URI.decode_www_form(query).to_h
    end
  end

  def json_response(payload = nil, status: 200, headers: {}, body: nil)
    raw = body || (payload.nil? ? "" : JSON.generate(payload))
    Receivify::Response.new(status, { "Content-Type" => "application/json" }.merge(headers), raw)
  end

  def error_response(code, detail, status:, headers: {})
    json_response({ "error" => code, "detail" => detail }, status: status, headers: headers)
  end

  def list_response(items, has_more: false, next_cursor: nil)
    json_response({ "data" => items, "has_more" => has_more, "next_cursor" => next_cursor })
  end

  # Returns [client, transport, slept] wired together, with sleeping recorded, not done.
  def make_client(responses = [], **opts)
    transport = FakeTransport.new(responses)
    slept = []
    client = Receivify::Client.new(
      api_url: opts.delete(:api_url) || "https://receivify.io",
      api_key: opts.key?(:api_key) ? opts.delete(:api_key) : "rcv_live_secretkey",
      transport: transport,
      sleeper: ->(s) { slept << s },
      **opts
    )
    [client, transport, slept]
  end

  INVOICE = {
    "id" => "8d1f3d4e-0000-4000-8000-000000000001",
    "reference" => "order-1001",
    "asset" => "btc",
    # 21 million BTC in satoshis; a Float would round a wei-scale value like this.
    "amount_expected" => "2100000000000000",
    "amount_received" => "0",
    "amount_fiat" => "25.00",
    "fiat_currency" => "USD",
    "chain" => nil,
    "provider" => "bitcoind",
    "address" => "bc1qexampleaddress",
    "status" => "pending",
    "expires_at" => "2026-08-25T12:00:00.000Z",
    "paid_at" => nil,
    "created_at" => "2026-08-25T11:00:00.000Z",
    "metadata" => { "cart" => "abc" },
    "checkout_url" => "https://receivify.io/pay/8d1f3d4e-0000-4000-8000-000000000001",
    "decimals" => 8,
    "amount_decimal" => "21000000.00000000",
    "payment_uri" => "bitcoin:bc1qexampleaddress?amount=21000000.00000000",
    "add_fee_ontop" => false,
    "amount_net_requested" => "2100000000000000",
    "fee_ontop" => "0"
  }.freeze

  FORWARD = {
    "id" => "11111111-2222-4333-8444-555555555555",
    "forward_id" => "11111111-2222-4333-8444-555555555555",
    "ticker" => "btc",
    "provider" => "bitcoind",
    "chain" => nil,
    "address" => "bc1qdepositaddress",
    "destination" => "bc1qmywallet",
    "callback_url" => "https://shop.example/hooks/receivify",
    "confirmations_required" => 2,
    "min_forward" => "0.000100",
    "status" => "active",
    "expires_at" => nil,
    "created_at" => "2026-08-25T11:00:00.000Z",
    "updated_at" => nil
  }.freeze
end
