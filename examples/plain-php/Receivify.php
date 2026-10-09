<?php
/**
 * Receivify PHP SDK — the reusable core every PHP cart integration wraps
 * (WooCommerce, OpenCart, PrestaShop, Magento, or a custom store).
 *
 * Implements the frozen v1 surface described by `openapi.yaml`:
 *
 *   invoices     createInvoice / getInvoice / listInvoices / cancelInvoice / bulkCancelInvoices
 *   forwarding   createAddress / getAddress / listAddresses / updateAddress / disableAddress
 *   catalogue    listTickers / listAssets
 *   webhooks     getWebhookSecretState / rotateWebhookSecret
 *                verifyWebhook / parseWebhook / readWebhook
 *
 * Three things that are easy to get wrong, and are therefore not optional here:
 *
 *   1. `verifyWebhook()` takes an ARRAY of secrets. Rotation keeps the previous
 *      secret valid for a grace window, so during a rotation TWO secrets are
 *      legitimately valid. A single string is still accepted and normalised to a
 *      one-element list.
 *   2. Verification runs over the RAW request body bytes (`php://input`). Never
 *      re-encode the JSON first — `json_encode(json_decode($body))` changes key
 *      order, spacing and escaping, and the HMAC will not match.
 *   3. Every money field is an exact-integer base-unit STRING (satoshis, wei, …).
 *      Never cast one to a float. Use `Receivify::fromBaseUnits()` for display.
 *
 * Requires PHP 7.4+ and ext-curl. No other dependencies. MIT.
 *
 * @see https://receivify.io
 */

/* ------------------------------------------------------------------ errors */

/** Base class for everything this SDK throws. */
class ReceivifyException extends Exception
{
}

/** The request never completed: DNS, TLS, connect or read timeout. */
class ReceivifyConnectionError extends ReceivifyException
{
}

/** A webhook did not verify (bad signature, stale timestamp, missing header). */
class ReceivifySignatureVerificationError extends ReceivifyException
{
}

/**
 * The API answered with `{ error: "<code>", detail: "<sentence>" }`.
 *
 * The raw `error` code is always preserved on `getErrorCode()`, so a code this
 * SDK has never heard of arrives as a plain `ReceivifyApiError` instead of
 * crashing an older client. Branch on the CODE, never on `detail` — the wording
 * is explicitly not part of the frozen contract.
 */
class ReceivifyApiError extends ReceivifyException
{
    /** @var string */
    protected $errorCode;
    /** @var string */
    protected $detail;
    /** @var int */
    protected $statusCode;
    /** @var array<string,string> */
    protected $responseHeaders;
    /** @var string */
    protected $responseBody;

    public function __construct(
        string $errorCode,
        string $detail,
        int $statusCode,
        array $responseHeaders = [],
        string $responseBody = ''
    ) {
        $label = $errorCode !== '' ? $errorCode : 'http_' . $statusCode;
        parent::__construct('Receivify [' . $label . '] ' . $detail, $statusCode);
        $this->errorCode = $errorCode;
        $this->detail = $detail;
        $this->statusCode = $statusCode;
        $this->responseHeaders = $responseHeaders;
        $this->responseBody = $responseBody;
    }

    /** The frozen `error` code, verbatim. `''` when the body was not conforming. */
    public function getErrorCode(): string
    {
        return $this->errorCode;
    }

    /** The human sentence. For logs and developers — do not branch on it. */
    public function getDetail(): string
    {
        return $this->detail;
    }

    public function getStatusCode(): int
    {
        return $this->statusCode;
    }

    /** @return array<string,string> lower-cased header names */
    public function getResponseHeaders(): array
    {
        return $this->responseHeaders;
    }

    public function getResponseBody(): string
    {
        return $this->responseBody;
    }

    /** True when simply trying again later is a sane response. */
    public function isRetryable(): bool
    {
        return in_array(
            $this->errorCode,
            ['rate_limited', 'internal', 'upstream_error', 'rate_unavailable'],
            true
        );
    }
}

/* 400 — malformed request: bad JSON, a missing field, a value that failed validation. */
class ReceivifyBadRequestError extends ReceivifyApiError
{
}
/* 400 — the forwarding ticker is unknown, misspelled, or not offered here. */
class ReceivifyBadTickerError extends ReceivifyApiError
{
}
/* 400 — the destination address is not valid for that ticker/chain. */
class ReceivifyBadDestinationError extends ReceivifyApiError
{
}
/* 400 — the asset is enabled but its settlement backend cannot accept it now. */
class ReceivifyAssetUnavailableError extends ReceivifyApiError
{
}
/* 400 — below the economic floor: forwarding would cost more than it earns. */
class ReceivifyAmountTooLowError extends ReceivifyApiError
{
}
/* 400 — the merchant account is suspended and cannot create new resources. */
class ReceivifySuspendedError extends ReceivifyApiError
{
}
/* 400 — a per-account quota for this resource is already at its maximum. */
class ReceivifyLimitExceededError extends ReceivifyApiError
{
}
/* 400 — the supplied two-factor (TOTP) code is not valid. */
class ReceivifyInvalidCodeError extends ReceivifyApiError
{
}
/* 400 — two-factor enrolment has not been started yet. */
class ReceivifyNoSetupError extends ReceivifyApiError
{
}
/* 400 — two-factor authentication is not enabled on this account. */
class ReceivifyNotEnabledError extends ReceivifyApiError
{
}
/* 400 — no registered, active webhook endpoint matched the request. */
class ReceivifyNoTargetError extends ReceivifyApiError
{
}
/* 400 — a single-use confirmation token is invalid, expired or already spent. */
class ReceivifyInvalidTokenError extends ReceivifyApiError
{
}
/* 401 — missing, malformed, revoked or unknown API key / session cookie. */
class ReceivifyUnauthorizedError extends ReceivifyApiError
{
}
/* 401 — an inbound provider webhook failed HMAC verification. */
class ReceivifyBadSignatureError extends ReceivifyApiError
{
}
/* 403 — self-serve signup is turned off on this deployment. */
class ReceivifySignupDisabledError extends ReceivifyApiError
{
}
/* 404 — no such resource, or it does not belong to the caller. */
class ReceivifyNotFoundError extends ReceivifyApiError
{
}
/* 409 — a resource with that unique identity already exists. */
class ReceivifyAlreadyExistsError extends ReceivifyApiError
{
}
/* 409 — the feature being enabled is already on. */
class ReceivifyAlreadyEnabledError extends ReceivifyApiError
{
}
/* 409 — the resource exists but its state forbids this action. */
class ReceivifyNotAllowedError extends ReceivifyApiError
{
}
/* 500 — an unhandled server error. Safe to retry. */
class ReceivifyInternalError extends ReceivifyApiError
{
}
/* 502 — a node or payment provider we depend on failed. Retryable. */
class ReceivifyUpstreamError extends ReceivifyApiError
{
}
/* 503 — no fresh FX rate is available to price the request. Retryable. */
class ReceivifyRateUnavailableError extends ReceivifyApiError
{
}

/** 429 — back off using `Retry-After` or `X-RateLimit-Reset`. */
class ReceivifyRateLimitedError extends ReceivifyApiError
{
    /** Seconds to wait, from `Retry-After`. `null` when the header was absent. */
    public function getRetryAfter(): ?int
    {
        $h = $this->responseHeaders;
        if (isset($h['retry-after']) && is_numeric($h['retry-after'])) {
            return (int) $h['retry-after'];
        }
        return null;
    }
}

/**
 * One page of a list endpoint: `{ data, has_more, next_cursor }`.
 *
 * `has_more` is a fact the server computed by reading `limit + 1` rows — never
 * infer it from `count($data) === $limit`.
 */
class ReceivifyList implements IteratorAggregate, Countable
{
    /** @var array<int,array> the page, newest first */
    public $data = [];
    /** @var bool */
    public $has_more = false;
    /** @var string|null pass back as `cursor` to fetch the next page */
    public $next_cursor = null;
    /** @var array the decoded envelope exactly as it arrived */
    public $raw = [];

    /** @param mixed $decoded */
    public static function fromResponse($decoded): ReceivifyList
    {
        $list = new self();
        if (!is_array($decoded)) {
            return $list;
        }
        $list->raw = $decoded;
        $list->data = isset($decoded['data']) && is_array($decoded['data']) ? array_values($decoded['data']) : [];
        $list->has_more = !empty($decoded['has_more']);
        $cursor = isset($decoded['next_cursor']) ? $decoded['next_cursor'] : null;
        $list->next_cursor = (is_string($cursor) && $cursor !== '') ? $cursor : null;
        return $list;
    }

    public function getIterator(): Iterator
    {
        return new ArrayIterator($this->data);
    }

    public function count(): int
    {
        return count($this->data);
    }

    /** First item, or null on an empty page. */
    public function first(): ?array
    {
        return isset($this->data[0]) && is_array($this->data[0]) ? $this->data[0] : null;
    }
}

/* ------------------------------------------------------------------ client */

class Receivify
{
    const VERSION = '2.0.0';

    /** Default replay window for webhook timestamps, in seconds. */
    const DEFAULT_TOLERANCE_SECONDS = 300;

    /* Event names — the frozen `EventName` enum. Receivers MUST ignore unknown ones. */
    const EVENT_INVOICE_CONFIRMING = 'invoice.confirming';
    const EVENT_INVOICE_PARTIAL = 'invoice.partial';
    const EVENT_INVOICE_PAID = 'invoice.paid';
    const EVENT_INVOICE_SWEPT = 'invoice.swept';
    const EVENT_INVOICE_EXPIRED = 'invoice.expired';
    const EVENT_INVOICE_PARTIAL_EXPIRED = 'invoice.partial_expired';
    const EVENT_INVOICE_CANCELLED = 'invoice.cancelled';
    const EVENT_INVOICE_FAILED = 'invoice.failed';
    const EVENT_SWEEP_SENT = 'sweep.sent';
    const EVENT_FORWARD_DETECTED = 'forward.detected';
    const EVENT_FORWARD_CONFIRMED = 'forward.confirmed';
    const EVENT_FORWARD_DEFERRED = 'forward.deferred';
    const EVENT_FORWARD_SENT = 'forward.sent';
    const EVENT_FORWARD_REVERSED = 'forward.reversed';
    const EVENT_FORWARD_FAILED = 'forward.failed';
    const EVENT_TEST = 'test';

    /** @var string */
    private $apiUrl;
    /** @var string */
    private $apiKey;
    /** @var array<int,string> current secret first, previous secret second */
    private $webhookSecrets = [];
    /** @var int */
    private $timeout = 30;
    /** @var int */
    private $connectTimeout = 10;
    /** @var int how many times a SAFE request is retried after the first attempt */
    private $maxRetries = 2;
    /** @var float upper bound on any single backoff sleep, in seconds */
    private $maxRetryDelay = 20.0;
    /** @var int */
    private $toleranceSeconds = self::DEFAULT_TOLERANCE_SECONDS;
    /** @var callable|null injected HTTP layer; tests use this to stay offline */
    private $transport = null;
    /** @var string */
    private $userAgent;

    /**
     * @param string       $apiUrl Base URL of your Receivify instance, e.g. https://receivify.io
     * @param string       $apiKey `rcv_live_…`. Server-side only — never ship it to a browser.
     * @param string|array $opts   For backwards compatibility this may be a single webhook
     *                             secret string, or a list of secret strings. Preferred form is
     *                             an options array:
     *                               webhook_secrets    string[] current first, then previous
     *                               webhook_secret     string   shorthand for a one-element list
     *                               timeout            int      seconds, default 30
     *                               connect_timeout    int      seconds, default 10
     *                               max_retries        int      default 2 (safe requests only)
     *                               tolerance_seconds  int      webhook replay window, default 300
     *                               user_agent         string   appended to `receivify-php/<ver>`
     *                               transport          callable inject the HTTP layer
     */
    public function __construct(string $apiUrl, string $apiKey = '', $opts = [])
    {
        $this->apiUrl = rtrim($apiUrl, '/');
        $this->apiKey = $apiKey;
        $this->userAgent = 'receivify-php/' . self::VERSION;

        if (is_string($opts)) {
            $opts = ['webhook_secret' => $opts];
        } elseif (is_array($opts) && $opts !== [] && array_keys($opts) === range(0, count($opts) - 1)) {
            $opts = ['webhook_secrets' => $opts];
        } elseif (!is_array($opts)) {
            $opts = [];
        }

        $secrets = [];
        if (isset($opts['webhook_secrets'])) {
            $secrets = array_merge($secrets, self::normaliseSecrets($opts['webhook_secrets']));
        }
        if (isset($opts['webhook_secret'])) {
            $secrets = array_merge($secrets, self::normaliseSecrets($opts['webhook_secret']));
        }
        $this->webhookSecrets = self::normaliseSecrets($secrets);

        if (isset($opts['timeout'])) {
            $this->timeout = max(1, (int) $opts['timeout']);
        }
        if (isset($opts['connect_timeout'])) {
            $this->connectTimeout = max(1, (int) $opts['connect_timeout']);
        }
        if (isset($opts['max_retries'])) {
            $this->maxRetries = max(0, (int) $opts['max_retries']);
        }
        if (isset($opts['max_retry_delay'])) {
            $this->maxRetryDelay = max(0.0, (float) $opts['max_retry_delay']);
        }
        if (isset($opts['tolerance_seconds'])) {
            $this->toleranceSeconds = max(0, (int) $opts['tolerance_seconds']);
        }
        if (!empty($opts['user_agent'])) {
            $this->userAgent .= ' ' . trim((string) $opts['user_agent']);
        }
        if (isset($opts['transport']) && is_callable($opts['transport'])) {
            $this->transport = $opts['transport'];
        }
    }

    /**
     * Replace the HTTP layer. The callable receives
     * `['method','url','headers'=>[name=>value],'body'=>?string,'timeout'=>int]`
     * and must return `['status'=>int,'headers'=>[lowercase-name=>value],'body'=>string]`.
     * Used by the offline test suite; also handy for platforms with their own
     * HTTP stack (WordPress `wp_remote_request`, Magento's `Curl` client, …).
     */
    public function setTransport(?callable $transport): self
    {
        $this->transport = $transport;
        return $this;
    }

    /** Replace the secret list at runtime, e.g. straight after a rotation. */
    public function setWebhookSecrets($secrets): self
    {
        $this->webhookSecrets = self::normaliseSecrets($secrets);
        return $this;
    }

    /** @return array<int,string> */
    public function getWebhookSecrets(): array
    {
        return $this->webhookSecrets;
    }

    /** Keeps the API key out of `var_dump()` / stack traces. */
    public function __debugInfo(): array
    {
        return [
            'apiUrl' => $this->apiUrl,
            'apiKey' => $this->apiKey === '' ? '' : '***redacted***',
            'webhookSecrets' => count($this->webhookSecrets) . ' configured',
            'timeout' => $this->timeout,
        ];
    }

    /* -------------------------------------------------------------- invoices */

    /**
     * Create an invoice. Always 201, including on both replay channels — an
     * `Idempotency-Key` replay and a `reference` this merchant has already used
     * both return the EXISTING invoice rather than minting a second one.
     *
     * Preferred form:
     *   $rcv->createInvoice([
     *     'amount_fiat'  => '25.00',       // decimal string; prefer a string over a float
     *     'currency'     => 'USD',
     *     'asset'        => 'btc',
     *     'reference'    => (string) $orderId,
     *     'callback_url' => 'https://store.example/receivify-webhook',
     *     'metadata'     => ['order_id' => $orderId],
     *     // optional: true = payer covers the commission; omit to use the account default
     *     'add_fee_ontop' => true,
     *   ], ['idempotency_key' => 'order-' . $orderId]);
     *
     * Legacy form (kept working): createInvoice($amountFiat, $currency, $opts).
     *
     * Exactly one amount field is required; they are tried `amount_fiat` (+
     * `currency`), `amount`, `amount_sats`, `amount_btc` and the first present
     * one wins.
     *
     * @param array|float|int|string $params
     * @param string|array|null      $currency legacy positional form only; when `$params` is an
     *                                         array this may be the options array instead
     * @param array                  $opts     idempotency_key
     */
    public function createInvoice($params, $currency = null, array $opts = []): array
    {
        if (is_array($params) && is_array($currency)) {
            // createInvoice([...], ['idempotency_key' => …]) — the natural call.
            $opts = $currency;
            $currency = null;
        }
        if (!is_array($params)) {
            $legacy = $opts;
            $opts = [];
            if (isset($legacy['idempotency_key'])) {
                $opts['idempotency_key'] = $legacy['idempotency_key'];
                unset($legacy['idempotency_key']);
            }
            $params = array_merge($legacy, [
                'amount_fiat' => $params,
                'currency' => $currency === null ? 'USD' : strtoupper((string) $currency),
            ]);
            if (!isset($params['asset'])) {
                $params['asset'] = 'btc';
            }
        }

        $idempotencyKey = self::takeIdempotencyKey($params, $opts);
        $body = self::pick($params, [
            'asset', 'chain', 'amount_fiat', 'currency', 'amount',
            'amount_sats', 'amount_btc', 'reference', 'callback_url', 'metadata',
            'add_fee_ontop',
        ]);

        // The API accepts ONLY a real JSON boolean here (a string is 400 bad_request),
        // and omitting it is not the same as false: omitted defers to the account default.
        if (isset($body['add_fee_ontop']) && !is_bool($body['add_fee_ontop'])) {
            throw new ReceivifyException('Receivify createInvoice: `add_fee_ontop` must be a bool.');
        }

        if (isset($body['currency'])) {
            $body['currency'] = strtoupper((string) $body['currency']);
        }
        foreach (['amount_fiat', 'amount', 'amount_sats', 'amount_btc'] as $k) {
            if (isset($body[$k])) {
                $body[$k] = self::numberToString($body[$k]);
            }
        }
        if (isset($body['reference'])) {
            $body['reference'] = (string) $body['reference'];
        }

        $res = $this->send('POST', '/api/v1/invoices', [
            'json' => $body,
            'idempotency_key' => $idempotencyKey,
        ]);
        return $this->decodeObject($res);
    }

    /**
     * The authoritative current state of an invoice. Poll this rather than
     * trusting a webhook replay, which re-sends a frozen snapshot of the moment
     * the event happened.
     */
    public function getInvoice(string $id): array
    {
        $res = $this->send('GET', '/api/v1/invoices/' . rawurlencode($id));
        return $this->decodeObject($res);
    }

    /**
     * One page of invoices, newest first.
     *
     * @param array $params limit (1..200), cursor, status
     */
    public function listInvoices(array $params = []): ReceivifyList
    {
        $res = $this->send('GET', '/api/v1/invoices', [
            'query' => self::pick($params, ['limit', 'cursor', 'status']),
        ]);
        return ReceivifyList::fromResponse($this->decodeObject($res));
    }

    /**
     * Every invoice, following `next_cursor` until `has_more` is false.
     *
     *   foreach ($rcv->autoPagingInvoices(['status' => 'paid']) as $invoice) { … }
     *
     * @return Generator<int,array>
     */
    public function autoPagingInvoices(array $params = []): Generator
    {
        return $this->autoPage([$this, 'listInvoices'], $params);
    }

    /**
     * Cancel a pending, unpaid invoice. NOT idempotent — a second call on an
     * already-cancelled invoice throws `ReceivifyNotAllowedError` (409). A
     * payment that lands mid-request wins, and the cancel fails.
     */
    public function cancelInvoice(string $id): array
    {
        $res = $this->send('POST', '/api/v1/invoices/' . rawurlencode($id) . '/cancel', [
            'retryable' => false,
        ]);
        return $this->decodeObject($res);
    }

    /**
     * Bulk-cancel. Naturally idempotent: the filter only matches pending, unpaid
     * invoices of yours, so a stray or already-paid id is skipped. Compare
     * `requested` with `cancelled` to see how many were eligible.
     *
     * @param array<int,string> $ids max 500
     */
    public function bulkCancelInvoices(array $ids): array
    {
        $res = $this->send('POST', '/api/v1/invoices/bulk', [
            'json' => ['ids' => array_values($ids), 'action' => 'cancel'],
            'retryable' => false,
        ]);
        return $this->decodeObject($res);
    }

    /**
     * Public invoice status — no API key, a deliberately narrow projection (no
     * reference, metadata or asset). Safe to poll from a thank-you page.
     * Server-side code should prefer `getInvoice()`.
     */
    public function getPublicInvoice(string $id): array
    {
        $res = $this->send('GET', '/api/public/invoices/' . rawurlencode($id), ['auth' => false]);
        return $this->decodeObject($res);
    }

    /* ------------------------------------------------------------ forwarding */

    /**
     * Create (or return) a forwarding address — a standing deposit address that
     * sweeps to `destination`.
     *
     * Deterministic: the identity is `(merchant, asset, destination, callback)`,
     * so repeating the call returns the same address (200) instead of burning a
     * second HD index (201). Single-session BTCPay tickers (ltc, doge, bch, xmr,
     * zec) are never deduped and always mint a fresh, expiring address.
     *
     * @param array $params ticker (aliases coin/currency/asset), destination
     *                      (address/to/payout_address), callback (callback_url/
     *                      webhook/webhook_url/url), confirmations (confs), metadata
     * @param array $opts   idempotency_key
     */
    public function createAddress(array $params, array $opts = []): array
    {
        $idempotencyKey = self::takeIdempotencyKey($params, $opts);

        $body = [];
        $ticker = self::firstOf($params, ['ticker', 'coin', 'currency', 'asset']);
        $destination = self::firstOf($params, ['destination', 'address', 'to', 'payout_address']);
        $callback = self::firstOf($params, ['callback', 'callback_url', 'webhook', 'webhook_url', 'url']);
        $confirmations = self::firstOf($params, ['confirmations', 'confs']);

        if ($ticker === null || trim((string) $ticker) === '') {
            throw new ReceivifyException('Receivify createAddress: `ticker` is required.');
        }
        if ($destination === null || trim((string) $destination) === '') {
            throw new ReceivifyException('Receivify createAddress: `destination` is required.');
        }

        $body['ticker'] = strtolower(trim((string) $ticker));
        $body['destination'] = trim((string) $destination);
        if ($callback !== null && $callback !== '') {
            $body['callback'] = (string) $callback;
        }
        if ($confirmations !== null && $confirmations !== '') {
            $body['confirmations'] = (int) $confirmations;
        }
        if (isset($params['metadata'])) {
            $body['metadata'] = $params['metadata'];
        }

        $res = $this->send('POST', '/api/v1/address', [
            'json' => $body,
            'idempotency_key' => $idempotencyKey,
        ]);
        return $this->decodeObject($res);
    }

    /**
     * A forwarding address with its payment and callback log. `payments` is
     * capped at 200 and `callbacks` at 50 — `payments_has_more` /
     * `callbacks_has_more` say when the view is truncated. Neither is paginated.
     */
    public function getAddress(string $id): array
    {
        $res = $this->send('GET', '/api/v1/address/' . rawurlencode($id));
        return $this->decodeObject($res);
    }

    /**
     * One page of forwarding addresses, newest first. Items are the plain
     * `Forward` shape — without the `payments` / `callbacks` detail arrays.
     *
     * @param array $params limit, cursor, status (active|paused|disabled|expired), ticker
     */
    public function listAddresses(array $params = []): ReceivifyList
    {
        $res = $this->send('GET', '/api/v1/address', [
            'query' => self::pick($params, ['limit', 'cursor', 'status', 'ticker']),
        ]);
        return ReceivifyList::fromResponse($this->decodeObject($res));
    }

    /** @return Generator<int,array> */
    public function autoPagingAddresses(array $params = []): Generator
    {
        return $this->autoPage([$this, 'listAddresses'], $params);
    }

    /**
     * The merchant kill switch. Only `status` (`active` <-> `paused`) and
     * `callback_url` are mutable.
     *
     * `destination` and `ticker` are IMMUTABLE — repointing a live deposit
     * address is exactly what a leaked key would be used for — and the API
     * refuses the WHOLE patch with 409 if one is present. This SDK fails fast
     * with a `ReceivifyException` before the request is sent.
     *
     * @param array $params status, callback_url (aliases callback/webhook/webhook_url/url)
     */
    public function updateAddress(string $id, array $params): array
    {
        $immutable = ['destination', 'address', 'to', 'payout_address', 'ticker', 'coin', 'currency', 'asset', 'chain', 'provider'];
        foreach ($immutable as $field) {
            if (array_key_exists($field, $params)) {
                throw new ReceivifyException(
                    'Receivify updateAddress: `' . $field . '` is immutable. Only `status` and '
                    . '`callback_url` can be changed; create a new forward instead.'
                );
            }
        }

        $body = [];
        if (array_key_exists('status', $params)) {
            $status = strtolower(trim((string) $params['status']));
            if (!in_array($status, ['active', 'paused'], true)) {
                throw new ReceivifyException(
                    'Receivify updateAddress: `status` must be "active" or "paused". '
                    . 'Use disableAddress() for a soft delete; `expired` is set by the system.'
                );
            }
            $body['status'] = $status;
        }
        $callback = self::firstOf($params, ['callback_url', 'callback', 'webhook', 'webhook_url', 'url'], true);
        if ($callback !== self::MISSING) {
            $body['callback_url'] = ($callback === null || $callback === '') ? null : (string) $callback;
        }
        if ($body === []) {
            throw new ReceivifyException('Receivify updateAddress: nothing to update — pass `status` and/or `callback_url`.');
        }

        $res = $this->send('PATCH', '/api/v1/address/' . rawurlencode($id), [
            'json' => $body,
            'retryable' => false,
        ]);
        return $this->decodeObject($res);
    }

    /** Kill switch: stop crediting NEW deposits. Money already credited still forwards. */
    public function pauseAddress(string $id): array
    {
        return $this->updateAddress($id, ['status' => 'paused']);
    }

    /** Undo a pause. */
    public function resumeAddress(string $id): array
    {
        return $this->updateAddress($id, ['status' => 'active']);
    }

    /**
     * Soft delete — sets `status: "disabled"`. The row is never removed, because
     * money can still arrive at a deposit address after you stop using it and a
     * deleted row would make it unattributable. Idempotent.
     */
    public function disableAddress(string $id): array
    {
        $res = $this->send('DELETE', '/api/v1/address/' . rawurlencode($id));
        return $this->decodeObject($res);
    }

    /* ------------------------------------------------------------- catalogue */

    /**
     * Every `(asset, chain)` pair this deployment offers for forwarding. Public,
     * and a finite catalogue returned as ONE page — `has_more` is always false.
     */
    public function listTickers(): ReceivifyList
    {
        $res = $this->send('GET', '/api/v1/tickers', ['auth' => false]);
        return ReceivifyList::fromResponse($this->decodeObject($res));
    }

    /**
     * The coins currently enabled for invoicing. Public. DISPLAY metadata: `min`
     * may be the literal word "network" and `network_fee` is English prose —
     * neither can be parsed as a number.
     */
    public function listAssets(): ReceivifyList
    {
        $res = $this->send('GET', '/api/v1/assets', ['auth' => false]);
        return ReceivifyList::fromResponse($this->decodeObject($res));
    }

    /* -------------------------------------------------------------- webhooks */

    /**
     * Rotation metadata for the webhook signing secret — no secret material.
     * `prev_active` is the live answer to "should my receiver still be trying
     * the old secret?".
     */
    public function getWebhookSecretState(): array
    {
        $res = $this->send('GET', '/api/v1/webhooks/secret');
        return $this->decodeObject($res);
    }

    /**
     * Mint a new signing secret. The current one moves to the previous slot with
     * a grace window (24h by default) and the new secret is returned ONCE — it is
     * never readable again.
     *
     * Receivify keeps SIGNING with the current secret only; the grace window is
     * an instruction to your receiver. Until `prev_expires_at`, verify against
     * BOTH — which is exactly why `verifyWebhook()` takes a list:
     *
     *   $r = $rcv->rotateWebhookSecret(['idempotency_key' => 'rotate-2026-08']);
     *   if ($r['secret'] !== null) {           // null on an Idempotency-Key replay
     *       store_secrets([$r['secret'], $old]);
     *   }
     *
     * @param array $opts idempotency_key
     */
    public function rotateWebhookSecret(array $opts = []): array
    {
        $params = [];
        $idempotencyKey = self::takeIdempotencyKey($params, $opts);
        $res = $this->send('POST', '/api/v1/webhooks/secret/rotate', [
            'idempotency_key' => $idempotencyKey,
            'retryable' => $idempotencyKey !== '',
        ]);
        return $this->decodeObject($res);
    }

    /**
     * Verify a signed webhook.
     *
     *   X-Receivify-Signature: t=<unix seconds>,v1=<hex>
     *   v1 = HMAC-SHA256(secret, "<t>.<rawBody>")
     *
     * `$rawBody` MUST be the raw request bytes — `file_get_contents('php://input')`
     * — never `json_encode(json_decode($body))`. Re-encoding changes key order,
     * spacing and escaping and the HMAC will not match.
     *
     * @param string            $rawBody
     * @param string            $signatureHeader
     * @param string|array|null $secrets          a list (current, previous) or a single
     *                                            string; null uses the configured list
     * @param int|null          $toleranceSeconds replay window, default 300; 0 disables
     */
    public function verifyWebhook(string $rawBody, string $signatureHeader, $secrets = null, ?int $toleranceSeconds = null): bool
    {
        $list = self::normaliseSecrets($secrets === null ? $this->webhookSecrets : $secrets);
        if ($list === []) {
            return false;
        }
        $parsed = self::parseSignatureHeader($signatureHeader);
        if ($parsed === null) {
            return false;
        }

        $tolerance = $toleranceSeconds === null ? $this->toleranceSeconds : max(0, (int) $toleranceSeconds);
        if ($tolerance > 0) {
            $skew = time() - $parsed['timestamp'];
            if ($skew > $tolerance || $skew < -$tolerance) {
                return false;
            }
        }

        $signed = $parsed['t'] . '.' . $rawBody;
        $ok = false;
        foreach ($list as $secret) {
            $expected = hash_hmac('sha256', $signed, $secret);
            foreach ($parsed['signatures'] as $candidate) {
                if (hash_equals($expected, $candidate)) {
                    $ok = true;
                }
            }
        }
        return $ok;
    }

    /**
     * Verify AND decode. Returns the envelope:
     *
     *   [ 'event'      => 'invoice.paid',            // switch on this
     *     'sequence'   => 12,                        // dedupe on (event, sequence)
     *     'created_at' => '2026-08-24T12:05:00.000Z',
     *     'data'       => [ … the resource … ] ]
     *
     * `data` is the invoice projection for `invoice.*`, `sweep.sent` and `test`,
     * and the forward payment for `forward.*`.
     *
     * @throws ReceivifySignatureVerificationError when the signature or timestamp fails
     * @throws ReceivifyException                  when the body is not a JSON envelope
     */
    public function parseWebhook(string $rawBody, string $signatureHeader, $secrets = null, ?int $toleranceSeconds = null): array
    {
        if (!$this->verifyWebhook($rawBody, $signatureHeader, $secrets, $toleranceSeconds)) {
            throw new ReceivifySignatureVerificationError(
                'Receivify webhook signature verification failed. Check that you passed the RAW '
                . 'request body, that the secret list is current (a rotation keeps the previous '
                . 'secret valid for its grace window), and that the server clock is not skewed.'
            );
        }
        $event = json_decode($rawBody, true);
        if (!is_array($event) || !isset($event['event']) || !is_string($event['event'])) {
            throw new ReceivifyException('Receivify webhook body is not a valid event envelope.');
        }
        if (!isset($event['data']) || !is_array($event['data'])) {
            $event['data'] = [];
        }
        $event['sequence'] = isset($event['sequence']) ? (int) $event['sequence'] : 0;
        return $event;
    }

    /**
     * Read + verify the CURRENT request's webhook. Returns the envelope, or null
     * when the signature does not verify (so a caller can answer 401 without
     * catching anything).
     *
     * Reads `php://input` and `HTTP_X_RECEIVIFY_SIGNATURE` unless you pass them.
     */
    public function readWebhook(?string $rawBody = null, ?string $signatureHeader = null, $secrets = null, ?int $toleranceSeconds = null): ?array
    {
        if ($rawBody === null) {
            $rawBody = (string) file_get_contents('php://input');
        }
        if ($signatureHeader === null) {
            $signatureHeader = isset($_SERVER['HTTP_X_RECEIVIFY_SIGNATURE'])
                ? (string) $_SERVER['HTTP_X_RECEIVIFY_SIGNATURE']
                : '';
        }
        try {
            return $this->parseWebhook($rawBody, $signatureHeader, $secrets, $toleranceSeconds);
        } catch (ReceivifyException $e) {
            return null;
        }
    }

    /* ------------------------------------------------- webhook static helpers */

    /** The event name, or `''`. Always keep a default branch: names are additive. */
    public static function eventName(array $event): string
    {
        return isset($event['event']) && is_string($event['event']) ? $event['event'] : '';
    }

    /** The `data` object carried by the envelope. */
    public static function webhookData(array $event): array
    {
        return isset($event['data']) && is_array($event['data']) ? $event['data'] : [];
    }

    /**
     * Delivery is at-least-once and can arrive out of order. `sequence` is a
     * globally monotonic counter allocated at enqueue time — it is NOT per
     * resource, so consecutive events for one invoice are not consecutive
     * numbers. Use `(event, sequence)` to de-duplicate.
     */
    public static function webhookSequence(array $event): int
    {
        return isset($event['sequence']) ? (int) $event['sequence'] : 0;
    }

    /**
     * The de-duplication key for one delivery: `"<event>:<sequence>"`. There is
     * no separate event id. Store this and drop a repeat.
     */
    public static function dedupeKey(array $event): string
    {
        return self::eventName($event) . ':' . self::webhookSequence($event);
    }

    /* ------------------------------------------------- shop order references */

    /**
     * A random per-install token for {@see orderReference()}. Generate it ONCE
     * per store install, persist it, and never change it: invoices created
     * under the old token would stop matching their orders.
     */
    public static function newStoreToken(): string
    {
        return bin2hex(random_bytes(5));
    }

    /**
     * The invoice `reference` for a shop order: `<platform>-<storeToken>-<orderId>`.
     *
     * `reference` de-duplicates per MERCHANT, not per shop. A bare order id
     * ("11") collides as soon as two shops share one Receivify account, or a
     * reinstalled shop restarts its order numbers. The server then either
     * replays the other order's invoice or refuses with `409 already_exists`.
     * The per-install token makes the reference unique to this shop.
     *
     * @param string $platform   short shop code, e.g. `wc`, `m2`, `ps`, `oc`
     * @param string $storeToken from {@see newStoreToken()}, persisted per install
     */
    public static function orderReference(string $platform, string $storeToken, string $orderId): string
    {
        return $platform . '-' . $storeToken . '-' . $orderId;
    }

    /**
     * Map a webhook's `data.reference` back to THIS shop's order id.
     *
     * Returns null when the reference belongs to another shop, so a delivery
     * meant for a previous install can never touch an unrelated order here.
     * A bare reference with no `<platform>-<token>-` prefix is returned as-is
     * when `$acceptLegacy` is true, so invoices created before references were
     * prefixed still find their order.
     */
    public static function parseOrderReference(
        ?string $reference,
        string $platform,
        string $storeToken,
        bool $acceptLegacy = true
    ): ?string {
        $reference = (string) $reference;
        if ($reference === '') {
            return null;
        }
        if (preg_match('/^([a-z0-9]+)-([a-f0-9]{6,64})-(.+)$/', $reference, $m)) {
            if ($m[1] !== $platform || $storeToken === '' || !hash_equals($storeToken, $m[2])) {
                return null;   // another shop's (or another platform's) invoice
            }
            return $m[3];
        }
        return $acceptLegacy ? $reference : null;
    }

    /**
     * Map an event name onto what a shop should DO. Every cart plugin in this
     * repo maps through here so the platforms agree.
     *
     * paid              money is in — release the order
     * confirming        seen on-chain, not final yet — note it, do not release
     * underpaid         less than expected arrived; the window is still open
     * underpaid_expired the window closed on an under-payment — refund/contact
     * expired           nothing arrived before the window closed
     * cancelled         the invoice was cancelled
     * failed            settlement failed — needs a human
     * test              POST /api/v1/webhooks/test — acknowledge, change nothing
     * ignore            forward.* or an event name this SDK predates
     */
    public static function classifyEvent(string $eventName): string
    {
        switch ($eventName) {
            case self::EVENT_INVOICE_PAID:
            case self::EVENT_INVOICE_SWEPT:
            case self::EVENT_SWEEP_SENT:
                return 'paid';
            case self::EVENT_INVOICE_CONFIRMING:
                return 'confirming';
            case self::EVENT_INVOICE_PARTIAL:
                return 'underpaid';
            case self::EVENT_INVOICE_PARTIAL_EXPIRED:
                return 'underpaid_expired';
            case self::EVENT_INVOICE_EXPIRED:
                return 'expired';
            case self::EVENT_INVOICE_CANCELLED:
                return 'cancelled';
            case self::EVENT_INVOICE_FAILED:
                return 'failed';
            case self::EVENT_TEST:
                return 'test';
            default:
                return 'ignore';
        }
    }

    /* --------------------------------------------------------------- helpers */

    /**
     * A forward's canonical id. `forward_id` is a deprecated but permanent alias
     * carrying the identical value — read `id`.
     */
    public static function forwardId(array $forward): string
    {
        if (isset($forward['id']) && is_string($forward['id'])) {
            return $forward['id'];
        }
        if (isset($forward['forward_id']) && is_string($forward['forward_id'])) {
            return $forward['forward_id'];
        }
        return '';
    }

    /**
     * Exact base units -> a display decimal string, without ever touching a
     * float. `fromBaseUnits('12345678', 8) === '0.12345678'`.
     */
    public static function fromBaseUnits(string $baseUnits, int $decimals): string
    {
        $negative = strncmp($baseUnits, '-', 1) === 0;
        $digits = ltrim($negative ? substr($baseUnits, 1) : $baseUnits, '+');
        if ($digits === '' || preg_match('/^[0-9]+$/', $digits) !== 1) {
            throw new ReceivifyException('Receivify fromBaseUnits: "' . $baseUnits . '" is not an integer string.');
        }
        if ($decimals <= 0) {
            $whole = ltrim($digits, '0');
            if ($whole === '') {
                $whole = '0';
            }
            return ($negative && $whole !== '0' ? '-' : '') . $whole;
        }
        $digits = str_pad($digits, $decimals + 1, '0', STR_PAD_LEFT);
        $whole = substr($digits, 0, strlen($digits) - $decimals);
        $frac = substr($digits, strlen($digits) - $decimals);
        $whole = ltrim($whole, '0');
        if ($whole === '') {
            $whole = '0';
        }
        return ($negative ? '-' : '') . $whole . '.' . $frac;
    }

    /**
     * A display decimal string -> exact base units, without touching a float.
     * `toBaseUnits('0.001', 8) === '100000'`. Extra fraction digits are an error
     * rather than a silent truncation.
     */
    public static function toBaseUnits(string $decimal, int $decimals): string
    {
        $value = trim($decimal);
        $negative = strncmp($value, '-', 1) === 0;
        if ($negative || strncmp($value, '+', 1) === 0) {
            $value = substr($value, 1);
        }
        if (preg_match('/^([0-9]*)(?:\.([0-9]*))?$/', $value, $m) !== 1 || ($m[1] === '' && !isset($m[2]))) {
            throw new ReceivifyException('Receivify toBaseUnits: "' . $decimal . '" is not a decimal number.');
        }
        $whole = $m[1] === '' ? '0' : $m[1];
        $frac = isset($m[2]) ? $m[2] : '';
        if (strlen($frac) > $decimals) {
            if (rtrim(substr($frac, $decimals), '0') !== '') {
                throw new ReceivifyException(
                    'Receivify toBaseUnits: "' . $decimal . '" has more precision than ' . $decimals . ' decimals allow.'
                );
            }
            $frac = substr($frac, 0, $decimals);
        }
        $frac = str_pad($frac, $decimals, '0', STR_PAD_RIGHT);
        $out = ltrim($whole . $frac, '0');
        if ($out === '') {
            $out = '0';
        }
        return ($negative && $out !== '0' ? '-' : '') . $out;
    }

    /* ------------------------------------------------------------- internals */

    /** Sentinel for "key not present", distinct from a null value. */
    const MISSING = "\0receivify-missing\0";

    /**
     * @param callable $lister
     * @return Generator<int,array>
     */
    private function autoPage(callable $lister, array $params): Generator
    {
        $seen = [];
        while (true) {
            /** @var ReceivifyList $page */
            $page = call_user_func($lister, $params);
            foreach ($page->data as $item) {
                yield $item;
            }
            if (!$page->has_more || $page->next_cursor === null) {
                return;
            }
            if (isset($seen[$page->next_cursor])) {
                throw new ReceivifyException('Receivify pagination: the server repeated a cursor — refusing to loop forever.');
            }
            $seen[$page->next_cursor] = true;
            $params['cursor'] = $page->next_cursor;
        }
    }

    /**
     * @param array $options query, json, auth, idempotency_key, retryable
     * @return array{status:int,headers:array<string,string>,body:string}
     */
    private function send(string $method, string $path, array $options = []): array
    {
        $auth = !array_key_exists('auth', $options) || $options['auth'];
        $headers = [
            'Accept' => 'application/json',
            'User-Agent' => $this->userAgent,
        ];

        $body = null;
        if (isset($options['json'])) {
            $body = json_encode($options['json'], JSON_UNESCAPED_SLASHES);
            if ($body === false) {
                throw new ReceivifyException('Receivify: request body could not be JSON-encoded.');
            }
            $headers['Content-Type'] = 'application/json';
        }

        if ($auth) {
            if ($this->apiKey === '') {
                throw new ReceivifyException('Receivify: an API key is required for ' . $method . ' ' . $path . '.');
            }
            // Header only — the key is never a query parameter and is never logged.
            $headers['Authorization'] = 'Bearer ' . $this->apiKey;
        }

        $idempotencyKey = isset($options['idempotency_key']) ? (string) $options['idempotency_key'] : '';
        if ($idempotencyKey !== '') {
            // Longer values are truncated to 255 server-side; do it here so the
            // key we send is the key that is stored.
            $headers['Idempotency-Key'] = substr($idempotencyKey, 0, 255);
        }

        // Retry only what is SAFE: GET, the idempotent DELETE, and anything
        // carrying an Idempotency-Key. Blind-retrying a bare mutating call is
        // how you charge someone twice.
        $retryable = array_key_exists('retryable', $options)
            ? (bool) $options['retryable']
            : ($method === 'GET' || $method === 'DELETE' || $idempotencyKey !== '');

        $url = $this->apiUrl . $path;
        $query = isset($options['query']) ? self::cleanQuery($options['query']) : [];
        if ($query !== []) {
            $url .= '?' . http_build_query($query);
        }

        $attempt = 0;
        while (true) {
            try {
                $res = $this->dispatch($method, $url, $headers, $body);
            } catch (ReceivifyConnectionError $e) {
                if ($retryable && $attempt < $this->maxRetries) {
                    $this->pause($this->backoff($attempt, []));
                    $attempt++;
                    continue;
                }
                throw $e;
            }

            if ($res['status'] >= 200 && $res['status'] < 300) {
                return $res;
            }
            if ($retryable && $attempt < $this->maxRetries && ($res['status'] === 429 || $res['status'] >= 500)) {
                $this->pause($this->backoff($attempt, $res['headers']));
                $attempt++;
                continue;
            }
            throw self::apiError($res);
        }
    }

    /** @return array{status:int,headers:array<string,string>,body:string} */
    private function dispatch(string $method, string $url, array $headers, ?string $body): array
    {
        if ($this->transport !== null) {
            $res = call_user_func($this->transport, [
                'method' => $method,
                'url' => $url,
                'headers' => $headers,
                'body' => $body,
                'timeout' => $this->timeout,
            ]);
            if (!is_array($res) || !isset($res['status'])) {
                throw new ReceivifyException('Receivify: the injected transport returned an unusable response.');
            }
            return [
                'status' => (int) $res['status'],
                'headers' => isset($res['headers']) && is_array($res['headers']) ? $res['headers'] : [],
                'body' => isset($res['body']) ? (string) $res['body'] : '',
            ];
        }

        if (!function_exists('curl_init')) {
            throw new ReceivifyException('Receivify: ext-curl is not available. Inject a transport with setTransport().');
        }

        $flat = [];
        foreach ($headers as $name => $value) {
            $flat[] = $name . ': ' . $value;
        }

        $responseHeaders = [];
        $ch = curl_init($url);
        curl_setopt_array($ch, [
            CURLOPT_CUSTOMREQUEST => $method,
            CURLOPT_RETURNTRANSFER => true,
            CURLOPT_HTTPHEADER => $flat,
            CURLOPT_TIMEOUT => $this->timeout,
            CURLOPT_CONNECTTIMEOUT => $this->connectTimeout,
            // Never follow a redirect: it would replay the Authorization header
            // at whatever host the redirect names.
            CURLOPT_FOLLOWLOCATION => false,
            CURLOPT_HEADERFUNCTION => function ($ch, $line) use (&$responseHeaders) {
                $parts = explode(':', $line, 2);
                if (count($parts) === 2) {
                    $responseHeaders[strtolower(trim($parts[0]))] = trim($parts[1]);
                }
                return strlen($line);
            },
        ]);
        if ($body !== null) {
            curl_setopt($ch, CURLOPT_POSTFIELDS, $body);
        }

        $raw = curl_exec($ch);
        if ($raw === false) {
            $err = curl_error($ch);
            curl_close($ch);
            throw new ReceivifyConnectionError('Receivify request failed: ' . $err);
        }
        $status = (int) curl_getinfo($ch, CURLINFO_HTTP_CODE);
        curl_close($ch);

        return ['status' => $status, 'headers' => $responseHeaders, 'body' => (string) $raw];
    }

    /** Turn a non-2xx response into the right typed exception. */
    private static function apiError(array $res): ReceivifyApiError
    {
        $decoded = json_decode($res['body'], true);
        $code = '';
        $detail = '';
        if (is_array($decoded)) {
            if (isset($decoded['error']) && is_string($decoded['error'])) {
                $code = $decoded['error'];
            }
            if (isset($decoded['detail']) && is_string($decoded['detail'])) {
                $detail = $decoded['detail'];
            }
        }
        if ($detail === '') {
            $detail = $res['body'] === ''
                ? 'HTTP ' . $res['status'] . ' with an empty body.'
                : 'HTTP ' . $res['status'] . ': ' . substr($res['body'], 0, 500);
        }

        // Frozen code set -> typed exception. An UNKNOWN code deliberately falls
        // through to ReceivifyApiError, so a future code never crashes an older SDK.
        $map = [
            'bad_request' => 'ReceivifyBadRequestError',
            'bad_ticker' => 'ReceivifyBadTickerError',
            'bad_destination' => 'ReceivifyBadDestinationError',
            'asset_unavailable' => 'ReceivifyAssetUnavailableError',
            'amount_too_low' => 'ReceivifyAmountTooLowError',
            'suspended' => 'ReceivifySuspendedError',
            'limit_exceeded' => 'ReceivifyLimitExceededError',
            'invalid_code' => 'ReceivifyInvalidCodeError',
            'no_setup' => 'ReceivifyNoSetupError',
            'not_enabled' => 'ReceivifyNotEnabledError',
            'no_target' => 'ReceivifyNoTargetError',
            'invalid_token' => 'ReceivifyInvalidTokenError',
            'unauthorized' => 'ReceivifyUnauthorizedError',
            'bad_signature' => 'ReceivifyBadSignatureError',
            'signup_disabled' => 'ReceivifySignupDisabledError',
            'not_found' => 'ReceivifyNotFoundError',
            'already_exists' => 'ReceivifyAlreadyExistsError',
            'already_enabled' => 'ReceivifyAlreadyEnabledError',
            'not_allowed' => 'ReceivifyNotAllowedError',
            'rate_limited' => 'ReceivifyRateLimitedError',
            'internal' => 'ReceivifyInternalError',
            'upstream_error' => 'ReceivifyUpstreamError',
            'rate_unavailable' => 'ReceivifyRateUnavailableError',
        ];
        $class = isset($map[$code]) ? $map[$code] : 'ReceivifyApiError';

        return new $class($code, $detail, (int) $res['status'], $res['headers'], $res['body']);
    }

    /** @return array the decoded JSON object */
    private function decodeObject(array $res): array
    {
        if (trim($res['body']) === '') {
            return [];
        }
        $decoded = json_decode($res['body'], true);
        if (!is_array($decoded)) {
            throw new ReceivifyException('Receivify: the response was not JSON: ' . substr($res['body'], 0, 200));
        }
        return $decoded;
    }

    /** Seconds to wait before the next attempt. */
    private function backoff(int $attempt, array $headers): float
    {
        if (isset($headers['retry-after']) && is_numeric($headers['retry-after'])) {
            $delay = (float) $headers['retry-after'];
        } elseif (isset($headers['x-ratelimit-reset']) && is_numeric($headers['x-ratelimit-reset'])) {
            $delay = (float) $headers['x-ratelimit-reset'] - time();
        } else {
            $delay = 0.5 * pow(2, $attempt);
        }
        if ($delay < 0.1) {
            $delay = 0.5 * pow(2, $attempt);
        }
        // Jitter so a fleet of workers does not retry in lockstep.
        $delay += mt_rand(0, 250) / 1000;
        return min($delay, $this->maxRetryDelay);
    }

    private function pause(float $seconds): void
    {
        if ($seconds > 0) {
            usleep((int) round($seconds * 1000000));
        }
    }

    /** @return array<int,string> unique, non-empty, in order */
    private static function normaliseSecrets($secrets): array
    {
        if ($secrets === null) {
            return [];
        }
        if (is_string($secrets)) {
            $secrets = [$secrets];
        }
        if (!is_array($secrets)) {
            return [];
        }
        $out = [];
        foreach ($secrets as $secret) {
            if (!is_string($secret)) {
                continue;
            }
            $secret = trim($secret);
            if ($secret !== '' && !in_array($secret, $out, true)) {
                $out[] = $secret;
            }
        }
        return $out;
    }

    /**
     * `t=<unix>,v1=<hex>` -> the timestamp (both raw and int) and every v1 value.
     *
     * @return array{t:string,timestamp:int,signatures:array<int,string>}|null
     */
    private static function parseSignatureHeader(string $header): ?array
    {
        $t = null;
        $signatures = [];
        foreach (explode(',', $header) as $part) {
            $kv = explode('=', trim($part), 2);
            if (count($kv) !== 2) {
                continue;
            }
            $key = strtolower(trim($kv[0]));
            $value = trim($kv[1]);
            if ($key === 't' && $t === null) {
                $t = $value;
            } elseif ($key === 'v1' && $value !== '') {
                $signatures[] = strtolower($value);
            }
        }
        if ($t === null || $signatures === [] || preg_match('/^[0-9]+$/', $t) !== 1) {
            return null;
        }
        // The signed payload uses the timestamp EXACTLY as it arrived on the wire.
        return ['t' => $t, 'timestamp' => (int) $t, 'signatures' => $signatures];
    }

    /** @return array only the listed keys that are actually present */
    private static function pick(array $source, array $keys): array
    {
        $out = [];
        foreach ($keys as $key) {
            if (array_key_exists($key, $source) && $source[$key] !== null) {
                $out[$key] = $source[$key];
            }
        }
        return $out;
    }

    /**
     * First present alias.
     *
     * @param bool $allowNull when true, a present-but-null key wins and null is
     *                        returned; MISSING means no alias was present at all
     * @return mixed
     */
    private static function firstOf(array $source, array $keys, bool $allowNull = false)
    {
        foreach ($keys as $key) {
            if (array_key_exists($key, $source)) {
                if ($source[$key] === null && !$allowNull) {
                    continue;
                }
                return $source[$key];
            }
        }
        return $allowNull ? self::MISSING : null;
    }

    /** Pull `idempotency_key` out of either bag, leaving neither in the body. */
    private static function takeIdempotencyKey(array &$params, array $opts): string
    {
        $key = '';
        foreach (['idempotency_key', 'Idempotency-Key', 'idempotencyKey'] as $name) {
            if (isset($opts[$name]) && $opts[$name] !== '') {
                $key = (string) $opts[$name];
                break;
            }
            if (isset($params[$name]) && $params[$name] !== '') {
                $key = (string) $params[$name];
                unset($params[$name]);
                break;
            }
        }
        unset($params['idempotency_key'], $params['Idempotency-Key'], $params['idempotencyKey']);
        return $key;
    }

    /**
     * Money never becomes a float on the way out either. A float that arrives
     * from a legacy caller is rendered exactly rather than through the locale.
     *
     * @param mixed $value
     * @return string
     */
    private static function numberToString($value): string
    {
        if (is_string($value)) {
            return $value;
        }
        if (is_int($value)) {
            return (string) $value;
        }
        if (is_float($value)) {
            $out = rtrim(rtrim(sprintf('%.8F', $value), '0'), '.');
            return $out === '' || $out === '-' ? '0' : $out;
        }
        return (string) $value;
    }

    /** Drop nulls and empty strings so they never reach the query string. */
    private static function cleanQuery($query): array
    {
        if (!is_array($query)) {
            return [];
        }
        $out = [];
        foreach ($query as $key => $value) {
            if ($value === null || $value === '' || is_array($value)) {
                continue;
            }
            if (is_bool($value)) {
                $value = $value ? 'true' : 'false';
            }
            $out[$key] = (string) $value;
        }
        return $out;
    }
}
