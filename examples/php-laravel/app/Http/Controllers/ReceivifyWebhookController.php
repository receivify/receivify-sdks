<?php

namespace App\Http\Controllers;

use App\Models\Order;
use Illuminate\Http\Request;
use Illuminate\Support\Facades\DB;
use Illuminate\Support\Facades\Log;
use Receivify;
use ReceivifyException;

/**
 * POST /webhooks/receivify: Receivify tells us what happened to an invoice.
 * This is the ONLY place an order is marked paid.
 *
 * The route is excluded from CSRF protection (see bootstrap/app.php): the request comes from
 * Receivify's servers, not from a browser form, and the HMAC signature is what proves it's real.
 */
class ReceivifyWebhookController extends Controller
{
    /**
     * invoice.paid is THE signal: the full amount is confirmed and can't be reversed.
     * invoice.swept follows it (funds forwarded to your wallet), so it also means paid.
     */
    private const STATUS_FOR_EVENT = [
        'invoice.confirming' => 'confirming', // seen on-chain, not final: don't ship yet
        'invoice.partial' => 'partially_paid',
        'invoice.paid' => 'paid',
        'invoice.swept' => 'paid',
        'invoice.expired' => 'expired',
        'invoice.partial_expired' => 'underpaid', // time ran out on an under-payment
        'invoice.cancelled' => 'cancelled',
        'invoice.failed' => 'failed',
    ];

    public function __invoke(Request $request, Receivify $receivify)
    {
        // getContent() is the body EXACTLY as sent. The signature is an HMAC over these bytes;
        // $request->json() + json_encode() would re-serialise it and the check would fail.
        $rawBody = $request->getContent();

        try {
            // Constant-time HMAC check + 5-minute replay window + JSON decode.
            $event = $receivify->parseWebhook($rawBody, (string) $request->header('X-Receivify-Signature'));
        } catch (ReceivifyException $e) {
            Log::warning('Rejected Receivify webhook: ' . $e->getMessage());
            return response('bad signature', 400);
        }

        if ($event['event'] === 'test') {
            Log::info('Test webhook received and verified.');
            return response('ok');
        }

        // Ignore anything that isn't about an invoice, including event names added later.
        $next = self::STATUS_FOR_EVENT[$event['event']] ?? null;
        $reference = $event['data']['reference'] ?? null;
        if ($next === null || $reference === null) {
            return response('ok');
        }

        // A transaction with a row lock, so two deliveries for the same order can't race.
        DB::transaction(function () use ($event, $next, $reference) {
            $order = Order::whereKey($reference)->lockForUpdate()->first();

            // Unknown (made elsewhere), a duplicate, or an older event arriving late: do nothing.
            // Delivery is at-least-once and may be out of order, but `sequence` only increases.
            if ($order === null || $event['sequence'] <= $order->last_seq) {
                return;
            }

            $order->last_seq = $event['sequence'];
            if ($order->status !== 'paid') { // paid is final for fulfilment
                $order->status = $next;
                $order->invoice_id = $event['data']['id'] ?? $order->invoice_id;
                // Fulfil when $next === 'paid', ideally by dispatching a queued job,
                // e.g. FulfilOrder::dispatch($order)->afterCommit();
            }
            $order->save();
        });

        return response('ok'); // 2xx fast; anything else is retried with backoff
    }
}
