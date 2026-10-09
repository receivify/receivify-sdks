<?php
/**
 * Minimal custom-store checkout. Prices the order in fiat, creates a Receivify
 * invoice, and redirects the customer to hosted checkout.
 */
// A copy of php/Receivify.php ships next to this file, so the
// example works on its own once unzipped.
require_once __DIR__ . '/Receivify.php';

$rcv = new Receivify(
    getenv('RECEIVIFY_URL') ?: 'https://receivify.io',
    getenv('RECEIVIFY_API_KEY') ?: '',
    ['webhook_secret' => getenv('RECEIVIFY_WEBHOOK_SECRET') ?: '']
);

$orderId  = isset($_POST['order_id']) ? $_POST['order_id'] : uniqid('order-');
$total    = isset($_POST['total']) ? (string) $_POST['total'] : '0';   // a STRING, in your store currency
$currency = isset($_POST['currency']) ? $_POST['currency'] : 'USD';

try {
    $invoice = $rcv->createInvoice([
        'amount_fiat'  => $total,
        'currency'     => $currency,
        'asset'        => isset($_POST['asset']) ? $_POST['asset'] : 'btc',
        'reference'    => (string) $orderId,
        'callback_url' => 'https://your-store.com/webhook.php',
        'metadata'     => ['order_id' => $orderId],
    ], ['idempotency_key' => 'order-' . $orderId]);

    header('Location: ' . $invoice['checkout_url']);
    exit;
} catch (ReceivifyApiError $e) {
    // Typed: getErrorCode() is the frozen code, getDetail() the human sentence.
    http_response_code($e->isRetryable() ? 503 : 400);
    echo 'Payment error: ' . htmlspecialchars($e->getDetail());
} catch (ReceivifyException $e) {
    http_response_code(502);
    echo 'Payment error: ' . htmlspecialchars($e->getMessage());
}
