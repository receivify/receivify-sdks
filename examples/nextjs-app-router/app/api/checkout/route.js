// POST /api/checkout: create the order, create a Receivify invoice for it, and send the
// customer to the hosted checkout.
import { NextResponse } from 'next/server'
import { ReceivifyApiError } from 'receivify'
import { receivify } from '@/lib/receivify'
import { PRODUCT, COINS } from '@/lib/catalog'
import { createOrder, updateOrder } from '@/lib/orders'

// The SDK uses Node APIs (node:crypto); keep this off the Edge runtime.
export const runtime = 'nodejs'

export async function POST(request) {
  const form = await request.formData()
  const coin = COINS[form.get('coin')] ?? COINS.btc
  const order = createOrder({ product: PRODUCT.name, amount: PRODUCT.price, currency: PRODUCT.currency })

  try {
    const invoice = await receivify().createInvoice(
      {
        amount_fiat: PRODUCT.price, // converted at today's rate and locked on the invoice
        currency: PRODUCT.currency,
        asset: coin.asset,
        ...(coin.chain ? { chain: coin.chain } : {}),
        // Comes back on every webhook as data.reference: that's how we find the order again.
        reference: order.id,
        metadata: { product: PRODUCT.name, source: 'nextjs-example' },
        // true = the customer covers the fee and you receive the full price.
        // Omit it to use your account default.
        ...(form.get('fee_ontop') ? { add_fee_ontop: true } : {}),
      },
      // Retrying with the same key returns the SAME invoice: no duplicates on a double submit.
      { idempotencyKey: `checkout-${order.id}` },
    )
    updateOrder(order.id, { invoiceId: invoice.id })

    // 303 turns the POST into a GET on Receivify's checkout page.
    return NextResponse.redirect(invoice.checkout_url, 303)
  } catch (err) {
    if (err instanceof ReceivifyApiError) {
      // `code` is stable (amount_too_low, asset_unavailable, ...); `detail` is for humans.
      return NextResponse.json({ error: err.code, detail: err.detail }, { status: 400 })
    }
    throw err
  }
}
