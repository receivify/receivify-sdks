package io.receivify;

import java.util.Map;

/**
 * A full round trip, for when you need more than the decoded body: the status (to tell a 201 from
 * a 200 on {@code createAddress}), the headers ({@code X-RateLimit-*}) or the replay flag.
 *
 * @param <T> the decoded body type
 */
public final class ReceivifyResponse<T> {
    private final int statusCode;
    private final Map<String, String> headers;
    private final T value;
    private final String rawBody;

    ReceivifyResponse(int statusCode, Map<String, String> headers, T value, String rawBody) {
        this.statusCode = statusCode;
        this.headers = headers;
        this.value = value;
        this.rawBody = rawBody;
    }

    /** @return the HTTP status */
    public int getStatusCode() {
        return statusCode;
    }

    /** @return response headers, keys lower-cased */
    public Map<String, String> getHeaders() {
        return headers;
    }

    /** @return the decoded body (for {@code request(...)}: the raw JSON tree), possibly {@code null} */
    public T getValue() {
        return value;
    }

    /** @return the untouched body text, possibly empty */
    public String getRawBody() {
        return rawBody;
    }

    /** @return true when the server replayed a stored Idempotency-Key response */
    public boolean isIdempotentReplay() {
        String v = headers.get("idempotent-replay");
        return v != null && v.trim().equalsIgnoreCase("true");
    }
}
