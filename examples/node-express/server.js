// Receivify + Express: a complete, tiny shop.
//
//   GET  /                         product page with a "Pay with crypto" form
//   POST /checkout                 creates the order + a Receivify invoice, redirects to checkout
//   GET  /orders/:id               order status page (where the customer comes back to)
//   POST /webhooks/receivify       Receivify tells us what happened; the ONLY place an order is marked paid
//
// Run:  cp .env.example .env  (fill it in)  ->  npm install  ->  npm start

import express from 'express'
import { Receivify, ReceivifyApiError, ReceivifyWebhookError, SIGNATURE_HEADER } from 'receivify'
import { createOrder, getOrder, updateOrder } from './orders.js'

const {
  RECEIVIFY_API_KEY,
  RECEIVIFY_WEBHOOK_SECRET,
  RECEIVIFY_API_URL = 'https://receivify.io',
  APP_URL = 'http://localhost:3000',
  PORT = 3000,
  HOST = '127.0.0.1', // localhost only by default; set HOST=0.0.0.0 in a container
} = process.env

if (!RECEIVIFY_API_KEY || !RECEIVIFY_WEBHOOK_SECRET) {
  console.error('Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET in .env (see .env.example).')
  process.exit(1)
}

// One client for the whole app. The webhook secret is a LIST so a rotation never drops
// a delivery: during the grace window put the new secret first and the old one second,
// e.g. RECEIVIFY_WEBHOOK_SECRET="whsec_new,whsec_old".
const receivify = new Receivify(RECEIVIFY_API_URL, RECEIVIFY_API_KEY, {
  webhookSecrets: RECEIVIFY_WEBHOOK_SECRET.split(',').map((s) => s.trim()),
})

// What we sell. Prices are strings: money never goes through a float.
const PRODUCT = { name: 'Receivify T-shirt', price: '25.00', currency: 'GBP' }

// What the customer may pay with. `chain` picks the network for tokens that live on
// several (USDC on Base, USDT on Tron...). Check GET /api/v1/assets for what's live.
const COINS = {
  btc: { label: 'Bitcoin', asset: 'btc' },
  ltc: { label: 'Litecoin', asset: 'ltc' },
  eth: { label: 'Ethereum', asset: 'eth', chain: 'ethereum' },
  'usdc-base': { label: 'USDC on Base', asset: 'usdc', chain: 'base' },
  'usdt-tron': { label: 'USDT on Tron', asset: 'usdt', chain: 'tron' },
  sol: { label: 'Solana', asset: 'sol', chain: 'solana' },
}

const app = express()

// ---------------------------------------------------------------------------
// The webhook route is registered BEFORE any JSON body parser, with express.raw().
// The signature is an HMAC over the EXACT bytes Receivify sent. If express.json()
// parses the body first and you re-serialise it, the bytes change (key order,
// spacing, unicode escapes) and every signature check fails.
// ---------------------------------------------------------------------------
app.post('/webhooks/receivify', express.raw({ type: '*/*', limit: '1mb' }), (req, res) => {
  let event
  try {
    // Verifies the HMAC-SHA256 signature in constant time, rejects anything older than
    // 5 minutes (replay protection), then JSON-parses. Throws if any of that fails.
    event = receivify.parseWebhook(req.body, req.get(SIGNATURE_HEADER))
  } catch (err) {
    if (err instanceof ReceivifyWebhookError) {
      // Not from Receivify (or the wrong secret). Don't do anything with it.
      console.warn('Rejected webhook:', err.message)
      return res.status(400).send('bad signature')
    }
    throw err
  }

  // `test` is what "Send test" in the dashboard delivers. Nothing to update.
  if (event.event === 'test') {
    console.log('Test webhook received and verified.')
    return res.sendStatus(200)
  }

  // Only invoice events concern orders. Ignore anything else, including event
  // names this code doesn't know yet: new ones can be added at any time.
  if (!event.event.startsWith('invoice.')) return res.sendStatus(200)

  const data = event.data // { id, reference, status, amount_expected, amount_received, ... }
  const order = data.reference ? getOrder(data.reference) : null
  if (!order) {
    // An invoice we didn't create from this shop (e.g. made in the dashboard). 200 so
    // Receivify doesn't keep retrying something we will never be able to handle.
    return res.sendStatus(200)
  }

  // Deliveries are AT-LEAST-ONCE and can arrive OUT OF ORDER (a retry of an old event
  // can land after a newer one). `sequence` only ever goes up, so apply an event only if
  // it is newer than the last one we applied to this order. This one line makes the
  // handler safe against duplicates, retries and reordering.
  if (event.sequence <= order.lastSeq) return res.sendStatus(200)

  // Once paid, an order stays paid as far as fulfilment is concerned.
  if (order.status === 'paid') {
    updateOrder(order.id, { lastSeq: event.sequence })
    return res.sendStatus(200)
  }

  const next = STATUS_FOR_EVENT[event.event]
  if (next) {
    updateOrder(order.id, { status: next, lastSeq: event.sequence, invoiceId: data.id })
    console.log(`Order ${order.id}: ${order.status} -> ${next} (${event.event})`)
    if (next === 'paid') {
      // Fulfil here: send the confirmation email, release the download, book the courier.
      // Keep it quick or hand it to a queue: Receivify waits for your 2xx.
    }
  }

  // Answer fast. Anything that isn't 2xx is retried with backoff.
  res.sendStatus(200)
})

// How each invoice event moves an order. `invoice.paid` is THE signal: the full amount
// has the confirmations it needs and can't be reversed. `invoice.swept` comes after it
// (funds forwarded to your wallet), so if it ever arrives first it also means paid.
const STATUS_FOR_EVENT = {
  'invoice.confirming': 'confirming', // seen on-chain, waiting for confirmations: don't ship yet
  'invoice.partial': 'partially_paid', // less than the amount arrived so far
  'invoice.paid': 'paid',
  'invoice.swept': 'paid',
  'invoice.expired': 'expired', // nothing arrived in time
  'invoice.partial_expired': 'underpaid', // time ran out on an under-payment: contact the customer
  'invoice.cancelled': 'cancelled',
  'invoice.failed': 'failed',
}

// Everything else can use the normal form/JSON parsers.
app.use(express.urlencoded({ extended: false }))

app.get('/', (_req, res) => {
  res.send(page('Shop', `
    <h1>${PRODUCT.name}</h1>
    <p class="price">£${PRODUCT.price}</p>
    <form method="post" action="/checkout">
      <label>Pay with
        <select name="coin">${Object.entries(COINS).map(([k, c]) => `<option value="${k}">${c.label}</option>`).join('')}</select>
      </label>
      <label class="check"><input type="checkbox" name="fee_ontop" value="1"> Customer pays the network fee on top</label>
      <button>Pay with crypto</button>
    </form>`))
})

app.post('/checkout', async (req, res, next) => {
  try {
    const coin = COINS[req.body.coin] ?? COINS.btc
    const order = createOrder({ product: PRODUCT.name, amount: PRODUCT.price, currency: PRODUCT.currency })

    const invoice = await receivify.createInvoice(
      {
        // Price in fiat; Receivify converts at today's rate and locks it on the invoice.
        amount_fiat: PRODUCT.price,
        currency: PRODUCT.currency,
        asset: coin.asset,
        ...(coin.chain ? { chain: coin.chain } : {}),
        // Your order id. It comes back on every webhook as `data.reference`, which is how
        // the webhook handler finds the order. It must be unique per order.
        reference: order.id,
        metadata: { product: PRODUCT.name, source: 'node-express-example' },
        // true: the customer covers our fee so you receive the full price.
        // Leave it out to use your account default.
        ...(req.body.fee_ontop ? { add_fee_ontop: true } : {}),
      },
      // Safe to retry: if this request is sent twice (double click, network retry),
      // the same key returns the same invoice instead of creating a second one.
      { idempotencyKey: `checkout-${order.id}` },
    )

    updateOrder(order.id, { invoiceId: invoice.id })

    // Send the customer to Receivify's hosted checkout (QR code, address, live status).
    // When they're done there, they come back to /orders/:id.
    res.redirect(303, invoice.checkout_url)
  } catch (err) {
    if (err instanceof ReceivifyApiError) {
      // e.g. amount_too_low, asset_unavailable. `code` is stable; `detail` is human-readable.
      return res.status(400).send(page('Could not start payment', `<p>${escape(err.detail || err.code)}</p><p><a href="/">Back</a></p>`))
    }
    next(err)
  }
})

app.get('/orders/:id', (req, res) => {
  const order = getOrder(req.params.id)
  if (!order) return res.status(404).send(page('Not found', '<p>No such order.</p>'))
  // NEVER mark an order paid because the customer landed here: anyone can open this URL.
  // This page only DISPLAYS what the signed webhook recorded.
  res.send(page(`Order ${order.id}`, `
    <h1>Order ${escape(order.id)}</h1>
    <p>${escape(order.product)}: £${escape(order.amount)}</p>
    <p class="status ${order.status}">${STATUS_TEXT[order.status] ?? order.status}</p>
    ${order.status === 'paid' ? '' : '<p class="muted">This page refreshes every 5 seconds.</p><meta http-equiv="refresh" content="5">'}
    <p><a href="${APP_URL}/">Back to the shop</a></p>`))
})

const STATUS_TEXT = {
  pending: 'Waiting for payment',
  confirming: 'Payment seen, waiting for confirmations',
  partially_paid: 'Part of the payment has arrived',
  paid: 'Paid. Thank you!',
  expired: 'The payment window closed',
  underpaid: 'Underpaid. We will be in touch',
  cancelled: 'Cancelled',
  failed: 'Payment failed',
}

const escape = (s) => String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c])
const page = (title, body) => `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>${escape(title)}</title>
<style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem;color:#18181b}
.price{font-size:2rem;font-weight:700}label{display:block;margin:1rem 0}select,button{font:inherit;padding:.6rem .9rem;border-radius:.6rem}
button{background:#a3e635;border:0;font-weight:600;cursor:pointer}.check{font-size:.9rem}.status{font-weight:700}.paid{color:#4d7c0f}.muted{color:#71717a;font-size:.9rem}</style>
</head><body>${body}</body></html>`

app.listen(PORT, HOST, () => console.log(`Shop running on http://localhost:${PORT}`))
