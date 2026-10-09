package io.receivify;

import java.util.Map;

/** Paging and the status filter for {@code GET /api/v1/invoices}. */
public final class ListInvoicesParams extends PageParams {
    private ListInvoicesParams(Map<String, String> query) {
        super(query);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** Builder for {@link ListInvoicesParams}. */
    public static final class Builder extends AbstractBuilder<Builder> {
        private Builder() {
        }

        /**
         * @param status exact-match status filter, e.g. {@link InvoiceStatus#PAID}
         * @return this builder
         */
        public Builder status(String status) {
            return set("status", status);
        }

        public ListInvoicesParams build() {
            return new ListInvoicesParams(query);
        }
    }
}
