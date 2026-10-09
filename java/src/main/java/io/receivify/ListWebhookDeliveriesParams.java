package io.receivify;

import java.util.Map;

/** Paging plus event and status filters for {@code GET /api/v1/webhooks/deliveries}. */
public final class ListWebhookDeliveriesParams extends PageParams {
    private ListWebhookDeliveriesParams(Map<String, String> query) {
        super(query);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** Builder for {@link ListWebhookDeliveriesParams}. */
    public static final class Builder extends AbstractBuilder<Builder> {
        private Builder() {
        }

        /**
         * @param event one of {@link EventNames}; anything else is a 400
         * @return this builder
         */
        public Builder event(String event) {
            return set("event", event);
        }

        /**
         * @param status {@code pending}, {@code delivered} or {@code failed}
         * @return this builder
         */
        public Builder status(String status) {
            return set("status", status);
        }

        public ListWebhookDeliveriesParams build() {
            return new ListWebhookDeliveriesParams(query);
        }
    }
}
