package io.receivify;

/**
 * 429 {@code rate_limited}: back off. Carries the parsed {@code Retry-After} and
 * {@code X-RateLimit-*} headers so you can schedule the retry yourself.
 */
public class RateLimitedException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public RateLimitedException(Context context) {
        super(context);
    }

    /** @return seconds to wait from {@code Retry-After}, or {@code null} */
    public Long getRetryAfterSeconds() {
        return header("retry-after");
    }

    /** @return Unix seconds at which the window resets, from {@code X-RateLimit-Reset}, or {@code null} */
    public Long getRateLimitReset() {
        return header("x-ratelimit-reset");
    }

    /** @return requests allowed in the window, or {@code null} */
    public Long getRateLimitLimit() {
        return header("x-ratelimit-limit");
    }

    /** @return requests left in the window, or {@code null} */
    public Long getRateLimitRemaining() {
        return header("x-ratelimit-remaining");
    }

    private Long header(String name) {
        String v = getHeaders().get(name);
        if (v == null) {
            return null;
        }
        try {
            return Long.valueOf(v.trim());
        } catch (NumberFormatException e) {
            return null;
        }
    }
}
