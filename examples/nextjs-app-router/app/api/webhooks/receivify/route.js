// POST /api/webhooks/receivify: Receivify tells us what happened to an invoice.
// This is the ONLY place an order is marked paid.
import { ReceivifyWebhookError, SIGNATURE_HEADER } from 'receivify'
import { receivify } from '@/lib/receivify'
import { getOrder, updateOrder } from '@/lib/orders'

export const runtime = 'nodejs'
// Never cache or pre-render a webhook.
export const dynamic = 'force-dynamic'

// invoice.paid is THE signal: the full amount is confirmed and can't be reversed.
// invoice.swept follows it (funds forwarded to your wallet), so it also means paid.
const STATUS_FOR_EVENT = {
  'invoice.confirming': 'confirming', // seen on-chain, not final yet: don't ship
  'invoice.partial': 'partially_paid',
  'invoice.paid': 'paid',
  'invoice.swept': 'paid',
  'invoice.expired': 'expired',
  'invoice.partial_expired': 'underpaid', // time ran out on an under-payment
  'invoice.cancelled': 'cancelled',
  'invoice.failed': 'failed',
}

export async function POST(request) {
  // request.text() gives the body EXACTLY as sent. The signature is an HMAC over those
  // bytes, so never use request.json() + JSON.stringify(): the bytes would change.
  const rawBody = await request.text()

  let event
  try {
    // Constant-time HMAC check + 5-minute replay window + JSON parse, in one call.
    event = receivify().parseWebhook(rawBody, request.headers.get(SIGNATURE_HEADER))
  } catch (err) {
    if (err instanceof ReceivifyWebhookError) return new Response('bad signature', { status: 400 })
    throw err
  }

  if (event.event === 'test') {
    console.log('Test webhook received and verified.')
    return new Response('ok')
  }
  // Ignore anything that isn't about an invoice, including event names added in future.
  if (!event.event.startsWith('invoice.')) return new Response('ok')

  const order = event.data.reference ? getOrder(event.data.reference) : null
  // Not one of ours (e.g. an invoice made in the dashboard): acknowledge so it isn't retried.
  if (!order) return new Response('ok')

  // At-least-once delivery, possibly out of order. `sequence` only increases, so applying
  // only events newer than the last one we applied makes duplicates and stale retries harmless.
  if (event.sequence <= order.lastSeq) return new Response('ok')

  if (order.status === 'paid') {
    // Paid is final for fulfilment purposes.
    updateOrder(order.id, { lastSeq: event.sequence })
    return new Response('ok')
  }

  const next = STATUS_FOR_EVENT[event.event]
  if (next) {
    updateOrder(order.id, { status: next, lastSeq: event.sequence, invoiceId: event.data.id })
    if (next === 'paid') {
      // Fulfil here (email, download, shipping). Keep it fast or enqueue it:
      // anything other than a quick 2xx gets retried.
    }
  }
  return new Response('ok')
}
