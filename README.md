# Receivify SDKs

Official client libraries for **[Receivify](https://receivify.io)**, the crypto payments API.

Receivify lets you accept crypto with one API call. You create an invoice priced in your own
currency, send your customer to a hosted checkout (or build your own), and get a signed webhook
when the payment lands. Funds are forwarded straight to your own wallet. There is no balance to
withdraw, and every payment is tracked with reorg-safe, double-entry accounting.

- **Coins and chains:** Bitcoin, Litecoin, Dogecoin, Bitcoin Cash, Monero, Zcash, Ether and
  stablecoins such as USDC and USDT on Ethereum, Base, Arbitrum, Optimism, Polygon, BNB Chain,
  Avalanche, Linea, Solana and Tron. The live list is at `GET /api/v1/tickers`.
- **Invoices:** fiat-priced, rate-locked, with a hosted checkout page.
- **Forwarding addresses:** standing deposit addresses that sweep everything to your wallet.
- **Signed webhooks:** HMAC-SHA256, with secret rotation.

**[Website](https://receivify.io)** · **[Docs](https://receivify.io/docs)** ·
**[API reference (OpenAPI)](https://receivify.io/openapi.yaml)** ·
**[Sign up](https://receivify.io/signup)** ·
**[Shop plugins](https://receivify.io/integrations)**

---

## Pick your language

| Language | Folder | Install | Needs | Dependencies |
|---|---|---|---|---|
| Node.js / TypeScript | [`node/`](node) | `npm install receivify` | Node 18+ | none |
| Python | [`python/`](python) | `pip install receivify` | Python 3.9+ | none |
| PHP | [`php/`](php) | copy `Receivify.php` | PHP 7.4+, ext-curl | none |
| .NET (C#) | [`dotnet/`](dotnet) | `dotnet add package Receivify` | .NET 8 | none |
| C++ | [`cpp/`](cpp) | CMake, header-only | C++17 | libcurl, OpenSSL (both optional) |
| No SDK (plain PHP) | [`examples/plain-php/`](examples/plain-php) | copy the folder | PHP 7.4+ | none |

> **Package registries:** the npm, PyPI and NuGet packages are being published. Until they are,
> install from this repository (each folder's README has a "from source" line) or download a
> zip from [receivify.io/sdks](https://receivify.io/sdks).

Every SDK has the same surface: create, get, list and cancel invoices, forwarding addresses,
tickers, webhook-secret rotation, and webhook verification. They all follow the same rules.

## Quickstart (Node.js)

Get an API key and webhook secret from your [Receivify dashboard](https://receivify.io/dashboard).

```js
import { Receivify } from 'receivify';

const receivify = new Receivify('https://receivify.io', process.env.RECEIVIFY_API_KEY, {
  webhookSecrets: [process.env.RECEIVIFY_WEBHOOK_SECRET],
});

// 1. Create an invoice priced in your currency, then redirect to the hosted checkout.
const invoice = await receivify.createInvoice(
  { amount_fiat: '25.00', currency: 'USD', asset: 'btc', reference: 'order-1001' },
  { idempotencyKey: 'order-1001' }, // a retry after a timeout never creates a second invoice
);
redirect(invoice.checkout_url);

// 2. In your webhook handler, verify the RAW request body before trusting anything.
const event = receivify.parseWebhook(rawBody, req.headers['x-receivify-signature']);
if (event.event === 'invoice.paid') markOrderPaid(event.data.reference);
```

The same flow in every other language is in that language's README:
[Python](python/README.md) · [PHP](php/README.md) · [.NET](dotnet/README.md) · [C++](cpp/README.md).

## Four rules every integration must follow

1. **Verify webhooks over the raw body.** The signature header is
   `X-Receivify-Signature: t=<unix>,v1=<hex>`, an HMAC-SHA256 of `<t>.<raw body>`. Re-serialised
   JSON changes key order and spacing, and will never match.
2. **Verify against a list of secrets.** When you rotate your webhook secret, the old one stays
   valid for a grace window. Pass both: current first, previous second.
3. **Money is a string.** Every amount is an exact-integer string in base units (satoshis, wei).
   Never parse one into a float. `amount_fiat` is a display price only.
4. **Use an idempotency key and a unique `reference`.** `reference` de-duplicates per account, so
   make it unique per order. If several shops share one account, prefix it with the shop.

## Running the tests

Every SDK has an offline test suite that needs no network and no API key.

```bash
cd node   && npm test
cd python && python -m unittest discover -s tests -t .
cd php    && php tests/run-tests.php
cd dotnet && dotnet run --project tests/Receivify.Tests
cmake -S cpp -B build && cmake --build build && ctest --test-dir build
```

CI runs all of these on every push (see [`.github/workflows/ci.yml`](.github/workflows/ci.yml)).

## Shop plugins

Ready-made plugins for WooCommerce, Magento 2, PrestaShop and OpenCart are available from
[receivify.io/integrations](https://receivify.io/integrations).

## Support and security

- Questions and bugs: open an issue in this repository.
- Security problems: please don't open a public issue. Contact us through
  [receivify.io](https://receivify.io).

## License

[MIT](LICENSE)
