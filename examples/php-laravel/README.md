# Receivify + PHP (Laravel)

The Receivify parts of a Laravel 11+ app: two controllers, a model and migration, routes, the CSRF exception for the webhook, and two Blade views. Not a full skeleton: create a Laravel app, then copy these files in.

## What it shows

A one-product shop that takes crypto, end to end:

1. **Buy**: the customer picks a coin and clicks *Pay with crypto*. The app creates an order, then a Receivify invoice priced in fiat (£25.00, converted and locked at today's rate) with the order id as the invoice `reference`, and redirects to the hosted checkout (`invoice.checkout_url`).
2. **Webhook**: Receivify POSTs signed events to `/webhooks/receivify`. The handler reads the **raw** body, verifies the `X-Receivify-Signature` with the SDK, ignores duplicates and out-of-order retries (using `sequence`), and marks the order **paid only on `invoice.paid`** (or `invoice.swept`, which comes after it).
3. **Order page**: `/orders/<id>` shows the status the webhook recorded and refreshes until it's paid. It never marks anything paid itself, because anyone can open that URL. (Receivify's hosted checkout doesn't send the customer back to your site automatically, so link this page from your confirmation email or the customer's account.)

The comments in the code explain *why* each step is there, not just what it does.

## Files

| File | What's in it |
|---|---|
| `app/Http/Controllers/CheckoutController.php` | Product page, checkout (creates the order + invoice), order page |
| `app/Http/Controllers/ReceivifyWebhookController.php` | Raw body via `$request->getContent()`, signature check, a locked DB transaction to apply the event |
| `app/Providers/AppServiceProvider.php` | Binds one `Receivify` client as a singleton |
| `bootstrap/app.php` | Excludes `webhooks/receivify` from CSRF (it's authenticated by its signature instead) |
| `routes/web.php`, `app/Models/Order.php`, the migration, the Blade views | The rest |
| `config/services.receivify.php`, `composer.receivify.json` | What to merge into your existing config files |

## Prerequisites

- PHP 8.2+ and Composer.
- A Receivify account: an **API key** (Dashboard → API keys) and the **webhook signing secret** (Dashboard → Webhooks).

## Install

```bash
composer create-project laravel/laravel shop && cd shop
# copy this example's app/, database/, resources/, routes/ and bootstrap/ folders into the new app
```

Install the SDK. Until `receivify/receivify` is listed on Packagist, point Composer at the GitHub repo (see `composer.receivify.json`):

```bash
composer config repositories.receivify vcs https://github.com/receivify/receivify-php
composer require receivify/receivify:^2.0
```

Then add the `receivify` entry from `config/services.receivify.php` to your `config/services.php`, put the `RECEIVIFY_*` values in Laravel's `.env`, and run the migration:

```bash
php artisan migrate
```

## Run

```bash
cp .env.example .env   # then fill in RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET
php artisan serve
```

Open http://localhost:8000, pick a coin, and pay.

## Receiving webhooks on your machine

Receivify has to reach your webhook URL over the internet. While developing, use a tunnel:

```bash
# any one of these
npx localtunnel --port 8000
cloudflared tunnel --url http://localhost:8000
ngrok http 8000
```

Then in the Receivify dashboard go to **Webhooks**, add `https://<your-tunnel-host>/webhooks/receivify` and press **Send test**. You should see `Test webhook received and verified.` in this app's log. The dashboard also lists every delivery with its response, and lets you replay one.

You can send the same test from code or curl:

```bash
curl -X POST https://receivify.io/api/v1/webhooks/test   -H "Authorization: Bearer $RECEIVIFY_API_KEY"   -H "Content-Type: application/json"   -d '{"url":"https://<your-tunnel-host>/webhooks/receivify"}'
```

To try a real payment, buy the product with a small amount (each coin has a minimum; a stablecoin on a cheap network like USDC on Base costs a few cents in fees) and watch the order page flip to **Paid**.

**Note:** keep the `RECEIVIFY_*` values in Laravel's `.env`. `php artisan serve` does not pass shell environment variables through to the app.

No Laravel? See [plain-php](../plain-php) for the same flow in three plain PHP files.

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
