package io.receivify;

import java.time.Instant;
import java.util.Map;

/**
 * The operator health report, returned on both 200 and 503 because its 503 body is deliberately
 * NOT an {@code {error, detail}}.
 */
public final class HealthReport extends ReceivifyObject {
    public HealthReport(Map<String, Object> raw) {
        super(raw);
    }

    /** @return {@code ok}, {@code warn} or {@code degraded} */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return when generated, or {@code null} */
    public Instant getTime() {
        return instant("time");
    }

    /** @return per-dependency detail, as raw JSON; never {@code null} */
    public Map<String, Object> getChecks() {
        return objectOrEmpty("checks");
    }

    /** @return true when a hard dependency is down */
    public boolean isDegraded() {
        return "degraded".equals(getStatus());
    }
}
