package io.receivify;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The body of {@code POST /api/v1/payment-links} and {@code PATCH /api/v1/payment-links/{id}}.
 *
 * <p>Only what you call is sent. On an update, {@code amountBtc(null)} sends an explicit
 * {@code null}, converting a fixed link into an open-amount one.
 */
public final class PaymentLinkParams {
    private final Map<String, Object> body;

    private PaymentLinkParams(Map<String, Object> body) {
        this.body = Collections.unmodifiableMap(body);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** @return the JSON body that will be sent */
    public Map<String, Object> toMap() {
        return body;
    }

    /** Builder for {@link PaymentLinkParams}. */
    public static final class Builder {
        private final Map<String, Object> body = new LinkedHashMap<>();

        private Builder() {
        }

        /**
         * @param title display title (max 120 characters)
         * @return this builder
         */
        public Builder title(String title) {
            body.put("title", title);
            return this;
        }

        /**
         * @param amountBtc a DECIMAL BTC string; {@code null} means open amount
         * @return this builder
         */
        public Builder amountBtc(String amountBtc) {
            body.put("amount_btc", amountBtc);
            return this;
        }

        /**
         * @param active false stops the link minting new invoices (update only)
         * @return this builder
         */
        public Builder active(boolean active) {
            body.put("active", active);
            return this;
        }

        public PaymentLinkParams build() {
            return new PaymentLinkParams(new LinkedHashMap<>(body));
        }
    }
}
