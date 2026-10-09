using System.Text.Json;

namespace Receivify;

/// <summary>
/// The frozen <c>ERROR_CODES</c> registry, in the order <c>openapi.yaml</c> declares it.
/// </summary>
/// <remarks>
/// Adding a code is an additive change under the API's versioning policy, so this SDK degrades to
/// <see cref="ReceivifyApiException"/> for anything it has not seen rather than failing. Branch on
/// <see cref="ReceivifyApiException.Code"/> or on the exception type — never on
/// <see cref="ReceivifyApiException.Detail"/>.
/// </remarks>
public static class ErrorCodes
{
    /// <summary>400 — malformed request: bad JSON, a missing field, or a value that failed validation.</summary>
    public const string BadRequest = "bad_request";

    /// <summary>400 — the forwarding ticker is unknown, misspelled, or not offered by this deployment.</summary>
    public const string BadTicker = "bad_ticker";

    /// <summary>400 — the destination address is not valid for that ticker/chain.</summary>
    public const string BadDestination = "bad_destination";

    /// <summary>400 — the asset is enabled but its settlement backend cannot accept it right now.</summary>
    public const string AssetUnavailable = "asset_unavailable";

    /// <summary>400 — below the economic floor: the gas to forward this amount would exceed the fee it earns.</summary>
    public const string AmountTooLow = "amount_too_low";

    /// <summary>400 — the merchant account is suspended and cannot create new resources.</summary>
    public const string Suspended = "suspended";

    /// <summary>400 — a per-account quota for this resource is already at its maximum.</summary>
    public const string LimitExceeded = "limit_exceeded";

    /// <summary>400 — the supplied two-factor (TOTP) code is not valid.</summary>
    public const string InvalidCode = "invalid_code";

    /// <summary>400 — two-factor enrolment has not been started yet.</summary>
    public const string NoSetup = "no_setup";

    /// <summary>400 — two-factor authentication is not enabled on this account.</summary>
    public const string NotEnabled = "not_enabled";

    /// <summary>400 — no registered, active webhook endpoint matched the request.</summary>
    public const string NoTarget = "no_target";

    /// <summary>400 — a single-use confirmation token is invalid, expired, or already spent.</summary>
    public const string InvalidToken = "invalid_token";

    /// <summary>401 — missing, malformed, revoked or unknown API key / session cookie.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>401 — an inbound provider webhook failed HMAC verification.</summary>
    public const string BadSignature = "bad_signature";

    /// <summary>403 — self-serve signup is turned off on this deployment.</summary>
    public const string SignupDisabled = "signup_disabled";

    /// <summary>404 — no such resource, or it does not belong to the caller.</summary>
    public const string NotFound = "not_found";

    /// <summary>409 — a resource with that unique identity already exists.</summary>
    public const string AlreadyExists = "already_exists";

    /// <summary>409 — the feature being enabled is already on.</summary>
    public const string AlreadyEnabled = "already_enabled";

    /// <summary>409 — the resource exists but its state forbids this action.</summary>
    public const string NotAllowed = "not_allowed";

    /// <summary>429 — rate limit exceeded. Back off using <c>X-RateLimit-Reset</c> / <c>Retry-After</c>.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>500 — an unhandled server error. Safe to retry.</summary>
    public const string Internal = "internal";

    /// <summary>502 — a node or payment provider we depend on failed. Retryable.</summary>
    public const string UpstreamError = "upstream_error";

    /// <summary>503 — no fresh FX rate is available to price the request. Retryable.</summary>
    public const string RateUnavailable = "rate_unavailable";

    /// <summary>Every code in the frozen registry, in spec order.</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        BadRequest,
        BadTicker,
        BadDestination,
        AssetUnavailable,
        AmountTooLow,
        Suspended,
        LimitExceeded,
        InvalidCode,
        NoSetup,
        NotEnabled,
        NoTarget,
        InvalidToken,
        Unauthorized,
        BadSignature,
        SignupDisabled,
        NotFound,
        AlreadyExists,
        AlreadyEnabled,
        NotAllowed,
        RateLimited,
        Internal,
        UpstreamError,
        RateUnavailable,
    };

    private static readonly HashSet<string> Retryable = new(StringComparer.Ordinal)
    {
        RateLimited,
        Internal,
        UpstreamError,
        RateUnavailable,
    };

    /// <summary>True for the codes the API documents as worth retrying.</summary>
    /// <param name="code">A raw error code, possibly null or unknown.</param>
    public static bool IsRetryable(string? code) => code is not null && Retryable.Contains(code);

    /// <summary>True when this SDK maps the code to a dedicated exception type.</summary>
    /// <param name="code">A raw error code, possibly null or unknown.</param>
    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

/// <summary>Turns a failed HTTP response into the right typed exception.</summary>
internal static class ReceivifyErrorFactory
{
    /// <summary>
    /// Build the exception for a 4xx/5xx. An unrecognised code — including one from a future API
    /// version — yields a plain <see cref="ReceivifyApiException"/> with the code intact.
    /// </summary>
    public static ReceivifyApiException Create(
        int statusCode,
        string? rawBody,
        IReadOnlyDictionary<string, string> headers,
        string request)
    {
        string? code = null;
        string? detail = null;

        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(rawBody);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("error", out JsonElement errorElement)
                        && errorElement.ValueKind == JsonValueKind.String)
                    {
                        code = errorElement.GetString();
                    }

                    if (document.RootElement.TryGetProperty("detail", out JsonElement detailElement)
                        && detailElement.ValueKind == JsonValueKind.String)
                    {
                        detail = detailElement.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // GET /api/health is documented as NOT conforming to { error, detail }, and a
                // reverse proxy can always interpose an HTML error page. Neither is a crash.
            }
        }

        var context = new ReceivifyErrorContext(code, detail, statusCode, headers, rawBody, request);

        return code switch
        {
            ErrorCodes.BadRequest => new BadRequestException(context),
            ErrorCodes.BadTicker => new BadTickerException(context),
            ErrorCodes.BadDestination => new BadDestinationException(context),
            ErrorCodes.AssetUnavailable => new AssetUnavailableException(context),
            ErrorCodes.AmountTooLow => new AmountTooLowException(context),
            ErrorCodes.Suspended => new SuspendedException(context),
            ErrorCodes.LimitExceeded => new LimitExceededException(context),
            ErrorCodes.InvalidCode => new InvalidCodeException(context),
            ErrorCodes.NoSetup => new NoSetupException(context),
            ErrorCodes.NotEnabled => new NotEnabledException(context),
            ErrorCodes.NoTarget => new NoTargetException(context),
            ErrorCodes.InvalidToken => new InvalidTokenException(context),
            ErrorCodes.Unauthorized => new UnauthorizedException(context),
            ErrorCodes.BadSignature => new BadSignatureException(context),
            ErrorCodes.SignupDisabled => new SignupDisabledException(context),
            ErrorCodes.NotFound => new NotFoundException(context),
            ErrorCodes.AlreadyExists => new AlreadyExistsException(context),
            ErrorCodes.AlreadyEnabled => new AlreadyEnabledException(context),
            ErrorCodes.NotAllowed => new NotAllowedException(context),
            ErrorCodes.RateLimited => new RateLimitedException(context),
            ErrorCodes.Internal => new InternalServerException(context),
            ErrorCodes.UpstreamError => new UpstreamException(context),
            ErrorCodes.RateUnavailable => new RateUnavailableException(context),

            // Unknown code: never crash an older SDK against a newer deployment. A 429 without a
            // recognised code still gets the rate-limit type so its headers stay reachable.
            _ => statusCode == 429
                ? new RateLimitedException(context)
                : new ReceivifyApiException(context),
        };
    }
}
