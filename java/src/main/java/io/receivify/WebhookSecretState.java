package io.receivify;

import java.time.Instant;
import java.util.Map;

/** Rotation metadata for the webhook signing secret. Contains no secret material. */
public final class WebhookSecretState extends ReceivifyObject {
    public WebhookSecretState(Map<String, Object> raw) {
        super(raw);
    }

    /** @return when last rotated, or {@code null} if never */
    public Instant getRotatedAt() {
        return instant("rotated_at");
    }

    /** @return when the previous secret stops being accepted, or {@code null} */
    public Instant getPrevExpiresAt() {
        return instant("prev_expires_at");
    }

    /** @return true while your receiver must still accept the previous secret too */
    public boolean isPrevActive() {
        return bool("prev_active");
    }
}
