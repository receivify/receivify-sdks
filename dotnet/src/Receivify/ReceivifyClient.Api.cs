namespace Receivify;

/// <content>The endpoint surface. Every method is async and every list has an auto-paginating twin.</content>
public sealed partial class ReceivifyClient
{
    /* ================================================================ *
     * Invoices
     * ================================================================ */

    /// <summary>
    /// <c>POST /api/v1/invoices</c> — create an invoice. <b>Always 201</b>, including on both
    /// replay channels.
    /// </summary>
    /// <remarks>
    /// An <c>Idempotency-Key</c> replay returns the stored body, and a <c>reference</c> this
    /// merchant has already used returns the EXISTING invoice — both with a 201, never a 409.
    /// There is no way to ask for a second invoice under one reference.
    /// </remarks>
    /// <param name="request">Exactly one amount field is required.</param>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Invoice> CreateInvoiceAsync(
        InvoiceCreateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Required(
            await RequestAsync<Invoice>(
                HttpMethod.Post,
                "/api/v1/invoices",
                query: null,
                request,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: true,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/invoices");
    }

    /// <summary>
    /// <c>GET /api/v1/invoices/{id}</c> — the authoritative current state.
    /// </summary>
    /// <remarks>
    /// Poll this rather than relying on a webhook replay, which re-sends a frozen snapshot of the
    /// moment the event happened.
    /// </remarks>
    /// <param name="id">The invoice id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Invoice> GetInvoiceAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<Invoice>(
                HttpMethod.Get,
                "/api/v1/invoices/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/v1/invoices/{id}");
    }

    /// <summary><c>GET /api/v1/invoices</c> — one page, newest first.</summary>
    /// <param name="parameters">Paging and status filter.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<Invoice>> ListInvoicesAsync(
        InvoiceListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<Invoice>("/api/v1/invoices", InvoiceQuery(parameters), options, cancellationToken);

    /// <summary>
    /// Auto-paginating <see cref="ListInvoicesAsync"/>: follows <c>next_cursor</c> until
    /// <c>has_more</c> is false.
    /// </summary>
    /// <example>
    /// <code>
    /// await foreach (Invoice invoice in client.EnumerateInvoicesAsync(new() { Status = InvoiceStatus.Paid }))
    /// {
    ///     Console.WriteLine(invoice.Id + " " + invoice.AmountExpected);
    /// }
    /// </code>
    /// </example>
    /// <param name="parameters">Paging and status filter. <c>Cursor</c> is the starting point.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<Invoice> EnumerateInvoicesAsync(
        InvoiceListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> query = InvoiceQuery(parameters);

        return PaginateAsync<Invoice>(
            (cursor, token) => ListAsync<Invoice>(
                "/api/v1/invoices",
                WithCursor(query, cursor),
                options,
                token),
            parameters?.Cursor,
            cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/invoices/{id}/cancel</c> — cancel a pending, unpaid invoice.
    /// </summary>
    /// <remarks>
    /// The match is atomic, so a payment that lands mid-request wins. NOT idempotent: a second call
    /// on an already-cancelled invoice is <c>409 not_allowed</c>. Returns the RESOURCE, unlike the
    /// other cancels, which return an ack.
    /// </remarks>
    /// <param name="id">The invoice id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Invoice> CancelInvoiceAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<Invoice>(
                HttpMethod.Post,
                "/api/v1/invoices/" + segment + "/cancel",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/invoices/{id}/cancel");
    }

    /// <summary>
    /// <c>POST /api/v1/invoices/bulk</c> — bulk cancel, maximum 500 ids.
    /// </summary>
    /// <remarks>
    /// Naturally idempotent (the filter only matches pending, unpaid invoices belonging to you), but
    /// the endpoint does NOT accept <c>Idempotency-Key</c>. Compare
    /// <see cref="BulkResult.Requested"/> with <see cref="BulkResult.Cancelled"/>; the response does
    /// not say which ones were eligible.
    /// </remarks>
    /// <param name="ids">Invoice ids, 1 to 500.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<BulkResult> BulkCancelInvoicesAsync(
        IEnumerable<string> ids,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var body = new BulkInvoiceRequest { Ids = ids.ToList(), Action = "cancel" };

        return Required(
            await RequestAsync<BulkResult>(
                HttpMethod.Post,
                "/api/v1/invoices/bulk",
                query: null,
                body,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/invoices/bulk");
    }

    /// <summary>
    /// <c>GET /api/public/invoices/{id}</c> — unauthenticated, minimal projection. Safe to call
    /// from a thank-you page; server-side code should use <see cref="GetInvoiceAsync"/>.
    /// </summary>
    /// <param name="id">The invoice id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PublicInvoice> GetPublicInvoiceAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<PublicInvoice>(
                HttpMethod.Get,
                "/api/public/invoices/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: false,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/public/invoices/{id}");
    }

    /* ================================================================ *
     * Forwarding addresses
     *
     * The spec's operationIds call these "forwards"; the shared SDK contract calls them
     * "addresses". Both names are exposed and do the same thing.
     * ================================================================ */

    /// <summary>
    /// <c>POST /api/v1/address</c> — create, or return, a forwarding address.
    /// </summary>
    /// <remarks>
    /// <b>Deterministic.</b> The identity is <c>(merchant, resolved asset, destination,
    /// callback_url)</c>, so repeating this call does not burn a second HD derivation index. A 201
    /// means a new address was derived and a 200 means an existing forward came back — use
    /// <see cref="CreateAddressWithStatusAsync"/> if you need to tell them apart.
    /// <para>
    /// Read the canonical id from <see cref="Forward.Id"/>; <c>forward_id</c> carries the same value
    /// and is deprecated.
    /// </para>
    /// </remarks>
    /// <param name="request">Ticker and destination are required.</param>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Forward> CreateAddressAsync(
        ForwardCreateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReceivifyResponse<Forward> response =
            await CreateAddressWithStatusAsync(request, options, cancellationToken).ConfigureAwait(false);

        return Required(response.Value, "POST /api/v1/address");
    }

    /// <summary>
    /// The same call as <see cref="CreateAddressAsync"/>, returning the status code so you can tell
    /// a 201 (newly derived) from a 200 (an existing forward with this identity).
    /// </summary>
    /// <param name="request">Ticker and destination are required.</param>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyResponse<Forward>> CreateAddressWithStatusAsync(
        ForwardCreateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return SendAsync<Forward>(
            HttpMethod.Post,
            "/api/v1/address",
            query: null,
            request,
            options,
            requiresAuth: true,
            supportsIdempotencyKey: true,
            throwOnError: true,
            cancellationToken);
    }

    /// <summary>
    /// <c>GET /api/v1/address/{id}</c> — the forward plus its capped payment and callback log.
    /// </summary>
    /// <remarks>
    /// <see cref="ForwardDetail.Payments"/> is capped at 200 and
    /// <see cref="ForwardDetail.Callbacks"/> at 50. Neither is paginated and there is no cursor to
    /// reach the rest — the <c>*HasMore</c> flags just tell you the view is truncated.
    /// </remarks>
    /// <param name="id">The forward id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<ForwardDetail> GetAddressAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<ForwardDetail>(
                HttpMethod.Get,
                "/api/v1/address/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/v1/address/{id}");
    }

    /// <summary>
    /// <c>GET /api/v1/address</c> — one page of plain <see cref="Forward"/> objects, WITHOUT the
    /// payments and callbacks arrays the detail view adds.
    /// </summary>
    /// <param name="parameters">Paging, status and ticker filters.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<Forward>> ListAddressesAsync(
        ForwardListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<Forward>("/api/v1/address", ForwardQuery(parameters), options, cancellationToken);

    /// <summary>Auto-paginating <see cref="ListAddressesAsync"/>.</summary>
    /// <param name="parameters">Paging, status and ticker filters.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<Forward> EnumerateAddressesAsync(
        ForwardListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> query = ForwardQuery(parameters);

        return PaginateAsync<Forward>(
            (cursor, token) => ListAsync<Forward>(
                "/api/v1/address",
                WithCursor(query, cursor),
                options,
                token),
            parameters?.Cursor,
            cancellationToken);
    }

    /// <summary>
    /// <c>PATCH /api/v1/address/{id}</c> — the merchant kill switch.
    /// </summary>
    /// <remarks>
    /// Mutable: status and callback URL. <c>destination</c> and <c>ticker</c> are IMMUTABLE and are
    /// refused with <c>409 not_allowed</c> before the forward is even looked up, so a patch carrying
    /// an illegal field alongside a legal one can never be half-applied. Does NOT accept
    /// <c>Idempotency-Key</c>. Returns the plain <see cref="Forward"/> shape.
    /// </remarks>
    /// <param name="id">The forward id.</param>
    /// <param name="request">Only the properties you assign are sent.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Forward> UpdateAddressAsync(
        string id,
        ForwardUpdateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));
        ArgumentNullException.ThrowIfNull(request);

        return Required(
            await RequestAsync<Forward>(
                HttpMethod.Patch,
                "/api/v1/address/" + segment,
                query: null,
                request.ToPayload(),
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "PATCH /api/v1/address/{id}");
    }

    /// <summary>
    /// <c>DELETE /api/v1/address/{id}</c> — soft delete, setting the status to <c>disabled</c>.
    /// </summary>
    /// <remarks>
    /// The row is NEVER removed: money can still arrive at a deposit address after a merchant stops
    /// using it, and a deleted row would make that money unattributable. Idempotent.
    /// </remarks>
    /// <param name="id">The forward id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Forward> DisableAddressAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<Forward>(
                HttpMethod.Delete,
                "/api/v1/address/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "DELETE /api/v1/address/{id}");
    }

    /// <summary>Alias of <see cref="CreateAddressAsync"/>, matching the spec's <c>createForward</c> operationId.</summary>
    /// <param name="request">Ticker and destination are required.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Forward> CreateForwardAsync(
        ForwardCreateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        CreateAddressAsync(request, options, cancellationToken);

    /// <summary>Alias of <see cref="GetAddressAsync"/>.</summary>
    /// <param name="id">The forward id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ForwardDetail> GetForwardAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        GetAddressAsync(id, options, cancellationToken);

    /// <summary>Alias of <see cref="ListAddressesAsync"/>.</summary>
    /// <param name="parameters">Paging, status and ticker filters.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<Forward>> ListForwardsAsync(
        ForwardListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAddressesAsync(parameters, options, cancellationToken);

    /// <summary>Alias of <see cref="EnumerateAddressesAsync"/>.</summary>
    /// <param name="parameters">Paging, status and ticker filters.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<Forward> EnumerateForwardsAsync(
        ForwardListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        EnumerateAddressesAsync(parameters, options, cancellationToken);

    /// <summary>Alias of <see cref="UpdateAddressAsync"/>.</summary>
    /// <param name="id">The forward id.</param>
    /// <param name="request">Only the properties you assign are sent.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Forward> UpdateForwardAsync(
        string id,
        ForwardUpdateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        UpdateAddressAsync(id, request, options, cancellationToken);

    /// <summary>Alias of <see cref="DisableAddressAsync"/>.</summary>
    /// <param name="id">The forward id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<Forward> DisableForwardAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        DisableAddressAsync(id, options, cancellationToken);

    /* ================================================================ *
     * Catalogue (public — no key required)
     * ================================================================ */

    /// <summary>
    /// <c>GET /api/v1/tickers</c> — every <c>(asset, chain)</c> pair offered for forwarding.
    /// </summary>
    /// <remarks>
    /// A finite catalogue returned as ONE page, so <c>has_more</c> is honestly false. A ticker
    /// listed here can still be rejected with <c>bad_ticker</c> at create time. This is where
    /// <see cref="Ticker.Decimals"/> comes from — never hard-code it.
    /// </remarks>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<Ticker>> ListTickersAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<Ticker>(
            "/api/v1/tickers",
            new List<KeyValuePair<string, string>>(),
            options,
            cancellationToken,
            requiresAuth: false);

    /// <summary>
    /// <c>GET /api/v1/assets</c> — the coins enabled for invoicing.
    /// </summary>
    /// <remarks>
    /// <b>Display metadata, not machine data:</b> <see cref="Asset.Min"/> may be the literal word
    /// <c>"network"</c> and <see cref="Asset.NetworkFee"/> is English prose.
    /// </remarks>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<Asset>> ListAssetsAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<Asset>(
            "/api/v1/assets",
            new List<KeyValuePair<string, string>>(),
            options,
            cancellationToken,
            requiresAuth: false);

    /* ================================================================ *
     * Webhooks — endpoints, delivery log, secret rotation
     * ================================================================ */

    /// <summary>
    /// <c>GET /api/v1/webhooks/endpoints</c> — account-level endpoints.
    /// </summary>
    /// <remarks>
    /// These receive every INVOICE event and <c>sweep.sent</c>. They deliberately do NOT receive
    /// <c>forward.*</c>, which goes only to a forward's own <c>callback_url</c>.
    /// </remarks>
    /// <param name="parameters">Paging.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<WebhookEndpoint>> ListWebhookEndpointsAsync(
        PageParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<WebhookEndpoint>(
            "/api/v1/webhooks/endpoints",
            QueryFrom(parameters),
            options,
            cancellationToken);

    /// <summary>Auto-paginating <see cref="ListWebhookEndpointsAsync"/>.</summary>
    /// <param name="parameters">Paging.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<WebhookEndpoint> EnumerateWebhookEndpointsAsync(
        PageParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> query = QueryFrom(parameters);

        return PaginateAsync<WebhookEndpoint>(
            (cursor, token) => ListAsync<WebhookEndpoint>(
                "/api/v1/webhooks/endpoints",
                WithCursor(query, cursor),
                options,
                token),
            parameters?.Cursor,
            cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/webhooks/endpoints</c> — register an endpoint. Maximum 20 per account, and
    /// that quota failure is a <b>400</b> <c>limit_exceeded</c>, not a 409.
    /// </summary>
    /// <param name="url">Must be https in production. Normalised server-side, so what comes back may differ.</param>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<WebhookEndpoint> CreateWebhookEndpointAsync(
        string url,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        return Required(
            await RequestAsync<WebhookEndpoint>(
                HttpMethod.Post,
                "/api/v1/webhooks/endpoints",
                query: null,
                new WebhookEndpointCreateRequest { Url = url },
                options,
                requiresAuth: true,
                supportsIdempotencyKey: true,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/webhooks/endpoints");
    }

    /// <summary>
    /// <c>PATCH /api/v1/webhooks/endpoints/{id}</c> — mutable: <c>active</c> and <c>url</c>.
    /// Prefer <c>active: false</c> over a delete if you may want the endpoint back.
    /// </summary>
    /// <param name="id">The endpoint id.</param>
    /// <param name="request">The fields to change.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<WebhookEndpoint> UpdateWebhookEndpointAsync(
        string id,
        WebhookEndpointUpdateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));
        ArgumentNullException.ThrowIfNull(request);

        return Required(
            await RequestAsync<WebhookEndpoint>(
                HttpMethod.Patch,
                "/api/v1/webhooks/endpoints/" + segment,
                query: null,
                request,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "PATCH /api/v1/webhooks/endpoints/{id}");
    }

    /// <summary>
    /// <c>DELETE /api/v1/webhooks/endpoints/{id}</c> — a HARD delete. Not idempotent: a second call
    /// is <c>404 not_found</c>.
    /// </summary>
    /// <param name="id">The endpoint id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<DeletedAck> DeleteWebhookEndpointAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<DeletedAck>(
                HttpMethod.Delete,
                "/api/v1/webhooks/endpoints/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "DELETE /api/v1/webhooks/endpoints/{id}");
    }

    /// <summary>
    /// <c>GET /api/v1/webhooks/deliveries</c> — the delivery log for BOTH families.
    /// </summary>
    /// <remarks>
    /// Every row carries both <c>invoice_id</c> and <c>forward_id</c>, exactly one of them non-null,
    /// so a failed delivery is always attributable. The delivered body itself is not returned here.
    /// </remarks>
    /// <param name="parameters">Paging, event and status filters. A typo in either filter is a 400.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<WebhookDelivery>> ListWebhookDeliveriesAsync(
        WebhookDeliveryListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<WebhookDelivery>(
            "/api/v1/webhooks/deliveries",
            DeliveryQuery(parameters),
            options,
            cancellationToken);

    /// <summary>Auto-paginating <see cref="ListWebhookDeliveriesAsync"/>.</summary>
    /// <param name="parameters">Paging, event and status filters.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<WebhookDelivery> EnumerateWebhookDeliveriesAsync(
        WebhookDeliveryListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> query = DeliveryQuery(parameters);

        return PaginateAsync<WebhookDelivery>(
            (cursor, token) => ListAsync<WebhookDelivery>(
                "/api/v1/webhooks/deliveries",
                WithCursor(query, cursor),
                options,
                token),
            parameters?.Cursor,
            cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/webhooks/deliveries/{id}/replay</c> — re-queue a delivery.
    /// </summary>
    /// <remarks>
    /// <b>The body is FROZEN.</b> A replay re-sends the original bytes, with the same
    /// <c>sequence</c> and the same <c>created_at</c>. It does NOT reflect current state — poll the
    /// resource for that.
    /// </remarks>
    /// <param name="id">A 24-character hex ObjectId from the delivery log.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<ReplayedAck> ReplayWebhookDeliveryAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<ReplayedAck>(
                HttpMethod.Post,
                "/api/v1/webhooks/deliveries/" + segment + "/replay",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/webhooks/deliveries/{id}/replay");
    }

    /// <summary>
    /// <c>GET /api/v1/webhooks/secret</c> — rotation METADATA only, never the secret.
    /// </summary>
    /// <remarks>
    /// <see cref="WebhookSecretState.PrevActive"/> answers the one question a running integration
    /// has: must my receiver still be trying the old secret?
    /// </remarks>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<WebhookSecretState> GetWebhookSecretStateAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Required(
            await RequestAsync<WebhookSecretState>(
                HttpMethod.Get,
                "/api/v1/webhooks/secret",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/v1/webhooks/secret");
    }

    /// <summary>
    /// <c>POST /api/v1/webhooks/secret/rotate</c> — mint a new signing secret and return it
    /// <b>once</b>. Answers 200, not 201.
    /// </summary>
    /// <remarks>
    /// The previous secret stays acceptable until
    /// <see cref="WebhookSecretRotated.PrevExpiresAt"/>. Until then pass BOTH to
    /// <c>VerifyWebhook</c>. An <c>Idempotency-Key</c> replay deliberately returns
    /// <see cref="WebhookSecretRotated.Secret"/> as null — the secret is never persisted for the
    /// replay window. If you lost it, rotate again.
    /// </remarks>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<WebhookSecretRotated> RotateWebhookSecretAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Required(
            await RequestAsync<WebhookSecretRotated>(
                HttpMethod.Post,
                "/api/v1/webhooks/secret/rotate",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: true,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/webhooks/secret/rotate");
    }

    /// <summary>
    /// <c>POST /api/v1/webhooks/test</c> — send a signed <c>test</c> event to your own registered
    /// endpoints.
    /// </summary>
    /// <remarks>
    /// SSRF-safe: only already-registered URLs are reachable. Returns 200 even when every delivery
    /// failed — compare <see cref="WebhookTestResult.Delivered"/> with
    /// <see cref="WebhookTestResult.Total"/>. The sample body is invoice-shaped even for a
    /// forwarding-only merchant.
    /// </remarks>
    /// <param name="url">Target one registered active endpoint, or null to hit every one.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<WebhookTestResult> SendTestWebhookAsync(
        string? url = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Required(
            await RequestAsync<WebhookTestResult>(
                HttpMethod.Post,
                "/api/v1/webhooks/test",
                query: null,
                new WebhookTestRequest { Url = url },
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/webhooks/test");
    }

    /* ================================================================ *
     * Payment links
     * ================================================================ */

    /// <summary><c>GET /api/v1/payment-links</c> — one page, newest first.</summary>
    /// <param name="parameters">Paging and the active filter.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<PaymentLink>> ListPaymentLinksAsync(
        PaymentLinkListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<PaymentLink>(
            "/api/v1/payment-links",
            PaymentLinkQuery(parameters),
            options,
            cancellationToken);

    /// <summary>Auto-paginating <see cref="ListPaymentLinksAsync"/>.</summary>
    /// <param name="parameters">Paging and the active filter.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<PaymentLink> EnumeratePaymentLinksAsync(
        PaymentLinkListParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> query = PaymentLinkQuery(parameters);

        return PaginateAsync<PaymentLink>(
            (cursor, token) => ListAsync<PaymentLink>(
                "/api/v1/payment-links",
                WithCursor(query, cursor),
                options,
                token),
            parameters?.Cursor,
            cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/payment-links</c> — returns the FULL resource, the same shape every other
    /// payment-link operation returns.
    /// </summary>
    /// <param name="request">Title and optional decimal-BTC amount.</param>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PaymentLink> CreatePaymentLinkAsync(
        PaymentLinkCreateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Required(
            await RequestAsync<PaymentLink>(
                HttpMethod.Post,
                "/api/v1/payment-links",
                query: null,
                request,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: true,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/payment-links");
    }

    /// <summary>
    /// <c>GET /api/v1/payment-links/{id}</c> — the merchant-side read. Unlike the public route it
    /// shows deactivated links too.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PaymentLink> GetPaymentLinkAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<PaymentLink>(
                HttpMethod.Get,
                "/api/v1/payment-links/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/v1/payment-links/{id}");
    }

    /// <summary><c>PATCH /api/v1/payment-links/{id}</c> — mutable: active, title and the amount.</summary>
    /// <param name="id">The link id.</param>
    /// <param name="request">Only the properties you assign are sent.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PaymentLink> UpdatePaymentLinkAsync(
        string id,
        PaymentLinkUpdateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));
        ArgumentNullException.ThrowIfNull(request);

        return Required(
            await RequestAsync<PaymentLink>(
                HttpMethod.Patch,
                "/api/v1/payment-links/" + segment,
                query: null,
                request.ToPayload(),
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "PATCH /api/v1/payment-links/{id}");
    }

    /// <summary>
    /// <c>DELETE /api/v1/payment-links/{id}</c> — soft deactivate. Invoices already minted from the
    /// link stay payable; this is not a way to cancel money in flight. Idempotent.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PaymentLink> DeactivatePaymentLinkAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<PaymentLink>(
                HttpMethod.Delete,
                "/api/v1/payment-links/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "DELETE /api/v1/payment-links/{id}");
    }

    /// <summary>
    /// <c>GET /api/public/links/{id}</c> — unauthenticated. A deactivated link answers 404, not 409.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PublicPaymentLink> GetPublicPaymentLinkAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<PublicPaymentLink>(
                HttpMethod.Get,
                "/api/public/links/" + segment,
                query: null,
                body: null,
                options,
                requiresAuth: false,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/public/links/{id}");
    }

    /* ================================================================ *
     * Recurring schedules
     * ================================================================ */

    /// <summary><c>GET /api/v1/recurring</c> — one page, newest first.</summary>
    /// <param name="parameters">Paging.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<RecurringSchedule>> ListRecurringAsync(
        PageParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<RecurringSchedule>(
            "/api/v1/recurring",
            QueryFrom(parameters),
            options,
            cancellationToken);

    /// <summary>Auto-paginating <see cref="ListRecurringAsync"/>.</summary>
    /// <param name="parameters">Paging.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<RecurringSchedule> EnumerateRecurringAsync(
        PageParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> query = QueryFrom(parameters);

        return PaginateAsync<RecurringSchedule>(
            (cursor, token) => ListAsync<RecurringSchedule>(
                "/api/v1/recurring",
                WithCursor(query, cursor),
                options,
                token),
            parameters?.Cursor,
            cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/recurring</c> — scheduled billing, not auto-debit: each cycle GENERATES a
    /// fresh invoice, because crypto has no pull mechanism.
    /// </summary>
    /// <param name="request">Provide either a fiat amount or a coin amount, plus an interval.</param>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<RecurringSchedule> CreateRecurringAsync(
        RecurringCreateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Required(
            await RequestAsync<RecurringSchedule>(
                HttpMethod.Post,
                "/api/v1/recurring",
                query: null,
                request,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: true,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/recurring");
    }

    /// <summary>
    /// <c>POST /api/v1/recurring/{id}/cancel</c> — returns an ACK, not the resource. Cancelling an
    /// already-cancelled schedule is <c>409 not_allowed</c>.
    /// </summary>
    /// <param name="id">The schedule id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<CancelledAck> CancelRecurringAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<CancelledAck>(
                HttpMethod.Post,
                "/api/v1/recurring/" + segment + "/cancel",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/recurring/{id}/cancel");
    }

    /* ================================================================ *
     * API keys
     * ================================================================ */

    /// <summary>
    /// <c>GET /api/v1/keys</c> — prefixes only. The secret is never stored, so it can never be
    /// listed. Revoked keys are included, flagged by <see cref="ApiKey.Revoked"/>.
    /// </summary>
    /// <param name="parameters">Paging.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public Task<ReceivifyList<ApiKey>> ListApiKeysAsync(
        PageParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ListAsync<ApiKey>("/api/v1/keys", QueryFrom(parameters), options, cancellationToken);

    /// <summary>Auto-paginating <see cref="ListApiKeysAsync"/>.</summary>
    /// <param name="parameters">Paging.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IAsyncEnumerable<ApiKey> EnumerateApiKeysAsync(
        PageParams? parameters = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, string>> query = QueryFrom(parameters);

        return PaginateAsync<ApiKey>(
            (cursor, token) => ListAsync<ApiKey>(
                "/api/v1/keys",
                WithCursor(query, cursor),
                options,
                token),
            parameters?.Cursor,
            cancellationToken);
    }

    /// <summary>
    /// <c>POST /api/v1/keys</c> — the raw secret is returned ONCE, here, and is never persisted.
    /// </summary>
    /// <remarks>
    /// An <c>Idempotency-Key</c> replay returns the same <see cref="ApiKeyCreated.Id"/> and prefix
    /// but <see cref="ApiKeyCreated.ApiKeyValue"/> is <b>null</b>. The bug the header exists to fix
    /// — a retry minting a SECOND live credential — is fixed regardless.
    /// </remarks>
    /// <param name="label">A human label. Defaults to <c>default</c> server-side.</param>
    /// <param name="options">Per-call options. This endpoint accepts <c>Idempotency-Key</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<ApiKeyCreated> CreateApiKeyAsync(
        string? label = null,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Required(
            await RequestAsync<ApiKeyCreated>(
                HttpMethod.Post,
                "/api/v1/keys",
                query: null,
                new ApiKeyCreateRequest { Label = label },
                options,
                requiresAuth: true,
                supportsIdempotencyKey: true,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/keys");
    }

    /// <summary>
    /// <c>POST /api/v1/keys/{id}/revoke</c> — pass the <see cref="ApiKey.Id"/>, NOT the prefix.
    /// Revoking an already-revoked or unknown key is <c>409 not_allowed</c>.
    /// </summary>
    /// <param name="id">The key id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<RevokedAck> RevokeApiKeyAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<RevokedAck>(
                HttpMethod.Post,
                "/api/v1/keys/" + segment + "/revoke",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/keys/{id}/revoke");
    }

    /* ================================================================ *
     * Settings, summary, payouts
     * ================================================================ */

    /// <summary><c>GET /api/v1/settings</c>.</summary>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Settings> GetSettingsAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Required(
            await RequestAsync<Settings>(
                HttpMethod.Get,
                "/api/v1/settings",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/v1/settings");
    }

    /// <summary>
    /// <c>PATCH /api/v1/settings</c> — returns an ACK carrying only what changed, NOT the
    /// <see cref="Settings"/> resource, so it is not interchangeable with the GET.
    /// </summary>
    /// <param name="request">The fields to change.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<SettingsUpdateAck> UpdateSettingsAsync(
        SettingsUpdateRequest request,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Required(
            await RequestAsync<SettingsUpdateAck>(
                HttpMethod.Patch,
                "/api/v1/settings",
                query: null,
                request,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "PATCH /api/v1/settings");
    }

    /// <summary>
    /// <c>GET /api/v1/summary</c> — the dashboard composite. Its <see cref="FxSnapshot"/> rates are
    /// advisory JSON numbers; nothing here is paginated.
    /// </summary>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<Summary> GetSummaryAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Required(
            await RequestAsync<Summary>(
                HttpMethod.Get,
                "/api/v1/summary",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/v1/summary");
    }

    /// <summary>
    /// <c>GET /api/v1/payouts</c> — the LIVE payout map plus every open staged change.
    /// </summary>
    /// <remarks>
    /// <see cref="PayoutState.Pending"/> is a bare, unbounded array — not a list envelope — so there
    /// is no paginator for it.
    /// </remarks>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PayoutState> GetPayoutsAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return Required(
            await RequestAsync<PayoutState>(
                HttpMethod.Get,
                "/api/v1/payouts",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "GET /api/v1/payouts");
    }

    /// <summary>
    /// <c>PATCH /api/v1/payouts</c> — stage payout-address changes.
    /// </summary>
    /// <remarks>
    /// Nothing is applied inline: a change is staged, may require email confirmation, and then waits
    /// out a timelock before the sweeper will send to it. An empty string CLEARS that asset's payout
    /// address. <see cref="PayoutStageAck.Applied"/> is non-empty only when the deployment runs with
    /// the timelock at zero AND email confirmation off.
    /// </remarks>
    /// <param name="addresses">Asset symbol to destination address.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<PayoutStageAck> StagePayoutChangesAsync(
        IReadOnlyDictionary<string, string> addresses,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        return Required(
            await RequestAsync<PayoutStageAck>(
                HttpMethod.Patch,
                "/api/v1/payouts",
                query: null,
                new PayoutStageRequest { Addresses = addresses },
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "PATCH /api/v1/payouts");
    }

    /// <summary>
    /// <c>POST /api/v1/payouts/changes/{id}/cancel</c> — returns an ACK. A change that is already
    /// applied or cancelled is <c>409 not_allowed</c>, indistinguishable from an id that never
    /// existed.
    /// </summary>
    /// <param name="id">The staged change id.</param>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<CancelledAck> CancelPayoutChangeAsync(
        string id,
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string segment = RequirePathSegment(id, nameof(id));

        return Required(
            await RequestAsync<CancelledAck>(
                HttpMethod.Post,
                "/api/v1/payouts/changes/" + segment + "/cancel",
                query: null,
                body: null,
                options,
                requiresAuth: true,
                supportsIdempotencyKey: false,
                cancellationToken).ConfigureAwait(false),
            "POST /api/v1/payouts/changes/{id}/cancel");
    }

    /* ================================================================ *
     * System
     * ================================================================ */

    /// <summary>
    /// <c>GET /api/health</c> — an OPERATOR report.
    /// </summary>
    /// <remarks>
    /// <b>Its 503 body does not conform to the error contract</b>: it is the same
    /// <c>{status, time, checks, alerts}</c> object as the 200, with no <c>error</c> key. This
    /// method therefore returns the report on both statuses instead of throwing. Read
    /// <see cref="HealthReport.IsDegraded"/> and <see cref="HealthReport.Checks"/>; do not try to
    /// map it to an error code.
    /// </remarks>
    /// <param name="options">Per-call options.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<HealthReport> GetHealthAsync(
        RequestOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ReceivifyResponse<HealthReport> response = await SendAsync<HealthReport>(
            HttpMethod.Get,
            "/api/health",
            query: null,
            body: null,
            options,
            requiresAuth: false,
            supportsIdempotencyKey: false,
            throwOnError: false,
            cancellationToken).ConfigureAwait(false);

        return Required(response.Value, "GET /api/health");
    }

    /* ================================================================ *
     * Query builders
     * ================================================================ */

    private static List<KeyValuePair<string, string>> InvoiceQuery(InvoiceListParams? parameters)
    {
        List<KeyValuePair<string, string>> query = QueryFrom(parameters);
        AddIfPresent(query, "status", parameters?.Status);
        return query;
    }

    private static List<KeyValuePair<string, string>> ForwardQuery(ForwardListParams? parameters)
    {
        List<KeyValuePair<string, string>> query = QueryFrom(parameters);
        AddIfPresent(query, "status", parameters?.Status);
        AddIfPresent(query, "ticker", parameters?.Ticker);
        return query;
    }

    private static List<KeyValuePair<string, string>> PaymentLinkQuery(PaymentLinkListParams? parameters)
    {
        List<KeyValuePair<string, string>> query = QueryFrom(parameters);
        AddIfPresent(query, "active", parameters?.Active);
        return query;
    }

    private static List<KeyValuePair<string, string>> DeliveryQuery(WebhookDeliveryListParams? parameters)
    {
        List<KeyValuePair<string, string>> query = QueryFrom(parameters);
        AddIfPresent(query, "event", parameters?.Event);
        AddIfPresent(query, "status", parameters?.Status);
        return query;
    }
}
