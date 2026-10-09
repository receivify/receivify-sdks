package io.receivify;

import java.util.Map;

/** Paging plus status and ticker filters for {@code GET /api/v1/address}. */
public final class ListAddressesParams extends PageParams {
    private ListAddressesParams(Map<String, String> query) {
        super(query);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** Builder for {@link ListAddressesParams}. */
    public static final class Builder extends AbstractBuilder<Builder> {
        private Builder() {
        }

        /**
         * @param status one of {@link ForwardStatus}; anything else is a 400 on this endpoint
         * @return this builder
         */
        public Builder status(String status) {
            return set("status", status);
        }

        /**
         * @param ticker exact match on the ticker as supplied at create time
         * @return this builder
         */
        public Builder ticker(String ticker) {
            return set("ticker", ticker);
        }

        public ListAddressesParams build() {
            return new ListAddressesParams(query);
        }
    }
}
