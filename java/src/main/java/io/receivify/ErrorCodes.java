package io.receivify;

import java.util.Arrays;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * The frozen {@code ERROR_CODES} registry, in the order {@code openapi.yaml} declares it.
 *
 * <p>Branch on {@link ReceivifyApiException#getCode()} or on the exception type, never on
 * {@link ReceivifyApiException#getDetail()}. A code this SDK has never seen still arrives intact on
 * a plain {@link ReceivifyApiException}.
 */
public final class ErrorCodes {
    private ErrorCodes() {
    }

    public static final String BAD_REQUEST = "bad_request";
    public static final String BAD_TICKER = "bad_ticker";
    public static final String BAD_DESTINATION = "bad_destination";
    public static final String ASSET_UNAVAILABLE = "asset_unavailable";
    public static final String AMOUNT_TOO_LOW = "amount_too_low";
    public static final String SUSPENDED = "suspended";
    public static final String LIMIT_EXCEEDED = "limit_exceeded";
    public static final String INVALID_CODE = "invalid_code";
    public static final String NO_SETUP = "no_setup";
    public static final String NOT_ENABLED = "not_enabled";
    public static final String NO_TARGET = "no_target";
    public static final String INVALID_TOKEN = "invalid_token";
    public static final String UNAUTHORIZED = "unauthorized";
    public static final String BAD_SIGNATURE = "bad_signature";
    public static final String SIGNUP_DISABLED = "signup_disabled";
    public static final String NOT_FOUND = "not_found";
    public static final String ALREADY_EXISTS = "already_exists";
    public static final String ALREADY_ENABLED = "already_enabled";
    public static final String NOT_ALLOWED = "not_allowed";
    public static final String RATE_LIMITED = "rate_limited";
    public static final String INTERNAL = "internal";
    public static final String UPSTREAM_ERROR = "upstream_error";
    public static final String RATE_UNAVAILABLE = "rate_unavailable";

    /** Every code, in spec order. */
    public static final List<String> ALL = Collections.unmodifiableList(Arrays.asList(
            BAD_REQUEST, BAD_TICKER, BAD_DESTINATION, ASSET_UNAVAILABLE, AMOUNT_TOO_LOW, SUSPENDED,
            LIMIT_EXCEEDED, INVALID_CODE, NO_SETUP, NOT_ENABLED, NO_TARGET, INVALID_TOKEN,
            UNAUTHORIZED, BAD_SIGNATURE, SIGNUP_DISABLED, NOT_FOUND, ALREADY_EXISTS,
            ALREADY_ENABLED, NOT_ALLOWED, RATE_LIMITED, INTERNAL, UPSTREAM_ERROR, RATE_UNAVAILABLE));

    private static final Set<String> RETRYABLE = Collections.unmodifiableSet(
            new HashSet<>(Arrays.asList(RATE_LIMITED, INTERNAL, UPSTREAM_ERROR, RATE_UNAVAILABLE)));

    /**
     * @param code a raw code, possibly {@code null}
     * @return true for the codes the API documents as worth retrying
     */
    public static boolean isRetryable(String code) {
        return code != null && RETRYABLE.contains(code);
    }

    /**
     * @param code a raw code, possibly {@code null}
     * @return true when this SDK maps the code to a dedicated exception type
     */
    public static boolean isKnown(String code) {
        return code != null && ALL.contains(code);
    }

    /** Build the right typed exception for a failed response. */
    static ReceivifyApiException toException(int status, String rawBody, Map<String, String> headers, String request) {
        String code = null;
        String detail = null;
        if (rawBody != null && !rawBody.trim().isEmpty()) {
            try {
                Object parsed = Json.parse(rawBody);
                if (parsed instanceof Map) {
                    Object e = ((Map<?, ?>) parsed).get("error");
                    Object d = ((Map<?, ?>) parsed).get("detail");
                    if (e instanceof String) {
                        code = (String) e;
                    }
                    if (d instanceof String) {
                        detail = (String) d;
                    }
                }
            } catch (Json.JsonException ignored) {
                // A proxy error page or the non-conforming health body: still an error, just untyped.
            }
        }
        ReceivifyApiException.Context c = new ReceivifyApiException.Context(code, detail, status, headers, rawBody, request);
        if (code == null) {
            return status == 429 ? new RateLimitedException(c) : new ReceivifyApiException(c);
        }
        switch (code) {
            case BAD_REQUEST: return new BadRequestException(c);
            case BAD_TICKER: return new BadTickerException(c);
            case BAD_DESTINATION: return new BadDestinationException(c);
            case ASSET_UNAVAILABLE: return new AssetUnavailableException(c);
            case AMOUNT_TOO_LOW: return new AmountTooLowException(c);
            case SUSPENDED: return new SuspendedException(c);
            case LIMIT_EXCEEDED: return new LimitExceededException(c);
            case INVALID_CODE: return new InvalidCodeException(c);
            case NO_SETUP: return new NoSetupException(c);
            case NOT_ENABLED: return new NotEnabledException(c);
            case NO_TARGET: return new NoTargetException(c);
            case INVALID_TOKEN: return new InvalidTokenException(c);
            case UNAUTHORIZED: return new UnauthorizedException(c);
            case BAD_SIGNATURE: return new BadSignatureException(c);
            case SIGNUP_DISABLED: return new SignupDisabledException(c);
            case NOT_FOUND: return new NotFoundException(c);
            case ALREADY_EXISTS: return new AlreadyExistsException(c);
            case ALREADY_ENABLED: return new AlreadyEnabledException(c);
            case NOT_ALLOWED: return new NotAllowedException(c);
            case RATE_LIMITED: return new RateLimitedException(c);
            case INTERNAL: return new InternalServerException(c);
            case UPSTREAM_ERROR: return new UpstreamException(c);
            case RATE_UNAVAILABLE: return new RateUnavailableException(c);
            default:
                // Unknown code: never crash an older SDK against a newer deployment.
                return status == 429 ? new RateLimitedException(c) : new ReceivifyApiException(c);
        }
    }
}
