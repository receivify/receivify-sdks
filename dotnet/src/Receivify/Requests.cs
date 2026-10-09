using System.Text.Json;
using System.Text.Json.Serialization;

namespace Receivify;

/// <summary>
/// Per-call options every method accepts.
/// </summary>
public sealed class RequestOptions
{
    /// <summary>
    /// A client-generated unique string, scoped to <c>(merchant, endpoint, key)</c> and retained
    /// for 24 hours. A replay returns the ORIGINAL body AND the ORIGINAL status code, plus an
    /// <c>Idempotent-Replay: true</c> header.
    /// </summary>
    /// <remarks>
    /// <b>Only some endpoints accept it</b>, and it is what unlocks automatic retries on a
    /// mutating call — see the retry note on <see cref="ReceivifyClient"/>. Supplying one on an
    /// endpoint that does not declare support still sends the header, but the SDK will not retry,
    /// because the server would not deduplicate it. Values over 255 characters are truncated
    /// server-side, so two keys sharing a 255-character prefix collide.
    /// </remarks>
    public string? IdempotencyKey { get; set; }

    /// <summary>Overrides the client-wide timeout for this one call.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Extra request headers. The API key is never one of these.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; set; }
}

/// <summary>Shared paging arguments.</summary>
public class PageParams
{
    /// <summary>
    /// Page size, 1 to 200. Anything larger is silently clamped to 200; a zero, a negative or a
    /// non-integer is a <c>400 bad_request</c>, never a silent fallback.
    /// </summary>
    public int? Limit { get; set; }

    /// <summary>
    /// An opaque keyset cursor from a previous response's <c>next_cursor</c>. A malformed cursor is
    /// a <c>400 bad_request</c>, never a silent reset to page one. Treat the value as opaque.
    /// </summary>
    public string? Cursor { get; set; }
}

/// <summary>Filters for <c>GET /api/v1/invoices</c>.</summary>
public sealed class InvoiceListParams : PageParams
{
    /// <summary>
    /// Exact-match filter on the invoice status. Not validated against the enum on this endpoint —
    /// an unknown value simply matches nothing rather than erroring.
    /// </summary>
    public string? Status { get; set; }
}

/// <summary>Filters for <c>GET /api/v1/address</c>.</summary>
public sealed class ForwardListParams : PageParams
{
    /// <summary>
    /// Validated against <see cref="ForwardStatus"/> — unlike the invoice list, an unknown value
    /// here is a <c>400 bad_request</c>.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>Exact match on the ticker string as it was supplied at create time, lower-cased.</summary>
    public string? Ticker { get; set; }
}

/// <summary>Filters for <c>GET /api/v1/payment-links</c>.</summary>
public sealed class PaymentLinkListParams : PageParams
{
    /// <summary>
    /// <c>true</c> also matches links created before the field existed. Any value other than true
    /// or false is a <c>400 bad_request</c>.
    /// </summary>
    public bool? Active { get; set; }
}

/// <summary>Filters for <c>GET /api/v1/webhooks/deliveries</c>.</summary>
public sealed class WebhookDeliveryListParams : PageParams
{
    /// <summary>Must be a member of <see cref="EventNames"/>; anything else is a <c>400 bad_request</c>.</summary>
    public string? Event { get; set; }

    /// <summary><c>pending</c>, <c>delivered</c> or <c>failed</c>. Anything else is a <c>400 bad_request</c>.</summary>
    public string? Status { get; set; }
}

/// <summary>
/// The body of <c>POST /api/v1/invoices</c>.
/// </summary>
/// <remarks>
/// Exactly one amount field is required. They are tried in this order and the first present one
/// wins: <see cref="AmountFiat"/> (+<see cref="Currency"/>), <see cref="Amount"/>,
/// <see cref="AmountSats"/>, <see cref="AmountBtc"/>. Unlike the forwarding create path, NO field
/// aliases are accepted here.
/// </remarks>
public sealed record InvoiceCreateRequest
{
    /// <summary>Lower-cased. Must be enabled on this deployment and settleable. Defaults to <c>btc</c>.</summary>
    [JsonPropertyName("asset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asset { get; init; }

    /// <summary>
    /// Only consulted for EVM assets, but defaulted for all. An <c>(asset, chain)</c> pair that is
    /// not a real native coin or registered token on that chain is <c>400 asset_unavailable</c>.
    /// </summary>
    [JsonPropertyName("chain")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Chain { get; init; }

    /// <summary>
    /// A fiat price, converted at the current rate and LOCKED onto the invoice. Send it as a
    /// decimal STRING such as <c>"25.00"</c>: the request side also tolerates a JSON number, but a
    /// string is what round-trips exactly and what every response uses.
    /// </summary>
    [JsonPropertyName("amount_fiat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AmountFiat { get; init; }

    /// <summary>ISO-4217 code for <see cref="AmountFiat"/>. Defaults to USD inside the pricing layer.</summary>
    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    /// <summary>A decimal amount in the coin's own units, as a string.</summary>
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Amount { get; init; }

    /// <summary>
    /// Exact base units as an integer string. Named <c>sats</c> for historical reasons but it
    /// applies to every asset — this is the precise way to ask for an amount.
    /// </summary>
    [JsonPropertyName("amount_sats")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AmountSats { get; init; }

    /// <summary>Legacy. Only honoured when <see cref="Asset"/> is <c>btc</c>.</summary>
    [JsonPropertyName("amount_btc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AmountBtc { get; init; }

    /// <summary>
    /// Your business key. It ALSO de-duplicates: reusing a reference returns the EXISTING invoice
    /// with a 201. There is no way to ask for a second invoice under one reference.
    /// </summary>
    [JsonPropertyName("reference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reference { get; init; }

    /// <summary>
    /// Per-invoice webhook target, in addition to every registered account-level endpoint. Must be
    /// https in production.
    /// </summary>
    [JsonPropertyName("callback_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackUrl { get; init; }

    /// <summary>Arbitrary data echoed back on the invoice.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>
    /// Add our commission ON TOP of the amount instead of taking it out: <c>true</c> quotes the payer
    /// a grossed-up <c>amount_expected</c> so you receive the amount you asked for in full;
    /// <c>false</c> forces fee-out-of-amount for this invoice. Leave it <c>null</c> (omitted) to use
    /// the account default — omitting it is NOT the same as sending <c>false</c>.
    /// </summary>
    [JsonPropertyName("add_fee_ontop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AddFeeOntop { get; init; }
}

/// <summary>
/// The body of <c>POST /api/v1/address</c>.
/// </summary>
/// <remarks>
/// Creation is deterministic: the identity of a forward is
/// <c>(merchant, resolved asset, destination, callback_url)</c>, backed by a unique index, so
/// repeating the call does not burn a second HD derivation index. A 201 means a new address was
/// derived; a 200 means an existing forward with that identity came back.
/// </remarks>
public sealed record ForwardCreateRequest
{
    /// <summary>Trimmed and lower-cased. See <c>ListTickersAsync</c> for the catalogue.</summary>
    [JsonPropertyName("ticker")]
    public string Ticker { get; init; } = string.Empty;

    /// <summary>
    /// Your wallet. Validated against the chain's address format. EVM destinations are compared
    /// case-insensitively for dedupe, because EIP-55 casing is cosmetic.
    /// </summary>
    [JsonPropertyName("destination")]
    public string Destination { get; init; } = string.Empty;

    /// <summary>
    /// Where <c>forward.*</c> events go. <b>Part of the forward's deterministic identity</b> — a
    /// different callback creates a DIFFERENT forward. Must be https in production.
    /// </summary>
    [JsonPropertyName("callback")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Callback { get; init; }

    /// <summary>
    /// Confirmations before a payment is credited. A non-integer or non-positive value falls back
    /// to the deployment default instead of erroring.
    /// </summary>
    [JsonPropertyName("confirmations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Confirmations { get; init; }

    /// <summary>Arbitrary data stored on the forward.</summary>
    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>
/// The body of <c>PATCH /api/v1/address/{id}</c> — the merchant kill switch.
/// </summary>
/// <remarks>
/// <para>
/// Mutable: <see cref="Status"/> (<c>active</c> to <c>paused</c> and back) and
/// <see cref="CallbackUrl"/>. <c>destination</c> and <c>ticker</c> are IMMUTABLE and are refused
/// with <c>409 not_allowed</c> BEFORE the forward is even looked up — repointing a live deposit
/// address is exactly what a leaked key would be used for.
/// </para>
/// <para>
/// This type tracks which properties you actually set, so assigning
/// <c>CallbackUrl = null</c> sends <c>"callback_url": null</c> (which CLEARS it) while leaving the
/// property alone omits the key entirely. That distinction is load-bearing.
/// </para>
/// </remarks>
public sealed class ForwardUpdateRequest
{
    private string? _status;
    private string? _callbackUrl;
    private bool _statusSet;
    private bool _callbackUrlSet;

    /// <summary>Only <c>active</c> and <c>paused</c> are assignable. Use DELETE for <c>disabled</c>.</summary>
    public string? Status
    {
        get => _status;
        set
        {
            _status = value;
            _statusSet = true;
        }
    }

    /// <summary>
    /// Setting null (or an empty string) CLEARS the callback. Changing it recomputes the forward's
    /// dedupe identity; if that would collide with another of your forwards the call is
    /// <c>409 already_exists</c>.
    /// </summary>
    public string? CallbackUrl
    {
        get => _callbackUrl;
        set
        {
            _callbackUrl = value;
            _callbackUrlSet = true;
        }
    }

    internal Dictionary<string, object?> ToPayload()
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_statusSet)
        {
            payload["status"] = _status;
        }

        if (_callbackUrlSet)
        {
            payload["callback_url"] = _callbackUrl;
        }

        return payload;
    }
}

/// <summary>The body of <c>POST /api/v1/payment-links</c>.</summary>
public sealed record PaymentLinkCreateRequest
{
    /// <summary>Truncated to 120 characters. An empty result is stored as null.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>
    /// A decimal BTC amount as a string. Omit for an open-amount link the payer prices themselves.
    /// Note the asymmetry: you set <c>amount_btc</c> and read
    /// <see cref="PaymentLink.AmountSats"/>.
    /// </summary>
    [JsonPropertyName("amount_btc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AmountBtc { get; init; }
}

/// <summary>
/// The body of <c>PATCH /api/v1/payment-links/{id}</c>.
/// </summary>
/// <remarks>
/// Mutable: <see cref="Active"/>, <see cref="Title"/> and the amount. Setting
/// <see cref="AmountBtc"/> to null converts a fixed link into an open-amount one, so — as with
/// <see cref="ForwardUpdateRequest"/> — this type tracks which properties you actually assigned.
/// <para>
/// Setting <c>Active = false</c> stops the link minting further invoices. Invoices ALREADY created
/// from it are untouched and remain payable: deactivating a link is not a way to cancel money in
/// flight.
/// </para>
/// </remarks>
public sealed class PaymentLinkUpdateRequest
{
    private bool? _active;
    private string? _title;
    private string? _amountBtc;
    private bool _activeSet;
    private bool _titleSet;
    private bool _amountBtcSet;

    /// <summary>Must be a real boolean — a string is a <c>400 bad_request</c>.</summary>
    public bool? Active
    {
        get => _active;
        set
        {
            _active = value;
            _activeSet = true;
        }
    }

    /// <summary>Truncated to 120 characters.</summary>
    public string? Title
    {
        get => _title;
        set
        {
            _title = value;
            _titleSet = true;
        }
    }

    /// <summary>Decimal BTC as a string. Null or an empty string makes the link open-amount.</summary>
    public string? AmountBtc
    {
        get => _amountBtc;
        set
        {
            _amountBtc = value;
            _amountBtcSet = true;
        }
    }

    internal Dictionary<string, object?> ToPayload()
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (_activeSet)
        {
            payload["active"] = _active;
        }

        if (_titleSet)
        {
            payload["title"] = _title;
        }

        if (_amountBtcSet)
        {
            payload["amount_btc"] = _amountBtc;
        }

        return payload;
    }
}

/// <summary>
/// The body of <c>POST /api/v1/recurring</c>. Provide EITHER <see cref="AmountFiat"/>
/// (+<see cref="Currency"/>) OR <see cref="Amount"/>; both absent is a <c>400 bad_request</c>.
/// </summary>
public sealed record RecurringCreateRequest
{
    /// <summary>Display title, max 120 characters.</summary>
    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; init; }

    /// <summary>Lower-case asset symbol. Defaults to <c>btc</c>.</summary>
    [JsonPropertyName("asset")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Asset { get; init; }

    /// <summary>A fiat price as a decimal string. The request side also tolerates a JSON number; prefer the string.</summary>
    [JsonPropertyName("amount_fiat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AmountFiat { get; init; }

    /// <summary>ISO-4217 for <see cref="AmountFiat"/>. Defaults to USD.</summary>
    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    /// <summary>A decimal amount in the coin, as a string.</summary>
    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Amount { get; init; }

    /// <summary>Days between cycles. Floored to an integer and clamped to at least 1.</summary>
    [JsonPropertyName("interval_days")]
    public int IntervalDays { get; init; }

    /// <summary>
    /// Copied onto every invoice this schedule generates. NOT validated at create time, unlike
    /// <c>POST /api/v1/invoices</c>.
    /// </summary>
    [JsonPropertyName("callback_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CallbackUrl { get; init; }
}

/// <summary>The body of <c>POST /api/v1/webhooks/endpoints</c>. Maximum 20 endpoints per account.</summary>
public sealed record WebhookEndpointCreateRequest
{
    /// <summary>Must be https in production. Normalised through the WHATWG URL parser.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

/// <summary>The body of <c>PATCH /api/v1/webhooks/endpoints/{id}</c>. Mutable: <c>active</c>, <c>url</c>.</summary>
public sealed record WebhookEndpointUpdateRequest
{
    /// <summary>The pause switch. An inactive endpoint receives nothing.</summary>
    [JsonPropertyName("active")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Active { get; init; }

    /// <summary>Must be https in production.</summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }
}

/// <summary>The body of <c>POST /api/v1/webhooks/test</c>.</summary>
public sealed record WebhookTestRequest
{
    /// <summary>
    /// Target exactly one registered ACTIVE endpoint. Omit to hit every one. SSRF-safe by
    /// construction: naming a URL that is not already registered is <c>400 no_target</c>, never a
    /// fetch.
    /// </summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }
}

/// <summary>The body of <c>POST /api/v1/keys</c>.</summary>
public sealed record ApiKeyCreateRequest
{
    /// <summary>A human label. Defaults to <c>default</c>.</summary>
    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; init; }
}

/// <summary>The body of <c>PATCH /api/v1/settings</c>. Returns an ACK, not the resource.</summary>
public sealed record SettingsUpdateRequest
{
    /// <summary>An ISO-4217 code from the supported set. Upper-cased before storage.</summary>
    [JsonPropertyName("display_currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayCurrency { get; init; }
}

internal sealed record BulkInvoiceRequest
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<string> Ids { get; init; } = Array.Empty<string>();

    [JsonPropertyName("action")]
    public string Action { get; init; } = "cancel";
}

internal sealed record PayoutStageRequest
{
    [JsonPropertyName("addresses")]
    public IReadOnlyDictionary<string, string> Addresses { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// A single request/response round trip, when you need more than the decoded body.
/// </summary>
/// <remarks>
/// Reach for this to tell a 200 from a 201 on <c>POST /api/v1/address</c> (existing versus newly
/// derived), to read <see cref="IdempotentReplay"/>, or to read the <c>X-RateLimit-*</c> headers.
/// </remarks>
/// <typeparam name="T">The decoded body type.</typeparam>
public sealed class ReceivifyResponse<T>
{
    internal ReceivifyResponse(
        int statusCode,
        IReadOnlyDictionary<string, string> headers,
        T? value,
        string? rawBody)
    {
        StatusCode = statusCode;
        Headers = headers;
        Value = value;
        RawBody = rawBody;
    }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Response headers, keys lower-cased.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The decoded body.</summary>
    public T? Value { get; }

    /// <summary>The untouched response body, for logging or for a field this SDK does not model.</summary>
    public string? RawBody { get; }

    /// <summary>
    /// True when this response was replayed from a stored <c>Idempotency-Key</c> record rather than
    /// produced by a fresh operation. Remember that a replayed
    /// <see cref="ApiKeyCreated.ApiKeyValue"/> or <see cref="WebhookSecretRotated.Secret"/> is null.
    /// </summary>
    public bool IdempotentReplay =>
        Headers.TryGetValue("idempotent-replay", out string? value)
        && string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parse the raw body as a <see cref="JsonElement"/> when you need a field the SDK does not model.</summary>
    public JsonElement? AsJson()
    {
        if (string.IsNullOrWhiteSpace(RawBody))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(RawBody);
        return document.RootElement.Clone();
    }
}
