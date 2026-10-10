import { notFound } from 'next/navigation'
import { getOrder } from '@/lib/orders'

// Read fresh on every request: the webhook may have just updated the order.
export const dynamic = 'force-dynamic'

const TEXT = {
  pending: 'Waiting for payment',
  confirming: 'Payment seen, waiting for confirmations',
  partially_paid: 'Part of the payment has arrived',
  paid: 'Paid. Thank you!',
  expired: 'The payment window closed',
  underpaid: 'Underpaid. We will be in touch',
  cancelled: 'Cancelled',
  failed: 'Payment failed',
}

// The customer lands here after checkout. This page only SHOWS what the signed webhook
// recorded: landing on a URL proves nothing, anyone can open it.
export default async function OrderPage({ params }) {
  const { id } = await params
  const order = getOrder(id)
  if (!order) notFound()

  return (
    <main>
      {order.status !== 'paid' && <meta httpEquiv="refresh" content="5" />}
      <h1>Order {order.id}</h1>
      <p>{order.product}: £{order.amount}</p>
      <p style={{ fontWeight: 700, color: order.status === 'paid' ? '#4d7c0f' : undefined }}>{TEXT[order.status] ?? order.status}</p>
      {order.status !== 'paid' && <p style={{ color: '#71717a', fontSize: '.9rem' }}>This page refreshes every 5 seconds.</p>}
      <p><a href="/">Back to the shop</a></p>
    </main>
  )
}
