// One Receivify client for the whole app, created on first use.
//
// `server-only` is a guard: if this file is ever imported into a Client Component by
// mistake, the build fails instead of shipping your API key to the browser.
import 'server-only'
import { Receivify } from 'receivify'

let client

export function receivify() {
  if (!client) {
    const { RECEIVIFY_API_KEY, RECEIVIFY_WEBHOOK_SECRET, RECEIVIFY_API_URL = 'https://receivify.io' } = process.env
    if (!RECEIVIFY_API_KEY || !RECEIVIFY_WEBHOOK_SECRET) {
      throw new Error('Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET in .env.local (see .env.example).')
    }
    client = new Receivify(RECEIVIFY_API_URL, RECEIVIFY_API_KEY, {
      // A list: during a secret rotation, the new secret first and the old one second.
      webhookSecrets: RECEIVIFY_WEBHOOK_SECRET.split(',').map((s) => s.trim()),
    })
  }
  return client
}
