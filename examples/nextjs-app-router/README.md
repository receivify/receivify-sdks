# Receivify + Next.js (App Router)

A complete, runnable Next.js App Router shop that takes crypto payments with the [Receivify Node.js SDK](../../node): Route Handlers for checkout and webhooks, a Server Component order page, and the API key kept server-side.

## What it shows

A one-product shop that takes crypto, end to end:

1. **Buy**: the customer picks a coin and clicks *Pay with crypto*. The app creates an order, then a Receivify invoice priced in fiat (£25.00, converted and locked at today's rate) with the order id as the invoice `reference`, and redirects to the hosted checkout (`invoice.checkout_url`).
2. **Webhook**: Receivify POSTs signed events to `/api/webhooks/receivify`. The handler reads the **raw** body, verifies the `X-Receivify-Signature` with the SDK, ignores duplicates and out-of-order retries (using `sequence`), and marks the order **paid only on `invoice.paid`** (or `invoice.swept`, which comes after it).
3. **Order page**: `/orders/<id>` shows the status the webhook recorded and refreshes until it's paid. It never marks anything paid itself, because anyone can open that URL. (Receivify's hosted checkout doesn't send the customer back to your site automatically, so link this page from your confirmation email or the customer's account.)

The comments in the code explain *why* each step is there, not just what it does.

## Files

| File | What's in it |
|---|---|
| `app/page.js` | Product page with a plain HTML form (no client JS needed) |
| `app/api/checkout/route.js` | Creates the order + invoice, redirects to `invoice.checkout_url` |
| `app/api/webhooks/receivify/route.js` | Reads the raw body with `await request.text()`, verifies, updates the order |
| `app/orders/[id]/page.js` | Order status page (dynamic, never cached) |
| `lib/receivify.js` | The SDK client, guarded with `server-only` so the key can never reach the browser |
| `lib/catalog.js`, `lib/orders.js` | Product and coins, and a JSON-file order store |

## Prerequisites

- Node.js 20 or newer.
- A Receivify account: an **API key** (Dashboard → API keys) and the **webhook signing secret** (Dashboard → Webhooks).

## Install

```bash
npm install
```

This installs [`receivify`](https://www.npmjs.com/package/receivify) from npm (it works the same with pnpm, yarn or bun).

## Run

```bash
cp .env.example .env   # then fill in RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET
mv .env .env.local   # Next.js reads .env.local
npm run dev
```

Open http://localhost:3000, pick a coin, and pay.

## Receiving webhooks on your machine

Receivify has to reach your webhook URL over the internet. While developing, use a tunnel:

```bash
# any one of these
npx localtunnel --port 3000
cloudflared tunnel --url http://localhost:3000
ngrok http 3000
```

Then in the Receivify dashboard go to **Webhooks**, add `https://<your-tunnel-host>/api/webhooks/receivify` and press **Send test**. You should see `Test webhook received and verified.` in this app's log. The dashboard also lists every delivery with its response, and lets you replay one.

You can send the same test from code or curl:

```bash
curl -X POST https://receivify.io/api/v1/webhooks/test   -H "Authorization: Bearer $RECEIVIFY_API_KEY"   -H "Content-Type: application/json"   -d '{"url":"https://<your-tunnel-host>/api/webhooks/receivify"}'
```

To try a real payment, buy the product with a small amount (each coin has a minimum; a stablecoin on a cheap network like USDC on Base costs a few cents in fees) and watch the order page flip to **Paid**.

## Deploying (Vercel or anywhere)

- Set `RECEIVIFY_API_KEY`, `RECEIVIFY_WEBHOOK_SECRET` and `RECEIVIFY_API_URL` as server environment variables (never with a `NEXT_PUBLIC_` prefix).
- Both route handlers use `export const runtime = 'nodejs'`. Keep them off the Edge runtime.
- Replace `lib/orders.js` with your database: serverless filesystems are read-only or per-instance.

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
