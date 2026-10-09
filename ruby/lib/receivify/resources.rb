# frozen_string_literal: true

require_relative "resource"

module Receivify
  # Open enums. Keep an `else` branch when you switch on these: adding a value is an additive
  # change and must not break an older SDK.
  INVOICE_STATUSES = %w[
    pending partial confirming paid swept expired partial_expired cancelled failed
  ].freeze

  FORWARD_STATUSES = %w[active paused disabled expired].freeze

  EVENT_NAMES = %w[
    invoice.confirming invoice.partial invoice.paid invoice.swept invoice.expired
    invoice.partial_expired invoice.cancelled invoice.failed sweep.sent
    forward.detected forward.confirmed forward.deferred forward.sent
    forward.reversed forward.failed test
  ].freeze

  # A one-off payment request with its own derived deposit address.
  # Identical on create, retrieve, list and cancel.
  class Invoice < Resource
    field :id
    field :reference, :opt_str
    field :asset
    # Exact-integer base units, as a STRING (satoshis for BTC, wei for ETH).
    field :amount_expected, :money
    # Exact-integer base units, as a STRING. "0" until something arrives.
    field :amount_received, :money
    # A DECIMAL string such as "25.00" -- a display price, never settle against it.
    field :amount_fiat, :opt_money
    field :fiat_currency, :opt_str
    field :chain, :opt_str
    field :provider, :opt_str
    field :address
    field :status
    field :expires_at, :opt_str
    field :paid_at, :opt_str
    field :created_at, :opt_str
    field :metadata, :hash
    field :checkout_url, :opt_str
    # Decimal places of the asset -- the divisor for amount_expected.
    field :decimals, :opt_int
    # amount_expected already divided by 10**decimals, as a decimal STRING. Display only.
    field :amount_decimal, :opt_str
    # Wallet-scannable URI for a QR code, or nil when the chain has no safe one.
    field :payment_uri, :opt_str
    # Whether the commission was added on top of the requested amount.
    field :add_fee_ontop, :bool
    # Exact-integer base units, as a STRING: what you asked to RECEIVE.
    field :amount_net_requested, :opt_money
    # Exact-integer base units, as a STRING: what the gross-up added. "0" when off.
    field :fee_ontop, :opt_money

    alias add_fee_ontop? add_fee_ontop

    # True once the money is yours: "paid" or "swept".
    def settled?
      %w[paid swept].include?(status)
    end
  end

  # The unauthenticated checkout-page projection -- deliberately narrower.
  class PublicInvoice < Resource
    field :id
    field :status
    field :address
    field :amount_expected, :money
    field :amount_received, :money
    field :expires_at, :opt_str
  end

  # A standing deposit address that forwards everything it receives to #destination.
  class Forward < Resource
    # The canonical identifier. The wire also carries a deprecated `forward_id` alias.
    field :id
    field :ticker
    field :provider
    field :chain, :opt_str
    field :address
    field :destination
    field :callback_url, :opt_str
    field :confirmations_required, :int
    # The one money field here that is NOT base units: a DISPLAY-unit decimal string.
    field :min_forward, :money
    field :status
    field :expires_at, :opt_str
    field :created_at, :opt_str
    field :updated_at, :opt_str

    # Deprecated alias for #id, kept because the wire keeps it.
    def forward_id
      id
    end

    private

    def decode_extra(raw, values)
      values[:id] = Util.str(raw, "forward_id") if values[:id].empty?
    end
  end

  # One deposit credited to a forwarding address.
  class ForwardPayment < Resource
    field :txid_in
    # Gross received, exact-integer base units, as a string.
    field :value, :money
    field :status
    field :txid_out, :opt_str
    field :confirmations, :int
    field :created_at, :opt_str
  end

  # One webhook delivery attempt for a forward. Outcome only -- never the body.
  class ForwardCallback < Resource
    field :event
    field :url
    field :status
    field :attempts, :int
    field :last_status, :opt_int
    field :last_error, :opt_str
    field :created_at, :opt_str
    field :delivered_at, :opt_str
  end

  # GET /api/v1/address/{id}: a Forward plus two capped, unpaginated sub-collections.
  class ForwardDetail < Forward
    field :payments_has_more, :bool
    field :callbacks_has_more, :bool

    def payments
      @values[:payments]
    end

    def callbacks
      @values[:callbacks]
    end

    private

    def decode_extra(raw, values)
      super
      values[:payments] = Array(raw["payments"]).select { |p| p.is_a?(Hash) }.map { |p| ForwardPayment.new(p) }.freeze
      values[:callbacks] = Array(raw["callbacks"]).select { |c| c.is_a?(Hash) }.map { |c| ForwardCallback.new(c) }.freeze
    end
  end

  # One (asset, chain) pair offered for forwarding.
  class Ticker < Resource
    field :ticker
    field :provider
    field :chain, :opt_str
    field :name
    # Base units per whole coin as a power of ten.
    field :decimals, :int
  end

  # An invoiceable coin. Display metadata.
  class Asset < Resource
    field :symbol
    field :name
    field :network
    field :color
    # A decimal string ("0.00008000") or the literal word "network".
    field :min
    field :network_fee
    field :live, :bool
  end

  # A payment link. You SET the amount with amount_btc and READ it as amount_sats (base units).
  class PaymentLink < Resource
    field :id
    field :title, :opt_str
    field :amount_sats, :opt_money
    field :open_amount, :bool
    field :active, :bool_true
    field :uses, :int
    field :created_at, :opt_str
    field :updated_at, :opt_str
    field :url
  end

  # Scheduled billing. Each cycle MINTS a new invoice and fires your webhook.
  class RecurringSchedule < Resource
    field :id
    field :title, :opt_str
    field :asset
    field :amount_fiat, :opt_money
    field :currency, :opt_str
    # A DECIMAL amount in the coin -- not base units.
    field :amount, :opt_money
    field :interval_days, :int
    field :active, :bool
    field :runs, :int
    field :next_run, :opt_str
    field :last_invoice_id, :opt_str
    field :created_at, :opt_str
  end

  # A listed API key. The secret is never stored, so it can never be listed.
  class ApiKey < Resource
    field :id
    field :prefix
    field :label, :opt_str
    field :created_at, :opt_str
    field :last_used_at, :opt_str
    field :revoked, :bool

    private

    def decode_extra(raw, values)
      values[:prefix] = Util.str(raw, "api_key_prefix") if values[:prefix].empty?
    end
  end

  # A freshly issued key. #api_key is shown exactly once; nil on an Idempotency-Key replay.
  class ApiKeyCreated < Resource
    field :id
    field :api_key, :opt_str
    field :prefix
    field :label, :opt_str
    field :created_at, :opt_str
    field :message

    def self.redacted_fields
      [:api_key]
    end

    def replay?
      api_key.nil?
    end

    private

    def decode_extra(raw, values)
      values[:prefix] = Util.str(raw, "api_key_prefix") if values[:prefix].empty?
    end
  end

  # An account-level endpoint. Receives every INVOICE event -- never forward.*.
  class WebhookEndpoint < Resource
    field :id
    field :url
    field :active, :bool_true
    field :created_at, :opt_str
    field :updated_at, :opt_str
  end

  # One row of the delivery log. Exactly one of invoice_id / forward_id is non-nil.
  class WebhookDelivery < Resource
    field :id
    field :invoice_id, :opt_str
    field :forward_id, :opt_str
    field :event
    field :status
    field :attempts, :int
    field :url
    field :last_status, :opt_int
    field :last_error, :opt_str
    field :created_at, :opt_str
    field :delivered_at, :opt_str
  end

  # Rotation metadata. Carries no secret material.
  class WebhookSecretState < Resource
    field :rotated_at, :opt_str
    field :prev_expires_at, :opt_str
    # While true, your receiver must accept EITHER secret.
    field :prev_active, :bool
  end

  # The result of a rotation. #secret is shown ONCE; nil on an Idempotency-Key replay.
  class WebhookSecretRotated < Resource
    field :secret, :opt_str
    field :rotated_at, :opt_str
    field :prev_expires_at, :opt_str
    field :grace_seconds, :int
    field :replaced_previous, :bool
    field :message

    def self.redacted_fields
      [:secret]
    end

    def replay?
      secret.nil?
    end
  end

  # A staged payout-address change, waiting out its timelock.
  class PayoutChange < Resource
    field :id
    field :asset
    field :new_address
    field :old_address, :opt_str
    field :status
    field :requested_at, :opt_str
    field :effective_at, :opt_str
    field :confirmed_at, :opt_str
    field :applied_at, :opt_str
  end

  # The LIVE payout map plus every open change.
  class PayoutState < Resource
    field :payout_addresses, :hash
    field :timelock_hours, :int
    field :email_confirmation, :bool
    field :instant, :bool

    def pending
      @values[:pending]
    end

    private

    def decode_extra(raw, values)
      values[:pending] = Array(raw["pending"]).select { |c| c.is_a?(Hash) }.map { |c| PayoutChange.new(c) }.freeze
    end
  end

  # Merchant preferences. fee_bps is an integer rate, not money.
  class Settings < Resource
    field :display_currency
    field :email
    field :fee_bps, :int
    # The ACCOUNT default for create_invoice(add_fee_ontop:).
    field :add_fee_ontop, :bool
  end
end
