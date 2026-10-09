package io.receivify;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The body of {@code POST /api/v1/address} (create a forwarding address). Creation is
 * deterministic: the same ticker, destination and callback always return the same forward.
 *
 * <pre>{@code
 * CreateAddressParams.builder()
 *     .ticker("usdc_base")
 *     .destination("0xYourWallet")
 *     .callback("https://example.com/hooks/receivify")
 *     .build();
 * }</pre>
 */
public final class CreateAddressParams {
    private final Map<String, Object> body;

    private CreateAddressParams(Map<String, Object> body) {
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

    /** Builder for {@link CreateAddressParams}. */
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

        /**
         * @param ticker from {@code listTickers()}, e.g. {@code btc}, {@code usdc_base} (required)
         * @return this builder
         */
        public Builder ticker(String ticker) {
            return put("ticker", ticker);
        }

        /**
         * @param destination your wallet, validated against the chain address format (required)
         * @return this builder
         */
        public Builder destination(String destination) {
            return put("destination", destination);
        }

        /**
         * @param callback where {@code forward.*} events go; part of the identity of the forward
         * @return this builder
         */
        public Builder callback(String callback) {
            return put("callback", callback);
        }

        /**
         * @param confirmations confirmations before a deposit is credited
         * @return this builder
         */
        public Builder confirmations(Integer confirmations) {
            return put("confirmations", confirmations);
        }

        /**
         * @param metadata arbitrary JSON-serialisable data stored on the forward
         * @return this builder
         */
        public Builder metadata(Map<String, ?> metadata) {
            return put("metadata", metadata == null ? null : new LinkedHashMap<String, Object>(metadata));
        }

        /**
         * @return the params
         * @throws IllegalArgumentException when ticker or destination is missing
         */
        public CreateAddressParams build() {
            if (!body.containsKey("ticker") || !body.containsKey("destination")) {
                throw new IllegalArgumentException("ticker and destination are required");
            }
            return new CreateAddressParams(new LinkedHashMap<>(body));
        }
    }
}
