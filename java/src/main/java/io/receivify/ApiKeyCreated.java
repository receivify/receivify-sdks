package io.receivify;

import java.time.Instant;
import java.util.Map;

/** A freshly issued API key. {@link #getApiKey()} is shown exactly once. */
public final class ApiKeyCreated extends ReceivifyObject {
    public ApiKeyCreated(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the id of the key, which is what revokes it */
    public String getId() {
        return stringOr("id", "");
    }

    /**
     * @return the raw {@code rcv_live_...} secret; <b>{@code null} on an Idempotency-Key replay</b>
     */
    public String getApiKey() {
        return string("api_key");
    }

    /** @return the visible prefix */
    public String getPrefix() {
        String p = string("prefix");
        return p != null ? p : string("api_key_prefix");
    }

    /** @return human label */
    public String getLabel() {
        return string("label");
    }

    /** @return when it was issued, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** Never prints the secret. */
    @Override
    public String toString() {
        return "ApiKeyCreated{id=" + getId() + ", prefix=" + getPrefix() + "}";
    }
}
