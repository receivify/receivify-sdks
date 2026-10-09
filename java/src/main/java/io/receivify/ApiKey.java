package io.receivify;

import java.time.Instant;
import java.util.Map;

/** A listed API key. The secret itself is never stored, so it never appears here. */
public final class ApiKey extends ReceivifyObject {
    public ApiKey(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the id that {@code revokeApiKey} takes (NOT the prefix) */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return the visible prefix */
    public String getPrefix() {
        return string("prefix");
    }

    /** @return human label */
    public String getLabel() {
        return string("label");
    }

    /** @return when it was issued, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return when it last authenticated a request, or {@code null} */
    public Instant getLastUsedAt() {
        return instant("last_used_at");
    }

    /** @return true when revoked (revoked keys are still listed) */
    public boolean isRevoked() {
        return bool("revoked");
    }
}
