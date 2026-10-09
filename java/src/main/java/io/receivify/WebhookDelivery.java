package io.receivify;

import java.time.Instant;
import java.util.Map;

/** One row of the webhook delivery log. */
public final class WebhookDelivery extends ReceivifyObject {
    public WebhookDelivery(Map<String, Object> raw) {
        super(raw);
    }

    /** @return a 24-character hex id; pass it to {@code replayWebhookDelivery} */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return set on invoice and sweep rows, {@code null} on forward rows */
    public String getInvoiceId() {
        return string("invoice_id");
    }

    /** @return set on forward rows, {@code null} otherwise */
    public String getForwardId() {
        return string("forward_id");
    }

    /** @return the event name */
    public String getEvent() {
        return stringOr("event", "");
    }

    /** @return {@code pending}, {@code delivered} or {@code failed} */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return attempts made */
    public int getAttempts() {
        return intOr("attempts", 0);
    }

    /** @return the target URL */
    public String getUrl() {
        return stringOr("url", "");
    }

    /** @return the last HTTP status the receiver answered with, or {@code null} */
    public Integer getLastStatus() {
        return integer("last_status");
    }

    /** @return the last error, or {@code null} */
    public String getLastError() {
        return string("last_error");
    }

    /** @return when enqueued, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return when accepted, or {@code null} */
    public Instant getDeliveredAt() {
        return instant("delivered_at");
    }
}
