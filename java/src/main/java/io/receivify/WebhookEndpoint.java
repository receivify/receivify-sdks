package io.receivify;

import java.time.Instant;
import java.util.Map;

/** A registered account-level webhook endpoint (receives every invoice event, not forward.*). */
public final class WebhookEndpoint extends ReceivifyObject {
    public WebhookEndpoint(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the endpoint id */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return the normalised URL */
    public String getUrl() {
        return stringOr("url", "");
    }

    /** @return false when paused */
    public boolean isActive() {
        return bool("active");
    }

    /** @return when registered, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return when last updated, or {@code null} */
    public Instant getUpdatedAt() {
        return instant("updated_at");
    }
}
