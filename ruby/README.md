# Receivify Ruby SDK

The official Ruby client for [Receivify](https://receivify.io), the crypto payments API. Use it to create invoices and forwarding addresses, walk through paginated lists, and verify signed webhooks.

- Uses only the standard library (`net/http`, `openssl`, `json`, `uri`). It has no runtime gem dependencies.
- Supports Ruby 2.7 and newer, and is tested on 2.7 and 3.3.
- Every amount stays an exact **string**. The SDK never turns money into a float.
- API reference: **https://receivify.io/docs**

---

## Install

```bash
gem install receivify
```

Or add it to your `Gemfile`:

```ruby
gem "receivify", "~> 1.0"
```

To install from source:

```bash
git clone https://github.com/receivify/receivify-sdks.git
cd receivify-sdks/ruby
gem build receivify.gemspec
gem install ./receivify-1.0.0.gem
```

In a `Gemfile` you can point at the source instead:

```ruby
gem "receivify", git: "https://github.com/receivify/receivify-sdks.git", glob: "ruby/*.gemspec"
```

---

## Quickstart

You get your API key and webhook secret when you sign up at https://receivify.io. Keep both in environment variables.

```ruby
require "receivify"

client = Receivify::Client.new(
  api_url: "https://receivify.io",
  api_key: ENV.fetch("RECEIVIFY_API_KEY"),
  webhook_secrets: [ENV.fetch("RECEIVIFY_WEBHOOK_SECRET")],
  timeout: 30
)

invoice = client.create_invoice(
  amount_fiat: "25.00",
  currency: "USD",
  asset: "btc",
  reference: "order-1001",          # your order id; it also de-duplicates
  idempotency_key: "order-1001"     # makes retries safe
)

invoice.id               # => "396c7274-2540-4445-ae98-45e5af1ca68d"
invoice.status           # => "pending"
invoice.amount_expected  # => "30272"  (base units: satoshis, as a String)
invoice.amount_decimal   # => "0.00030272"
invoice.checkout_url     # => "https://receivify.io/pay/396c7274-..."
invoice["checkout_url"]  # the same value, read from the raw Hash
```

After that, send the customer to `invoice.checkout_url`. In Rails that is `redirect_to invoice.checkout_url, allow_other_host: true`.

### Rails: create the invoice and redirect

```ruby
# config/initializers/receivify.rb
RECEIVIFY = Receivify::Client.new(
  api_url: "https://receivify.io",
  api_key: ENV.fetch("RECEIVIFY_API_KEY"),
  webhook_secrets: [ENV.fetch("RECEIVIFY_WEBHOOK_SECRET"), ENV["RECEIVIFY_WEBHOOK_SECRET_PREVIOUS"]].compact
)
```

```ruby
# app/controllers/checkouts_controller.rb
class CheckoutsController < ApplicationController
  def create
    order = current_user.orders.find(params[:order_id])

    invoice = RECEIVIFY.create_invoice(
      amount_fiat: order.total,             # a BigDecimal or a String like "25.00" -- never a Float
      currency: "USD",
      asset: params.fetch(:asset, "btc"),
      reference: "shop-#{order.id}",
      metadata: { order_id: order.id },
      idempotency_key: "shop-#{order.id}"
    )
    order.update!(receivify_invoice_id: invoice.id)

    redirect_to invoice.checkout_url, allow_other_host: true
  rescue Receivify::APIError => e
    Rails.logger.warn("Receivify #{e.code} (#{e.status}): #{e.detail}")
    redirect_to order_path(order), alert: "Crypto checkout is unavailable right now."
  end
end
```

### Rails: webhook handler

Verify the signature against the **raw** request body. Do not use `params` and do not re-encode the JSON, because a changed key order or whitespace breaks the HMAC.

```ruby
# config/routes.rb
post "/webhooks/receivify", to: "receivify_webhooks#create"
```

```ruby
# app/controllers/receivify_webhooks_controller.rb
class ReceivifyWebhooksController < ActionController::Base
  skip_forgery_protection

  def create
    event = RECEIVIFY.parse_webhook(
      request.raw_post,                                  # the RAW body
      request.headers["X-Receivify-Signature"]
    )

    case event.event
    when "invoice.paid"
      # A webhook describes one moment in time. Re-read the invoice before you ship anything.
      invoice = RECEIVIFY.get_invoice(event.data["id"])
      Order.find_by(receivify_invoice_id: invoice.id)&.mark_paid! if invoice.settled?
    when "invoice.expired", "invoice.cancelled"
      Order.find_by(receivify_invoice_id: event.data["id"])&.release_stock!
    else
      # Ignore event names you don't recognise. New ones may be added later.
    end

    head :ok
  rescue Receivify::SignatureVerificationError
    head :bad_request
  end
end
```

### Sinatra / plain Rack: webhook handler

```ruby
require "sinatra"
require "receivify"

SECRETS = [ENV.fetch("RECEIVIFY_WEBHOOK_SECRET"), ENV["RECEIVIFY_WEBHOOK_SECRET_PREVIOUS"]].compact

post "/webhooks/receivify" do
  raw = request.body.read                                   # the RAW body
  header = request.env["HTTP_X_RECEIVIFY_SIGNATURE"]        # Receivify::Webhook::RACK_HEADER

  begin
    event = Receivify::Webhook.parse(raw, header, SECRETS)  # tolerance: 300 seconds by default
  rescue Receivify::SignatureVerificationError
    halt 400
  end

  if event.event == "invoice.paid"
    puts "paid: #{event.data['id']} (#{event.invoice.amount_received} base units)"
  end
  status 200
end
```

If you only need a yes or no answer, use `Receivify::Webhook.verify(raw, header, secrets)`. It returns `true` or `false` and never raises on a bad signature.

Deliveries are at-least-once and can arrive out of order. Use `event.dedupe_key` (`[event, sequence]`) to ignore repeats.

| Event | `event.data` |
|---|---|
| `invoice.confirming`, `invoice.partial`, `invoice.paid`, `invoice.swept`, `invoice.expired`, `invoice.partial_expired`, `invoice.cancelled`, `invoice.failed`, `sweep.sent`, `test` | invoice payload (`event.invoice` returns a typed view) |
| `forward.detected`, `forward.confirmed`, `forward.deferred`, `forward.sent`, `forward.reversed`, `forward.failed` | forward payload (`event.forward` returns a typed view) |

---

## Coin amounts, chains and `add_fee_ontop`

```ruby
client.create_invoice(amount: "0.001", asset: "btc")                  # amount_expected "100000"
client.create_invoice(amount: "25", asset: "usdc", chain: "base")     # amount_expected "25000000"
client.create_invoice(amount: "0.05", asset: "eth", chain: "ethereum")
client.create_invoice(amount_sats: "42000", asset: "btc")             # exact base units
```

Pass exactly one amount: `amount_fiat` (with `currency`), `amount`, `amount_sats` or `amount_btc`. Amounts can be a `String`, an `Integer` or a `BigDecimal`. A `Float` raises `TypeError` before any request is sent.

`add_fee_ontop` decides who pays the Receivify commission:

```ruby
inv = client.create_invoice(amount: "25", asset: "usdc", chain: "base", add_fee_ontop: true)
inv.add_fee_ontop          # => true
inv.amount_net_requested   # => "25000000"   what you receive
inv.amount_expected        # => "25252525"   what the payer is asked to send
inv.fee_ontop              # => "252525"     the commission added on top
```

| You pass | The SDK sends | Result |
|---|---|---|
| nothing, or `nil` | nothing | the **account default** (`client.get_settings.add_fee_ontop`) applies |
| `add_fee_ontop: true` | `true` | the payer covers the fee |
| `add_fee_ontop: false` | `false` | the fee comes out of the amount, whatever the account default is |
| `"true"`, `1`, anything else | nothing | raises `TypeError` before any request |

Leaving the option out is **not** the same as passing `false`.

You can change the account default with `client.update_settings(add_fee_ontop: true)`.

Other invoice fields: `decimals` (Integer), `amount_decimal` (display String), `payment_uri` (a String to put in a QR code, or `nil` on account-based chains), `address`, `expires_at`, `metadata`.

---

## Forwarding addresses

A forwarding address is a permanent deposit address. Everything sent to it is forwarded to your own wallet.

```ruby
fwd = client.create_address(
  ticker: "btc",
  destination: "bc1qyourcoldwallet...",
  callback: "https://shop.example/webhooks/receivify",
  idempotency_key: "customer-42-btc"
)
fwd.address        # give this to the customer
fwd.min_forward    # "0.000100" -- display units

detail = client.get_address(fwd.id)    # includes .payments and .callbacks
client.update_address(fwd.id, status: "paused")      # pause
client.update_address(fwd.id, callback_url: nil)     # clear the callback
client.disable_address(fwd.id)                       # soft delete

client.list_tickers.each { |t| puts "#{t.ticker} #{t.chain} decimals=#{t.decimals}" }
```

Calling `create_address` again with the same ticker, destination and callback returns the same address. `*_forward` aliases exist for every `*_address` method.

---

## Pagination

Every list method returns a `Receivify::Page` (`data`, `has_more?`, `next_cursor`), and a page is `Enumerable`:

```ruby
page = client.list_invoices(limit: 50, status: "paid")
page.each { |inv| puts inv.id }
page = client.list_invoices(limit: 50, cursor: page.next_cursor) if page.has_more?
```

The `each_*` methods follow `next_cursor` for you. They fetch one page at a time, only when they need it:

```ruby
client.each_invoice(status: "paid") { |inv| puts "#{inv.id} #{inv.amount_received}" }

# without a block you get an Enumerator
recent = client.each_invoice(limit: 100).first(250)
total  = client.each_invoice(status: "paid").sum { |inv| inv.amount_received.to_i }
```

Auto-paginators: `each_invoice`, `each_address`, `each_payment_link`, `each_recurring`, `each_api_key`, `each_webhook_endpoint`, `each_webhook_delivery`.

---

## Errors

Every API failure raises a subclass of `Receivify::APIError` with `code`, `detail` and `status`:

```ruby
begin
  client.get_invoice("00000000-0000-4000-8000-000000000000")
rescue Receivify::NotFoundError => e
  e.code     # => "not_found"
  e.status   # => 404
  e.detail   # => "No such invoice."
rescue Receivify::RateLimitedError => e
  sleep(e.retry_after || 1)
rescue Receivify::APIError => e
  # any other code, including codes added after this release (then e.class == Receivify::APIError)
  raise if e.retryable?
end
```

| Class | Code | Status |
|---|---|---|
| `BadRequestError` | `bad_request` | 400 |
| `BadTickerError` / `BadDestinationError` | `bad_ticker` / `bad_destination` | 400 |
| `AssetUnavailableError` / `AmountTooLowError` | `asset_unavailable` / `amount_too_low` | 400 |
| `SuspendedError` / `LimitExceededError` | `suspended` / `limit_exceeded` | 400 |
| `InvalidCodeError` / `NoSetupError` / `NotEnabledError` / `NoTargetError` / `InvalidTokenError` | ... | 400 |
| `UnauthorizedError` | `unauthorized` | 401 |
| `SignupDisabledError` | `signup_disabled` | 403 |
| `NotFoundError` | `not_found` | 404 |
| `AlreadyExistsError` / `AlreadyEnabledError` / `NotAllowedError` | ... | 409 |
| `RateLimitedError` | `rate_limited` | 429 |
| `InternalError` / `UpstreamError` / `RateUnavailableError` | `internal` / `upstream_error` / `rate_unavailable` | 500 / 502 / 503 |

Other errors, all under `Receivify::Error`:

- `Receivify::TransportError` (and `Receivify::TimeoutError`): the request got no response at all.
- `Receivify::SignatureVerificationError`: a webhook did not verify.
- `Receivify::WebhookPayloadError`: the webhook verified, but its body is not a JSON object.

**Retries.** The SDK retries GET requests, and mutating requests that carry an `idempotency_key`. It retries on 429, 5xx and connection failures, up to `max_retries` times (default 2), with exponential backoff that honours `Retry-After` and `X-RateLimit-Reset`. A POST **without** an idempotency key is never retried, so a timeout can never create a second invoice.

---

## Rotating the webhook secret

```ruby
rotated = client.rotate_webhook_secret(idempotency_key: "rotate-2026-10")
rotated.secret            # the NEW secret, shown once -- store it now
rotated.prev_expires_at   # the old secret stays valid until then (24 h by default)
```

During the grace window, verify against both secrets with the new one first:

```ruby
Receivify::Webhook.verify(raw, header, [new_secret, old_secret])
```

Receivify signs with the new secret straight away. Once `prev_expires_at` has passed, drop the old secret. `client.get_webhook_secret_state.prev_active` tells you whether the old secret is still accepted.

---

## Money helpers

Amounts arrive as integer strings in base units (satoshis, wei, and so on). The helpers below convert by moving the decimal point in the string, so they stay exact at any size:

```ruby
Receivify::Money.format_amount("42000", 8)               # => "0.00042000"
Receivify::Money.format_amount("42000", 8, trim: true)   # => "0.00042"
Receivify::Money.to_base_units("0.00042", 8)             # => "42000"
Receivify::Money.to_base_units("0.000000001", 8)         # raises ArgumentError (would lose money)
Receivify::Money.to_decimal("1000000000000000001", 18)   # => BigDecimal("1.000000000000000001")

inv = client.get_invoice(id)
Receivify::Money.format_amount(inv.amount_received, inv.decimals)
```

`to_decimal` loads `bigdecimal` only when you call it. On Ruby 3.4 and later, add `gem "bigdecimal"` to your Gemfile if you use it.

---

## Resource objects

Each response object is immutable. You can read its fields as methods or by key:

```ruby
invoice.status          # reader
invoice["status"]       # raw value
invoice[:status]        # symbols work too
invoice.raw             # the frozen Hash, including fields added after this release
invoice.to_h            # a mutable deep copy
invoice.dig(:metadata, :order_id)
```

`inspect` never prints your API key, a newly created key, or a rotated webhook secret.

---

## Full method list

| Area | Methods |
|---|---|
| Invoices | `create_invoice`, `get_invoice`, `list_invoices`, `each_invoice`, `cancel_invoice`, `bulk_cancel_invoices` |
| Forwarding | `create_address`, `get_address`, `list_addresses`, `each_address`, `update_address`, `disable_address` (plus `*_forward` aliases) |
| Catalogue (no key) | `list_tickers`, `list_assets` |
| Payment links | `create_payment_link`, `get_payment_link`, `list_payment_links`, `each_payment_link`, `update_payment_link`, `deactivate_payment_link` |
| Recurring | `create_recurring`, `list_recurring`, `each_recurring`, `cancel_recurring` |
| API keys | `list_api_keys`, `each_api_key`, `create_api_key`, `revoke_api_key` |
| Webhooks | `list_webhook_endpoints`, `each_webhook_endpoint`, `create_webhook_endpoint`, `update_webhook_endpoint`, `delete_webhook_endpoint`, `list_webhook_deliveries`, `each_webhook_delivery`, `replay_webhook_delivery`, `get_webhook_secret_state`, `rotate_webhook_secret`, `send_test_webhook`, `verify_webhook`, `parse_webhook` |
| Payouts and settings | `get_payouts`, `stage_payout_changes`, `cancel_payout_change`, `get_settings`, `update_settings`, `get_summary` |
| Public | `get_public_invoice`, `get_public_payment_link`, `get_health` |
| Low level | `request(method, path, params:, body:, idempotency_key:)` returns the raw `Receivify::Response` |

Client options: `api_url:` (default `https://receivify.io`), `api_key:`, `webhook_secrets:`, `timeout:` (default 30 s), `max_retries:` (default 2), `transport:`, `sleeper:` and `user_agent:` (default `receivify-ruby/<version>`).

---

## Testing your integration

To test your own webhook handler, sign a sample payload:

```ruby
body = '{"event":"invoice.paid","sequence":1,"created_at":"2026-01-01T00:00:00.000Z","data":{"id":"..."}}'
header = Receivify::Webhook.generate_header(body, "whsec_test")
post "/webhooks/receivify", body, { "HTTP_X_RECEIVIFY_SIGNATURE" => header }
```

The transport is pluggable. Anything that responds to `call(request)` can stand in for the network:

```ruby
fake = ->(req) { Receivify::Response.new(200, { "Content-Type" => "application/json" }, '{"id":"inv_1","status":"paid"}') }
client = Receivify::Client.new(api_key: "rcv_live_test", transport: fake)
client.get_invoice("inv_1").status   # => "paid"
```

## Running the SDK's tests

The suite uses Minitest from the standard library and needs no network or gems:

```bash
ruby -Ilib -Itest test/run_all.rb
```

To run it in Docker:

```bash
docker run --rm -v "$PWD":/sdk -w /sdk ruby:2.7-slim ruby -Ilib -Itest test/run_all.rb
docker run --rm -v "$PWD":/sdk -w /sdk ruby:3.3-slim ruby -Ilib -Itest test/run_all.rb
```

---

## Links

- Website: https://receivify.io
- API documentation: https://receivify.io/docs

## License

MIT. See [LICENSE](LICENSE).
