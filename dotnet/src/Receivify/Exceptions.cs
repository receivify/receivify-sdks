using System.Globalization;

namespace Receivify;

/// <summary>
/// Everything this SDK throws derives from here.
/// </summary>
/// <example>
/// <code>
/// try { await client.CreateAddressAsync(request); }
/// catch (BadDestinationException ex) { /* the payer's wallet address is wrong */ }
/// catch (ReceivifyApiException ex) { /* any other API-level failure; ex.Code is preserved */ }
/// catch (ReceivifyException ex)    { /* transport, timeout, webhook, ... */ }
/// </code>
/// </example>
public class ReceivifyException : Exception
{
    /// <summary>Creates the exception.</summary>
    public ReceivifyException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The request never produced an HTTP response: DNS, TLS, socket, or this SDK's own timeout.
/// </summary>
public sealed class ReceivifyConnectionException : ReceivifyException
{
    /// <summary>Creates the exception.</summary>
    public ReceivifyConnectionException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyConnectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyConnectionException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Human-readable description. Never contains the API key.</param>
    /// <param name="isTimeout">True when this was the configured client timeout rather than a transport error.</param>
    /// <param name="innerException">The underlying transport failure, if any.</param>
    public ReceivifyConnectionException(string message, bool isTimeout, Exception? innerException = null)
        : base(message, innerException)
    {
        IsTimeout = isTimeout;
    }

    /// <summary>True when the failure was this SDK's own timeout rather than a transport error.</summary>
    public bool IsTimeout { get; }
}

/// <summary>Why a webhook failed verification or parsing.</summary>
public enum WebhookFailureReason
{
    /// <summary>Nothing failed. Only ever seen on a successful verification result.</summary>
    None = 0,

    /// <summary>No secret was supplied, so nothing could be checked.</summary>
    NoSecrets,

    /// <summary>The <c>X-Receivify-Signature</c> header was absent or not <c>t=&lt;unix&gt;,v1=&lt;hex&gt;</c>.</summary>
    BadHeader,

    /// <summary>The <c>t=</c> timestamp fell outside the replay tolerance window.</summary>
    StaleTimestamp,

    /// <summary>The signature matched none of the supplied secrets.</summary>
    NoMatch,

    /// <summary>The body verified but is not valid JSON.</summary>
    BadJson,

    /// <summary>The body is valid JSON but not a <c>{ event, sequence, created_at, data }</c> envelope.</summary>
    BadEnvelope,
}

/// <summary>
/// A webhook could not be authenticated or parsed. Never thrown by
/// <see cref="ReceivifyWebhooks"/><c>.Verify</c>, which returns a <see cref="bool"/>; only by
/// <c>Parse</c>.
/// </summary>
public sealed class ReceivifyWebhookException : ReceivifyException
{
    /// <summary>Creates the exception.</summary>
    public ReceivifyWebhookException()
    {
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyWebhookException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyWebhookException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">Human-readable description.</param>
    /// <param name="reason">Machine-readable cause, safe to branch on.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public ReceivifyWebhookException(string message, WebhookFailureReason reason, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Machine-readable cause. Branch on this, not on the message.</summary>
    public WebhookFailureReason Reason { get; }
}

/// <summary>
/// Everything an <c>{ error, detail }</c> failure carries, packaged so each typed subclass needs
/// exactly one constructor.
/// </summary>
/// <param name="Code">The raw <c>error</c> string. Always preserved, even when unrecognised.</param>
/// <param name="Detail">The human sentence from <c>detail</c>. For logs, never for branching.</param>
/// <param name="StatusCode">The HTTP status code.</param>
/// <param name="Headers">Response headers, keys lower-cased.</param>
/// <param name="RawBody">The undecoded response body.</param>
/// <param name="Request">The method and path, for logs. Never contains the API key.</param>
public readonly record struct ReceivifyErrorContext(
    string? Code,
    string? Detail,
    int StatusCode,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? RawBody = null,
    string? Request = null);

/// <summary>
/// An <c>{ error, detail }</c> body came back with a 4xx/5xx status.
/// </summary>
/// <remarks>
/// This is also the fallback for any error code this SDK does not recognise, so it is the class
/// to catch when you want "any API-level failure". A code from a future deployment lands here
/// with <see cref="Code"/> intact rather than crashing an older SDK.
/// </remarks>
public class ReceivifyApiException : ReceivifyException
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the exception.</summary>
    public ReceivifyApiException()
        : this(new ReceivifyErrorContext(null, null, 0))
    {
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyApiException(string message)
        : base(message)
    {
        Headers = NoHeaders;
    }

    /// <summary>Creates the exception.</summary>
    public ReceivifyApiException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Headers = NoHeaders;
    }

    /// <summary>Creates the exception from a decoded error response.</summary>
    /// <param name="context">Everything the failure carried.</param>
    public ReceivifyApiException(ReceivifyErrorContext context)
        : base(BuildMessage(context))
    {
        Code = context.Code;
        Detail = context.Detail;
        StatusCode = context.StatusCode;
        Headers = context.Headers ?? NoHeaders;
        RawBody = context.RawBody;
        Request = context.Request;
    }

    /// <summary>The raw <c>error</c> string from the body. Always preserved, even when unrecognised.</summary>
    public string? Code { get; }

    /// <summary>
    /// The human sentence from <c>detail</c>. Intended for a log or a developer, NOT for
    /// branching — the wording is explicitly not part of the frozen contract.
    /// </summary>
    public string? Detail { get; }

    /// <summary>The HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Response headers, keys lower-cased. Never empty-checked for you.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The undecoded response body, for logging an unexpected shape.</summary>
    public string? RawBody { get; }

    /// <summary>The request method and path, for logs. Never contains the API key.</summary>
    public string? Request { get; }

    /// <summary>True for the codes the API documents as worth retrying, plus any 5xx.</summary>
    public bool IsRetryable => ErrorCodes.IsRetryable(Code) || StatusCode >= 500;

    private static string BuildMessage(ReceivifyErrorContext context)
    {
        if (!string.IsNullOrEmpty(context.Detail))
        {
            return context.Detail;
        }

        if (!string.IsNullOrEmpty(context.Code))
        {
            return "Receivify: " + context.Code;
        }

        return string.Create(CultureInfo.InvariantCulture, $"Receivify: HTTP {context.StatusCode}");
    }
}

/// <summary>400 <c>bad_request</c> — malformed request: bad JSON, a missing field, or a value that failed validation.</summary>
public sealed class BadRequestException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public BadRequestException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>bad_ticker</c> — the forwarding ticker is unknown, misspelled, or not offered by this deployment.</summary>
public sealed class BadTickerException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public BadTickerException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>bad_destination</c> — the destination address is not valid for that ticker/chain.</summary>
public sealed class BadDestinationException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public BadDestinationException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>asset_unavailable</c> — the asset is enabled but its settlement backend cannot accept it right now.</summary>
public sealed class AssetUnavailableException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public AssetUnavailableException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>amount_too_low</c> — below the economic floor: forwarding gas would exceed the fee earned.</summary>
public sealed class AmountTooLowException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public AmountTooLowException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>suspended</c> — the merchant account is suspended and cannot create new resources.</summary>
public sealed class SuspendedException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public SuspendedException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>limit_exceeded</c> — a per-account quota for this resource is already at its maximum. Note: a 400, not a 409.</summary>
public sealed class LimitExceededException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public LimitExceededException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>invalid_code</c> — the supplied two-factor (TOTP) code is not valid.</summary>
public sealed class InvalidCodeException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public InvalidCodeException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>no_setup</c> — two-factor enrolment has not been started yet.</summary>
public sealed class NoSetupException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public NoSetupException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>not_enabled</c> — two-factor authentication is not enabled on this account.</summary>
public sealed class NotEnabledException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public NotEnabledException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>no_target</c> — no registered, active webhook endpoint matched the request.</summary>
public sealed class NoTargetException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public NoTargetException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>400 <c>invalid_token</c> — a single-use confirmation token is invalid, expired, or already spent.</summary>
public sealed class InvalidTokenException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public InvalidTokenException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>401 <c>unauthorized</c> — missing, malformed, revoked or unknown API key / session cookie.</summary>
public sealed class UnauthorizedException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public UnauthorizedException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>401 <c>bad_signature</c> — an inbound provider webhook failed HMAC verification. Never returned to merchant callers.</summary>
public sealed class BadSignatureException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public BadSignatureException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>403 <c>signup_disabled</c> — self-serve signup is turned off on this deployment.</summary>
public sealed class SignupDisabledException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public SignupDisabledException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>404 <c>not_found</c> — no such resource, or it does not belong to the caller. The two are deliberately indistinguishable.</summary>
public sealed class NotFoundException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public NotFoundException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>409 <c>already_exists</c> — a resource with that unique identity already exists.</summary>
public sealed class AlreadyExistsException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public AlreadyExistsException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>409 <c>already_enabled</c> — the feature being enabled is already on.</summary>
public sealed class AlreadyEnabledException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public AlreadyEnabledException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>409 <c>not_allowed</c> — the resource exists but its state forbids this action.</summary>
public sealed class NotAllowedException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public NotAllowedException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>500 <c>internal</c> — an unhandled server error. Safe to retry.</summary>
public sealed class InternalServerException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public InternalServerException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>502 <c>upstream_error</c> — a node or payment provider we depend on failed. Retryable.</summary>
public sealed class UpstreamException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public UpstreamException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>503 <c>rate_unavailable</c> — no fresh FX rate is available to price the request. Retryable.</summary>
public sealed class RateUnavailableException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public RateUnavailableException(ReceivifyErrorContext context) : base(context) { }
}

/// <summary>
/// 429 <c>rate_limited</c> — back off. Carries the parsed <c>Retry-After</c> and
/// <c>X-RateLimit-*</c> headers so a caller can schedule the retry itself.
/// </summary>
public sealed class RateLimitedException : ReceivifyApiException
{
    /// <summary>Creates the exception.</summary>
    public RateLimitedException(ReceivifyErrorContext context)
        : base(context)
    {
        RetryAfterSeconds = IntOrNull("retry-after");
        RateLimitReset = LongOrNull("x-ratelimit-reset");
        RateLimitLimit = IntOrNull("x-ratelimit-limit");
        RateLimitRemaining = IntOrNull("x-ratelimit-remaining");
    }

    /// <summary>Seconds to wait, from <c>Retry-After</c>. Null when the header was absent.</summary>
    public int? RetryAfterSeconds { get; }

    /// <summary>Unix seconds at which the window resets, from <c>X-RateLimit-Reset</c>.</summary>
    public long? RateLimitReset { get; }

    /// <summary>Requests allowed in the current window.</summary>
    public int? RateLimitLimit { get; }

    /// <summary>Requests left in the current window.</summary>
    public int? RateLimitRemaining { get; }

    private long? LongOrNull(string header)
    {
        if (Headers.TryGetValue(header, out string? raw)
            && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
        {
            return value;
        }

        return null;
    }

    private int? IntOrNull(string header)
    {
        long? value = LongOrNull(header);
        return value is null ? null : (int)Math.Clamp(value.Value, int.MinValue, int.MaxValue);
    }
}
