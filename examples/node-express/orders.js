// A deliberately tiny order store: a JSON file on disk, so orders survive a restart
// while you test. In a real shop this is your database table; the shape is what matters:
//
//   id         your order id (also sent to Receivify as the invoice `reference`)
//   status     pending -> paid (or expired / underpaid / cancelled / failed)
//   invoiceId  the Receivify invoice id, handy for support and for getInvoice()
//   lastSeq    the highest webhook `sequence` already applied to this order (see webhook handler)

import { readFileSync, writeFileSync, existsSync } from 'node:fs'
import { randomUUID } from 'node:crypto'

const FILE = new URL('./orders.json', import.meta.url)
const load = () => (existsSync(FILE) ? JSON.parse(readFileSync(FILE, 'utf8')) : {})
const save = (all) => writeFileSync(FILE, JSON.stringify(all, null, 2))

export function createOrder({ product, amount, currency }) {
  const all = load()
  // Short, unique and safe to put in a URL. Prefixed so it can't collide with another
  // shop on the same Receivify account (references are de-duplication keys).
  const id = 'demo-' + randomUUID().slice(0, 8)
  all[id] = { id, product, amount, currency, status: 'pending', invoiceId: null, lastSeq: 0, createdAt: new Date().toISOString() }
  save(all)
  return all[id]
}

export function getOrder(id) {
  return load()[id] ?? null
}

export function updateOrder(id, patch) {
  const all = load()
  if (!all[id]) return null
  all[id] = { ...all[id], ...patch }
  save(all)
  return all[id]
}
