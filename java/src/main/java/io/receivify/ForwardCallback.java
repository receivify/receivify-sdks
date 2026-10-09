package io.receivify;

import java.time.Instant;
import java.util.Map;

/** One webhook delivery for a forward. Outcome only: no body, no signature material. */
public final class ForwardCallback extends ReceivifyObject {
    public ForwardCallback(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the event name; see {@link EventNames} */
    public String getEvent() {
        return stringOr("event", "");
    }

    /** @return the target URL */
    public String getUrl() {
        return stringOr("url", "");
    }

    /** @return {@code pending}, {@code delivered} or {@code failed} */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return attempts made so far */
    public int getAttempts() {
        return intOr("attempts", 0);
    }

    /** @return the last HTTP status the receiver answered with, or {@code null} */
    public Integer getLastStatus() {
        return integer("last_status");
    }

    /** @return the last error (truncated), or {@code null} */
    public String getLastError() {
        return string("last_error");
    }

    /** @return when it was enqueued, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return when it was accepted, or {@code null} */
    public Instant getDeliveredAt() {
        return instant("delivered_at");
    }
}
