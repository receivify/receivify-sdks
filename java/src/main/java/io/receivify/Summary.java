package io.receivify;

import java.util.List;
import java.util.Map;

/**
 * The dashboard composite from {@code GET /api/v1/summary}. Its FX rates are advisory JSON numbers;
 * read the rest through {@link #raw()}.
 */
public final class Summary extends ReceivifyObject {
    public Summary(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the merchant block, as raw JSON; never {@code null} */
    public Map<String, Object> getMerchant() {
        return objectOrEmpty("merchant");
    }

    /** @return invoice counts keyed by status, as raw JSON; never {@code null} */
    public Map<String, Object> getByStatus() {
        return objectOrEmpty("by_status");
    }

    /** @return the advisory FX snapshot, as raw JSON; never {@code null} */
    public Map<String, Object> getFx() {
        return objectOrEmpty("fx");
    }

    /** @return supported fiat currency codes */
    public List<String> getCurrencies() {
        return stringList("currencies");
    }
}
