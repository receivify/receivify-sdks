package io.receivify;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/** The body of {@code PATCH /api/v1/settings}. Only what you set is sent. */
public final class UpdateSettingsParams {
    private final Map<String, Object> body;

    private UpdateSettingsParams(Map<String, Object> body) {
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

    /** Builder for {@link UpdateSettingsParams}. */
    public static final class Builder {
        private final Map<String, Object> body = new LinkedHashMap<>();

        private Builder() {
        }

        /**
         * @param displayCurrency ISO-4217 code
         * @return this builder
         */
        public Builder displayCurrency(String displayCurrency) {
            body.put("display_currency", displayCurrency);
            return this;
        }

        /**
         * @param addFeeOntop the ACCOUNT default for invoices that do not state the flag
         * @return this builder
         */
        public Builder addFeeOntop(boolean addFeeOntop) {
            body.put("add_fee_ontop", addFeeOntop);
            return this;
        }

        public UpdateSettingsParams build() {
            return new UpdateSettingsParams(new LinkedHashMap<>(body));
        }
    }
}
