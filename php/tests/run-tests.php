<?php
/**
 * Receivify PHP SDK test suite.
 *
 *   php tests/run-tests.php
 *
 * Stock tooling only — no PHPUnit, no Composer, and NO NETWORK. Every request
 * goes through an injected transport that records what was built and replays a
 * canned response.
 */

require __DIR__ . '/../Receivify.php';

/* --------------------------------------------------------------- harness */

$GLOBALS['rcv_pass'] = 0;
$GLOBALS['rcv_fail'] = 0;

function ok(string $name, bool $condition, string $note = ''): void
{
    if ($condition) {
        $GLOBALS['rcv_pass']++;
        echo "  ok   $name\n";
    } else {
        $GLOBALS['rcv_fail']++;
        echo "  FAIL $name" . ($note !== '' ? " — $note" : '') . "\n";
    }
}

function is_same(string $name, $expected, $actual): void
{
    ok($name, $expected === $actual, 'expected ' . var_export($expected, true) . ', got ' . var_export($actual, true));
}

function throws(string $name, string $expectedClass, callable $fn): void
{
    try {
        $fn();
    } catch (Throwable $e) {
        ok($name, $e instanceof $expectedClass, 'got ' . get_class($e) . ': ' . $e->getMessage());
        return;
    }
    ok($name, false, 'nothing was thrown');
}

function section(string $title): void
{
    echo "\n$title\n";
}

/**
 * A transport that records requests and returns queued responses.
 * Each queued response is ['status' => int, 'body' => string|array, 'headers' => array].
 */
class FakeTransport
{
    public $requests = [];
    private $responses;

    public function __construct(array $responses)
    {
        $this->responses = $responses;
    }

    public function __invoke(array $request): array
    {
        $this->requests[] = $request;
        $res = array_shift($this->responses);
        if ($res === null) {
            throw new RuntimeException('FakeTransport: no queued response for ' . $request['method'] . ' ' . $request['url']);
        }
        $body = isset($res['body']) ? $res['body'] : '';
        return [
            'status' => isset($res['status']) ? $res['status'] : 200,
            'headers' => isset($res['headers']) ? $res['headers'] : [],
            'body' => is_string($body) ? $body : json_encode($body),
        ];
    }

    public function last(): array
    {
        return end($this->requests) ?: [];
    }

    public function lastJson(): array
    {
        $req = $this->last();
        return json_decode(isset($req['body']) ? (string) $req['body'] : '', true) ?: [];
    }
}

function client(FakeTransport $t, array $opts = []): Receivify
{
    $opts['transport'] = $t;
    if (!isset($opts['max_retry_delay'])) {
        $opts['max_retry_delay'] = 0.0; // keep the suite instant
    }
    return new Receivify('https://api.example.test/', 'rcv_live_testkey', $opts);
}

function sign(string $body, string $secret, ?int $ts = null): string
{
    $ts = $ts === null ? time() : $ts;
    return 't=' . $ts . ',v1=' . hash_hmac('sha256', $ts . '.' . $body, $secret);
}

/* ------------------------------------------------------- request building */

section('Request building');

$invoiceBody = [
    'id' => '11111111-1111-4111-8111-111111111111',
    'reference' => '1001',
    'asset' => 'btc',
    // A wei-sized value: it must survive as a STRING, not a float.
    'amount_expected' => '1000000000000000000000',
    'amount_received' => '0',
    'amount_fiat' => '25.00',
    'fiat_currency' => 'USD',
    'chain' => null,
    'provider' => 'bitcoind',
    'address' => 'bcrt1qexample',
    'status' => 'pending',
    'expires_at' => '2026-08-24T13:00:00.000Z',
    'paid_at' => null,
    'created_at' => '2026-08-24T12:00:00.000Z',
    'metadata' => ['order_id' => 1001],
    'checkout_url' => 'https://api.example.test/pay/11111111-1111-4111-8111-111111111111',
];

$t = new FakeTransport([['status' => 201, 'body' => $invoiceBody]]);
$rcv = client($t);
$invoice = $rcv->createInvoice([
    'amount_fiat' => '25.00',
    'currency' => 'usd',
    'asset' => 'btc',
    'reference' => 1001,
    'callback_url' => 'https://store.example/receivify',
    'metadata' => ['order_id' => 1001],
], ['idempotency_key' => 'order-1001']);

$req = $t->last();
is_same('createInvoice uses POST', 'POST', $req['method']);
is_same('createInvoice hits /api/v1/invoices', 'https://api.example.test/api/v1/invoices', $req['url']);
is_same('bearer key is sent in the header', 'Bearer rcv_live_testkey', $req['headers']['Authorization']);
ok('the api key never reaches the URL', strpos($req['url'], 'rcv_live_testkey') === false);
is_same('Idempotency-Key is forwarded', 'order-1001', $req['headers']['Idempotency-Key']);
is_same('User-Agent identifies the SDK', 'receivify-php/' . Receivify::VERSION, $req['headers']['User-Agent']);
is_same('Content-Type is JSON', 'application/json', $req['headers']['Content-Type']);

$sent = $t->lastJson();
is_same('currency is upper-cased', 'USD', $sent['currency']);
is_same('reference is stringified', '1001', $sent['reference']);
is_same('amount_fiat stays a string', '25.00', $sent['amount_fiat']);
ok('idempotency_key never leaks into the body', !isset($sent['idempotency_key']));
ok('unknown keys are dropped from the body', !isset($sent['nope']));

is_same('amount_expected is returned as a string', '1000000000000000000000', $invoice['amount_expected']);
ok('amount_expected is not a float', is_string($invoice['amount_expected']));
is_same('amount_fiat is a decimal string', '25.00', $invoice['amount_fiat']);

/* the legacy positional signature still works */
$t = new FakeTransport([['status' => 201, 'body' => $invoiceBody]]);
$rcv = client($t);
$rcv->createInvoice(25.0, 'eur', ['reference' => 'A-7', 'idempotency_key' => 'legacy-7', 'asset' => 'ltc']);
$sent = $t->lastJson();
is_same('legacy createInvoice(float, currency, opts) still builds a body', 'EUR', $sent['currency']);
is_same('legacy float amount is rendered exactly', '25', $sent['amount_fiat']);
is_same('legacy asset survives', 'ltc', $sent['asset']);
is_same('legacy idempotency_key becomes a header', 'legacy-7', $t->last()['headers']['Idempotency-Key']);

/* add_fee_ontop: forwarded only when given, and only as a real bool */
$t = new FakeTransport([['status' => 201, 'body' => $invoiceBody], ['status' => 201, 'body' => $invoiceBody]]);
$rcv = client($t);
$rcv->createInvoice(['amount_sats' => '1000']);
ok('add_fee_ontop is omitted when not given', !array_key_exists('add_fee_ontop', $t->lastJson()));
$rcv->createInvoice(['amount_sats' => '1000', 'add_fee_ontop' => false]);
ok('add_fee_ontop=false is sent as a JSON false', $t->lastJson()['add_fee_ontop'] === false);
$threw = false;
try { $rcv->createInvoice(['amount_sats' => '1000', 'add_fee_ontop' => 'true']); } catch (ReceivifyException $e) { $threw = true; }
ok('a non-bool add_fee_ontop is rejected before the request', $threw);

/* public + catalogue routes must not send the key */
$t = new FakeTransport([['status' => 200, 'body' => ['data' => [], 'has_more' => false, 'next_cursor' => null]]]);
$rcv = client($t);
$rcv->listTickers();
ok('listTickers is unauthenticated', !isset($t->last()['headers']['Authorization']));

/* ------------------------------------------------------------ list + pages */

section('List envelope and auto-pagination');

$page1 = [
    'data' => [['id' => 'a'], ['id' => 'b']],
    'has_more' => true,
    'next_cursor' => 'cursor-2',
];
$page2 = [
    'data' => [['id' => 'c']],
    'has_more' => false,
    'next_cursor' => null,
];

$t = new FakeTransport([['status' => 200, 'body' => $page1]]);
$rcv = client($t);
$list = $rcv->listInvoices(['limit' => 2, 'status' => 'paid']);
ok('listInvoices returns a ReceivifyList', $list instanceof ReceivifyList);
is_same('page carries has_more', true, $list->has_more);
is_same('page carries next_cursor', 'cursor-2', $list->next_cursor);
is_same('page is countable', 2, count($list));
ok('page is iterable', iterator_to_array($list) === $page1['data']);
ok('limit and status become query parameters', strpos($t->last()['url'], 'limit=2') !== false && strpos($t->last()['url'], 'status=paid') !== false);

$t = new FakeTransport([
    ['status' => 200, 'body' => $page1],
    ['status' => 200, 'body' => $page2],
]);
$rcv = client($t);
$ids = [];
foreach ($rcv->autoPagingInvoices(['limit' => 2]) as $item) {
    $ids[] = $item['id'];
}
is_same('auto-pagination walks every page', ['a', 'b', 'c'], $ids);
ok('auto-pagination passes the cursor back', strpos($t->requests[1]['url'], 'cursor=cursor-2') !== false);
is_same('auto-pagination stops when has_more is false', 2, count($t->requests));

/* a server that repeats a cursor must not spin forever */
$loop = ['data' => [['id' => 'x']], 'has_more' => true, 'next_cursor' => 'same'];
$t = new FakeTransport([
    ['status' => 200, 'body' => $loop],
    ['status' => 200, 'body' => $loop],
]);
$rcv = client($t);
throws('a repeated cursor aborts auto-pagination', 'ReceivifyException', function () use ($rcv) {
    foreach ($rcv->autoPagingAddresses() as $ignored) {
        // drain
    }
});

/* ------------------------------------------------------------------ errors */

section('Typed errors');

$t = new FakeTransport([['status' => 404, 'body' => ['error' => 'not_found', 'detail' => 'No such invoice.']]]);
$rcv = client($t);
throws('404 not_found maps to ReceivifyNotFoundError', 'ReceivifyNotFoundError', function () use ($rcv) {
    $rcv->getInvoice('11111111-1111-4111-8111-111111111111');
});

$t = new FakeTransport([['status' => 400, 'body' => ['error' => 'bad_ticker', 'detail' => 'Did you mean "usdc"?']]]);
$rcv = client($t);
try {
    $rcv->createAddress(['ticker' => 'usdcc', 'destination' => '0xabc']);
    ok('bad_ticker throws', false);
} catch (ReceivifyBadTickerError $e) {
    ok('bad_ticker maps to ReceivifyBadTickerError', true);
    is_same('the raw code is preserved', 'bad_ticker', $e->getErrorCode());
    is_same('the detail is preserved', 'Did you mean "usdc"?', $e->getDetail());
    is_same('the status code is preserved', 400, $e->getStatusCode());
    ok('bad_ticker is not retryable', !$e->isRetryable());
}

/* An error code this SDK has never seen must NOT crash it. */
$t = new FakeTransport([['status' => 418, 'body' => ['error' => 'teapot_unavailable', 'detail' => 'Invented in the future.']]]);
$rcv = client($t);
try {
    $rcv->getInvoice('x');
    ok('an unknown error code throws', false);
} catch (ReceivifyApiError $e) {
    ok('an unknown error code falls back to ReceivifyApiError', get_class($e) === 'ReceivifyApiError');
    is_same('the unknown code is still readable', 'teapot_unavailable', $e->getErrorCode());
}

/* A non-conforming body still produces a usable error. */
$t = new FakeTransport([['status' => 502, 'body' => '<html>bad gateway</html>']]);
$rcv = client($t, ['max_retries' => 0]);
try {
    $rcv->getInvoice('x');
    ok('a non-JSON error body throws', false);
} catch (ReceivifyApiError $e) {
    is_same('a non-JSON error body yields an empty code', '', $e->getErrorCode());
    ok('the raw body is preserved for debugging', strpos($e->getResponseBody(), 'bad gateway') !== false);
}

$t = new FakeTransport([['status' => 429, 'body' => ['error' => 'rate_limited', 'detail' => 'Slow down.'], 'headers' => ['retry-after' => '7']]]);
$rcv = client($t, ['max_retries' => 0]);
try {
    $rcv->listInvoices();
    ok('429 throws', false);
} catch (ReceivifyRateLimitedError $e) {
    is_same('Retry-After is exposed', 7, $e->getRetryAfter());
    ok('rate_limited is retryable', $e->isRetryable());
}

/* --------------------------------------------------------------- retries */

section('Retries');

$t = new FakeTransport([
    ['status' => 429, 'body' => ['error' => 'rate_limited', 'detail' => 'Slow down.'], 'headers' => ['retry-after' => '0']],
    ['status' => 200, 'body' => ['data' => [], 'has_more' => false, 'next_cursor' => null]],
]);
$rcv = client($t);
$rcv->listInvoices();
is_same('a safe GET is retried after a 429', 2, count($t->requests));

$t = new FakeTransport([
    ['status' => 500, 'body' => ['error' => 'internal', 'detail' => 'Unexpected error.']],
    ['status' => 200, 'body' => ['id' => 'x']],
]);
$rcv = client($t);
$rcv->createInvoice(['amount_sats' => '1000'], null, ['idempotency_key' => 'k-1']);
is_same('a POST carrying an Idempotency-Key is retried', 2, count($t->requests));

$t = new FakeTransport([
    ['status' => 500, 'body' => ['error' => 'internal', 'detail' => 'Unexpected error.']],
    ['status' => 200, 'body' => ['id' => 'x']],
]);
$rcv = client($t);
throws('a bare mutating call is NEVER blind-retried', 'ReceivifyInternalError', function () use ($rcv) {
    $rcv->cancelInvoice('11111111-1111-4111-8111-111111111111');
});
is_same('… and it was attempted exactly once', 1, count($t->requests));

/* ------------------------------------------------------------- forwarding */

section('Forwarding');

$forward = [
    'id' => '22222222-2222-4222-8222-222222222222',
    'forward_id' => '22222222-2222-4222-8222-222222222222',
    'ticker' => 'btc',
    'provider' => 'bitcoind',
    'chain' => null,
    'address' => 'bcrt1qdeposit',
    'destination' => 'bcrt1qpayout',
    'callback_url' => 'https://store.example/forward',
    'confirmations_required' => 2,
    'min_forward' => '0.000100',
    'status' => 'active',
    'expires_at' => null,
    'created_at' => '2026-08-24T12:00:00.000Z',
    'updated_at' => null,
];

$t = new FakeTransport([['status' => 201, 'body' => $forward]]);
$rcv = client($t);
$created = $rcv->createAddress([
    'coin' => 'BTC',                       // alias for ticker
    'to' => 'bcrt1qpayout',                // alias for destination
    'webhook' => 'https://store.example/forward', // alias for callback
    'confs' => 3,
], ['idempotency_key' => 'fwd-1']);
$sent = $t->lastJson();
is_same('createAddress normalises the ticker alias', 'btc', $sent['ticker']);
is_same('createAddress normalises the destination alias', 'bcrt1qpayout', $sent['destination']);
is_same('createAddress normalises the callback alias', 'https://store.example/forward', $sent['callback']);
is_same('createAddress normalises the confirmations alias', 3, $sent['confirmations']);
is_same('the canonical id is `id`', '22222222-2222-4222-8222-222222222222', Receivify::forwardId($created));

throws('createAddress requires a destination', 'ReceivifyException', function () use ($rcv) {
    $rcv->createAddress(['ticker' => 'btc']);
});

$t = new FakeTransport([['status' => 200, 'body' => array_merge($forward, ['status' => 'paused'])]]);
$rcv = client($t);
$paused = $rcv->pauseAddress($forward['id']);
is_same('the kill switch PATCHes', 'PATCH', $t->last()['method']);
is_same('the kill switch sends status=paused', ['status' => 'paused'], $t->lastJson());
is_same('the kill switch returns the paused forward', 'paused', $paused['status']);

$t = new FakeTransport([['status' => 200, 'body' => $forward]]);
$rcv = client($t);
throws('destination is refused before the request is sent', 'ReceivifyException', function () use ($rcv, $forward) {
    $rcv->updateAddress($forward['id'], ['destination' => 'bcrt1qattacker']);
});
is_same('… and nothing was sent', 0, count($t->requests));

throws('an invalid status is refused locally', 'ReceivifyException', function () use ($rcv, $forward) {
    $rcv->updateAddress($forward['id'], ['status' => 'disabled']);
});

$t = new FakeTransport([['status' => 200, 'body' => array_merge($forward, ['status' => 'disabled'])]]);
$rcv = client($t);
$rcv->disableAddress($forward['id']);
is_same('disableAddress is a DELETE', 'DELETE', $t->last()['method']);
is_same('disableAddress targets the forward', 'https://api.example.test/api/v1/address/' . $forward['id'], $t->last()['url']);

/* ---------------------------------------------------------------- secrets */

section('Webhook secret rotation');

$t = new FakeTransport([[
    'status' => 200,
    'body' => [
        'secret' => 'whsec_new',
        'rotated_at' => '2026-08-24T12:00:00.000Z',
        'prev_expires_at' => '2026-08-25T12:00:00.000Z',
        'grace_seconds' => 86400,
        'replaced_previous' => false,
        'message' => 'Store this now.',
    ],
]]);
$rcv = client($t);
$rotated = $rcv->rotateWebhookSecret(['idempotency_key' => 'rotate-1']);
is_same('rotate POSTs', 'POST', $t->last()['method']);
is_same('rotate hits the rotate route', 'https://api.example.test/api/v1/webhooks/secret/rotate', $t->last()['url']);
is_same('rotate forwards the Idempotency-Key', 'rotate-1', $t->last()['headers']['Idempotency-Key']);
is_same('rotate returns the new secret once', 'whsec_new', $rotated['secret']);

/* ------------------------------------------------------------ verification */

section('Webhook verification');

$current = 'whsec_current';
$previous = 'whsec_previous';
$body = '{"event":"invoice.paid","sequence":42,"created_at":"2026-08-24T12:05:00.000Z","data":{"id":"11111111-1111-4111-8111-111111111111","reference":"1001","status":"paid","amount_expected":"100000","amount_received":"100000","address":"bcrt1qexample","paid_at":"2026-08-24T12:05:00.000Z"}}';

$rcv = new Receivify('https://api.example.test', '', ['webhook_secrets' => [$current, $previous]]);

ok('verifies with the CURRENT secret', $rcv->verifyWebhook($body, sign($body, $current)));
ok('verifies with the PREVIOUS secret (rotation grace window)', $rcv->verifyWebhook($body, sign($body, $previous)));
ok('rejects an unknown secret', !$rcv->verifyWebhook($body, sign($body, 'whsec_attacker')));

$tampered = str_replace('"amount_received":"100000"', '"amount_received":"1"', $body);
ok('rejects a tampered body', !$rcv->verifyWebhook($tampered, sign($body, $current)));

ok('rejects a stale timestamp', !$rcv->verifyWebhook($body, sign($body, $current, time() - 3600)));
ok('rejects a far-future timestamp', !$rcv->verifyWebhook($body, sign($body, $current, time() + 3600)));
ok('accepts a stale timestamp when tolerance is disabled', $rcv->verifyWebhook($body, sign($body, $current, time() - 3600), null, 0));
ok('accepts a timestamp inside the window', $rcv->verifyWebhook($body, sign($body, $current, time() - 120)));

ok('rejects a missing header', !$rcv->verifyWebhook($body, ''));
ok('rejects a header with no v1', !$rcv->verifyWebhook($body, 't=' . time()));
ok('rejects a header with no t', !$rcv->verifyWebhook($body, 'v1=' . hash_hmac('sha256', 'x', $current)));
ok('rejects a non-numeric t', !$rcv->verifyWebhook($body, 't=abc,v1=' . hash_hmac('sha256', 'abc.' . $body, $current)));
ok('accepts an upper-case hex signature', $rcv->verifyWebhook($body, strtoupper(sign($body, $current))));

/* Re-encoding the JSON must NOT verify — this is the mistake the README shouts
   about. A real delivery can carry whitespace, escaped slashes or a key order
   json_encode() will not reproduce; any one of them changes the bytes. */
$spaced = '{"event": "invoice.paid", "sequence": 42, "created_at": "2026-08-24T12:05:00.000Z", "data": {"id": "11111111-1111-4111-8111-111111111111"}}';
$reencoded = json_encode(json_decode($spaced, true));
ok('the re-encode really does change the bytes', $reencoded !== $spaced);
ok('a re-encoded body does not verify (raw bytes only)', !$rcv->verifyWebhook($reencoded, sign($spaced, $current)));
ok('… while the raw body does', $rcv->verifyWebhook($spaced, sign($spaced, $current)));

/* a single string secret is still accepted and normalised to a list */
$single = new Receivify('https://api.example.test', '', $current);
ok('a single secret string is normalised to a list', $single->verifyWebhook($body, sign($body, $current)));
is_same('… and is stored as a one-element list', [$current], $single->getWebhookSecrets());

$listed = new Receivify('https://api.example.test', '', [$current, $previous]);
ok('a bare array of secrets is accepted as the third argument', $listed->verifyWebhook($body, sign($body, $previous)));

$noSecrets = new Receivify('https://api.example.test', '');
ok('no configured secret can never verify', !$noSecrets->verifyWebhook($body, sign($body, $current)));
ok('per-call secrets override the configured list', $noSecrets->verifyWebhook($body, sign($body, $current), [$current]));

/* ---------------------------------------------------------- parse + helpers */

section('Webhook envelope');

$event = $rcv->parseWebhook($body, sign($body, $current));
is_same('the envelope carries the event name', 'invoice.paid', Receivify::eventName($event));
is_same('the envelope carries the sequence', 42, Receivify::webhookSequence($event));
is_same('the dedupe key is event:sequence', 'invoice.paid:42', Receivify::dedupeKey($event));
$data = Receivify::webhookData($event);
is_same('data is the resource, not a nested `invoice` key', '1001', $data['reference']);
is_same('data amounts stay strings', '100000', $data['amount_received']);
ok('the old nested {invoice:{…}} shape is gone', !isset($event['invoice']));

throws('parseWebhook throws on a bad signature', 'ReceivifySignatureVerificationError', function () use ($rcv, $body) {
    $rcv->parseWebhook($body, sign($body, 'whsec_attacker'));
});

is_same('readWebhook returns null on a bad signature', null, $rcv->readWebhook($body, sign($body, 'whsec_attacker')));
$read = $rcv->readWebhook($body, sign($body, $current));
is_same('readWebhook returns the envelope', 'invoice.paid', $read['event']);

$forwardBody = '{"event":"forward.sent","sequence":9,"created_at":"2026-08-24T12:05:00.000Z","data":{"forward_id":"22222222-2222-4222-8222-222222222222","ticker":"btc","address_in":"bcrt1qdeposit","address_out":"bcrt1qpayout","txid_in":"aa","txid_out":"bb","value":"100000","value_forwarded":"99000","fee":"1000","confirmations":3,"pending":false}}';
$fwdEvent = $rcv->parseWebhook($forwardBody, sign($forwardBody, $current));
is_same('forward.* uses the same envelope', 'forward.sent', Receivify::eventName($fwdEvent));
is_same('forward data is CryptAPI-shaped', 'bcrt1qdeposit', Receivify::webhookData($fwdEvent)['address_in']);

section('Event classification');

is_same('invoice.paid releases the order', 'paid', Receivify::classifyEvent('invoice.paid'));
is_same('invoice.swept releases the order', 'paid', Receivify::classifyEvent('invoice.swept'));
is_same('sweep.sent releases the order', 'paid', Receivify::classifyEvent('sweep.sent'));
is_same('invoice.confirming is not payment', 'confirming', Receivify::classifyEvent('invoice.confirming'));
is_same('invoice.partial is an under-payment', 'underpaid', Receivify::classifyEvent('invoice.partial'));
is_same('invoice.partial_expired is a closed under-payment', 'underpaid_expired', Receivify::classifyEvent('invoice.partial_expired'));
is_same('invoice.expired cancels', 'expired', Receivify::classifyEvent('invoice.expired'));
is_same('invoice.cancelled cancels', 'cancelled', Receivify::classifyEvent('invoice.cancelled'));
is_same('invoice.failed needs a human', 'failed', Receivify::classifyEvent('invoice.failed'));
is_same('test changes nothing', 'test', Receivify::classifyEvent('test'));
is_same('forward.* is ignored by a shop', 'ignore', Receivify::classifyEvent('forward.confirmed'));
is_same('an event name from the future is ignored, not fatal', 'ignore', Receivify::classifyEvent('invoice.teleported'));

section('Shop order references');

$tok = Receivify::newStoreToken();
ok('a store token is 10 lowercase hex chars', (bool) preg_match('/^[a-f0-9]{10}$/', $tok));
ok('two store tokens differ', $tok !== Receivify::newStoreToken());
is_same('orderReference is <platform>-<token>-<id>', 'wc-' . $tok . '-11', Receivify::orderReference('wc', $tok, '11'));
is_same('this shop\'s reference maps back to its order', '11', Receivify::parseOrderReference('wc-' . $tok . '-11', 'wc', $tok));
is_same('an increment id with leading zeros survives', '000000011', Receivify::parseOrderReference('m2-' . $tok . '-000000011', 'm2', $tok));
ok('another shop\'s token is not ours', Receivify::parseOrderReference('wc-0123456789-11', 'wc', $tok) === null);
ok('another platform\'s reference is not ours', Receivify::parseOrderReference('ps-' . $tok . '-11', 'wc', $tok) === null);
is_same('a legacy bare reference still finds its order', '11', Receivify::parseOrderReference('11', 'wc', $tok));
ok('legacy can be refused', Receivify::parseOrderReference('11', 'wc', $tok, false) === null);
ok('an empty reference is nobody\'s', Receivify::parseOrderReference('', 'wc', $tok) === null);
ok('a null reference is nobody\'s', Receivify::parseOrderReference(null, 'wc', $tok) === null);

section('Base-unit maths');

is_same('satoshis to BTC', '0.12345678', Receivify::fromBaseUnits('12345678', 8));
is_same('one whole coin', '1.00000000', Receivify::fromBaseUnits('100000000', 8));
is_same('zero', '0.00000000', Receivify::fromBaseUnits('0', 8));
is_same('a wei value never becomes a float', '1000.000000000000000000', Receivify::fromBaseUnits('1000000000000000000000', 18));
is_same('zero decimals', '42', Receivify::fromBaseUnits('42', 0));
is_same('BTC to satoshis', '100000', Receivify::toBaseUnits('0.001', 8));
is_same('a whole number', '100000000', Receivify::toBaseUnits('1', 8));
is_same('trailing zeros beyond precision are fine', '100000000', Receivify::toBaseUnits('1.000000000000', 8));
throws('excess precision is an error, not a silent truncation', 'ReceivifyException', function () {
    Receivify::toBaseUnits('0.000000001', 8);
});
throws('a non-numeric amount is an error', 'ReceivifyException', function () {
    Receivify::fromBaseUnits('1.5', 8);
});

section('Safety');

$rcv = client(new FakeTransport([]));
$dump = print_r($rcv->__debugInfo(), true);
ok('the api key is never exposed by var_dump', strpos($dump, 'rcv_live_testkey') === false);

/* ------------------------------------------------------------------ result */

echo "\n" . $GLOBALS['rcv_pass'] . ' passed, ' . $GLOBALS['rcv_fail'] . " failed\n";
exit($GLOBALS['rcv_fail'] === 0 ? 0 : 1);
