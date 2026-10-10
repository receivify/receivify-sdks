# Receivify + Ruby (Sinatra)

A complete, runnable Sinatra shop that takes crypto payments with the [Receivify Ruby SDK](../../ruby).

## What it shows

A one-product shop that takes crypto, end to end:

1. **Buy**: the customer picks a coin and clicks *Pay with crypto*. The app creates an order, then a Receivify invoice priced in fiat (£25.00, converted and locked at today's rate) with the order id as the invoice `reference`, and redirects to the hosted checkout (`invoice.checkout_url`).
2. **Webhook**: Receivify POSTs signed events to `/webhooks/receivify`. The handler reads the **raw** body, verifies the `X-Receivify-Signature` with the SDK, ignores duplicates and out-of-order retries (using `sequence`), and marks the order **paid only on `invoice.paid`** (or `invoice.swept`, which comes after it).
3. **Order page**: `/orders/<id>` shows the status the webhook recorded and refreshes until it's paid. It never marks anything paid itself, because anyone can open that URL. (Receivify's hosted checkout doesn't send the customer back to your site automatically, so link this page from your confirmation email or the customer's account.)

The comments in the code explain *why* each step is there, not just what it does.

## Files

| File | What's in it |
|---|---|
| `app.rb` | The whole app: routes, checkout, webhook (`request.body.read` for the raw body), order page, a mutex-guarded order store |
| `Gemfile` | Sinatra, Puma, dotenv, receivify |

## Prerequisites

- Ruby 3.0 or newer and Bundler.
- A Receivify account: an **API key** (Dashboard → API keys) and the **webhook signing secret** (Dashboard → Webhooks).

## Install

```bash
bundle install
```

The `Gemfile` loads the SDK from this repository (`path: "../../ruby"`). Once it's on RubyGems, use `gem "receivify", "~> 1.0"` instead.

## Run

```bash
cp .env.example .env   # then fill in RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET
bundle exec ruby app.rb
```

Open http://localhost:4567, pick a coin, and pay.

## Receiving webhooks on your machine

Receivify has to reach your webhook URL over the internet. While developing, use a tunnel:

```bash
# any one of these
npx localtunnel --port 4567
cloudflared tunnel --url http://localhost:4567
ngrok http 4567
```

Then in the Receivify dashboard go to **Webhooks**, add `https://<your-tunnel-host>/webhooks/receivify` and press **Send test**. You should see `Test webhook received and verified.` in this app's log. The dashboard also lists every delivery with its response, and lets you replay one.

You can send the same test from code or curl:

```bash
curl -X POST https://receivify.io/api/v1/webhooks/test   -H "Authorization: Bearer $RECEIVIFY_API_KEY"   -H "Content-Type: application/json"   -d '{"url":"https://<your-tunnel-host>/webhooks/receivify"}'
```

To try a real payment, buy the product with a small amount (each coin has a minimum; a stablecoin on a cheap network like USDC on Base costs a few cents in fees) and watch the order page flip to **Paid**.

## The same thing in Rails

```ruby
# config/initializers/receivify.rb
RECEIVIFY = Receivify::Client.new(
  api_url: ENV.fetch("RECEIVIFY_API_URL", "https://receivify.io"),
  api_key: ENV.fetch("RECEIVIFY_API_KEY"),
  webhook_secrets: ENV.fetch("RECEIVIFY_WEBHOOK_SECRET").split(",")
)

# app/controllers/receivify_webhooks_controller.rb
class ReceivifyWebhooksController < ActionController::Base
  skip_forgery_protection # server-to-server; the HMAC signature authenticates it

  def create
    event = RECEIVIFY.parse_webhook(request.raw_post, request.headers["X-Receivify-Signature"])
    Order.apply_receivify_event!(event) if event.event.start_with?("invoice.") # your sequence-checked update
    head :ok
  rescue Receivify::SignatureVerificationError, Receivify::WebhookPayloadError
    head :bad_request
  end
end

# config/routes.rb
post "/webhooks/receivify", to: "receivify_webhooks#create"
```

In the checkout action: `invoice = RECEIVIFY.create_invoice(amount_fiat: "25.00", currency: "GBP", asset: "btc", reference: order.id, idempotency_key: "checkout-#{order.id}")`, then `redirect_to invoice.checkout_url, allow_other_host: true, status: :see_other`.

## Going live checklist

- [ ] API key and webhook secret come from environment variables or a secret manager, never from code or git.
- [ ] The webhook URL is **https** and publicly reachable; it is registered in Dashboard → Webhooks.
- [ ] The webhook route gets the **raw** request body (no JSON parser in front of it).
- [ ] Orders are marked paid **only** from a verified `invoice.paid` (or `invoice.swept`) webhook, never from the redirect back to your site.
- [ ] Each order has a unique `reference` (prefix it with your store name if several shops share one Receivify account).
- [ ] The webhook handler is idempotent (duplicates and retries are normal) and answers 2xx quickly; slow work goes to a queue.
- [ ] Unknown event names are ignored with a 200, so new events never break you.
- [ ] Your payout wallet address is set in Dashboard → Payouts, and you have made one small real payment end to end.
- [ ] Orders live in your real database (the store here is a demo).
- [ ] When you rotate the webhook secret, configure both secrets (new first) until the old one expires.

See also: [all examples](../README.md) · [Receivify docs](https://receivify.io/docs)
