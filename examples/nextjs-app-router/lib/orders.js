// A demo order store: one JSON file under ./data, so orders survive `next dev` reloads.
// In production this is your database. What matters is the shape:
//   id (also the Receivify invoice `reference`), status, invoiceId, lastSeq.
import 'server-only'
import { mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs'
import { join } from 'node:path'
import { randomUUID } from 'node:crypto'

const DIR = join(process.cwd(), 'data')
const FILE = join(DIR, 'orders.json')

const load = () => (existsSync(FILE) ? JSON.parse(readFileSync(FILE, 'utf8')) : {})
const save = (all) => {
  mkdirSync(DIR, { recursive: true })
  writeFileSync(FILE, JSON.stringify(all, null, 2))
}

export function createOrder({ product, amount, currency }) {
  const all = load()
  // Prefixed so it can't collide with another shop on the same Receivify account:
  // `reference` is also a de-duplication key on Receivify's side.
  const id = 'next-' + randomUUID().slice(0, 8)
  all[id] = { id, product, amount, currency, status: 'pending', invoiceId: null, lastSeq: 0, createdAt: new Date().toISOString() }
  save(all)
  return all[id]
}

export const getOrder = (id) => load()[id] ?? null

export function updateOrder(id, patch) {
  const all = load()
  if (!all[id]) return null
  all[id] = { ...all[id], ...patch }
  save(all)
  return all[id]
}
