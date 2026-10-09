package io.receivify;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The body of {@code POST /api/v1/recurring}. Give EITHER {@code amountFiat} (+ currency) OR
 * {@code amount}, plus {@code intervalDays}.
 */
public final class CreateRecurringParams {
    private final Map<String, Object> body;

    private CreateRecurringParams(Map<String, Object> body) {
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

    /** Builder for {@link CreateRecurringParams}. */
    public static final class Builder {
        private final Map<String, Object> body = new LinkedHashMap<>();

        private Builder() {
        }

        private Builder put(String key, Object value) {
            if (value == null) {
                body.remove(key);
            } else {
                body.put(key, value);
            }
            return this;
        }

        public Builder title(String title) {
            return put("title", title);
        }

        public Builder asset(String asset) {
            return put("asset", asset);
        }

        /**
         * @param amountFiat a decimal fiat string such as {@code "25.00"}
         * @return this builder
         */
        public Builder amountFiat(String amountFiat) {
            return put("amount_fiat", amountFiat);
        }

        public Builder currency(String currency) {
            return put("currency", currency);
        }

        /**
         * @param amount a DECIMAL coin amount (not base units)
         * @return this builder
         */
        public Builder amount(String amount) {
            return put("amount", amount);
        }

        public Builder intervalDays(int intervalDays) {
            return put("interval_days", intervalDays);
        }

        public Builder callbackUrl(String callbackUrl) {
            return put("callback_url", callbackUrl);
        }

        public CreateRecurringParams build() {
            return new CreateRecurringParams(new LinkedHashMap<>(body));
        }
    }
}
