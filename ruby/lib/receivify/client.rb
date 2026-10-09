# frozen_string_literal: true

require "json"
require "uri"
require_relative "version"
require_relative "errors"
require_relative "http"
require_relative "resources"
require_relative "page"
require_relative "webhook"

module Receivify
  # Sentinel meaning "argument not supplied", distinct from an explicit nil.
  #
  #   client.update_address(id, callback_url: nil)   # CLEARS the callback
  #   client.update_address(id, status: "paused")    # leaves the callback alone
  UNSET = Object.new
  def UNSET.inspect
    "Receivify::UNSET"
  end
  UNSET.freeze

  # A client for one Receivify deployment and one API key.
  #
  #   client = Receivify::Client.new(
  #     api_url: "https://receivify.io",
  #     api_key: ENV.fetch("RECEIVIFY_API_KEY"),
  #     webhook_secrets: [ENV.fetch("RECEIVIFY_WEBHOOK_SECRET")]
  #   )
  #   invoice = client.create_invoice(amount_fiat: "25.00", currency: "USD", reference: "order-1001")
  #   redirect_to invoice.checkout_url
  #
  # Design notes:
  # * The API key is a header, never a URL parameter, and never appears in #inspect.
  # * Retries are conservative. A GET is safe to repeat; a mutating call is repeated ONLY when
  #   you gave it an idempotency_key. Retried statuses: 429, 500, 502, 503, 504 and connection
  #   failures, with exponential backoff that honours Retry-After / X-RateLimit-Reset.
  # * Amounts are Strings. Passing a Float raises TypeError: 0.1 + 0.2 is not 0.3, and a
  #   satoshi is not divisible.
  #
  # A Client keeps no per-request state and is safe to share between threads.
  class Client
    DEFAULT_API_URL = "https://receivify.io"
    DEFAULT_TIMEOUT = 30
    DEFAULT_MAX_RETRIES = 2
    RETRYABLE_STATUSES = [429, 500, 502, 503, 504].freeze
    BASE_BACKOFF = 0.5
    MAX_BACKOFF = 20.0
    # Hard ceiling on a server-suggested wait, so a bad Retry-After cannot park a worker.
    MAX_SERVER_WAIT = 60.0

    attr_reader :api_url, :timeout, :max_retries

    # api_url:         deployment base URL (default https://receivify.io).
    # api_key:         your rcv_live_... key. Optional for the public catalogue endpoints.
    # webhook_secrets: default secrets for #verify_webhook / #parse_webhook. During a
    #                  rotation pass BOTH [current, previous]. A single String is accepted.
    # timeout:         per-request timeout in seconds (default 30).
    # max_retries:     extra attempts for retry-safe requests (default 2).
    # transport:       anything responding to call(Receivify::Request) -> Receivify::Response.
    #                  The seam the test suite uses instead of a network.
    # sleeper:         callable taking seconds; injected by tests to skip real sleeping.
    # user_agent:      overrides the default "receivify-ruby/<version>".
    def initialize(api_url: DEFAULT_API_URL, api_key: nil, webhook_secrets: nil, webhook_secret: nil,
                   timeout: DEFAULT_TIMEOUT, max_retries: DEFAULT_MAX_RETRIES, transport: nil,
                   sleeper: nil, user_agent: nil)
      raise ArgumentError, "api_url is required, e.g. 'https://receivify.io'" if api_url.nil? || api_url.to_s.strip.empty?

      @api_url = api_url.to_s.strip.sub(%r{/+\z}, "").freeze
      @api_key = api_key.to_s.dup.freeze
      @timeout = timeout
      @max_retries = [max_retries.to_i, 0].max
      @transport = transport || NetHTTPTransport.new
      @sleeper = sleeper || ->(seconds) { sleep(seconds) }
      @user_agent = (user_agent || "receivify-ruby/#{VERSION}").freeze
      secrets = Webhook.normalize_secrets(webhook_secrets) + Webhook.normalize_secrets(webhook_secret)
      @webhook_secrets = secrets.freeze
    end

    # Never prints the API key: inspect output ends up in logs and bug reports.
    def inspect
      "#<Receivify::Client api_url=#{@api_url.inspect} api_key=#{masked_key.inspect}>"
    end
    alias to_s inspect

    def pretty_print(q)
      q.text(inspect)
    end

    # ------------------------------------------------------------------ invoices

    # Create an invoice -- POST /api/v1/invoices.
    #
    # Exactly one amount is required. The API tries them in this order and the first present
    # one wins: amount_fiat (+ currency), amount, amount_sats, amount_btc.
    #
    # A `reference` you have used before (with the same terms) and an idempotency_key replay
    # both return the EXISTING invoice with a 201, not a second one.
    #
    # add_fee_ontop: true grosses the amount up so the payer covers the commission and you
    # receive exactly what you asked for; false forces fee-out-of-amount for this invoice.
    # Leave it out (nil) to use the account default -- omitting it is NOT the same as false.
    # Anything other than true, false or nil raises TypeError before any request is made.
    def create_invoice(amount_fiat: nil, currency: nil, amount: nil, amount_sats: nil, amount_btc: nil,
                       asset: nil, chain: nil, reference: nil, callback_url: nil, metadata: nil,
                       add_fee_ontop: nil, idempotency_key: nil)
      body = {}
      body["amount_fiat"] = coerce_amount(amount_fiat, "amount_fiat") unless amount_fiat.nil?
      body["currency"] = currency.to_s.upcase unless currency.nil?
      body["amount"] = coerce_amount(amount, "amount") unless amount.nil?
      body["amount_sats"] = coerce_amount(amount_sats, "amount_sats") unless amount_sats.nil?
      body["amount_btc"] = coerce_amount(amount_btc, "amount_btc") unless amount_btc.nil?
      unless %w[amount_fiat amount amount_sats amount_btc].any? { |k| body.key?(k) }
        raise ArgumentError, "One of amount_fiat, amount, amount_sats or amount_btc is required."
      end

      body["asset"] = asset.to_s unless asset.nil?
      body["chain"] = chain.to_s unless chain.nil?
      body["reference"] = reference.to_s unless reference.nil?
      body["callback_url"] = callback_url.to_s unless callback_url.nil?
      body["metadata"] = hash_arg(metadata, "metadata") unless metadata.nil?
      unless add_fee_ontop.nil?
        unless add_fee_ontop == true || add_fee_ontop == false
          # The API rejects anything but a real JSON boolean with 400 bad_request.
          raise TypeError, "add_fee_ontop must be true, false or nil (account default), got #{add_fee_ontop.inspect}"
        end

        body["add_fee_ontop"] = add_fee_ontop
      end
      Invoice.new(expect_object(request_json("POST", "/api/v1/invoices", body: body, idempotency_key: idempotency_key)))
    end

    # Retrieve an invoice -- the authoritative current state.
    def get_invoice(id)
      Invoice.new(expect_object(request_json("GET", "/api/v1/invoices/#{Util.segment(id)}")))
    end

    # One page of invoices, newest first. Returns a Receivify::Page of Receivify::Invoice.
    def list_invoices(limit: nil, cursor: nil, status: nil)
      page("/api/v1/invoices", { limit: limit, cursor: cursor, status: status }, Invoice)
    end

    # Every invoice across every page, following next_cursor lazily. Yields each invoice, or
    # returns an Enumerator when called without a block.
    def each_invoice(limit: nil, status: nil, &block)
      paginate(block) { |cursor| list_invoices(limit: limit, cursor: cursor, status: status) }
    end

    # Cancel a pending, unpaid invoice. NOT idempotent: cancelling an already-cancelled
    # invoice raises Receivify::NotAllowedError (409).
    def cancel_invoice(id)
      Invoice.new(expect_object(request_json("POST", "/api/v1/invoices/#{Util.segment(id)}/cancel")))
    end

    # Cancel up to 500 invoices. Returns {"ok", "action", "requested", "cancelled"}.
    # Naturally idempotent: only pending, unpaid invoices of yours are touched.
    def bulk_cancel_invoices(ids)
      ids = Array(ids).map(&:to_s)
      raise ArgumentError, "ids must not be empty." if ids.empty?
      raise ArgumentError, "At most 500 ids per bulk call, got #{ids.size}." if ids.size > 500

      expect_object(request_json("POST", "/api/v1/invoices/bulk", body: { "ids" => ids, "action" => "cancel" }))
    end

    # ------------------------------------------------------- forwarding addresses

    # Create -- or return -- a standing forwarding address (POST /api/v1/address).
    #
    # Deterministic: identity is (merchant, asset, destination, callback), so repeating the
    # call returns the same address instead of deriving a new one. The single-session tickers
    # (ltc, doge, bch, xmr, zec) always mint a fresh address.
    def create_address(ticker:, destination:, callback: nil, confirmations: nil, metadata: nil, idempotency_key: nil)
      body = { "ticker" => ticker.to_s, "destination" => destination.to_s }
      body["callback"] = callback.to_s unless callback.nil?
      body["confirmations"] = Integer(confirmations) unless confirmations.nil?
      body["metadata"] = hash_arg(metadata, "metadata") unless metadata.nil?
      Forward.new(expect_object(request_json("POST", "/api/v1/address", body: body, idempotency_key: idempotency_key)))
    end

    # Retrieve a forwarding address with its payment (max 200) and callback (max 50) log.
    def get_address(id)
      ForwardDetail.new(expect_object(request_json("GET", "/api/v1/address/#{Util.segment(id)}")))
    end

    # One page of forwarding addresses, newest first.
    def list_addresses(limit: nil, cursor: nil, status: nil, ticker: nil)
      page("/api/v1/address", { limit: limit, cursor: cursor, status: status, ticker: ticker }, Forward)
    end

    # Every forwarding address across every page.
    def each_address(limit: nil, status: nil, ticker: nil, &block)
      paginate(block) { |cursor| list_addresses(limit: limit, cursor: cursor, status: status, ticker: ticker) }
    end

    # The kill switch -- PATCH /api/v1/address/{id}. Only status ("active" <-> "paused") and
    # callback_url are mutable. Pass callback_url: nil to clear it; omit it to leave it alone.
    def update_address(id, status: UNSET, callback_url: UNSET)
      body = {}
      body["status"] = status unless UNSET.equal?(status)
      body["callback_url"] = callback_url unless UNSET.equal?(callback_url)
      raise ArgumentError, "Supply status: and/or callback_url:; a patch with neither is a 400." if body.empty?

      Forward.new(expect_object(request_json("PATCH", "/api/v1/address/#{Util.segment(id)}", body: body)))
    end

    # Soft-delete a forwarding address (status "disabled"). Idempotent. Already credited
    # funds still forward; no new deposit is credited.
    def disable_address(id)
      Forward.new(expect_object(request_json("DELETE", "/api/v1/address/#{Util.segment(id)}")))
    end

    alias create_forward create_address
    alias get_forward get_address
    alias list_forwards list_addresses
    alias each_forward each_address
    alias update_forward update_address
    alias disable_forward disable_address

    # ------------------------------------------------------------ public catalogue

    # Every (asset, chain) pair offered for forwarding. No API key needed or sent.
    def list_tickers
      Page.new(request_json("GET", "/api/v1/tickers", auth: false)) { |h| Ticker.new(h) }
    end

    # The coins enabled for invoicing. No API key needed or sent.
    def list_assets
      Page.new(request_json("GET", "/api/v1/assets", auth: false)) { |h| Asset.new(h) }
    end

    # -------------------------------------------------------------- payment links

    # Create a payment link. Omit amount_btc for an open-amount link.
    def create_payment_link(title: nil, amount_btc: nil, idempotency_key: nil)
      body = {}
      body["title"] = title.to_s unless title.nil?
      body["amount_btc"] = coerce_amount(amount_btc, "amount_btc") unless amount_btc.nil?
      PaymentLink.new(expect_object(request_json("POST", "/api/v1/payment-links", body: body, idempotency_key: idempotency_key)))
    end

    def get_payment_link(id)
      PaymentLink.new(expect_object(request_json("GET", "/api/v1/payment-links/#{Util.segment(id)}")))
    end

    def list_payment_links(limit: nil, cursor: nil, active: nil)
      page("/api/v1/payment-links", { limit: limit, cursor: cursor, active: active }, PaymentLink)
    end

    def each_payment_link(limit: nil, active: nil, &block)
      paginate(block) { |cursor| list_payment_links(limit: limit, cursor: cursor, active: active) }
    end

    # Update a link. amount_btc: nil converts a fixed link to open-amount.
    def update_payment_link(id, active: UNSET, title: UNSET, amount_btc: UNSET)
      body = {}
      body["active"] = active ? true : false unless UNSET.equal?(active)
      body["title"] = title unless UNSET.equal?(title)
      unless UNSET.equal?(amount_btc)
        body["amount_btc"] = amount_btc.nil? ? nil : coerce_amount(amount_btc, "amount_btc")
      end
      raise ArgumentError, "Supply at least one of active:, title: or amount_btc:." if body.empty?

      PaymentLink.new(expect_object(request_json("PATCH", "/api/v1/payment-links/#{Util.segment(id)}", body: body)))
    end

    # Soft-deactivate a link. Idempotent.
    def deactivate_payment_link(id)
      PaymentLink.new(expect_object(request_json("DELETE", "/api/v1/payment-links/#{Util.segment(id)}")))
    end

    # ------------------------------------------------------------------ recurring

    # Create a billing schedule. Provide EITHER amount_fiat (+ currency) OR amount.
    def create_recurring(interval_days:, title: nil, asset: nil, amount_fiat: nil, currency: nil, amount: nil,
                         callback_url: nil, idempotency_key: nil)
      body = { "interval_days" => Integer(interval_days) }
      body["title"] = title.to_s unless title.nil?
      body["asset"] = asset.to_s unless asset.nil?
      body["amount_fiat"] = coerce_amount(amount_fiat, "amount_fiat") unless amount_fiat.nil?
      body["currency"] = currency.to_s.upcase unless currency.nil?
      body["amount"] = coerce_amount(amount, "amount") unless amount.nil?
      raise ArgumentError, "One of amount_fiat or amount is required." if amount_fiat.nil? && amount.nil?

      body["callback_url"] = callback_url.to_s unless callback_url.nil?
      RecurringSchedule.new(expect_object(request_json("POST", "/api/v1/recurring", body: body, idempotency_key: idempotency_key)))
    end

    def list_recurring(limit: nil, cursor: nil)
      page("/api/v1/recurring", { limit: limit, cursor: cursor }, RecurringSchedule)
    end

    def each_recurring(limit: nil, &block)
      paginate(block) { |cursor| list_recurring(limit: limit, cursor: cursor) }
    end

    # Cancel a schedule. Returns an ack Hash {"ok", "cancelled"}.
    def cancel_recurring(id)
      expect_object(request_json("POST", "/api/v1/recurring/#{Util.segment(id)}/cancel"))
    end

    # ------------------------------------------------------------------- API keys

    def list_api_keys(limit: nil, cursor: nil)
      page("/api/v1/keys", { limit: limit, cursor: cursor }, ApiKey)
    end

    def each_api_key(limit: nil, &block)
      paginate(block) { |cursor| list_api_keys(limit: limit, cursor: cursor) }
    end

    # Issue a key. #api_key is returned ONCE -- store it now. nil on an idempotency replay.
    def create_api_key(label: nil, idempotency_key: nil)
      body = {}
      body["label"] = label.to_s unless label.nil?
      ApiKeyCreated.new(expect_object(request_json("POST", "/api/v1/keys", body: body, idempotency_key: idempotency_key)))
    end

    # Revoke a key by its id (NOT its prefix). Returns {"ok", "revoked"}.
    def revoke_api_key(id)
      expect_object(request_json("POST", "/api/v1/keys/#{Util.segment(id)}/revoke"))
    end

    # ----------------------------------------------- webhook endpoints & deliveries

    def list_webhook_endpoints(limit: nil, cursor: nil)
      page("/api/v1/webhooks/endpoints", { limit: limit, cursor: cursor }, WebhookEndpoint)
    end

    def each_webhook_endpoint(limit: nil, &block)
      paginate(block) { |cursor| list_webhook_endpoints(limit: limit, cursor: cursor) }
    end

    # Register an account-level endpoint (max 20). It receives every INVOICE event.
    def create_webhook_endpoint(url, idempotency_key: nil)
      body = { "url" => url.to_s }
      WebhookEndpoint.new(expect_object(request_json("POST", "/api/v1/webhooks/endpoints", body: body, idempotency_key: idempotency_key)))
    end

    # Update an endpoint. active: false is the pause switch.
    def update_webhook_endpoint(id, active: UNSET, url: UNSET)
      body = {}
      body["active"] = active ? true : false unless UNSET.equal?(active)
      body["url"] = url unless UNSET.equal?(url)
      raise ArgumentError, "Supply active: and/or url:." if body.empty?

      WebhookEndpoint.new(expect_object(request_json("PATCH", "/api/v1/webhooks/endpoints/#{Util.segment(id)}", body: body)))
    end

    # HARD delete: a second call is a 404. Prefer update_webhook_endpoint(id, active: false).
    def delete_webhook_endpoint(id)
      expect_object(request_json("DELETE", "/api/v1/webhooks/endpoints/#{Util.segment(id)}"))
    end

    def list_webhook_deliveries(limit: nil, cursor: nil, event: nil, status: nil)
      page("/api/v1/webhooks/deliveries", { limit: limit, cursor: cursor, event: event, status: status }, WebhookDelivery)
    end

    def each_webhook_delivery(limit: nil, event: nil, status: nil, &block)
      paginate(block) { |cursor| list_webhook_deliveries(limit: limit, cursor: cursor, event: event, status: status) }
    end

    # Re-queue a delivery. The body is FROZEN: same bytes, same sequence and created_at.
    def replay_webhook_delivery(id)
      expect_object(request_json("POST", "/api/v1/webhooks/deliveries/#{Util.segment(id)}/replay"))
    end

    # Rotation metadata (no secret material): is the previous secret still valid, and until when?
    def get_webhook_secret_state
      WebhookSecretState.new(expect_object(request_json("GET", "/api/v1/webhooks/secret")))
    end

    # Mint a new webhook signing secret. Returned ONCE (nil on an idempotency replay).
    # The previous secret stays valid until #prev_expires_at, so deploy
    # webhook_secrets: [new_secret, old_secret] until then.
    def rotate_webhook_secret(idempotency_key: nil)
      WebhookSecretRotated.new(expect_object(request_json("POST", "/api/v1/webhooks/secret/rotate", idempotency_key: idempotency_key)))
    end

    # Deliver a signed "test" event to your registered, active endpoints.
    def send_test_webhook(url: nil)
      body = {}
      body["url"] = url.to_s unless url.nil?
      expect_object(request_json("POST", "/api/v1/webhooks/test", body: body))
    end

    # ----------------------------------------------------- payouts, settings, summary

    # The LIVE payout map plus every open (staged) change.
    def get_payouts
      PayoutState.new(expect_object(request_json("GET", "/api/v1/payouts")))
    end

    # Stage payout-address changes ({"btc" => "bc1q..."}). Nothing is applied inline: a change
    # may need an emailed confirmation and then waits out a timelock.
    def stage_payout_changes(addresses)
      expect_object(request_json("PATCH", "/api/v1/payouts", body: { "addresses" => hash_arg(addresses, "addresses") }))
    end

    def cancel_payout_change(id)
      expect_object(request_json("POST", "/api/v1/payouts/changes/#{Util.segment(id)}/cancel"))
    end

    def get_settings
      Settings.new(expect_object(request_json("GET", "/api/v1/settings")))
    end

    # Update settings. Returns an ack carrying only what changed -- not a Settings object.
    # add_fee_ontop: sets the ACCOUNT default for invoices that do not state it.
    def update_settings(display_currency: UNSET, add_fee_ontop: UNSET)
      body = {}
      body["display_currency"] = display_currency unless UNSET.equal?(display_currency)
      unless UNSET.equal?(add_fee_ontop)
        raise TypeError, "add_fee_ontop must be true or false, got #{add_fee_ontop.inspect}" unless [true, false].include?(add_fee_ontop)

        body["add_fee_ontop"] = add_fee_ontop
      end
      raise ArgumentError, "Supply at least one updatable setting." if body.empty?

      expect_object(request_json("PATCH", "/api/v1/settings", body: body))
    end

    # The dashboard composite, as a Hash.
    def get_summary
      expect_object(request_json("GET", "/api/v1/summary"))
    end

    # -------------------------------------------------------- public / unauthenticated

    # The checkout-page projection. No API key sent; safe to poll from a thank-you page.
    def get_public_invoice(id)
      PublicInvoice.new(expect_object(request_json("GET", "/api/public/invoices/#{Util.segment(id)}", auth: false)))
    end

    def get_public_payment_link(id)
      expect_object(request_json("GET", "/api/public/links/#{Util.segment(id)}", auth: false))
    end

    # The operator health report. Does NOT raise on 503: a degraded check answers with the
    # same {status, time, checks, alerts} body. Read "status" yourself.
    def get_health
      response = request("GET", "/api/health", auth: false, retry_request: false, raise_on_error: false)
      unless [200, 503].include?(response.status)
        raise Receivify.error_from_response(response.status, response.headers, response.body)
      end

      expect_object(safe_json(response))
    end

    # ------------------------------------------------------------------- webhooks

    # Verify a delivery against the client's webhook_secrets (or the ones you pass).
    # Pass the RAW body: request.raw_post (Rails) or request.body.read (Sinatra/Rack).
    def verify_webhook(raw_body, header, secrets = nil, tolerance: Webhook::DEFAULT_TOLERANCE, now: nil)
      keys = secrets.nil? ? @webhook_secrets : secrets
      Webhook.verify(raw_body, header, keys, tolerance: tolerance, now: now)
    end

    # Verify and decode in one step. Raises Receivify::SignatureVerificationError.
    def parse_webhook(raw_body, header, secrets = nil, tolerance: Webhook::DEFAULT_TOLERANCE, now: nil)
      keys = secrets.nil? ? @webhook_secrets : secrets
      Webhook.parse(raw_body, header, keys, tolerance: tolerance, now: now)
    end

    # ------------------------------------------------------------------ low level

    # Escape hatch: issue any request against this deployment and get the raw
    # Receivify::Response back (headers included: Idempotent-Replay, X-RateLimit-*).
    def request(method, path, params: nil, body: nil, idempotency_key: nil, auth: true,
                retry_request: nil, raise_on_error: true, headers: nil)
      method = method.to_s.upcase
      url = @api_url + path
      query = encode_params(params)
      url = "#{url}?#{query}" unless query.empty?

      hdrs = { "Accept" => "application/json", "User-Agent" => @user_agent }
      # Header, never a query parameter: URLs are logged, headers are not.
      hdrs["Authorization"] = "Bearer #{@api_key}" if auth && !@api_key.empty?
      hdrs["Idempotency-Key"] = idempotency_key.to_s if idempotency_key && !idempotency_key.to_s.empty?
      hdrs.merge!(headers) if headers
      encoded = nil
      unless body.nil?
        encoded = JSON.generate(body)
        hdrs["Content-Type"] = "application/json"
      end

      req = Request.new(method: method, url: url, headers: hdrs, body: encoded, timeout: @timeout)
      retryable = retry_request.nil? ? retry_safe?(method, idempotency_key) : retry_request
      response = execute(req, retryable)
      if raise_on_error && response.status >= 400
        raise Receivify.error_from_response(response.status, response.headers, response.body)
      end

      response
    end

    private

    def masked_key
      return "" if @api_key.empty?

      @api_key.length > 12 ? "#{@api_key[0, 12]}..." : "..."
    end

    def request_json(method, path, params: nil, body: nil, idempotency_key: nil, auth: true)
      response = request(method, path, params: params, body: body, idempotency_key: idempotency_key, auth: auth)
      return nil if response.body.empty?

      begin
        response.json
      rescue JSON::ParserError
        raise Receivify::Error, "Receivify returned a non-JSON body for #{method} #{path} (status #{response.status})."
      end
    end

    def page(path, params, klass)
      Page.new(request_json("GET", path, params: params)) { |h| klass.new(h) }
    end

    def paginate(block, &fetch)
      enum = Pagination.enumerate(&fetch)
      return enum unless block

      enum.each(&block)
      nil
    end

    # A GET changes nothing. A mutating call is safe to repeat ONLY with an Idempotency-Key:
    # without one, a retry after a timeout can create a second invoice or a second API key.
    def retry_safe?(method, idempotency_key)
      return true if %w[GET HEAD].include?(method)

      !idempotency_key.nil? && !idempotency_key.to_s.empty?
    end

    def execute(req, retryable)
      attempt = 0
      loop do
        begin
          response = @transport.call(req)
        rescue TransportError
          raise unless retryable && attempt < @max_retries

          @sleeper.call(backoff(attempt, nil))
          attempt += 1
          next
        end
        return response if !RETRYABLE_STATUSES.include?(response.status) || !retryable || attempt >= @max_retries

        @sleeper.call(backoff(attempt, response))
        attempt += 1
      end
    end

    # Exponential backoff with jitter, overridden upward by what the server asks for.
    def backoff(attempt, response)
      delay = [MAX_BACKOFF, BASE_BACKOFF * (2**attempt)].min
      server = server_requested_wait(response)
      delay = [MAX_SERVER_WAIT, [delay, server].max].min unless server.nil?
      delay + (rand * delay * 0.25)
    end

    # Honour Retry-After (seconds), else X-RateLimit-Reset (unix seconds).
    def server_requested_wait(response)
      return nil if response.nil?

      retry_after = response.header("retry-after")
      return [retry_after.strip.to_f, 0.0].max if retry_after && retry_after.strip =~ /\A\d+(\.\d+)?\z/

      reset = response.header("x-ratelimit-reset")
      return [reset.strip.to_f - Time.now.to_f, 0.0].max if reset && reset.strip =~ /\A\d+(\.\d+)?\z/

      nil
    end

    def encode_params(params)
      return "" if params.nil? || params.empty?

      pairs = params.each_with_object([]) do |(k, v), out|
        next if v.nil?

        out << [k.to_s, v == true ? "true" : v == false ? "false" : v.to_s]
      end
      URI.encode_www_form(pairs)
    end

    # Normalise a money input to a String, refusing Floats.
    def coerce_amount(value, name)
      case value
      when Float
        raise TypeError, "#{name} must not be a Float -- pass a String like \"25.00\" (or an Integer/BigDecimal). " \
                         "Floats cannot represent base-unit amounts exactly."
      when Integer
        value.to_s
      when String
        text = value.strip
        raise ArgumentError, "#{name} must not be empty." if text.empty?

        text
      else
        if defined?(::BigDecimal) && value.is_a?(::BigDecimal)
          raise ArgumentError, "#{name} must be finite." unless value.finite?

          value.frac.zero? ? value.to_i.to_s : value.to_s("F")
        else
          raise TypeError, "#{name} must be a String, Integer or BigDecimal, got #{value.class}."
        end
      end
    end

    def hash_arg(value, name)
      raise TypeError, "#{name} must be a Hash, got #{value.class}." unless value.is_a?(Hash)

      value.each_with_object({}) { |(k, v), h| h[k.to_s] = v }
    end

    def expect_object(payload)
      return payload if payload.is_a?(Hash)

      raise Receivify::Error, "Expected a JSON object in the response, got #{payload.class}."
    end

    def safe_json(response)
      response.json
    rescue JSON::ParserError
      nil
    end
  end
end
