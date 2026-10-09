package io.receivify;

import java.time.Duration;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Per-call options: an {@code Idempotency-Key}, a timeout override, extra headers.
 *
 * <pre>{@code
 * client.createInvoice(params, RequestOptions.idempotencyKey("order-1"));
 * client.getInvoice(id, RequestOptions.builder().timeout(Duration.ofSeconds(5)).build());
 * }</pre>
 */
public final class RequestOptions {
    private static final RequestOptions NONE = new RequestOptions(null, null, Collections.<String, String>emptyMap());

    private final String idempotencyKey;
    private final Duration timeout;
    private final Map<String, String> headers;

    private RequestOptions(String idempotencyKey, Duration timeout, Map<String, String> headers) {
        this.idempotencyKey = idempotencyKey;
        this.timeout = timeout;
        this.headers = headers;
    }

    /** @return options carrying nothing */
    public static RequestOptions none() {
        return NONE;
    }

    /**
     * An {@code Idempotency-Key}: the server replays the original response for 24 hours, and on an
     * endpoint that supports it the SDK may then retry the call safely.
     *
     * @param key a client-generated unique string (max 255 characters)
     * @return options carrying the key
     */
    public static RequestOptions idempotencyKey(String key) {
        return builder().idempotencyKey(key).build();
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** @return the key, or {@code null} */
    public String getIdempotencyKey() {
        return idempotencyKey;
    }

    /** @return the timeout override, or {@code null} for the client default */
    public Duration getTimeout() {
        return timeout;
    }

    /** @return extra headers; never {@code null} */
    public Map<String, String> getHeaders() {
        return headers;
    }

    /** Builder for {@link RequestOptions}. */
    public static final class Builder {
        private String idempotencyKey;
        private Duration timeout;
        private final Map<String, String> headers = new LinkedHashMap<>();

        private Builder() {
        }

        public Builder idempotencyKey(String key) {
            this.idempotencyKey = key;
            return this;
        }

        public Builder timeout(Duration timeout) {
            this.timeout = timeout;
            return this;
        }

        /**
         * Add a request header. {@code Authorization} cannot be overridden here.
         *
         * @param name header name
         * @param value header value
         * @return this builder
         */
        public Builder header(String name, String value) {
            if ("authorization".equalsIgnoreCase(name)) {
                throw new IllegalArgumentException("Set the API key on the client, not as a per-call header");
            }
            headers.put(name, value);
            return this;
        }

        public RequestOptions build() {
            return new RequestOptions(idempotencyKey, timeout, Collections.unmodifiableMap(new LinkedHashMap<>(headers)));
        }
    }
}
