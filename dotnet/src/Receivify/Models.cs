using System.Text.Json;
using System.Text.Json.Serialization;

namespace Receivify;

/// <summary>
/// The one and only list envelope: <c>{ data, has_more, next_cursor }</c>.
/// </summary>
/// <remarks>
/// Every paginated endpoint returns exactly this shape; no endpoint returns a bare array.
/// <see cref="HasMore"/> is a fact (the server reads <c>limit + 1</c> rows), never something to
/// infer from <c>Data.Count == limit</c>.
/// <para>
/// A few endpoints return bare, unpaginated arrays and are deliberately NOT modelled with this
/// type: <see cref="PayoutState.Pending"/>, <see cref="ForwardDetail.Payments"/>,
/// <see cref="ForwardDetail.Callbacks"/>, <see cref="Summary.ByAsset"/> and
/// <see cref="WebhookTestResult.Results"/>. They have no cursor and no auto-paginator.
/// </para>
/// </remarks>
/// <typeparam name="T">The resource type on the page.</typeparam>
public sealed record ReceivifyList<T>
{
    /// <summary>The page, newest first.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyList<T> Data { get; init; } = Array.Empty<T>();

    /// <summary>Whether another page exists. A fact, not a guess.</summary>
    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    /// <summary>Pass back as <c>cursor</c> to fetch the next page. Null exactly when <see cref="HasMore"/> is false.</summary>
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// The invoice lifecycle. Treat it as an OPEN set and keep a default branch: a new value is an
/// additive change under the API's versioning policy, which is why these are constants rather
/// than an <c>enum</c>.
/// </summary>
public static class InvoiceStatus
{
    /// <summary>The creation state.</summary>
    public const string Pending = "pending";

    /// <summary>Less than expected arrived.</summary>
    public const string Partial = "partial";

    /// <summary>Seen on-chain but not yet final.</summary>
    public const string Confirming = "confirming";

    /// <summary>Paid in full.</summary>
    public const string Paid = "paid";

    /// <summary>Funds left the deposit address for the payout wallet.</summary>
    public const string Swept = "swept";

    /// <summary>The payment window closed with nothing received.</summary>
    public const string Expired = "expired";

    /// <summary>The window closed on an under-payment.</summary>
    public const string PartialExpired = "partial_expired";

    /// <summary>Cancelled while still pending and unpaid.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Terminal failure.</summary>
    public const string Failed = "failed";
}

/// <summary>Forwarding-address states. Also an OPEN set — keep a default branch.</summary>
public static class ForwardStatus
{
    /// <summary>Watching for new deposits and forwarding everything credited.</summary>
    public const string Active = "active";

    /// <summary>The merchant kill switch. No NEW deposit is credited; money already credited still forwards.</summary>
    public const string Paused = "paused";

    /// <summary>Soft delete. Same money semantics as <see cref="Paused"/>; the row is never removed.</summary>
    public const string Disabled = "disabled";

    /// <summary>A single-session BTCPay deposit window closed. Set by the system, never assignable.</summary>
    public const string Expired = "expired";
}

/// <summary>The settlement backends that can derive an address.</summary>
public static class Providers
{
    /// <summary>Bitcoin Core.</summary>
    public const string Bitcoind = "bitcoind";

    /// <summary>BTCPay Server (the single-session altcoins).</summary>
    public const string Btcpay = "btcpay";

    /// <summary>EVM chains.</summary>
    public const string Evm = "evm";

    /// <summary>Solana.</summary>
    public const string Solana = "solana";

    /// <summary>Tron.</summary>
    public const string Tron = "tron";
}

/// <summary>An invoice, exactly as create, retrieve, list and cancel all return it.</summary>
public sealed record Invoice
{
    /// <summary>The invoice id (a UUID).</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Your own business key. It is ALSO a de-duplication key: creating a second invoice with a
    /// reference this merchant has already used returns the EXISTING invoice, with a 201.
    /// </summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    /// <summary>Lower-case symbol, e.g. <c>btc</c>, <c>usdc</c>.</summary>
    [JsonPropertyName("asset")]
    public string? Asset { get; init; }

    /// <summary>
    /// <b>Exact-integer base units, as a string</b> — satoshis for BTC, wei for ETH. Use
    /// <see cref="Money"/> if you need arithmetic; never parse it into a double.
    /// </summary>
    [JsonPropertyName("amount_expected")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string AmountExpected { get; init; } = "0";

    /// <summary>Exact-integer base units, as a string. <c>"0"</c> until something arrives.</summary>
    [JsonPropertyName("amount_received")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string AmountReceived { get; init; } = "0";

    /// <summary>
    /// A DECIMAL string such as <c>"25.00"</c> — a display price captured at create time, not base
    /// units and never a JSON number. The authoritative amount is <see cref="AmountExpected"/>;
    /// never settle against this.
    /// </summary>
    [JsonPropertyName("amount_fiat")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? AmountFiat { get; init; }

    /// <summary>ISO-4217 code that <see cref="AmountFiat"/> is denominated in.</summary>
    [JsonPropertyName("fiat_currency")]
    public string? FiatCurrency { get; init; }

    /// <summary>The EVM chain, the literal <c>solana</c>/<c>tron</c>, or null for BTC and BTCPay coins.</summary>
    [JsonPropertyName("chain")]
    public string? Chain { get; init; }

    /// <summary>The settlement backend that derived the address. See <see cref="Providers"/>.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>The deposit address. Send exactly this asset on exactly this chain.</summary>
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    /// <summary>See <see cref="InvoiceStatus"/>. Treat it as an open set.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>When the payment window closes.</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When it was paid, or null.</summary>
    [JsonPropertyName("paid_at")]
    public DateTimeOffset? PaidAt { get; init; }

    /// <summary>When it was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Whatever you stored. Always an object, never null — an omitted metadata comes back as <c>{}</c>.</summary>
    [JsonPropertyName("metadata")]
    public IReadOnlyDictionary<string, JsonElement>? Metadata { get; init; }

    /// <summary>The hosted checkout page for this invoice.</summary>
    [JsonPropertyName("checkout_url")]
    public string? CheckoutUrl { get; init; }

    /// <summary>
    /// Decimal places of this invoice's asset — the divisor for <see cref="AmountExpected"/>. Per-token
    /// on EVM (USDC 6, DAI 18), so it cannot be assumed from the chain.
    /// </summary>
    [JsonPropertyName("decimals")]
    public int? Decimals { get; init; }

    /// <summary><see cref="AmountExpected"/> already divided by <c>10^decimals</c>, as a decimal string. Display only.</summary>
    [JsonPropertyName("amount_decimal")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? AmountDecimal { get; init; }

    /// <summary>A wallet-scannable URI for a QR code, or null when the chain has no safe one (show the bare address).</summary>
    [JsonPropertyName("payment_uri")]
    public string? PaymentUri { get; init; }

    /// <summary>Whether our commission was added on top of the requested amount.</summary>
    [JsonPropertyName("add_fee_ontop")]
    public bool AddFeeOntop { get; init; }

    /// <summary>Exact-integer base units, as a string: what you asked to RECEIVE.</summary>
    [JsonPropertyName("amount_net_requested")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? AmountNetRequested { get; init; }

    /// <summary>Exact-integer base units, as a string: what the gross-up added. <c>"0"</c> when the flag is off.</summary>
    [JsonPropertyName("fee_ontop")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? FeeOntop { get; init; }
}

/// <summary>
/// The checkout-page projection from <c>GET /api/public/invoices/{id}</c>. Deliberately narrower
/// than <see cref="Invoice"/>: no reference, metadata, asset, chain or merchant.
/// </summary>
public sealed record PublicInvoice
{
    /// <summary>The invoice id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>See <see cref="InvoiceStatus"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>The deposit address.</summary>
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    /// <summary>Exact-integer base units, as a string.</summary>
    [JsonPropertyName("amount_expected")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string AmountExpected { get; init; } = "0";

    /// <summary>Exact-integer base units, as a string.</summary>
    [JsonPropertyName("amount_received")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string AmountReceived { get; init; } = "0";

    /// <summary>When the payment window closes.</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }
}

/// <summary>The result of a bulk cancel. Compare <see cref="Requested"/> with <see cref="Cancelled"/>.</summary>
public sealed record BulkResult
{
    /// <summary>Always true on a 200.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>The action performed; only <c>cancel</c> exists today.</summary>
    [JsonPropertyName("action")]
    public string? Action { get; init; }

    /// <summary>How many ids survived the string filter and were submitted.</summary>
    [JsonPropertyName("requested")]
    public int Requested { get; init; }

    /// <summary>How many were actually eligible and changed. The response does not say which.</summary>
    [JsonPropertyName("cancelled")]
    public int Cancelled { get; init; }
}

/// <summary>A standing deposit address that sweeps everything it receives to a destination wallet.</summary>
public record Forward
{
    /// <summary>
    /// <b>The canonical identifier.</b> Read this, not <see cref="ForwardId"/>.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// <b>Deprecated alias for <see cref="Id"/></b>, carrying the identical value.
    /// </summary>
    /// <remarks>
    /// It is not going away — removing a response field would be a breaking change, so it will be
    /// emitted for the whole life of v1. New code should read <see cref="Id"/>. Note that
    /// <see cref="ForwardEventData.ForwardId"/> inside a <c>forward.*</c> webhook is NOT deprecated:
    /// there it names a relationship rather than the payload's own identity.
    /// </remarks>
    [JsonPropertyName("forward_id")]
    [Obsolete("Read Id instead. forward_id is a permanent deprecated alias carrying the same value.")]
    public string? ForwardId { get; init; }

    /// <summary>The ticker string exactly as you supplied it at create time, not the canonical form.</summary>
    [JsonPropertyName("ticker")]
    public string Ticker { get; init; } = string.Empty;

    /// <summary>The settlement backend. See <see cref="Providers"/>.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>Null for <c>btc</c> and the BTCPay-settled coins, which have no chain concept here.</summary>
    [JsonPropertyName("chain")]
    public string? Chain { get; init; }

    /// <summary>The deposit address to hand to payers.</summary>
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    /// <summary>Your wallet. IMMUTABLE — a PATCH that names it is refused with <c>409 not_allowed</c>.</summary>
    [JsonPropertyName("destination")]
    public string Destination { get; init; } = string.Empty;

    /// <summary>Receives <c>forward.*</c> events only. Account-level webhook endpoints do NOT receive them.</summary>
    [JsonPropertyName("callback_url")]
    public string? CallbackUrl { get; init; }

    /// <summary>Confirmations before a payment is credited.</summary>
    [JsonPropertyName("confirmations_required")]
    public int ConfirmationsRequired { get; init; }

    /// <summary>
    /// <b>The one money field on this object that is NOT base units.</b> A DISPLAY-UNIT decimal
    /// string such as <c>"0.500000"</c>. Multiply by <c>10 ^ decimals</c> — or use
    /// <see cref="Money.Parse"/> — before comparing it with a <see cref="ForwardPayment.Value"/>.
    /// </summary>
    [JsonPropertyName("min_forward")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? MinForward { get; init; }

    /// <summary>See <see cref="ForwardStatus"/>. Treat it as an open set.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Null for every permanent address; a timestamp only for the single-session BTCPay coins.</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>When it was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Null on a forward that has never been patched.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>
/// <c>GET /api/v1/address/{id}</c> only: the <see cref="Forward"/> fields spread at the top level,
/// plus two capped sub-collections.
/// </summary>
/// <remarks>
/// Neither sub-collection is paginated and neither has a cursor. The caps are declared rather than
/// silent — <see cref="PaymentsHasMore"/> and <see cref="CallbacksHasMore"/> tell you when you are
/// seeing a truncated view, and there is no way to reach the rest.
/// </remarks>
public sealed record ForwardDetail : Forward
{
    /// <summary>Newest first, capped at 200. Not paginated.</summary>
    [JsonPropertyName("payments")]
    public IReadOnlyList<ForwardPayment> Payments { get; init; } = Array.Empty<ForwardPayment>();

    /// <summary>True when the 200 cap truncated <see cref="Payments"/>.</summary>
    [JsonPropertyName("payments_has_more")]
    public bool PaymentsHasMore { get; init; }

    /// <summary>Delivery log for this forward's own webhooks. Newest first, capped at 50. Not paginated.</summary>
    [JsonPropertyName("callbacks")]
    public IReadOnlyList<ForwardCallback> Callbacks { get; init; } = Array.Empty<ForwardCallback>();

    /// <summary>True when the 50 cap truncated <see cref="Callbacks"/>.</summary>
    [JsonPropertyName("callbacks_has_more")]
    public bool CallbacksHasMore { get; init; }
}

/// <summary>One deposit credited to a forwarding address.</summary>
public sealed record ForwardPayment
{
    /// <summary>The inbound transaction that funded the deposit address.</summary>
    [JsonPropertyName("txid_in")]
    public string TxidIn { get; init; } = string.Empty;

    /// <summary>Gross amount received, in exact-integer base units, as a string.</summary>
    [JsonPropertyName("value")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string Value { get; init; } = "0";

    /// <summary>
    /// <c>seen</c>/<c>confirmed</c> are inbound; <c>forwarding</c>/<c>sent</c> describe the sweep;
    /// <c>orphaned</c>/<c>invalidated</c> are reorg outcomes; <c>needs_review</c> is a manual hold.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>The forwarding transaction, once broadcast.</summary>
    [JsonPropertyName("txid_out")]
    public string? TxidOut { get; init; }

    /// <summary>Confirmations on the inbound transaction. Zero when unknown, never null.</summary>
    [JsonPropertyName("confirmations")]
    public int Confirmations { get; init; }

    /// <summary>When the payment row was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>One webhook delivery attempt for a forward. Outcome only — no body, no signature material.</summary>
public sealed record ForwardCallback
{
    /// <summary>See <see cref="EventNames"/>.</summary>
    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    /// <summary>The target URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary><c>pending</c>, <c>delivered</c> or <c>failed</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>How many attempts have been made.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; init; }

    /// <summary>The receiver's last HTTP status code.</summary>
    [JsonPropertyName("last_status")]
    public int? LastStatus { get; init; }

    /// <summary>Truncated to 300 characters.</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    /// <summary>When the delivery was enqueued.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When it was accepted by the receiver.</summary>
    [JsonPropertyName("delivered_at")]
    public DateTimeOffset? DeliveredAt { get; init; }
}

/// <summary>One <c>(asset, chain)</c> pair this deployment offers for forwarding.</summary>
public sealed record Ticker
{
    /// <summary>
    /// The string to pass as <c>ticker</c> when creating a forward. A bare symbol resolves to that
    /// asset's default chain; the <c>&lt;symbol&gt;_&lt;chain&gt;</c> form selects another one.
    /// </summary>
    [JsonPropertyName("ticker")]
    public string Value { get; init; } = string.Empty;

    /// <summary>The settlement backend. See <see cref="Providers"/>.</summary>
    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    /// <summary>The chain, or null where the concept does not apply.</summary>
    [JsonPropertyName("chain")]
    public string? Chain { get; init; }

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Base units per whole coin, as a power of ten. This is the number to give
    /// <see cref="Money.Format"/> and <see cref="Money.Parse"/>.
    /// </summary>
    [JsonPropertyName("decimals")]
    public int Decimals { get; init; }
}

/// <summary>
/// An invoiceable coin. <b>Display metadata, not a machine contract</b> — two of its fields cannot
/// be parsed at all.
/// </summary>
public sealed record Asset
{
    /// <summary>Lower-case symbol.</summary>
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>A human sentence naming the networks, e.g. <c>ETH · Base · Arbitrum</c>. Not a machine-readable list.</summary>
    [JsonPropertyName("network")]
    public string? Network { get; init; }

    /// <summary>Brand hex colour for UI use.</summary>
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    /// <summary>
    /// <b>Not always a number.</b> Either a display-unit decimal string or the literal word
    /// <c>"network"</c>, meaning the floor is whatever the chain's economics make it at the time.
    /// </summary>
    [JsonPropertyName("min")]
    public string? Min { get; init; }

    /// <summary><b>English prose</b>, not a value: <c>"~0.000001 BTC"</c>, <c>"dynamic gas"</c>. Display only.</summary>
    [JsonPropertyName("network_fee")]
    public string? NetworkFee { get; init; }

    /// <summary>Whether the coin is fully wired end to end. False means catalogue-only.</summary>
    [JsonPropertyName("live")]
    public bool Live { get; init; }
}

/// <summary>A shareable link that mints an invoice when a payer opens it.</summary>
/// <remarks>
/// Note the asymmetry, which is by design: you SET the amount with <c>amount_btc</c> (a decimal
/// BTC string) and READ it as <see cref="AmountSats"/> (base units).
/// </remarks>
public sealed record PaymentLink
{
    /// <summary>Eight base64url characters — not a UUID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display title, truncated to 120 characters at create time.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Exact-integer base units as a string for a fixed link; null for an open-amount one.</summary>
    [JsonPropertyName("amount_sats")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? AmountSats { get; init; }

    /// <summary>True when the payer chooses the amount. Always the inverse of <see cref="AmountSats"/> being set.</summary>
    [JsonPropertyName("open_amount")]
    public bool OpenAmount { get; init; }

    /// <summary>A link created before this field existed reads as true.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    /// <summary>How many invoices have been minted from this link.</summary>
    [JsonPropertyName("uses")]
    public int Uses { get; init; }

    /// <summary>When it was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When it was last updated, or null.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The shareable URL.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary>The unauthenticated view of a payment link. A deactivated link answers 404, not 409.</summary>
public sealed record PublicPaymentLink
{
    /// <summary>The link id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>True when the payer chooses the amount.</summary>
    [JsonPropertyName("open_amount")]
    public bool OpenAmount { get; init; }

    /// <summary>
    /// <b>A decimal BTC string, not base units</b> — a reference value for a fixed link. Null for
    /// an open-amount link.
    /// </summary>
    [JsonPropertyName("amount_btc")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? AmountBtc { get; init; }

    /// <summary>The coins this link can actually be paid in — enabled AND settleable right now.</summary>
    [JsonPropertyName("assets")]
    public IReadOnlyList<PublicPaymentLinkAsset> Assets { get; init; } = Array.Empty<PublicPaymentLinkAsset>();

    /// <summary>An advisory rate snapshot. Floating-point — display only.</summary>
    [JsonPropertyName("fx")]
    public PublicPaymentLinkFx? Fx { get; init; }
}

/// <summary>One payable coin on a public payment link.</summary>
public sealed record PublicPaymentLinkAsset
{
    /// <summary>Lower-case symbol.</summary>
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Brand hex colour.</summary>
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    /// <summary>Base units per whole coin, as a power of ten.</summary>
    [JsonPropertyName("decimals")]
    public int Decimals { get; init; }
}

/// <summary>
/// The narrow FX snapshot on a public payment link. <b>Advisory floating-point rates.</b> Never
/// settle against them.
/// </summary>
public sealed record PublicPaymentLinkFx
{
    /// <summary>Coin symbol to price.</summary>
    [JsonPropertyName("crypto")]
    public IReadOnlyDictionary<string, double>? Crypto { get; init; }

    /// <summary>Currency code to units per base.</summary>
    [JsonPropertyName("fiat")]
    public IReadOnlyDictionary<string, double>? Fiat { get; init; }
}

/// <summary>A listed API key. The secret is never stored and therefore never appears here.</summary>
public sealed record ApiKey
{
    /// <summary>The identifier <c>POST /api/v1/keys/{id}/revoke</c> matches on — NOT the prefix.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The visible prefix, for identifying the key in a UI.</summary>
    [JsonPropertyName("prefix")]
    public string? Prefix { get; init; }

    /// <summary>Human label.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>When it was issued.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When it last authenticated a request.</summary>
    [JsonPropertyName("last_used_at")]
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>Revoked keys are listed too, flagged here.</summary>
    [JsonPropertyName("revoked")]
    public bool Revoked { get; init; }
}

/// <summary>A freshly issued API key. <see cref="ApiKeyValue"/> is shown exactly once.</summary>
public sealed record ApiKeyCreated
{
    /// <summary>The key's real identifier — the one that revokes it.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// The raw <c>rcv_live_…</c> secret. Store it now.
    /// <b>Null on an <c>Idempotency-Key</c> replay</b>, because the secret is deliberately never
    /// persisted, not even for the 24-hour replay window. A replay still carries the same
    /// <see cref="Id"/> and <see cref="Prefix"/>, so you can still identify and revoke what you made.
    /// </summary>
    [JsonPropertyName("api_key")]
    public string? ApiKeyValue { get; init; }

    /// <summary>Legacy name for <see cref="Prefix"/>. Both are emitted.</summary>
    [JsonPropertyName("api_key_prefix")]
    public string? ApiKeyPrefix { get; init; }

    /// <summary>The visible prefix.</summary>
    [JsonPropertyName("prefix")]
    public string? Prefix { get; init; }

    /// <summary>Human label.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>When it was issued.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Human note. Different text on a replay — informational only, do not branch on it.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>A registered account-level webhook endpoint.</summary>
/// <remarks>
/// Account endpoints receive every INVOICE event (and <c>sweep.sent</c>), in addition to an
/// invoice's own <c>callback_url</c>. They deliberately do NOT receive <c>forward.*</c> events —
/// those go only to the forward's own callback.
/// </remarks>
public sealed record WebhookEndpoint
{
    /// <summary>The endpoint id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Normalised through the WHATWG URL parser, so it may differ from what you sent.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>Inactive endpoints receive nothing. An endpoint stored before this field existed reads as true.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    /// <summary>When it was registered.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When it was last updated, or null.</summary>
    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>One row of the webhook delivery log. The delivered body is not exposed — only the outcome.</summary>
public sealed record WebhookDelivery
{
    /// <summary>
    /// A 24-character hex Mongo ObjectId — the only identifier in this API that is not a UUID or a
    /// short token. Pass it to <c>ReplayWebhookDeliveryAsync</c>.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Set on <c>invoice.*</c> and <c>sweep.sent</c> rows; explicitly null on a <c>forward.*</c> row.</summary>
    [JsonPropertyName("invoice_id")]
    public string? InvoiceId { get; init; }

    /// <summary>
    /// Set on <c>forward.*</c> rows; explicitly null otherwise. Exactly one of this and
    /// <see cref="InvoiceId"/> is non-null on any row, so a failed delivery is always attributable.
    /// </summary>
    [JsonPropertyName("forward_id")]
    public string? ForwardId { get; init; }

    /// <summary>See <see cref="EventNames"/>.</summary>
    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    /// <summary><c>pending</c>, <c>delivered</c> or <c>failed</c>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>How many attempts have been made.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; init; }

    /// <summary>The target URL.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>The receiver's last HTTP status code.</summary>
    [JsonPropertyName("last_status")]
    public int? LastStatus { get; init; }

    /// <summary>The last transport or receiver error.</summary>
    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    /// <summary>When the delivery was enqueued.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When it was accepted.</summary>
    [JsonPropertyName("delivered_at")]
    public DateTimeOffset? DeliveredAt { get; init; }
}

/// <summary>
/// Rotation metadata for the webhook signing secret. Carries NO secret material of any kind.
/// </summary>
public sealed record WebhookSecretState
{
    /// <summary>When the secret was last rotated. Null on an account still using its signup secret.</summary>
    [JsonPropertyName("rotated_at")]
    public DateTimeOffset? RotatedAt { get; init; }

    /// <summary>
    /// When the previous secret stops being acceptable. Null when there is no previous secret AND
    /// once the grace window has passed — this is the live answer to "should my receiver still be
    /// trying the old one?", not a historical record.
    /// </summary>
    [JsonPropertyName("prev_expires_at")]
    public DateTimeOffset? PrevExpiresAt { get; init; }

    /// <summary>
    /// True while a previous secret is still inside its grace window. When true, your receiver must
    /// accept EITHER secret — which is exactly why <see cref="ReceivifyWebhooks"/><c>.Verify</c>
    /// takes a list of secrets rather than one.
    /// </summary>
    [JsonPropertyName("prev_active")]
    public bool PrevActive { get; init; }
}

/// <summary>The result of a rotation. <see cref="Secret"/> is shown once, here, and never again.</summary>
public sealed record WebhookSecretRotated
{
    /// <summary>
    /// The raw <c>whsec_…</c> signing secret. <b>Null on an <c>Idempotency-Key</c> replay</b> — the
    /// same shown-once discipline as <see cref="ApiKeyCreated.ApiKeyValue"/>. If the secret itself
    /// was lost, rotate again; there is no read-back path.
    /// </summary>
    [JsonPropertyName("secret")]
    public string? Secret { get; init; }

    /// <summary>When this rotation happened.</summary>
    [JsonPropertyName("rotated_at")]
    public DateTimeOffset? RotatedAt { get; init; }

    /// <summary>Until this instant, verify against the NEW secret and fall back to the previous one.</summary>
    [JsonPropertyName("prev_expires_at")]
    public DateTimeOffset? PrevExpiresAt { get; init; }

    /// <summary>The length of the grace window in seconds. 86400 (24h) by default.</summary>
    [JsonPropertyName("grace_seconds")]
    public int GraceSeconds { get; init; }

    /// <summary>
    /// True when this rotation retired a previous secret that was STILL inside its grace window.
    /// At most two secrets are ever valid at once, so any receiver still on that older secret
    /// starts failing now.
    /// </summary>
    [JsonPropertyName("replaced_previous")]
    public bool ReplacedPrevious { get; init; }

    /// <summary>Human note. Different text on a replay — informational only.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// The outcome of a signed test send. Answers 200 even when every delivery failed — compare
/// <see cref="Delivered"/> with <see cref="Total"/>.
/// </summary>
public sealed record WebhookTestResult
{
    /// <summary>Always <c>test</c>.</summary>
    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    /// <summary>How many targets answered 2xx.</summary>
    [JsonPropertyName("delivered")]
    public int Delivered { get; init; }

    /// <summary>How many targets were attempted.</summary>
    [JsonPropertyName("total")]
    public int Total { get; init; }

    /// <summary>Per-target outcomes. A bare array, not a list envelope.</summary>
    [JsonPropertyName("results")]
    public IReadOnlyList<WebhookTestTarget> Results { get; init; } = Array.Empty<WebhookTestTarget>();
}

/// <summary>One target's outcome in a test send.</summary>
public sealed record WebhookTestTarget
{
    /// <summary>The endpoint that was hit.</summary>
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    /// <summary>The receiver's own outcome, not this endpoint's.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>The receiver's HTTP status, or null when the request never completed.</summary>
    [JsonPropertyName("status")]
    public int? Status { get; init; }

    /// <summary>Present only on a transport failure. Truncated to 200 characters.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// A scheduled billing cycle. Crypto has no pull mechanism, so this GENERATES a fresh invoice each
/// cycle rather than debiting anyone.
/// </summary>
public sealed record RecurringSchedule
{
    /// <summary>The schedule id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Display title.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>Lower-case asset symbol.</summary>
    [JsonPropertyName("asset")]
    public string? Asset { get; init; }

    /// <summary>A DECIMAL string such as <c>"25.00"</c>. Null when the schedule is priced in the coin.</summary>
    [JsonPropertyName("amount_fiat")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? AmountFiat { get; init; }

    /// <summary>ISO-4217 for <see cref="AmountFiat"/>.</summary>
    [JsonPropertyName("currency")]
    public string? Currency { get; init; }

    /// <summary>
    /// <b>A DECIMAL amount in the coin — not base units</b> — stored verbatim as a string. Null
    /// when the schedule is priced in fiat.
    /// </summary>
    [JsonPropertyName("amount")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string? Amount { get; init; }

    /// <summary>Days between cycles.</summary>
    [JsonPropertyName("interval_days")]
    public int IntervalDays { get; init; }

    /// <summary>False once cancelled.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    /// <summary>How many invoices this schedule has generated.</summary>
    [JsonPropertyName("runs")]
    public int Runs { get; init; }

    /// <summary>Set to the creation instant, so the first invoice appears on the next worker tick.</summary>
    [JsonPropertyName("next_run")]
    public DateTimeOffset? NextRun { get; init; }

    /// <summary>The most recently generated invoice.</summary>
    [JsonPropertyName("last_invoice_id")]
    public string? LastInvoiceId { get; init; }

    /// <summary>When the schedule was created.</summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

/// <summary>A staged payout-address change. The confirmation token hash is never exposed.</summary>
public sealed record PayoutChange
{
    /// <summary>The change id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The asset whose payout address is changing.</summary>
    [JsonPropertyName("asset")]
    public string Asset { get; init; } = string.Empty;

    /// <summary>An empty string means "clear this asset's payout address".</summary>
    [JsonPropertyName("new_address")]
    public string NewAddress { get; init; } = string.Empty;

    /// <summary>What it is replacing.</summary>
    [JsonPropertyName("old_address")]
    public string? OldAddress { get; init; }

    /// <summary><c>pending_email</c> and <c>confirmed</c> are the OPEN states that appear in pending lists.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>When the change was staged.</summary>
    [JsonPropertyName("requested_at")]
    public DateTimeOffset? RequestedAt { get; init; }

    /// <summary>When the timelock elapses. Confirming by email does not move this.</summary>
    [JsonPropertyName("effective_at")]
    public DateTimeOffset? EffectiveAt { get; init; }

    /// <summary>When the emailed confirmation was consumed.</summary>
    [JsonPropertyName("confirmed_at")]
    public DateTimeOffset? ConfirmedAt { get; init; }

    /// <summary>When the change went live.</summary>
    [JsonPropertyName("applied_at")]
    public DateTimeOffset? AppliedAt { get; init; }
}

/// <summary>The live payout map plus every open staged change.</summary>
public sealed record PayoutState
{
    /// <summary>
    /// The LIVE map the sweeper reads, asset symbol to address. Staged changes are not in here
    /// until they go live — that separation is the whole point of the timelock.
    /// </summary>
    [JsonPropertyName("payout_addresses")]
    public IReadOnlyDictionary<string, string>? PayoutAddresses { get; init; }

    /// <summary>
    /// Every open change. <b>A bare array — NOT a list envelope and NOT bounded.</b> There is no
    /// <c>has_more</c> and no cursor, so there is no paginator for it.
    /// </summary>
    [JsonPropertyName("pending")]
    public IReadOnlyList<PayoutChange> Pending { get; init; } = Array.Empty<PayoutChange>();

    /// <summary>Deployment-wide delay between confirmation and going live.</summary>
    [JsonPropertyName("timelock_hours")]
    public int TimelockHours { get; init; }

    /// <summary>Whether this deployment requires an emailed confirmation click.</summary>
    [JsonPropertyName("email_confirmation")]
    public bool EmailConfirmation { get; init; }

    /// <summary>True only when the timelock is zero AND email confirmation is off.</summary>
    [JsonPropertyName("instant")]
    public bool Instant { get; init; }
}

/// <summary>What a payout staging call did.</summary>
public sealed record PayoutStageAck
{
    /// <summary>Always true on a 200.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Changes created by THIS call. An address equal to the live one is a no-op and appears nowhere.</summary>
    [JsonPropertyName("staged")]
    public IReadOnlyList<PayoutChange> Staged { get; init; } = Array.Empty<PayoutChange>();

    /// <summary>Changes that went live during this call. Non-empty only on an instant deployment.</summary>
    [JsonPropertyName("applied")]
    public IReadOnlyList<PayoutChange> Applied { get; init; } = Array.Empty<PayoutChange>();

    /// <summary>Every open change after the call.</summary>
    [JsonPropertyName("pending")]
    public IReadOnlyList<PayoutChange> Pending { get; init; } = Array.Empty<PayoutChange>();

    /// <summary>Deployment-wide timelock.</summary>
    [JsonPropertyName("timelock_hours")]
    public int TimelockHours { get; init; }

    /// <summary>Whether an emailed confirmation is required.</summary>
    [JsonPropertyName("email_confirmation")]
    public bool EmailConfirmation { get; init; }

    /// <summary>A human sentence describing what happens next. Do not branch on it.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>Merchant preferences.</summary>
public sealed record Settings
{
    /// <summary>ISO-4217 code used to render fiat values in the dashboard.</summary>
    [JsonPropertyName("display_currency")]
    public string? DisplayCurrency { get; init; }

    /// <summary>The account email.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>
    /// The account's default fee in basis points, as an INTEGER — it is a rate, not money. The
    /// rate charged on an individual invoice is resolved through override layers and snapshotted
    /// onto the invoice, so it may differ from this value.
    /// </summary>
    [JsonPropertyName("fee_bps")]
    public int FeeBps { get; init; }
}

/// <summary>
/// An ACK, not the <see cref="Settings"/> resource: only the fields actually written are echoed
/// back, so it is not interchangeable with the GET.
/// </summary>
public sealed record SettingsUpdateAck
{
    /// <summary>Always true on a 200.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Present only if it was in the request.</summary>
    [JsonPropertyName("display_currency")]
    public string? DisplayCurrency { get; init; }
}

/// <summary>An ad-hoc dashboard composite. Not a resource, and nothing here is paginated.</summary>
public sealed record Summary
{
    /// <summary>Who the caller is.</summary>
    [JsonPropertyName("merchant")]
    public SummaryMerchant? Merchant { get; init; }

    /// <summary><c>total</c> plus one key per status. The per-status keys duplicate <see cref="ByStatus"/>.</summary>
    [JsonPropertyName("counts")]
    public IReadOnlyDictionary<string, int>? Counts { get; init; }

    /// <summary>Per-asset volume and fees. A bare array.</summary>
    [JsonPropertyName("by_asset")]
    public IReadOnlyList<SummaryAsset> ByAsset { get; init; } = Array.Empty<SummaryAsset>();

    /// <summary>Invoice counts keyed by status.</summary>
    [JsonPropertyName("by_status")]
    public IReadOnlyDictionary<string, int>? ByStatus { get; init; }

    /// <summary>Exactly 30 entries, oldest first, gap-filled.</summary>
    [JsonPropertyName("series")]
    public IReadOnlyList<SummarySeriesPoint> Series { get; init; } = Array.Empty<SummarySeriesPoint>();

    /// <summary>An advisory FX snapshot. See <see cref="FxSnapshot"/>.</summary>
    [JsonPropertyName("fx")]
    public FxSnapshot? Fx { get; init; }

    /// <summary>The fiat currency codes this deployment supports.</summary>
    [JsonPropertyName("currencies")]
    public IReadOnlyList<string> Currencies { get; init; } = Array.Empty<string>();
}

/// <summary>The merchant block of a <see cref="Summary"/>.</summary>
public sealed record SummaryMerchant
{
    /// <summary>The merchant id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>The account email.</summary>
    [JsonPropertyName("email")]
    public string? Email { get; init; }

    /// <summary>Legacy BTC-only field kept alongside the map.</summary>
    [JsonPropertyName("payout_address")]
    public string? PayoutAddress { get; init; }

    /// <summary>The live payout map.</summary>
    [JsonPropertyName("payout_addresses")]
    public IReadOnlyDictionary<string, string>? PayoutAddresses { get; init; }

    /// <summary>Default fee in basis points. A rate, not money.</summary>
    [JsonPropertyName("fee_bps")]
    public int FeeBps { get; init; }

    /// <summary>The prefix of the key currently in use.</summary>
    [JsonPropertyName("api_key_prefix")]
    public string? ApiKeyPrefix { get; init; }

    /// <summary>ISO-4217 display currency.</summary>
    [JsonPropertyName("display_currency")]
    public string? DisplayCurrency { get; init; }
}

/// <summary>Per-asset totals in a <see cref="Summary"/>. All three amounts are base-unit strings.</summary>
public sealed record SummaryAsset
{
    /// <summary>Lower-case symbol.</summary>
    [JsonPropertyName("symbol")]
    public string Symbol { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>Brand hex colour.</summary>
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    /// <summary>Base units per whole coin, as a power of ten.</summary>
    [JsonPropertyName("decimals")]
    public int Decimals { get; init; }

    /// <summary>Invoices in any state.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; }

    /// <summary>Invoices that reached paid or swept.</summary>
    [JsonPropertyName("paid_count")]
    public int PaidCount { get; init; }

    /// <summary>Exact-integer base units as a string. Paid and swept invoices only.</summary>
    [JsonPropertyName("volume_base")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string VolumeBase { get; init; } = "0";

    /// <summary>Exact-integer base units as a string.</summary>
    [JsonPropertyName("fee_base")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string FeeBase { get; init; } = "0";

    /// <summary><c>volume_base - fee_base</c>, exact-integer base units as a string.</summary>
    [JsonPropertyName("net_base")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string NetBase { get; init; } = "0";
}

/// <summary>One day of the 30-day volume series.</summary>
public sealed record SummarySeriesPoint
{
    /// <summary><c>YYYY-MM-DD</c>, a plain date rather than a timestamp.</summary>
    [JsonPropertyName("day")]
    public string Day { get; init; } = string.Empty;

    /// <summary>Asset symbol to exact-integer base-unit volume string. Empty on a day with no volume.</summary>
    [JsonPropertyName("assets")]
    public IReadOnlyDictionary<string, string>? Assets { get; init; }
}

/// <summary>
/// <b>Advisory only.</b> Every rate here is a JSON floating-point number, not an exact value.
/// </summary>
/// <remarks>
/// These are the only numbers in the API that look monetary, and they stay numbers on purpose: a
/// rate is not money. Nothing is ever settled against them — the authoritative conversion happens
/// server-side at invoice creation and is locked onto the invoice. Use this to render an
/// approximate fiat figure and nothing else.
/// </remarks>
public sealed record FxSnapshot
{
    /// <summary>The unit the rate maps are denominated in, e.g. <c>USD</c>.</summary>
    [JsonPropertyName("base")]
    public string? Base { get; init; }

    /// <summary>Coin symbol to price in <see cref="Base"/>.</summary>
    [JsonPropertyName("crypto")]
    public IReadOnlyDictionary<string, double>? Crypto { get; init; }

    /// <summary>Currency code to units per <see cref="Base"/>.</summary>
    [JsonPropertyName("fiat")]
    public IReadOnlyDictionary<string, double>? Fiat { get; init; }

    /// <summary>When the snapshot was taken.</summary>
    [JsonPropertyName("fetched_at")]
    public DateTimeOffset? FetchedAt { get; init; }

    /// <summary>The upstream feed.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }

    /// <summary>True when the snapshot is older than the freshness threshold. A stale snapshot is still returned.</summary>
    [JsonPropertyName("stale")]
    public bool Stale { get; init; }
}

/// <summary>The ack returned by the recurring-schedule and payout-change cancels.</summary>
/// <remarks>Note that cancelling an INVOICE returns the resource instead, not this.</remarks>
public sealed record CancelledAck
{
    /// <summary>Always true on a 200.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>The id that was cancelled — echoed from the path, not re-read from the database.</summary>
    [JsonPropertyName("cancelled")]
    public string? Cancelled { get; init; }
}

/// <summary>The ack returned by an API-key revoke.</summary>
public sealed record RevokedAck
{
    /// <summary>Always true on a 200.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>The id that was revoked.</summary>
    [JsonPropertyName("revoked")]
    public string? Revoked { get; init; }
}

/// <summary>The ack returned by a webhook-endpoint delete.</summary>
public sealed record DeletedAck
{
    /// <summary>Always true on a 200.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>The id that was deleted.</summary>
    [JsonPropertyName("deleted")]
    public string? Deleted { get; init; }
}

/// <summary>The ack returned by a delivery replay.</summary>
public sealed record ReplayedAck
{
    /// <summary>Always true on a 200.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>The delivery id that was re-queued.</summary>
    [JsonPropertyName("replayed")]
    public string? Replayed { get; init; }
}

/// <summary>
/// The operator health report. <b>Returned unchanged on both 200 and 503</b> — it never becomes an
/// <c>{ error, detail }</c>, which is why <c>GetHealthAsync</c> returns it instead of throwing on
/// a 503.
/// </summary>
public sealed record HealthReport
{
    /// <summary>
    /// <c>ok</c> = every hard dependency up. <c>warn</c> = up, but a soft alert fired (still 200).
    /// <c>degraded</c> = a hard dependency is down (503).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>When the report was generated.</summary>
    [JsonPropertyName("time")]
    public DateTimeOffset? Time { get; init; }

    /// <summary>Per-dependency detail. Left as raw JSON: it is an operator diagnostic, not a contract.</summary>
    [JsonPropertyName("checks")]
    public JsonElement Checks { get; init; }

    /// <summary>Soft alerts that fired.</summary>
    [JsonPropertyName("alerts")]
    public IReadOnlyList<HealthAlert> Alerts { get; init; } = Array.Empty<HealthAlert>();

    /// <summary>True when a hard dependency is down.</summary>
    [JsonIgnore]
    public bool IsDegraded => string.Equals(Status, "degraded", StringComparison.Ordinal);
}

/// <summary>One soft alert on a <see cref="HealthReport"/>.</summary>
public sealed record HealthAlert
{
    /// <summary>Currently always <c>warn</c>.</summary>
    [JsonPropertyName("level")]
    public string Level { get; init; } = string.Empty;

    /// <summary><c>indexer_lag</c>, <c>fx_stale</c> or <c>sweep_queue_stale</c>.</summary>
    [JsonPropertyName("signal")]
    public string Signal { get; init; } = string.Empty;

    /// <summary>The observed value.</summary>
    [JsonPropertyName("value")]
    public long? Value { get; init; }

    /// <summary>The threshold that was crossed.</summary>
    [JsonPropertyName("threshold")]
    public long? Threshold { get; init; }
}
