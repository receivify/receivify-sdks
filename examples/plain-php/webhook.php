<?php
/**
 * Minimal custom-store webhook receiver. Verifies the signature, then marks your
 * order paid. Point Receivify's callback_url here.
 *
 * Two rules this file exists to demonstrate:
 *   - verification runs over the RAW body (`php://input`), never re-encoded JSON;
 *   - verification takes a LIST of secrets, because a rotation keeps the previous
 *     one valid for a grace window.
 */
// A copy of php/Receivify.php ships next to this file, so the
// example works on its own once unzipped.
require_once __DIR__ . '/Receivify.php';

$rcv = new Receivify(
    getenv('RECEIVIFY_URL') ?: 'https://receivify.io',
    getenv('RECEIVIFY_API_KEY') ?: '',
    [
        'webhook_secrets' => array_filter([
            getenv('RECEIVIFY_WEBHOOK_SECRET'),
            // Set this during a rotation's grace window, then remove it.
            getenv('RECEIVIFY_WEBHOOK_SECRET_PREVIOUS'),
        ]),
    ]
);

$event = $rcv->readWebhook();       // null if the signature or timestamp is invalid
if ($event === null) {
    http_response_code(401);
    echo 'bad signature';
    exit;
}

// Delivery is at-least-once: retries and manual replays re-send the same bytes.
// De-duplicate on (event, sequence) BEFORE touching the order.
$dedupeKey = Receivify::dedupeKey($event);   // e.g. "invoice.paid:42"
// if (already_processed($dedupeKey)) { http_response_code(200); echo 'ok'; exit; }

$data    = Receivify::webhookData($event);   // the `data` object — NOT $event['invoice']
$orderId = isset($data['reference']) ? $data['reference'] : null;

switch (Receivify::classifyEvent(Receivify::eventName($event))) {
    case 'paid':                  // invoice.paid / invoice.swept / sweep.sent
        // mark_order_paid($orderId);
        break;
    case 'confirming':            // seen on-chain, not final — do NOT release goods
        // add_order_note($orderId, 'Payment detected, awaiting confirmations.');
        break;
    case 'underpaid':             // less than expected arrived, window still open
    case 'underpaid_expired':     // window closed on an under-payment
        // add_order_note($orderId, 'Under-payment — needs a human.');
        break;
    case 'expired':
    case 'cancelled':
        // mark_order_cancelled($orderId);
        break;
    case 'failed':
        // flag_for_review($orderId);
        break;
    // 'test'  — POST /api/v1/webhooks/test. Acknowledge, change nothing.
    // 'ignore' — forward.* and any event name added after this code was written.
}

// remember_processed($dedupeKey);

http_response_code(200);            // any 2xx marks the delivery delivered
echo 'ok';
