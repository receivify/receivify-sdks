package io.receivify;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The body of {@code PATCH /api/v1/address/{id}}: pause/resume and the callback URL.
 *
 * <p>Only what you call is sent. {@link Builder#callbackUrl(String)} with {@code null} (or
 * {@link Builder#clearCallbackUrl()}) sends an explicit {@code null}, which CLEARS the callback;
 * not calling it leaves the callback alone. Destination and ticker are immutable.
 */
public final class UpdateAddressParams {
    private final Map<String, Object> body;

    private UpdateAddressParams(Map<String, Object> body) {
        this.body = Collections.unmodifiableMap(body);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** @return pause the forward (the kill switch) */
    public static UpdateAddressParams pause() {
        return builder().status(ForwardStatus.PAUSED).build();
    }

    /** @return resume a paused forward */
    public static UpdateAddressParams resume() {
        return builder().status(ForwardStatus.ACTIVE).build();
    }

    /** @return the JSON body that will be sent */
    public Map<String, Object> toMap() {
        return body;
    }

    /** Builder for {@link UpdateAddressParams}. */
    public static final class Builder {
        private final Map<String, Object> body = new LinkedHashMap<>();

        private Builder() {
        }

        /**
         * @param status {@link ForwardStatus#ACTIVE} or {@link ForwardStatus#PAUSED}
         * @return this builder
         */
        public Builder status(String status) {
            body.put("status", status);
            return this;
        }

        /**
         * @param callbackUrl the new callback; {@code null} clears it
         * @return this builder
         */
        public Builder callbackUrl(String callbackUrl) {
            body.put("callback_url", callbackUrl);
            return this;
        }

        /** @return this builder, set to clear the callback */
        public Builder clearCallbackUrl() {
            return callbackUrl(null);
        }

        public UpdateAddressParams build() {
            return new UpdateAddressParams(new LinkedHashMap<>(body));
        }
    }
}
