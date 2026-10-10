<?php

namespace App\Http\Controllers;

use App\Models\Order;
use Illuminate\Http\Request;
use Illuminate\Support\Str;
use Receivify;
use ReceivifyApiError;

/**
 * The shop side: product page, checkout, order status page.
 * The webhook (the only place an order becomes paid) is in ReceivifyWebhookController.
 */
class CheckoutController extends Controller
{
    // What we sell. Prices are strings: money never goes through a float.
    public const PRODUCT = ['name' => 'Receivify T-shirt', 'price' => '25.00', 'currency' => 'GBP'];

    // `chain` picks the network for tokens that live on several. GET /api/v1/assets lists what's live.
    public const COINS = [
        'btc' => ['label' => 'Bitcoin', 'asset' => 'btc'],
        'ltc' => ['label' => 'Litecoin', 'asset' => 'ltc'],
        'eth' => ['label' => 'Ethereum', 'asset' => 'eth', 'chain' => 'ethereum'],
        'usdc-base' => ['label' => 'USDC on Base', 'asset' => 'usdc', 'chain' => 'base'],
        'usdt-tron' => ['label' => 'USDT on Tron', 'asset' => 'usdt', 'chain' => 'tron'],
        'sol' => ['label' => 'Solana', 'asset' => 'sol', 'chain' => 'solana'],
    ];

    public function shop()
    {
        return view('shop', ['product' => self::PRODUCT, 'coins' => self::COINS]);
    }

    // Receivify is bound as a singleton in AppServiceProvider, so it's injected here.
    public function checkout(Request $request, Receivify $receivify)
    {
        $coin = self::COINS[$request->input('coin')] ?? self::COINS['btc'];

        $order = Order::create([
            // Prefixed so it can't collide with another shop on the same Receivify account:
            // `reference` is also a de-duplication key on Receivify's side.
            'id' => 'laravel-' . Str::lower(Str::random(8)),
            'product' => self::PRODUCT['name'],
            'amount' => self::PRODUCT['price'],
            'status' => 'pending',
            'last_seq' => 0,
        ]);

        $params = [
            'amount_fiat' => self::PRODUCT['price'], // converted at today's rate and locked on the invoice
            'currency' => self::PRODUCT['currency'],
            'asset' => $coin['asset'],
            // Comes back on every webhook as data.reference: that's how we find the order.
            'reference' => $order->id,
            'metadata' => ['product' => self::PRODUCT['name'], 'source' => 'laravel-example'],
        ];
        if (isset($coin['chain'])) {
            $params['chain'] = $coin['chain'];
        }
        if ($request->boolean('fee_ontop')) {
            // The customer covers the fee so you receive the full price. Leave it out for your account default.
            $params['add_fee_ontop'] = true;
        }

        try {
            // Same idempotency key = same invoice: a double submit or a retry can't create two.
            $invoice = $receivify->createInvoice($params, ['idempotency_key' => 'checkout-' . $order->id]);
        } catch (ReceivifyApiError $e) {
            // getErrorCode() is stable (amount_too_low, asset_unavailable, ...); getDetail() is for humans.
            return back()->withErrors(['payment' => $e->getDetail() ?: $e->getErrorCode()]);
        }

        $order->update(['invoice_id' => $invoice['id']]);

        // 303: the browser follows with a GET to Receivify's hosted checkout.
        return redirect()->away($invoice['checkout_url'], 303);
    }

    public function show(Order $order)
    {
        // Display only. Landing on this URL proves nothing; the webhook is the source of truth.
        return view('orders.show', ['order' => $order]);
    }
}
