package io.receivify;

import java.io.IOException;
import java.net.SocketTimeoutException;
import java.net.URLEncoder;
import java.net.http.HttpTimeoutException;
import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ThreadLocalRandom;
import java.util.function.Function;

/**
 * The Receivify API client. Thread-safe and meant to be long-lived: build one and share it.
 *
 * <pre>{@code
 * ReceivifyClient client = ReceivifyClient.builder()
 *     .apiUrl("https://receivify.io")
 *     .apiKey(System.getenv("RECEIVIFY_API_KEY"))
 *     .webhookSecrets(List.of(System.getenv("RECEIVIFY_WEBHOOK_SECRET")))
 *     .timeout(Duration.ofSeconds(30))
 *     .build();
 *
 * Invoice invoice = client.createInvoice(
 *     CreateInvoiceParams.builder().amountFiat("25.00").currency("USD").asset("btc").reference("order-1").build(),
 *     RequestOptions.idempotencyKey("order-1"));
 * }</pre>
 *
 * <p><b>Retry policy.</b> A request is retried on 429 and 5xx (and on connection failures) only when
 * it is safe: a {@code GET}, or a mutating call that carries an {@code Idempotency-Key} AND targets
 * an endpoint that supports one. Backoff honours {@code Retry-After}, then {@code X-RateLimit-Reset},
 * then exponential backoff with jitter.
 *
 * <p>The API key travels only in the {@code Authorization} header. It is never put in a URL, never
 * logged, and never appears in {@link #toString()} or on an exception.
 */
public final class ReceivifyClient {
    /** The SDK version. */
    public static final String VERSION = "1.0.0";

    /** The User-Agent this SDK sends. */
    public static final String USER_AGENT = "receivify-java/" + VERSION;

    /** The production API base URL. */
    public static final String DEFAULT_API_URL = "https://receivify.io";

    /** How the client waits between retries. Replace it in tests to make retries instant. */
    @FunctionalInterface
    public interface Sleeper {
        /**
         * @param duration how long to wait
         * @throws InterruptedException when interrupted
         */
        void sleep(Duration duration) throws InterruptedException;
    }

    private final String apiUrl;
    private final String apiKey;
    private final List<String> webhookSecrets;
    private final Duration timeout;
    private final int maxRetries;
    private final Duration retryBaseDelay;
    private final Duration maxRetryDelay;
    private final Transport transport;
    private final String userAgent;
    private final Clock clock;
    private final Sleeper sleeper;

    private ReceivifyClient(Builder b) {
        String url = b.apiUrl == null ? DEFAULT_API_URL : b.apiUrl.trim();
        while (url.endsWith("/")) {
            url = url.substring(0, url.length() - 1);
        }
        if (!url.startsWith("https://") && !url.startsWith("http://")) {
            throw new IllegalArgumentException("apiUrl must start with https:// (or http:// for local development)");
        }
        this.apiUrl = url;
        this.apiKey = b.apiKey == null || b.apiKey.trim().isEmpty() ? null : b.apiKey.trim();
        this.webhookSecrets = Webhooks.normaliseSecrets(b.webhookSecrets);
        this.timeout = b.timeout;
        this.maxRetries = Math.max(0, b.maxRetries);
        this.retryBaseDelay = b.retryBaseDelay;
        this.maxRetryDelay = b.maxRetryDelay;
        this.transport = b.transport != null ? b.transport : new JdkHttpTransport();
        this.userAgent = b.userAgentSuffix == null || b.userAgentSuffix.trim().isEmpty()
                ? USER_AGENT : USER_AGENT + " " + b.userAgentSuffix.trim();
        this.clock = b.clock;
        this.sleeper = b.sleeper;
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** @return the base URL, without a trailing slash */
    public String getApiUrl() {
        return apiUrl;
    }

    /** @return the configured webhook secrets, current first; never {@code null} */
    public List<String> getWebhookSecrets() {
        return webhookSecrets;
    }

    /** Never includes the API key or secrets. */
    @Override
    public String toString() {
        return "ReceivifyClient{apiUrl=" + apiUrl + ", apiKey=" + (apiKey == null ? "none" : "***") + "}";
    }

    /* ================================================================ *
     * Invoices
     * ================================================================ */

    /**
     * {@code POST /api/v1/invoices}. Pass an Idempotency-Key so a retry after a timeout cannot create
     * a second invoice; reusing a {@code reference} also returns the existing invoice.
     *
     * @param params the invoice
     * @return the created (or existing) invoice
     */
    public Invoice createInvoice(CreateInvoiceParams params) {
        return createInvoice(params, null);
    }

    /**
     * @param params the invoice
     * @param options per-call options; this endpoint accepts an Idempotency-Key
     * @return the created (or existing) invoice
     */
    public Invoice createInvoice(CreateInvoiceParams params, RequestOptions options) {
        requireNonNull(params, "params");
        return new Invoice(object(send("POST", "/api/v1/invoices", null, params.toMap(), options, true, true, true)));
    }

    /**
     * {@code GET /api/v1/invoices/{id}}: the authoritative current state.
     *
     * @param id the invoice id
     * @return the invoice
     */
    public Invoice getInvoice(String id) {
        return getInvoice(id, null);
    }

    /**
     * @param id the invoice id
     * @param options per-call options
     * @return the invoice
     */
    public Invoice getInvoice(String id, RequestOptions options) {
        return new Invoice(object(send("GET", "/api/v1/invoices/" + segment(id), null, null, options, true, false, true)));
    }

    /** @return the first page of invoices, newest first */
    public Page<Invoice> listInvoices() {
        return listInvoices(null, null);
    }

    /**
     * @param params paging and status filter (may be {@code null})
     * @return one page of invoices
     */
    public Page<Invoice> listInvoices(ListInvoicesParams params) {
        return listInvoices(params, null);
    }

    /**
     * @param params paging and status filter (may be {@code null})
     * @param options per-call options
     * @return one page of invoices
     */
    public Page<Invoice> listInvoices(ListInvoicesParams params, RequestOptions options) {
        return list("/api/v1/invoices", query(params), options, Invoice::new, true);
    }

    /** @return every invoice, newest first, fetched lazily page by page */
    public AutoPager<Invoice> autoPagingInvoices() {
        return autoPagingInvoices(null, null);
    }

    /**
     * @param params filters; its cursor (if any) is the starting point
     * @return every matching invoice, fetched lazily
     */
    public AutoPager<Invoice> autoPagingInvoices(ListInvoicesParams params) {
        return autoPagingInvoices(params, null);
    }

    /**
     * @param params filters; its cursor (if any) is the starting point
     * @param options per-call options applied to every page request
     * @return every matching invoice, fetched lazily
     */
    public AutoPager<Invoice> autoPagingInvoices(ListInvoicesParams params, RequestOptions options) {
        return pager("/api/v1/invoices", query(params), options, Invoice::new, true);
    }

    /**
     * {@code POST /api/v1/invoices/{id}/cancel}: cancel a pending, unpaid invoice. A second call is a
     * 409 {@link NotAllowedException}.
     *
     * @param id the invoice id
     * @return the cancelled invoice
     */
    public Invoice cancelInvoice(String id) {
        return cancelInvoice(id, null);
    }

    /**
     * @param id the invoice id
     * @param options per-call options
     * @return the cancelled invoice
     */
    public Invoice cancelInvoice(String id, RequestOptions options) {
        return new Invoice(object(send("POST", "/api/v1/invoices/" + segment(id) + "/cancel", null, null, options, true, false, true)));
    }

    /**
     * {@code POST /api/v1/invoices/bulk}: cancel up to 500 pending invoices.
     *
     * @param ids invoice ids
     * @return how many were requested and how many were cancelled
     */
    public BulkResult bulkCancelInvoices(Collection<String> ids) {
        return bulkCancelInvoices(ids, null);
    }

    /**
     * @param ids invoice ids
     * @param options per-call options
     * @return how many were requested and how many were cancelled
     */
    public BulkResult bulkCancelInvoices(Collection<String> ids, RequestOptions options) {
        requireNonNull(ids, "ids");
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("ids", new ArrayList<>(ids));
        body.put("action", "cancel");
        return new BulkResult(object(send("POST", "/api/v1/invoices/bulk", null, body, options, true, false, true)));
    }

    /**
     * {@code GET /api/public/invoices/{id}}: unauthenticated, browser-safe status.
     *
     * @param id the invoice id
     * @return the public projection
     */
    public PublicInvoice getPublicInvoice(String id) {
        return getPublicInvoice(id, null);
    }

    /**
     * @param id the invoice id
     * @param options per-call options
     * @return the public projection
     */
    public PublicInvoice getPublicInvoice(String id, RequestOptions options) {
        return new PublicInvoice(object(send("GET", "/api/public/invoices/" + segment(id), null, null, options, false, false, true)));
    }

    /* ================================================================ *
     * Forwarding addresses
     * ================================================================ */

    /**
     * {@code POST /api/v1/address}: create, or return, a forwarding address. Deterministic: the same
     * ticker, destination and callback always return the same forward.
     *
     * @param params ticker and destination
     * @return the forward
     */
    public Forward createAddress(CreateAddressParams params) {
        return createAddress(params, null);
    }

    /**
     * @param params ticker and destination
     * @param options per-call options; this endpoint accepts an Idempotency-Key
     * @return the forward
     */
    public Forward createAddress(CreateAddressParams params, RequestOptions options) {
        return createAddressWithResponse(params, options).getValue();
    }

    /**
     * The same as {@link #createAddress(CreateAddressParams, RequestOptions)}, with the status code:
     * 201 means newly derived, 200 means an existing forward came back.
     *
     * @param params ticker and destination
     * @param options per-call options (may be {@code null})
     * @return the response
     */
    public ReceivifyResponse<Forward> createAddressWithResponse(CreateAddressParams params, RequestOptions options) {
        requireNonNull(params, "params");
        ReceivifyResponse<Object> r = send("POST", "/api/v1/address", null, params.toMap(), options, true, true, true);
        return new ReceivifyResponse<>(r.getStatusCode(), r.getHeaders(), new Forward(object(r)), r.getRawBody());
    }

    /**
     * {@code GET /api/v1/address/{id}}: the forward plus its capped payment and callback logs.
     *
     * @param id the forward id
     * @return the detail view
     */
    public ForwardDetail getAddress(String id) {
        return getAddress(id, null);
    }

    /**
     * @param id the forward id
     * @param options per-call options
     * @return the detail view
     */
    public ForwardDetail getAddress(String id, RequestOptions options) {
        return new ForwardDetail(object(send("GET", "/api/v1/address/" + segment(id), null, null, options, true, false, true)));
    }

    /** @return the first page of forwarding addresses */
    public Page<Forward> listAddresses() {
        return listAddresses(null, null);
    }

    /**
     * @param params paging, status and ticker filters (may be {@code null})
     * @return one page
     */
    public Page<Forward> listAddresses(ListAddressesParams params) {
        return listAddresses(params, null);
    }

    /**
     * @param params paging, status and ticker filters (may be {@code null})
     * @param options per-call options
     * @return one page
     */
    public Page<Forward> listAddresses(ListAddressesParams params, RequestOptions options) {
        return list("/api/v1/address", query(params), options, Forward::new, true);
    }

    /**
     * @param params filters (may be {@code null})
     * @return every matching forward, fetched lazily
     */
    public AutoPager<Forward> autoPagingAddresses(ListAddressesParams params) {
        return autoPagingAddresses(params, null);
    }

    /**
     * @param params filters (may be {@code null})
     * @param options per-call options
     * @return every matching forward, fetched lazily
     */
    public AutoPager<Forward> autoPagingAddresses(ListAddressesParams params, RequestOptions options) {
        return pager("/api/v1/address", query(params), options, Forward::new, true);
    }

    /**
     * {@code PATCH /api/v1/address/{id}}: pause/resume or change the callback.
     *
     * @param id the forward id
     * @param params what to change
     * @return the updated forward
     */
    public Forward updateAddress(String id, UpdateAddressParams params) {
        return updateAddress(id, params, null);
    }

    /**
     * @param id the forward id
     * @param params what to change
     * @param options per-call options
     * @return the updated forward
     */
    public Forward updateAddress(String id, UpdateAddressParams params, RequestOptions options) {
        requireNonNull(params, "params");
        return new Forward(object(send("PATCH", "/api/v1/address/" + segment(id), null, params.toMap(), options, true, false, true)));
    }

    /**
     * {@code DELETE /api/v1/address/{id}}: soft delete (status {@code disabled}). Idempotent.
     *
     * @param id the forward id
     * @return the disabled forward
     */
    public Forward disableAddress(String id) {
        return disableAddress(id, null);
    }

    /**
     * @param id the forward id
     * @param options per-call options
     * @return the disabled forward
     */
    public Forward disableAddress(String id, RequestOptions options) {
        return new Forward(object(send("DELETE", "/api/v1/address/" + segment(id), null, null, options, true, false, true)));
    }

    /* ================================================================ *
     * Catalogue (public)
     * ================================================================ */

    /**
     * {@code GET /api/v1/tickers}: every forwarding ticker, with its decimals. One page.
     *
     * @return the tickers
     */
    public Page<Ticker> listTickers() {
        return listTickers(null);
    }

    /**
     * @param options per-call options
     * @return the tickers
     */
    public Page<Ticker> listTickers(RequestOptions options) {
        return list("/api/v1/tickers", null, options, Ticker::new, false);
    }

    /**
     * {@code GET /api/v1/assets}: coins enabled for invoicing (display metadata).
     *
     * @return the assets
     */
    public Page<Asset> listAssets() {
        return listAssets(null);
    }

    /**
     * @param options per-call options
     * @return the assets
     */
    public Page<Asset> listAssets(RequestOptions options) {
        return list("/api/v1/assets", null, options, Asset::new, false);
    }

    /* ================================================================ *
     * Webhooks
     * ================================================================ */

    /**
     * @param params paging (may be {@code null})
     * @return one page of account-level webhook endpoints
     */
    public Page<WebhookEndpoint> listWebhookEndpoints(ListParams params) {
        return listWebhookEndpoints(params, null);
    }

    /**
     * @param params paging (may be {@code null})
     * @param options per-call options
     * @return one page
     */
    public Page<WebhookEndpoint> listWebhookEndpoints(ListParams params, RequestOptions options) {
        return list("/api/v1/webhooks/endpoints", query(params), options, WebhookEndpoint::new, true);
    }

    /**
     * @param params paging (may be {@code null})
     * @return every endpoint, fetched lazily
     */
    public AutoPager<WebhookEndpoint> autoPagingWebhookEndpoints(ListParams params) {
        return pager("/api/v1/webhooks/endpoints", query(params), null, WebhookEndpoint::new, true);
    }

    /**
     * {@code POST /api/v1/webhooks/endpoints} (max 20 per account).
     *
     * @param url the https URL
     * @return the endpoint
     */
    public WebhookEndpoint createWebhookEndpoint(String url) {
        return createWebhookEndpoint(url, null);
    }

    /**
     * @param url the https URL
     * @param options per-call options; accepts an Idempotency-Key
     * @return the endpoint
     */
    public WebhookEndpoint createWebhookEndpoint(String url, RequestOptions options) {
        requireNonNull(url, "url");
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("url", url);
        return new WebhookEndpoint(object(send("POST", "/api/v1/webhooks/endpoints", null, body, options, true, true, true)));
    }

    /**
     * {@code PATCH /api/v1/webhooks/endpoints/{id}}. A {@code null} argument is left unchanged.
     *
     * @param id the endpoint id
     * @param active the pause switch, or {@code null}
     * @param url a new URL, or {@code null}
     * @return the updated endpoint
     */
    public WebhookEndpoint updateWebhookEndpoint(String id, Boolean active, String url) {
        return updateWebhookEndpoint(id, active, url, null);
    }

    /**
     * @param id the endpoint id
     * @param active the pause switch, or {@code null}
     * @param url a new URL, or {@code null}
     * @param options per-call options
     * @return the updated endpoint
     */
    public WebhookEndpoint updateWebhookEndpoint(String id, Boolean active, String url, RequestOptions options) {
        Map<String, Object> body = new LinkedHashMap<>();
        if (active != null) {
            body.put("active", active);
        }
        if (url != null) {
            body.put("url", url);
        }
        return new WebhookEndpoint(object(send("PATCH", "/api/v1/webhooks/endpoints/" + segment(id), null, body, options, true, false, true)));
    }

    /**
     * {@code DELETE /api/v1/webhooks/endpoints/{id}}: a hard delete.
     *
     * @param id the endpoint id
     * @return the ack ({@code deleted})
     */
    public Ack deleteWebhookEndpoint(String id) {
        return deleteWebhookEndpoint(id, null);
    }

    /**
     * @param id the endpoint id
     * @param options per-call options
     * @return the ack
     */
    public Ack deleteWebhookEndpoint(String id, RequestOptions options) {
        return new Ack(object(send("DELETE", "/api/v1/webhooks/endpoints/" + segment(id), null, null, options, true, false, true)));
    }

    /**
     * @param params paging, event and status filters (may be {@code null})
     * @return one page of the delivery log
     */
    public Page<WebhookDelivery> listWebhookDeliveries(ListWebhookDeliveriesParams params) {
        return listWebhookDeliveries(params, null);
    }

    /**
     * @param params paging, event and status filters (may be {@code null})
     * @param options per-call options
     * @return one page
     */
    public Page<WebhookDelivery> listWebhookDeliveries(ListWebhookDeliveriesParams params, RequestOptions options) {
        return list("/api/v1/webhooks/deliveries", query(params), options, WebhookDelivery::new, true);
    }

    /**
     * @param params filters (may be {@code null})
     * @return every delivery, fetched lazily
     */
    public AutoPager<WebhookDelivery> autoPagingWebhookDeliveries(ListWebhookDeliveriesParams params) {
        return pager("/api/v1/webhooks/deliveries", query(params), null, WebhookDelivery::new, true);
    }

    /**
     * {@code POST /api/v1/webhooks/deliveries/{id}/replay}: re-send the ORIGINAL frozen body.
     *
     * @param id the delivery id
     * @return the ack ({@code replayed})
     */
    public Ack replayWebhookDelivery(String id) {
        return replayWebhookDelivery(id, null);
    }

    /**
     * @param id the delivery id
     * @param options per-call options
     * @return the ack
     */
    public Ack replayWebhookDelivery(String id, RequestOptions options) {
        return new Ack(object(send("POST", "/api/v1/webhooks/deliveries/" + segment(id) + "/replay", null, null, options, true, false, true)));
    }

    /** @return rotation metadata (never the secret) */
    public WebhookSecretState getWebhookSecretState() {
        return getWebhookSecretState(null);
    }

    /**
     * @param options per-call options
     * @return rotation metadata
     */
    public WebhookSecretState getWebhookSecretState(RequestOptions options) {
        return new WebhookSecretState(object(send("GET", "/api/v1/webhooks/secret", null, null, options, true, false, true)));
    }

    /**
     * {@code POST /api/v1/webhooks/secret/rotate}: mint a new signing secret, returned ONCE. Until
     * {@link WebhookSecretRotated#getPrevExpiresAt()} verify with {@code List.of(newSecret, oldSecret)}.
     *
     * @return the rotation, including the new secret
     */
    public WebhookSecretRotated rotateWebhookSecret() {
        return rotateWebhookSecret(null);
    }

    /**
     * @param options per-call options; accepts an Idempotency-Key (a replay returns a null secret)
     * @return the rotation
     */
    public WebhookSecretRotated rotateWebhookSecret(RequestOptions options) {
        return new WebhookSecretRotated(object(send("POST", "/api/v1/webhooks/secret/rotate", null, null, options, true, true, true)));
    }

    /**
     * {@code POST /api/v1/webhooks/test}: send a signed {@code test} event to your registered endpoints.
     *
     * @param url one registered endpoint, or {@code null} for all of them
     * @return the outcome
     */
    public WebhookTestResult sendTestWebhook(String url) {
        return sendTestWebhook(url, null);
    }

    /**
     * @param url one registered endpoint, or {@code null} for all
     * @param options per-call options
     * @return the outcome
     */
    public WebhookTestResult sendTestWebhook(String url, RequestOptions options) {
        Map<String, Object> body = new LinkedHashMap<>();
        if (url != null) {
            body.put("url", url);
        }
        return new WebhookTestResult(object(send("POST", "/api/v1/webhooks/test", null, body, options, true, false, true)));
    }

    /**
     * Verify a delivery against this client's configured secrets (300s tolerance).
     *
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the {@code X-Receivify-Signature} value
     * @return true when authentic
     */
    public boolean verifyWebhook(byte[] rawBody, String signatureHeader) {
        return Webhooks.verify(rawBody, signatureHeader, webhookSecrets);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the {@code X-Receivify-Signature} value
     * @return true when authentic
     */
    public boolean verifyWebhook(String rawBody, String signatureHeader) {
        return Webhooks.verify(rawBody, signatureHeader, webhookSecrets);
    }

    /**
     * Verify against the configured secrets and decode the envelope.
     *
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the {@code X-Receivify-Signature} value
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     */
    public WebhookEvent parseWebhook(byte[] rawBody, String signatureHeader) {
        return Webhooks.parse(rawBody, signatureHeader, webhookSecrets);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the {@code X-Receivify-Signature} value
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     */
    public WebhookEvent parseWebhook(String rawBody, String signatureHeader) {
        return Webhooks.parse(rawBody, signatureHeader, webhookSecrets);
    }

    /* ================================================================ *
     * Payment links
     * ================================================================ */

    /**
     * @param params paging and the active filter (may be {@code null})
     * @return one page of payment links
     */
    public Page<PaymentLink> listPaymentLinks(ListPaymentLinksParams params) {
        return listPaymentLinks(params, null);
    }

    /**
     * @param params paging and the active filter (may be {@code null})
     * @param options per-call options
     * @return one page
     */
    public Page<PaymentLink> listPaymentLinks(ListPaymentLinksParams params, RequestOptions options) {
        return list("/api/v1/payment-links", query(params), options, PaymentLink::new, true);
    }

    /**
     * @param params filters (may be {@code null})
     * @return every payment link, fetched lazily
     */
    public AutoPager<PaymentLink> autoPagingPaymentLinks(ListPaymentLinksParams params) {
        return pager("/api/v1/payment-links", query(params), null, PaymentLink::new, true);
    }

    /**
     * @param params title and optional decimal-BTC amount
     * @return the link
     */
    public PaymentLink createPaymentLink(PaymentLinkParams params) {
        return createPaymentLink(params, null);
    }

    /**
     * @param params title and optional decimal-BTC amount
     * @param options per-call options; accepts an Idempotency-Key
     * @return the link
     */
    public PaymentLink createPaymentLink(PaymentLinkParams params, RequestOptions options) {
        requireNonNull(params, "params");
        return new PaymentLink(object(send("POST", "/api/v1/payment-links", null, params.toMap(), options, true, true, true)));
    }

    /**
     * @param id the link id
     * @return the link
     */
    public PaymentLink getPaymentLink(String id) {
        return getPaymentLink(id, null);
    }

    /**
     * @param id the link id
     * @param options per-call options
     * @return the link
     */
    public PaymentLink getPaymentLink(String id, RequestOptions options) {
        return new PaymentLink(object(send("GET", "/api/v1/payment-links/" + segment(id), null, null, options, true, false, true)));
    }

    /**
     * @param id the link id
     * @param params what to change
     * @return the updated link
     */
    public PaymentLink updatePaymentLink(String id, PaymentLinkParams params) {
        return updatePaymentLink(id, params, null);
    }

    /**
     * @param id the link id
     * @param params what to change
     * @param options per-call options
     * @return the updated link
     */
    public PaymentLink updatePaymentLink(String id, PaymentLinkParams params, RequestOptions options) {
        requireNonNull(params, "params");
        return new PaymentLink(object(send("PATCH", "/api/v1/payment-links/" + segment(id), null, params.toMap(), options, true, false, true)));
    }

    /**
     * Soft deactivate. Invoices already minted stay payable.
     *
     * @param id the link id
     * @return the deactivated link
     */
    public PaymentLink deactivatePaymentLink(String id) {
        return deactivatePaymentLink(id, null);
    }

    /**
     * @param id the link id
     * @param options per-call options
     * @return the deactivated link
     */
    public PaymentLink deactivatePaymentLink(String id, RequestOptions options) {
        return new PaymentLink(object(send("DELETE", "/api/v1/payment-links/" + segment(id), null, null, options, true, false, true)));
    }

    /**
     * @param id the link id
     * @return the unauthenticated view
     */
    public PublicPaymentLink getPublicPaymentLink(String id) {
        return new PublicPaymentLink(object(send("GET", "/api/public/links/" + segment(id), null, null, null, false, false, true)));
    }

    /* ================================================================ *
     * Recurring
     * ================================================================ */

    /**
     * @param params paging (may be {@code null})
     * @return one page of schedules
     */
    public Page<RecurringSchedule> listRecurring(ListParams params) {
        return list("/api/v1/recurring", query(params), null, RecurringSchedule::new, true);
    }

    /**
     * @param params paging (may be {@code null})
     * @return every schedule, fetched lazily
     */
    public AutoPager<RecurringSchedule> autoPagingRecurring(ListParams params) {
        return pager("/api/v1/recurring", query(params), null, RecurringSchedule::new, true);
    }

    /**
     * @param params the schedule
     * @return the schedule
     */
    public RecurringSchedule createRecurring(CreateRecurringParams params) {
        return createRecurring(params, null);
    }

    /**
     * @param params the schedule
     * @param options per-call options; accepts an Idempotency-Key
     * @return the schedule
     */
    public RecurringSchedule createRecurring(CreateRecurringParams params, RequestOptions options) {
        requireNonNull(params, "params");
        return new RecurringSchedule(object(send("POST", "/api/v1/recurring", null, params.toMap(), options, true, true, true)));
    }

    /**
     * @param id the schedule id
     * @return the ack ({@code cancelled})
     */
    public Ack cancelRecurring(String id) {
        return new Ack(object(send("POST", "/api/v1/recurring/" + segment(id) + "/cancel", null, null, null, true, false, true)));
    }

    /* ================================================================ *
     * API keys
     * ================================================================ */

    /**
     * @param params paging (may be {@code null})
     * @return one page of keys (prefixes only)
     */
    public Page<ApiKey> listApiKeys(ListParams params) {
        return list("/api/v1/keys", query(params), null, ApiKey::new, true);
    }

    /**
     * @param params paging (may be {@code null})
     * @return every key, fetched lazily
     */
    public AutoPager<ApiKey> autoPagingApiKeys(ListParams params) {
        return pager("/api/v1/keys", query(params), null, ApiKey::new, true);
    }

    /**
     * @param label a human label, or {@code null}
     * @return the new key; its secret is shown once
     */
    public ApiKeyCreated createApiKey(String label) {
        return createApiKey(label, null);
    }

    /**
     * @param label a human label, or {@code null}
     * @param options per-call options; accepts an Idempotency-Key (a replay returns a null secret)
     * @return the new key
     */
    public ApiKeyCreated createApiKey(String label, RequestOptions options) {
        Map<String, Object> body = new LinkedHashMap<>();
        if (label != null) {
            body.put("label", label);
        }
        return new ApiKeyCreated(object(send("POST", "/api/v1/keys", null, body, options, true, true, true)));
    }

    /**
     * @param id the key id (NOT the prefix)
     * @return the ack ({@code revoked})
     */
    public Ack revokeApiKey(String id) {
        return revokeApiKey(id, null);
    }

    /**
     * @param id the key id
     * @param options per-call options
     * @return the ack
     */
    public Ack revokeApiKey(String id, RequestOptions options) {
        return new Ack(object(send("POST", "/api/v1/keys/" + segment(id) + "/revoke", null, null, options, true, false, true)));
    }

    /* ================================================================ *
     * Settings, summary, payouts, health
     * ================================================================ */

    /** @return merchant preferences */
    public Settings getSettings() {
        return new Settings(object(send("GET", "/api/v1/settings", null, null, null, true, false, true)));
    }

    /**
     * @param params what to change
     * @return an ack echoing only what changed
     */
    public Ack updateSettings(UpdateSettingsParams params) {
        requireNonNull(params, "params");
        return new Ack(object(send("PATCH", "/api/v1/settings", null, params.toMap(), null, true, false, true)));
    }

    /** @return the dashboard composite */
    public Summary getSummary() {
        return new Summary(object(send("GET", "/api/v1/summary", null, null, null, true, false, true)));
    }

    /** @return the live payout map and pending changes */
    public PayoutState getPayouts() {
        return new PayoutState(object(send("GET", "/api/v1/payouts", null, null, null, true, false, true)));
    }

    /**
     * Stage payout-address changes (timelocked; may need email confirmation). An empty string clears
     * that asset.
     *
     * @param addresses asset symbol to address
     * @return the staging ack; read {@code staged}, {@code applied}, {@code pending} from {@link Ack#raw()}
     */
    public Ack stagePayoutChanges(Map<String, String> addresses) {
        requireNonNull(addresses, "addresses");
        Map<String, Object> body = new LinkedHashMap<>();
        body.put("addresses", new LinkedHashMap<>(addresses));
        return new Ack(object(send("PATCH", "/api/v1/payouts", null, body, null, true, false, true)));
    }

    /**
     * @param id the staged change id
     * @return the ack ({@code cancelled})
     */
    public Ack cancelPayoutChange(String id) {
        return new Ack(object(send("POST", "/api/v1/payouts/changes/" + segment(id) + "/cancel", null, null, null, true, false, true)));
    }

    /**
     * {@code GET /api/health}. Returned on both 200 and 503 (its 503 body is not an error body).
     *
     * @return the report
     */
    public HealthReport getHealth() {
        return new HealthReport(object(send("GET", "/api/health", null, null, null, false, false, false)));
    }

    /* ================================================================ *
     * Escape hatch
     * ================================================================ */

    /**
     * Call any endpoint, including ones this SDK version does not model yet. The same auth, retry
     * and error mapping apply. Retries for a non-GET happen only when you pass an Idempotency-Key.
     *
     * @param method HTTP method
     * @param path absolute path starting with {@code /}, e.g. {@code /api/v1/fees/tier}
     * @param query query parameters (may be {@code null})
     * @param body a JSON-serialisable body (Map, List, ...) or {@code null}
     * @param options per-call options (may be {@code null})
     * @return the response; {@link ReceivifyResponse#getValue()} is the decoded JSON tree
     */
    public ReceivifyResponse<Object> request(String method, String path, Map<String, String> query, Object body, RequestOptions options) {
        requireNonNull(method, "method");
        requireNonNull(path, "path");
        if (!path.startsWith("/")) {
            throw new IllegalArgumentException("path must start with /");
        }
        return send(method.toUpperCase(java.util.Locale.ROOT), path, query, body, options, true, true, true);
    }

    /* ================================================================ *
     * Internals
     * ================================================================ */

    private ReceivifyResponse<Object> send(String method, String path, Map<String, String> query, Object body,
            RequestOptions options, boolean auth, boolean supportsIdempotency, boolean throwOnError) {
        RequestOptions opts = options == null ? RequestOptions.none() : options;
        String url = apiUrl + path + queryString(query);
        String label = method + " " + path;
        Duration callTimeout = opts.getTimeout() != null ? opts.getTimeout() : timeout;
        String idem = opts.getIdempotencyKey();
        boolean hasIdem = idem != null && !idem.isEmpty();

        Map<String, String> headers = new LinkedHashMap<>();
        headers.put("Accept", "application/json");
        headers.put("User-Agent", userAgent);
        if (auth && apiKey != null) {
            headers.put("Authorization", "Bearer " + apiKey);
        }
        if (hasIdem) {
            headers.put("Idempotency-Key", idem);
        }
        headers.putAll(opts.getHeaders());
        byte[] payload = null;
        if (body != null) {
            payload = Json.write(body).getBytes(StandardCharsets.UTF_8);
            headers.put("Content-Type", "application/json");
        }
        Transport.Request request = new Transport.Request(method, url, headers, payload, callTimeout);

        boolean safe = "GET".equals(method) || (supportsIdempotency && hasIdem);
        int maxAttempts = safe ? maxRetries + 1 : 1;

        for (int attempt = 1; ; attempt++) {
            Transport.Response response;
            try {
                response = transport.send(request);
            } catch (HttpTimeoutException | SocketTimeoutException e) {
                ReceivifyConnectionException err = new ReceivifyConnectionException(
                        "Receivify " + label + " timed out after " + callTimeout.toMillis() + "ms.", true, e);
                if (attempt >= maxAttempts) {
                    throw err;
                }
                pause(backoff(attempt, null));
                continue;
            } catch (IOException e) {
                ReceivifyConnectionException err = new ReceivifyConnectionException(
                        "Receivify " + label + " failed: " + e.getMessage(), false, e);
                if (attempt >= maxAttempts) {
                    throw err;
                }
                pause(backoff(attempt, null));
                continue;
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
                throw new ReceivifyConnectionException("Receivify " + label + " was interrupted.", false, e);
            }

            int status = response.getStatusCode();
            String text = new String(response.getBody(), StandardCharsets.UTF_8);
            if (status >= 400 && throwOnError) {
                ReceivifyApiException err = ErrorCodes.toException(status, text, response.getHeaders(), label);
                if (safe && (status == 429 || status >= 500) && attempt < maxAttempts) {
                    pause(backoff(attempt, response.getHeaders()));
                    continue;
                }
                throw err;
            }
            Object value = null;
            if (!text.trim().isEmpty()) {
                try {
                    value = Json.parse(text);
                } catch (Json.JsonException e) {
                    throw new ReceivifyException("Receivify " + label + " returned a body that is not valid JSON (HTTP "
                            + status + "): " + e.getMessage(), e);
                }
            }
            return new ReceivifyResponse<>(status, response.getHeaders(), value, text);
        }
    }

    private void pause(Duration d) {
        try {
            sleeper.sleep(d);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
            throw new ReceivifyConnectionException("Receivify retry wait was interrupted.", false, e);
        }
    }

    Duration backoff(int attempt, Map<String, String> headers) {
        if (headers != null) {
            String retryAfter = headers.get("retry-after");
            if (retryAfter != null) {
                try {
                    double seconds = Double.parseDouble(retryAfter.trim());
                    if (seconds > 0) {
                        return min(Duration.ofMillis((long) (seconds * 1000)), maxRetryDelay);
                    }
                } catch (NumberFormatException ignored) {
                    // An HTTP-date Retry-After: fall through.
                }
            }
            String reset = headers.get("x-ratelimit-reset");
            if (reset != null) {
                try {
                    long resetUnix = Long.parseLong(reset.trim());
                    long waitMs = resetUnix * 1000 - clock.millis();
                    if (waitMs > 0) {
                        return min(Duration.ofMillis(waitMs), maxRetryDelay);
                    }
                } catch (NumberFormatException ignored) {
                    // fall through to exponential backoff
                }
            }
        }
        long base = retryBaseDelay.toMillis();
        long exp = base * (1L << Math.min(attempt - 1, 20));
        long jitter = base > 0 ? ThreadLocalRandom.current().nextLong(base) : 0;
        return min(Duration.ofMillis(exp + jitter), maxRetryDelay);
    }

    private static Duration min(Duration a, Duration b) {
        return a.compareTo(b) <= 0 ? a : b;
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> object(ReceivifyResponse<Object> response) {
        Object v = response.getValue();
        if (v instanceof Map) {
            return (Map<String, Object>) v;
        }
        throw new ReceivifyException("Receivify returned " + (v == null ? "an empty body" : "a non-object body")
                + " (HTTP " + response.getStatusCode() + ") where a resource was expected.");
    }

    @SuppressWarnings("unchecked")
    private <T> Page<T> list(String path, Map<String, String> query, RequestOptions options,
            Function<Map<String, Object>, T> factory, boolean auth) {
        ReceivifyResponse<Object> r = send("GET", path, query, null, options, auth, false, true);
        Map<String, Object> env = object(r);
        List<T> items = new ArrayList<>();
        Object data = env.get("data");
        if (data instanceof List) {
            for (Object item : (List<Object>) data) {
                if (item instanceof Map) {
                    items.add(factory.apply((Map<String, Object>) item));
                }
            }
        }
        Object next = env.get("next_cursor");
        return new Page<>(items, Boolean.TRUE.equals(env.get("has_more")), next instanceof String ? (String) next : null);
    }

    private <T> AutoPager<T> pager(String path, Map<String, String> query, RequestOptions options,
            Function<Map<String, Object>, T> factory, boolean auth) {
        final Map<String, String> base = new LinkedHashMap<>(query);
        String start = base.remove("cursor");
        return new AutoPager<>(cursor -> {
            Map<String, String> q = new LinkedHashMap<>(base);
            if (cursor != null) {
                q.put("cursor", cursor);
            }
            return list(path, q, options, factory, auth);
        }, start);
    }

    private static Map<String, String> query(PageParams params) {
        return params == null ? Collections.<String, String>emptyMap() : params.toQuery();
    }

    private static String queryString(Map<String, String> query) {
        if (query == null || query.isEmpty()) {
            return "";
        }
        StringBuilder sb = new StringBuilder();
        for (Map.Entry<String, String> e : query.entrySet()) {
            if (e.getValue() == null) {
                continue;
            }
            sb.append(sb.length() == 0 ? '?' : '&').append(encode(e.getKey())).append('=').append(encode(e.getValue()));
        }
        return sb.toString();
    }

    private static String encode(String s) {
        return URLEncoder.encode(s, StandardCharsets.UTF_8).replace("+", "%20");
    }

    private static String segment(String id) {
        if (id == null || id.trim().isEmpty()) {
            throw new IllegalArgumentException("id must not be empty");
        }
        return encode(id);
    }

    private static void requireNonNull(Object value, String name) {
        if (value == null) {
            throw new IllegalArgumentException(name + " must not be null");
        }
    }

    /* ================================================================ *
     * Builder
     * ================================================================ */

    /** Builder for {@link ReceivifyClient}. */
    public static final class Builder {
        private String apiUrl = DEFAULT_API_URL;
        private String apiKey;
        private List<String> webhookSecrets = Collections.emptyList();
        private Duration timeout = Duration.ofSeconds(30);
        private int maxRetries = 2;
        private Duration retryBaseDelay = Duration.ofMillis(500);
        private Duration maxRetryDelay = Duration.ofSeconds(20);
        private Transport transport;
        private String userAgentSuffix;
        private Clock clock = Clock.systemUTC();
        private Sleeper sleeper = d -> Thread.sleep(Math.max(0, d.toMillis()));

        private Builder() {
        }

        /**
         * @param apiUrl the deployment base URL; defaults to {@code https://receivify.io}
         * @return this builder
         */
        public Builder apiUrl(String apiUrl) {
            this.apiUrl = apiUrl;
            return this;
        }

        /**
         * @param apiKey the merchant {@code rcv_live_...} key; optional for public routes only
         * @return this builder
         */
        public Builder apiKey(String apiKey) {
            this.apiKey = apiKey;
            return this;
        }

        /**
         * @param secrets webhook signing secrets, CURRENT FIRST, previous second during a rotation
         * @return this builder
         */
        public Builder webhookSecrets(List<String> secrets) {
            this.webhookSecrets = secrets == null ? Collections.<String>emptyList() : new ArrayList<>(secrets);
            return this;
        }

        /**
         * @param secret the single webhook signing secret
         * @return this builder
         */
        public Builder webhookSecret(String secret) {
            this.webhookSecrets = secret == null ? Collections.<String>emptyList() : Collections.singletonList(secret);
            return this;
        }

        /**
         * @param timeout per-request timeout; defaults to 30 seconds
         * @return this builder
         */
        public Builder timeout(Duration timeout) {
            if (timeout == null || timeout.isNegative() || timeout.isZero()) {
                throw new IllegalArgumentException("timeout must be positive");
            }
            this.timeout = timeout;
            return this;
        }

        /**
         * @param maxRetries retries for SAFE requests; defaults to 2 (up to 3 attempts)
         * @return this builder
         */
        public Builder maxRetries(int maxRetries) {
            this.maxRetries = maxRetries;
            return this;
        }

        /**
         * @param delay the first backoff step (doubles each attempt); defaults to 500ms
         * @return this builder
         */
        public Builder retryBaseDelay(Duration delay) {
            this.retryBaseDelay = delay;
            return this;
        }

        /**
         * @param delay the ceiling on any single wait; defaults to 20 seconds
         * @return this builder
         */
        public Builder maxRetryDelay(Duration delay) {
            this.maxRetryDelay = delay;
            return this;
        }

        /**
         * @param transport the HTTP layer; defaults to {@link JdkHttpTransport}
         * @return this builder
         */
        public Builder transport(Transport transport) {
            this.transport = transport;
            return this;
        }

        /**
         * @param suffix appended to the {@code receivify-java/<version>} User-Agent
         * @return this builder
         */
        public Builder userAgentSuffix(String suffix) {
            this.userAgentSuffix = suffix;
            return this;
        }

        /**
         * @param clock the clock used to interpret {@code X-RateLimit-Reset} (tests)
         * @return this builder
         */
        public Builder clock(Clock clock) {
            this.clock = clock;
            return this;
        }

        /**
         * @param sleeper how retries wait (tests pass a no-op)
         * @return this builder
         */
        public Builder sleeper(Sleeper sleeper) {
            this.sleeper = sleeper;
            return this;
        }

        /** @return the client */
        public ReceivifyClient build() {
            return new ReceivifyClient(this);
        }
    }
}
