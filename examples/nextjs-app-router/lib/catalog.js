// What the shop sells and which coins it offers. Safe to import anywhere (no secrets).
// Prices are strings: money never goes through a float.
export const PRODUCT = { name: 'Receivify T-shirt', price: '25.00', currency: 'GBP' }

// `chain` picks the network for tokens that exist on several (USDC on Base, USDT on Tron...).
// GET /api/v1/assets lists what's live on your account.
export const COINS = {
  btc: { label: 'Bitcoin', asset: 'btc' },
  ltc: { label: 'Litecoin', asset: 'ltc' },
  eth: { label: 'Ethereum', asset: 'eth', chain: 'ethereum' },
  'usdc-base': { label: 'USDC on Base', asset: 'usdc', chain: 'base' },
  'usdt-tron': { label: 'USDT on Tron', asset: 'usdt', chain: 'tron' },
  sol: { label: 'Solana', asset: 'sol', chain: 'solana' },
}
