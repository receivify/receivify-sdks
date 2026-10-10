import { PRODUCT, COINS } from '@/lib/catalog'

// A plain HTML form posting to the checkout route: works without any client JavaScript.
export default function Shop() {
  return (
    <main>
      <h1>{PRODUCT.name}</h1>
      <p style={{ fontSize: '2rem', fontWeight: 700 }}>£{PRODUCT.price}</p>
      <form method="post" action="/api/checkout">
        <label style={{ display: 'block', margin: '1rem 0' }}>
          Pay with{' '}
          <select name="coin" defaultValue="btc">
            {Object.entries(COINS).map(([key, c]) => (
              <option key={key} value={key}>{c.label}</option>
            ))}
          </select>
        </label>
        <label style={{ display: 'block', margin: '1rem 0', fontSize: '.9rem' }}>
          <input type="checkbox" name="fee_ontop" value="1" /> Customer pays the fee on top
        </label>
        <button style={{ background: '#a3e635', border: 0, padding: '.6rem 1rem', borderRadius: '.6rem', fontWeight: 600 }}>
          Pay with crypto
        </button>
      </form>
    </main>
  )
}
