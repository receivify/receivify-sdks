package io.receivify;

import java.util.Map;

/** Paging plus the active filter for {@code GET /api/v1/payment-links}. */
public final class ListPaymentLinksParams extends PageParams {
    private ListPaymentLinksParams(Map<String, String> query) {
        super(query);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** Builder for {@link ListPaymentLinksParams}. */
    public static final class Builder extends AbstractBuilder<Builder> {
        private Builder() {
        }

        /**
         * @param active true or false; {@code null} for both
         * @return this builder
         */
        public Builder active(Boolean active) {
            return set("active", active == null ? null : active.toString());
        }

        public ListPaymentLinksParams build() {
            return new ListPaymentLinksParams(query);
        }
    }
}
