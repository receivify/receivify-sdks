package io.receivify;

import java.time.Instant;
import java.util.Map;

/** The result of a rotation. {@link #getSecret()} is shown once, here, and never again. */
public final class WebhookSecretRotated extends ReceivifyObject {
    public WebhookSecretRotated(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the new {@code whsec_...} secret; <b>{@code null} on an Idempotency-Key replay</b> */
    public String getSecret() {
        return string("secret");
    }

    /** @return when this rotation happened, or {@code null} */
    public Instant getRotatedAt() {
        return instant("rotated_at");
    }

    /** @return until this instant, verify with BOTH the new and the previous secret */
    public Instant getPrevExpiresAt() {
        return instant("prev_expires_at");
    }

    /** @return the grace window in seconds (86400 by default) */
    public int getGraceSeconds() {
        return intOr("grace_seconds", 0);
    }

    /** @return true when this rotation retired a previous secret still inside its grace window */
    public boolean isReplacedPrevious() {
        return bool("replaced_previous");
    }

    /** Never prints the secret. */
    @Override
    public String toString() {
        return "WebhookSecretRotated{rotatedAt=" + getRotatedAt() + ", prevExpiresAt=" + getPrevExpiresAt() + "}";
    }
}
