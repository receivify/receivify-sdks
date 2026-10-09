# frozen_string_literal: true

require "openssl"
require "json"
require_relative "errors"
require_relative "resource"

module Receivify
  # The `data` of an invoice.*, sweep.sent or test event.
  #
  # A narrow projection: it carries NO asset and NO chain, so a multi-coin merchant should
  # call client.get_invoice(id) to learn which coin the event is about.
  class InvoiceEventData < Resource
    field :id
    field :reference, :opt_str
    field :status
    # Exact-integer base units, as a string.
    field :amount_expected, :money
    # Exact-integer base units, as a string.
    field :amount_received, :money
    field :address
    field :paid_at, :opt_str
  end

  # The `data` of a forward.confirmed or forward.sent event.
  #
  # address_in is the deposit address (Forward#address); address_out is the destination.
  class ForwardEventData < Resource
    field :forward_id
    field :ticker
    field :address_in
    field :address_out
    field :txid_in
    field :txid_out, :opt_str
    # Gross received, exact-integer base units, as a string.
    field :value, :money
    # Net sent to the destination, exact-integer base units, as a string.
    field :value_forwarded, :money
    # Fee retained, exact-integer base units, as a string.
    field :fee, :money
    field :confirmations, :int
    field :pending, :bool
  end

  # The unified webhook envelope: `{event, sequence, created_at, data}`.
  #
  # Switch on #event and keep an `else` branch: receivers MUST ignore event names they do
  # not recognise. Delivery is at-least-once and can arrive out of order; [event, sequence]
  # is the de-duplication key. A webhook is a statement about a moment -- call
  # client.get_invoice(id) for the current state.
  class WebhookEvent < Resource
    field :event
    # Globally monotonic, allocated at enqueue time. Not per-resource.
    field :sequence, :int
    # When the EVENT happened, not when this delivery was sent. Frozen across retries.
    field :created_at, :opt_str

    # The event payload as a frozen Hash with String keys.
    def data
      @values[:data]
    end

    # invoice.*, sweep.sent and test all carry the invoice payload.
    def invoice_event?
      event.start_with?("invoice.") || event == "sweep.sent" || event == "test"
    end

    def forward_event?
      event.start_with?("forward.")
    end

    # The invoice payload, or nil when this is not an invoice-family event.
    def invoice
      invoice_event? ? InvoiceEventData.new(data) : nil
    end

    # The forward payload, or nil when this is not a forward.* event.
    def forward
      forward_event? ? ForwardEventData.new(data) : nil
    end

    # [event, sequence] -- store the highest applied value and ignore replays.
    def dedupe_key
      [event, sequence].freeze
    end

    private

    def decode_extra(raw, values)
      data = raw["data"]
      # Tolerate the pre-envelope invoice shape so an integration keeps working through a rollout.
      data = raw["invoice"] unless data.is_a?(Hash)
      values[:data] = data.is_a?(Hash) ? data : {}.freeze
    end
  end

  # Webhook signature verification.
  #
  #   X-Receivify-Signature: t=<unix seconds>,v1=<hex>
  #
  # where v1 = HMAC-SHA256(secret, "#{t}.#{raw_body}").
  #
  # Three things go wrong in the field, and this module is shaped around all three:
  #
  # 1. Re-serialised JSON. Verify against the RAW request body. Rails: request.raw_post.
  #    Sinatra/Rack: request.body.read. Never params.to_json or JSON.generate(JSON.parse(...)).
  # 2. A single secret. A rotation keeps the previous secret valid for a grace window, so
  #    secrets is a LIST ([current, previous]). A single String is accepted too.
  # 3. No replay window. The t= timestamp is checked against `tolerance` (300 seconds by
  #    default), so a captured delivery replayed later fails.
  module Webhook
    SIGNATURE_HEADER = "X-Receivify-Signature"
    # The same header as Rack exposes it in the env hash.
    RACK_HEADER = "HTTP_X_RECEIVIFY_SIGNATURE"
    DEFAULT_TOLERANCE = 300

    module_function

    # True when `header` authenticates `raw_body` under any of `secrets`. Never raises for a
    # bad signature; returns false.
    #
    # raw_body:  the RAW body String exactly as received (an IO such as request.body is read
    #            and rewound for you).
    # header:    the X-Receivify-Signature value.
    # secrets:   the current secret, or during a rotation [current, previous].
    # tolerance: replay window in seconds; nil disables the check.
    # now:       unix seconds, for tests. Defaults to the wall clock.
    def verify(raw_body, header, secrets, tolerance: DEFAULT_TOLERANCE, now: nil)
      keys = normalize_secrets(secrets)
      return false if keys.empty?

      timestamp, candidates = parse_header(header)
      return false if timestamp.nil? || candidates.empty?

      if tolerance && tolerance.to_i >= 0
        current = (now || Time.now).to_i
        # abs(): a receiver whose clock runs slightly fast must not reject live traffic.
        return false if (current - timestamp).abs > tolerance.to_i
      end

      payload = "#{timestamp}.".b + body_bytes(raw_body)
      matched = false
      keys.each do |key|
        expected = OpenSSL::HMAC.hexdigest("SHA256", key, payload)
        # Constant-time comparison, and no early exit, so the loop is uniform.
        candidates.each { |candidate| matched = secure_compare(expected, candidate) || matched }
      end
      matched
    end

    # Verify, then decode. Returns a Receivify::WebhookEvent.
    #
    # Raises Receivify::SignatureVerificationError when the signature does not verify (wrong
    # secret, tampered body, missing header, timestamp outside the tolerance window), and
    # Receivify::WebhookPayloadError when it verified but the body is not a JSON object.
    def parse(raw_body, header, secrets, tolerance: DEFAULT_TOLERANCE, now: nil)
      body = body_bytes(raw_body)
      unless verify(body, header, secrets, tolerance: tolerance, now: now)
        raise SignatureVerificationError,
              "Receivify webhook signature verification failed. Check that you passed the RAW " \
              "request body (Rails: request.raw_post, Sinatra/Rack: request.body.read) and, during " \
              "a secret rotation, both the current and previous secrets."
      end
      begin
        payload = JSON.parse(body.dup.force_encoding(Encoding::UTF_8))
      rescue JSON::ParserError => e
        raise WebhookPayloadError, "Webhook body is not valid JSON: #{e.message}"
      end
      raise WebhookPayloadError, "Webhook body is not a JSON object." unless payload.is_a?(Hash)

      WebhookEvent.new(payload)
    end

    # Split "t=<unix>,v1=<hex>" into [timestamp, [signatures]]. Returns [nil, []] for
    # anything malformed. Several v1= entries are tolerated for a future multi-signature rollout.
    def parse_header(header)
      return [nil, []] unless header.is_a?(String) && !header.empty?

      timestamp = nil
      signatures = []
      header.split(",").each do |part|
        key, sep, value = part.strip.partition("=")
        next if sep.empty?

        key = key.strip
        value = value.strip
        if key == "t" && timestamp.nil?
          return [nil, []] unless value =~ /\A\d+\z/

          timestamp = value.to_i
        elsif key == "v1" && !value.empty?
          signatures << value.downcase
        end
      end
      [timestamp, signatures]
    end

    # The hex v1 value for a body/timestamp/secret triple.
    def compute_signature(raw_body, timestamp, secret)
      OpenSSL::HMAC.hexdigest("SHA256", secret.to_s.b, "#{timestamp}.".b + body_bytes(raw_body))
    end

    # A complete X-Receivify-Signature value -- handy for testing your own webhook handler.
    def generate_header(raw_body, secret, timestamp: Time.now.to_i)
      "t=#{timestamp.to_i},v1=#{compute_signature(raw_body, timestamp.to_i, secret)}"
    end

    # Accept a single secret or an Array of them; drop nil and empty entries.
    def normalize_secrets(secrets)
      list = secrets.is_a?(Array) ? secrets : [secrets]
      list.each_with_object([]) do |secret, out|
        next if secret.nil?
        raise TypeError, "webhook secrets must be Strings, got #{secret.class}" unless secret.is_a?(String)

        out << secret.b unless secret.empty?
      end
    end

    def body_bytes(raw_body)
      if raw_body.is_a?(String)
        raw_body.b
      elsif raw_body.respond_to?(:read)
        text = raw_body.read.to_s
        raw_body.rewind if raw_body.respond_to?(:rewind)
        text.b
      else
        raise TypeError, "raw_body must be the raw request body String, got #{raw_body.class}"
      end
    end

    def secure_compare(a, b)
      return false unless a.bytesize == b.bytesize

      if OpenSSL.respond_to?(:fixed_length_secure_compare)
        OpenSSL.fixed_length_secure_compare(a, b)
      else
        l = a.unpack("C*")
        r = 0
        i = -1
        b.each_byte { |byte| r |= byte ^ l[i += 1] }
        r.zero?
      end
    end
  end
end
