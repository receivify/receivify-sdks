# Receivify + Sinatra: a complete, tiny shop.
#
#   GET  /                      product page with a "Pay with crypto" form
#   POST /checkout              creates the order + a Receivify invoice, redirects to checkout
#   GET  /orders/:id            order status page
#   POST /webhooks/receivify    signed events from Receivify; the ONLY place an order is marked paid
#
# Run: cp .env.example .env (fill it in) -> bundle install -> bundle exec ruby app.rb

require "sinatra"
require "dotenv/load"
require "securerandom"
require "erb"
require "receivify"

API_KEY = ENV.fetch("RECEIVIFY_API_KEY", "")
WEBHOOK_SECRET = ENV.fetch("RECEIVIFY_WEBHOOK_SECRET", "")
abort("Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET in .env (see .env.example).") if API_KEY.empty? || WEBHOOK_SECRET.empty?

# One client for the app. webhook_secrets is a LIST: during a rotation, the new secret first
# and the old one second ("whsec_new,whsec_old") so no delivery is rejected mid-switch.
RECEIVIFY = Receivify::Client.new(
  api_url: ENV.fetch("RECEIVIFY_API_URL", "https://receivify.io"),
  api_key: API_KEY,
  webhook_secrets: WEBHOOK_SECRET.split(",").map(&:strip).reject(&:empty?)
)

# What we sell. Prices are strings: money never goes through a float.
PRODUCT = { name: "Receivify T-shirt", price: "25.00", currency: "GBP" }.freeze

# `chain` picks the network for tokens that live on several. GET /api/v1/assets lists what's live.
COINS = {
  "btc" => { label: "Bitcoin", asset: "btc" },
  "ltc" => { label: "Litecoin", asset: "ltc" },
  "eth" => { label: "Ethereum", asset: "eth", chain: "ethereum" },
  "usdc-base" => { label: "USDC on Base", asset: "usdc", chain: "base" },
  "usdt-tron" => { label: "USDT on Tron", asset: "usdt", chain: "tron" },
  "sol" => { label: "Solana", asset: "sol", chain: "solana" }
}.freeze

# invoice.paid is THE signal: the full amount is confirmed and can't be reversed.
# invoice.swept follows it (funds forwarded to your wallet), so it also means paid.
STATUS_FOR_EVENT = {
  "invoice.confirming" => "confirming",
  "invoice.partial" => "partially_paid",
  "invoice.paid" => "paid",
  "invoice.swept" => "paid",
  "invoice.expired" => "expired",
  "invoice.partial_expired" => "underpaid",
  "invoice.cancelled" => "cancelled",
  "invoice.failed" => "failed"
}.freeze

# A deliberately tiny in-memory order store, behind a mutex (Puma serves on several threads).
# Use your database in a real shop.
ORDERS = {}
LOCK = Mutex.new

set :port, Integer(ENV.fetch("PORT", 4567))

helpers do
  def h(text) = ERB::Util.html_escape(text.to_s)

  def page(title, body)
    <<~HTML
      <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>#{h title}</title>
      <style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}.price{font-size:2rem;font-weight:700}
      label{display:block;margin:1rem 0}button{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}
      .status{font-weight:700}.paid{color:#4d7c0f}.muted{color:#71717a;font-size:.9rem}</style></head><body>#{body}</body></html>
    HTML
  end
end

get "/" do
  options = COINS.map { |key, c| %(<option value="#{key}">#{h c[:label]}</option>) }.join
  page("Shop", <<~HTML)
    <h1>#{PRODUCT[:name]}</h1><p class="price">£#{PRODUCT[:price]}</p>
    <form method="post" action="/checkout">
      <label>Pay with <select name="coin">#{options}</select></label>
      <label><input type="checkbox" name="fee_ontop" value="1"> Customer pays the fee on top</label>
      <button>Pay with crypto</button>
    </form>
  HTML
end

post "/checkout" do
  coin = COINS.fetch(params["coin"], COINS["btc"])
  # Prefixed so it can't collide with another shop on the same Receivify account:
  # `reference` is also a de-duplication key on Receivify's side.
  order_id = "rb-#{SecureRandom.hex(4)}"
  LOCK.synchronize do
    ORDERS[order_id] = { id: order_id, product: PRODUCT[:name], amount: PRODUCT[:price], status: "pending", invoice_id: nil, last_seq: 0 }
  end

  begin
    invoice = RECEIVIFY.create_invoice(
      amount_fiat: PRODUCT[:price], # converted at today's rate and locked on the invoice
      currency: PRODUCT[:currency],
      asset: coin[:asset],
      chain: coin[:chain],
      reference: order_id,          # comes back on every webhook as data["reference"]
      metadata: { product: PRODUCT[:name], source: "sinatra-example" },
      add_fee_ontop: params["fee_ontop"] ? true : nil, # nil = your account default
      idempotency_key: "checkout-#{order_id}"          # a retry returns the same invoice
    )
  rescue Receivify::APIError => e
    # e.code is stable (amount_too_low, asset_unavailable, ...); e.detail is for humans.
    halt 400, page("Could not start payment", "<p>#{h(e.detail || e.code)}</p><p><a href='/'>Back</a></p>")
  end

  LOCK.synchronize { ORDERS[order_id][:invoice_id] = invoice.id }
  # 303: the browser follows with a GET to Receivify's hosted checkout.
  redirect invoice.checkout_url, 303
end

post "/webhooks/receivify" do
  # The body EXACTLY as sent. The signature is an HMAC over these bytes; parsing JSON
  # and re-generating it would change them and fail verification.
  request.body.rewind
  raw_body = request.body.read

  begin
    # Constant-time HMAC check + 5-minute replay window + JSON parse.
    event = RECEIVIFY.parse_webhook(raw_body, request.env["HTTP_X_RECEIVIFY_SIGNATURE"])
  rescue Receivify::SignatureVerificationError, Receivify::WebhookPayloadError => e
    logger.warn("Rejected webhook: #{e.message}")
    halt 400, "bad signature"
  end

  if event.event == "test"
    logger.info("Test webhook received and verified.")
    return "ok"
  end
  # Ignore anything that isn't about an invoice, including event names added later.
  return "ok" unless event.event.start_with?("invoice.")

  data = event.data # frozen Hash with String keys: "id", "reference", "status", ...
  LOCK.synchronize do
    order = ORDERS[data["reference"].to_s]
    # Unknown (made elsewhere), a duplicate, or an older event arriving late: do nothing.
    # Delivery is at-least-once and may be out of order; `sequence` only increases.
    next if order.nil? || event.sequence <= order[:last_seq]

    order[:last_seq] = event.sequence
    new_status = STATUS_FOR_EVENT[event.event]
    if new_status && order[:status] != "paid" # paid is final for fulfilment
      order[:status] = new_status
      order[:invoice_id] = data["id"]
      # Fulfil here when new_status == "paid" (email, download, shipping); keep it fast.
    end
  end
  "ok" # 2xx fast; anything else is retried with backoff
end

get "/orders/:id" do
  order = ORDERS[params["id"]]
  halt 404, "No such order" if order.nil?
  # Display only. Landing on this URL proves nothing; the webhook is the source of truth.
  refresh = order[:status] == "paid" ? "" : %(<meta http-equiv="refresh" content="5"><p class="muted">Refreshes every 5 seconds.</p>)
  page("Order #{order[:id]}", <<~HTML)
    <h1>Order #{h order[:id]}</h1><p>#{h order[:product]}: £#{h order[:amount]}</p>
    <p class="status #{order[:status]}">#{h order[:status]}</p>#{refresh}<p><a href="/">Back to the shop</a></p>
  HTML
end
