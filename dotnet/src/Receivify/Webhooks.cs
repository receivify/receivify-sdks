using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Receivify;

/// <summary>
/// The complete, frozen set of event names Receivify can emit.
/// </summary>
/// <remarks>
/// Receivers MUST ignore names they do not recognise — adding one is an additive change, which is
/// why these are constants rather than an <c>enum</c>.
/// </remarks>
public static class EventNames
{
    /// <summary>Seen on-chain but not yet final.</summary>
    public const string InvoiceConfirming = "invoice.confirming";

    /// <summary>Less than expected arrived.</summary>
    public const string InvoicePartial = "invoice.partial";

    /// <summary>Paid in full.</summary>
    public const string InvoicePaid = "invoice.paid";

    /// <summary>Funds left the deposit address for the payout wallet.</summary>
    public const string InvoiceSwept = "invoice.swept";

    /// <summary>The payment window closed with nothing received.</summary>
    public const string InvoiceExpired = "invoice.expired";

    /// <summary>The window closed on an under-payment.</summary>
    public const string InvoicePartialExpired = "invoice.partial_expired";

    /// <summary>Cancelled while still pending and unpaid.</summary>
    public const string InvoiceCancelled = "invoice.cancelled";

    /// <summary>Terminal failure.</summary>
    public const string InvoiceFailed = "invoice.failed";

    /// <summary>Funds physically left a deposit address. Carries the INVOICE payload.</summary>
    public const string SweepSent = "sweep.sent";

    /// <summary>A forwarding deposit reached its confirmation threshold.</summary>
    public const string ForwardConfirmed = "forward.confirmed";

    /// <summary>A forwarding sweep was broadcast.</summary>
    public const string ForwardSent = "forward.sent";

    /// <summary>Emitted exclusively by <c>POST /api/v1/webhooks/test</c>. Carries the INVOICE payload.</summary>
    public const string Test = "test";

    /// <summary>Every event name, in spec order.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        InvoiceConfirming,
        InvoicePartial,
        InvoicePaid,
        InvoiceSwept,
        InvoiceExpired,
        InvoicePartialExpired,
        InvoiceCancelled,
        InvoiceFailed,
        SweepSent,
        ForwardConfirmed,
        ForwardSent,
        Test,
    };

    /// <summary>
    /// True for the events whose <c>data</c> is invoice-shaped: every <c>invoice.*</c>, plus
    /// <see cref="SweepSent"/> and <see cref="Test"/>.
    /// </summary>
    /// <param name="eventName">A raw event name, possibly unknown.</param>
    public static bool IsInvoiceEvent(string? eventName) =>
        eventName is not null
        && (eventName.StartsWith("invoice.", StringComparison.Ordinal)
            || string.Equals(eventName, SweepSent, StringComparison.Ordinal)
            || string.Equals(eventName, Test, StringComparison.Ordinal));

    /// <summary>True for the events whose <c>data</c> is forward-payment-shaped.</summary>
    /// <param name="eventName">A raw event name, possibly unknown.</param>
    public static bool IsForwardEvent(string? eventName) =>
        eventName is not null && eventName.StartsWith("forward.", StringComparison.Ordinal);
}

/// <summary>
/// The unified webhook envelope: <c>{ event, sequence, created_at, data }</c>.
/// </summary>
/// <remarks>
/// Both families ship the same four keys; only <see cref="Data"/> differs, and which shape it
/// takes is determined entirely by <see cref="Event"/>. Switch on <see cref="Event"/> first — the
/// envelope is never flattened.
/// </remarks>
public sealed record WebhookEvent
{
    /// <summary>The discriminator. See <see cref="EventNames"/>; treat it as an open set.</summary>
    [JsonPropertyName("event")]
    public string Event { get; init; } = string.Empty;

    /// <summary>
    /// A globally monotonic counter allocated at enqueue time, present on BOTH families. It is not
    /// per-resource. <c>(Event, Sequence)</c> is the de-duplication key — there is no separate
    /// event id. Delivery is at-least-once and can arrive out of order: store the highest sequence
    /// you have applied per resource and ignore anything at or below it.
    /// </summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    /// <summary>
    /// When the EVENT happened, not when this delivery was sent. Frozen: a retry and a replay carry
    /// the original value. The <c>test</c> event hard-codes the Unix epoch.
    /// </summary>
    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>
    /// The resource the event is about, left as raw JSON so nothing is lost when a deployment adds
    /// a field. Use <see cref="AsInvoiceData"/> or <see cref="AsForwardData"/> to type it.
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; init; }

    /// <summary>True for <c>invoice.*</c>, <c>sweep.sent</c> and <c>test</c>.</summary>
    [JsonIgnore]
    public bool IsInvoiceEvent => EventNames.IsInvoiceEvent(Event);

    /// <summary>True for <c>forward.confirmed</c> and <c>forward.sent</c>.</summary>
    [JsonIgnore]
    public bool IsForwardEvent => EventNames.IsForwardEvent(Event);

    /// <summary>The invoice payload, or null when this is not an invoice-shaped event.</summary>
    public InvoiceEventData? AsInvoiceData() =>
        IsInvoiceEvent && Data.ValueKind == JsonValueKind.Object
            ? Data.Deserialize<InvoiceEventData>(ReceivifyJson.Options)
            : null;

    /// <summary>The forward-payment payload, or null when this is not a forward event.</summary>
    public ForwardEventData? AsForwardData() =>
        IsForwardEvent && Data.ValueKind == JsonValueKind.Object
            ? Data.Deserialize<ForwardEventData>(ReceivifyJson.Options)
            : null;
}

/// <summary>
/// <c>data</c> for <c>invoice.*</c>, <c>sweep.sent</c> and <c>test</c>.
/// </summary>
/// <remarks>
/// A deliberately narrow projection: it carries NO <c>asset</c> and no <c>chain</c>, so a
/// multi-coin merchant must call <c>GetInvoiceAsync</c> to learn which coin the event is about.
/// A webhook is a statement about a moment, never about now.
/// </remarks>
public sealed record InvoiceEventData
{
    /// <summary>The invoice id.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>Your business key.</summary>
    [JsonPropertyName("reference")]
    public string? Reference { get; init; }

    /// <summary>See <see cref="InvoiceStatus"/>.</summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    /// <summary>Exact-integer base units, as a string.</summary>
    [JsonPropertyName("amount_expected")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string AmountExpected { get; init; } = "0";

    /// <summary>Exact-integer base units, as a string.</summary>
    [JsonPropertyName("amount_received")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string AmountReceived { get; init; } = "0";

    /// <summary>The deposit address.</summary>
    [JsonPropertyName("address")]
    public string Address { get; init; } = string.Empty;

    /// <summary>When it was paid, or null.</summary>
    [JsonPropertyName("paid_at")]
    public DateTimeOffset? PaidAt { get; init; }
}

/// <summary>
/// <c>data</c> for <c>forward.confirmed</c> and <c>forward.sent</c>.
/// </summary>
/// <remarks>
/// CryptAPI-shaped: the field names are inherited from that convention and differ from the REST
/// resource — <see cref="AddressIn"/> is what <see cref="Forward.Address"/> calls the deposit
/// address, and <see cref="AddressOut"/> is <see cref="Forward.Destination"/>.
/// </remarks>
public sealed record ForwardEventData
{
    /// <summary>
    /// The forward this event is about. <b>Not deprecated here</b> — inside a webhook payload this
    /// names a relationship to a forward rather than the payload's own identity.
    /// </summary>
    [JsonPropertyName("forward_id")]
    public string ForwardId { get; init; } = string.Empty;

    /// <summary>The ticker as supplied at create time.</summary>
    [JsonPropertyName("ticker")]
    public string Ticker { get; init; } = string.Empty;

    /// <summary>The deposit address — the same value the REST resource calls <c>address</c>.</summary>
    [JsonPropertyName("address_in")]
    public string AddressIn { get; init; } = string.Empty;

    /// <summary>The destination — the same value the REST resource calls <c>destination</c>.</summary>
    [JsonPropertyName("address_out")]
    public string AddressOut { get; init; } = string.Empty;

    /// <summary>The inbound transaction.</summary>
    [JsonPropertyName("txid_in")]
    public string TxidIn { get; init; } = string.Empty;

    /// <summary>Null on <c>forward.confirmed</c>; populated on <c>forward.sent</c>.</summary>
    [JsonPropertyName("txid_out")]
    public string? TxidOut { get; init; }

    /// <summary>Gross received, exact-integer base units as a string.</summary>
    [JsonPropertyName("value")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string Value { get; init; } = "0";

    /// <summary>Net sent to the destination, exact-integer base units as a string.</summary>
    [JsonPropertyName("value_forwarded")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string ValueForwarded { get; init; } = "0";

    /// <summary>Fee retained, exact-integer base units as a string.</summary>
    [JsonPropertyName("fee")]
    [JsonConverter(typeof(MoneyStringConverter))]
    public string Fee { get; init; } = "0";

    /// <summary>Confirmations on the inbound transaction.</summary>
    [JsonPropertyName("confirmations")]
    public int Confirmations { get; init; }

    /// <summary>
    /// <b>Dead field.</b> Hard-coded <c>false</c> on every forward event ever sent. Present only
    /// because removing it would be a breaking change.
    /// </summary>
    [JsonPropertyName("pending")]
    public bool Pending { get; init; }
}

/// <summary>Knobs for signature verification.</summary>
public sealed class WebhookVerifyOptions
{
    /// <summary>
    /// The replay window applied to the <c>t=</c> timestamp, in seconds. Defaults to 300. No
    /// window is enforced server-side, so this one is what protects you. Set it to 0 to disable
    /// the check entirely — only do that in a test.
    /// </summary>
    public int ToleranceSeconds { get; set; } = ReceivifyWebhooks.DefaultToleranceSeconds;

    /// <summary>Override "now" for the tolerance check. Exists for tests.</summary>
    public DateTimeOffset? Now { get; set; }
}

/// <summary>The verification outcome, with a reason worth logging.</summary>
/// <param name="Ok">Whether the delivery is authentic.</param>
/// <param name="Reason">Why it failed. Meaningless when <paramref name="Ok"/> is true.</param>
/// <param name="Message">A human sentence for your logs.</param>
/// <param name="Timestamp">The <c>t=</c> value, once the header parsed.</param>
public readonly record struct WebhookVerificationResult(
    bool Ok,
    WebhookFailureReason Reason,
    string Message,
    long? Timestamp);

/// <summary>
/// Webhook signature verification and envelope parsing. Pure functions — no network, no client.
/// </summary>
/// <remarks>
/// <para>
/// <b>Use the RAW body. Always.</b> The signature is
/// <c>HMAC-SHA256(secret, "&lt;t&gt;.&lt;rawBody&gt;")</c> over the EXACT bytes Receivify sent. If
/// you re-serialise a parsed object — <c>JsonSerializer.Serialize(model)</c>, a model-bound
/// <c>[FromBody]</c> parameter, anything — key order, whitespace and number formatting all change
/// and verification will fail even for a perfectly genuine delivery. In ASP.NET Core, read
/// <c>HttpRequest.Body</c> yourself before any model binding runs.
/// </para>
/// <para>
/// <b>Verify against a LIST of secrets.</b> Rotating the signing secret keeps the previous one
/// acceptable until its <see cref="WebhookSecretRotated.PrevExpiresAt"/> (24h by default), so
/// during a rotation TWO secrets are legitimately valid at once. Receivify always SIGNS with the
/// current one; the grace window is an instruction to your receiver. Pass both and drop the old
/// one after the window closes — <c>GetWebhookSecretStateAsync</c> tells a running process whether
/// the fallback is still needed.
/// </para>
/// </remarks>
public static class ReceivifyWebhooks
{
    /// <summary>The header Receivify signs every delivery with.</summary>
    public const string SignatureHeader = "X-Receivify-Signature";

    /// <summary>The default replay window, in seconds.</summary>
    public const int DefaultToleranceSeconds = 300;

    /// <summary>Verify a signed delivery. Returns false rather than throwing on a bad signature.</summary>
    /// <param name="rawBody">The EXACT bytes of the request body.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">The current secret first, then the previous one during a rotation.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public static bool Verify(
        byte[] rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets,
        WebhookVerifyOptions? options = null) =>
        Describe(rawBody, signatureHeader, secrets, options).Ok;

    /// <summary>Verify a signed delivery whose body you already hold as a string.</summary>
    /// <remarks>
    /// Only safe when the string is the untouched request body. If you got it by re-serialising a
    /// parsed object, the bytes have changed and this will fail.
    /// </remarks>
    /// <param name="rawBody">The EXACT body text.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">The current secret first, then the previous one during a rotation.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public static bool Verify(
        string rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets,
        WebhookVerifyOptions? options = null) =>
        Verify(ToBytes(rawBody), signatureHeader, secrets, options);

    /// <summary>Convenience overload for the single-secret case; normalises to a one-item list.</summary>
    /// <param name="rawBody">The EXACT bytes of the request body.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secret">The one secret to try.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public static bool Verify(
        byte[] rawBody,
        string? signatureHeader,
        string secret,
        WebhookVerifyOptions? options = null) =>
        Verify(rawBody, signatureHeader, new[] { secret }, options);

    /// <summary>
    /// The same check as <see cref="Verify(byte[], string, IEnumerable{string}, WebhookVerifyOptions)"/>,
    /// but with a machine-readable reason you can log.
    /// </summary>
    /// <param name="rawBody">The EXACT bytes of the request body.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">The current secret first, then the previous one during a rotation.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public static WebhookVerificationResult Describe(
        byte[] rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets,
        WebhookVerifyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawBody);

        IReadOnlyList<string> list = NormaliseSecrets(secrets);
        if (list.Count == 0)
        {
            return new WebhookVerificationResult(
                false,
                WebhookFailureReason.NoSecrets,
                "No webhook secret configured, so nothing could be verified.",
                null);
        }

        ParsedSignature? parsed = ParseSignatureHeader(signatureHeader);
        if (parsed is null)
        {
            return new WebhookVerificationResult(
                false,
                WebhookFailureReason.BadHeader,
                "Missing or malformed " + SignatureHeader + " header (expected \"t=<unix>,v1=<hex>\").",
                null);
        }

        ParsedSignature signature = parsed.Value;

        int tolerance = options?.ToleranceSeconds ?? DefaultToleranceSeconds;
        if (tolerance > 0)
        {
            long now = (options?.Now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            if (Math.Abs(now - signature.Timestamp) > tolerance)
            {
                return new WebhookVerificationResult(
                    false,
                    WebhookFailureReason.StaleTimestamp,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Webhook timestamp {signature.Timestamp} is outside the {tolerance}s tolerance window."),
                    signature.Timestamp);
            }
        }

        byte[] signed = BuildSignedPayload(signature.RawTimestamp, rawBody);

        // No early exit. Every secret is tried and every comparison is fixed-time, so neither the
        // number of secrets nor which one matched is observable from the outside.
        bool matched = false;
        foreach (string secret in list)
        {
            byte[] expected = HMACSHA256.HashData(ToBytes(secret), signed);
            foreach (string candidateHex in signature.Signatures)
            {
                byte[]? candidate = TryDecodeHex(candidateHex);
                if (candidate is not null
                    && candidate.Length == expected.Length
                    && CryptographicOperations.FixedTimeEquals(expected, candidate))
                {
                    matched = true;
                }
            }
        }

        return matched
            ? new WebhookVerificationResult(true, WebhookFailureReason.None, "ok", signature.Timestamp)
            : new WebhookVerificationResult(
                false,
                WebhookFailureReason.NoMatch,
                "Webhook signature did not match any configured secret.",
                signature.Timestamp);
    }

    /// <summary>Verify and deserialise a delivery in one step.</summary>
    /// <param name="rawBody">The EXACT bytes of the request body.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">The current secret first, then the previous one during a rotation.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    /// <exception cref="ReceivifyWebhookException">
    /// Verification failed, the body is not JSON, or it is not a
    /// <c>{ event, sequence, created_at, data }</c> envelope. Check
    /// <see cref="ReceivifyWebhookException.Reason"/>.
    /// </exception>
    public static WebhookEvent Parse(
        byte[] rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets,
        WebhookVerifyOptions? options = null)
    {
        WebhookVerificationResult result = Describe(rawBody, signatureHeader, secrets, options);
        if (!result.Ok)
        {
            throw new ReceivifyWebhookException(result.Message, result.Reason);
        }

        WebhookEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<WebhookEvent>(rawBody, ReceivifyJson.Options);
        }
        catch (JsonException ex)
        {
            throw new ReceivifyWebhookException(
                "Webhook body is not valid JSON: " + ex.Message,
                WebhookFailureReason.BadJson,
                ex);
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.Event))
        {
            throw new ReceivifyWebhookException(
                "Webhook body is not a { event, sequence, created_at, data } envelope.",
                WebhookFailureReason.BadEnvelope);
        }

        return parsed;
    }

    /// <summary>Verify and deserialise a delivery whose body you already hold as a string.</summary>
    /// <param name="rawBody">The EXACT body text.</param>
    /// <param name="signatureHeader">The value of <c>X-Receivify-Signature</c>.</param>
    /// <param name="secrets">The current secret first, then the previous one during a rotation.</param>
    /// <param name="options">Tolerance window and clock override.</param>
    public static WebhookEvent Parse(
        string rawBody,
        string? signatureHeader,
        IEnumerable<string>? secrets,
        WebhookVerifyOptions? options = null) =>
        Parse(ToBytes(rawBody), signatureHeader, secrets, options);

    /// <summary>
    /// Sign a body the way Receivify does, producing a full <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c>
    /// header value. Exposed for tests and local fixtures — you never need it to RECEIVE a webhook.
    /// </summary>
    /// <param name="rawBody">The body bytes to sign.</param>
    /// <param name="secret">The signing secret.</param>
    /// <param name="timestamp">Unix seconds to embed as <c>t=</c>.</param>
    public static string Sign(byte[] rawBody, string secret, long timestamp)
    {
        ArgumentNullException.ThrowIfNull(rawBody);

        string t = timestamp.ToString(CultureInfo.InvariantCulture);
        byte[] signed = BuildSignedPayload(t, rawBody);
        byte[] mac = HMACSHA256.HashData(ToBytes(secret), signed);
        return "t=" + t + ",v1=" + Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>Sign a body given as a string. See <see cref="Sign(byte[], string, long)"/>.</summary>
    /// <param name="rawBody">The body text to sign.</param>
    /// <param name="secret">The signing secret.</param>
    /// <param name="timestamp">Unix seconds to embed as <c>t=</c>.</param>
    public static string Sign(string rawBody, string secret, long timestamp) =>
        Sign(ToBytes(rawBody), secret, timestamp);

    /// <summary>Accepts null, a single secret, or a list. Blank entries are dropped.</summary>
    /// <param name="secrets">Whatever the caller had.</param>
    public static IReadOnlyList<string> NormaliseSecrets(IEnumerable<string>? secrets)
    {
        if (secrets is null)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (string secret in secrets)
        {
            if (!string.IsNullOrEmpty(secret))
            {
                list.Add(secret);
            }
        }

        return list;
    }

    private static byte[] BuildSignedPayload(string rawTimestamp, byte[] rawBody)
    {
        // The RAW timestamp characters go into the HMAC, not a re-formatted number: the server
        // signed exactly the bytes it sent.
        byte[] prefix = ToBytes(rawTimestamp + ".");
        var signed = new byte[prefix.Length + rawBody.Length];
        Buffer.BlockCopy(prefix, 0, signed, 0, prefix.Length);
        Buffer.BlockCopy(rawBody, 0, signed, prefix.Length, rawBody.Length);
        return signed;
    }

    private static byte[] ToBytes(string value) => Encoding.UTF8.GetBytes(value);

    private static byte[]? TryDecodeHex(string value)
    {
        if (string.IsNullOrEmpty(value) || (value.Length % 2) != 0)
        {
            return null;
        }

        foreach (char c in value)
        {
            bool hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex)
            {
                return null;
            }
        }

        return Convert.FromHexString(value);
    }

    private static ParsedSignature? ParseSignatureHeader(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        string? rawTimestamp = null;
        var signatures = new List<string>();

        foreach (string part in header.Split(','))
        {
            int index = part.IndexOf('=');
            if (index < 0)
            {
                continue;
            }

            string key = part[..index].Trim();
            string value = part[(index + 1)..].Trim();

            if (string.Equals(key, "t", StringComparison.Ordinal) && rawTimestamp is null)
            {
                rawTimestamp = value;
            }
            else if (string.Equals(key, "v1", StringComparison.Ordinal) && value.Length > 0)
            {
                signatures.Add(value);
            }
        }

        if (rawTimestamp is null || signatures.Count == 0)
        {
            return null;
        }

        if (!long.TryParse(rawTimestamp, NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp))
        {
            return null;
        }

        return new ParsedSignature(rawTimestamp, timestamp, signatures);
    }

    private readonly record struct ParsedSignature(
        string RawTimestamp,
        long Timestamp,
        IReadOnlyList<string> Signatures);
}
