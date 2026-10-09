# frozen_string_literal: true

# Receivify -- the official Ruby SDK for the Receivify crypto payment gateway.
# https://receivify.io  |  https://receivify.io/docs
#
# Standard library only (net/http, openssl, json, uri). Ruby 2.7+.
#
#   require "receivify"
#
#   client = Receivify::Client.new(
#     api_url: "https://receivify.io",
#     api_key: ENV.fetch("RECEIVIFY_API_KEY"),
#     webhook_secrets: [ENV.fetch("RECEIVIFY_WEBHOOK_SECRET")]
#   )
#
#   # 1. turn an order into a hosted checkout
#   invoice = client.create_invoice(amount_fiat: "25.00", currency: "USD", reference: "order-1001")
#   invoice.checkout_url
#
#   # 2. authenticate the signed callback before trusting a word of it
#   event = Receivify::Webhook.parse(request.raw_post, request.headers["X-Receivify-Signature"], secrets)

require_relative "receivify/version"
require_relative "receivify/errors"
require_relative "receivify/http"
require_relative "receivify/resource"
require_relative "receivify/resources"
require_relative "receivify/page"
require_relative "receivify/webhook"
require_relative "receivify/money"
require_relative "receivify/client"
