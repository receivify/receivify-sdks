package io.receivify;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Base of every list-arguments type: a limit, a cursor and endpoint-specific filters, sent as query
 * parameters. Use {@link ListParams} for plain paging, or the typed subclasses
 * ({@link ListInvoicesParams}, {@link ListAddressesParams}, ...) for endpoints with filters.
 */
public abstract class PageParams {
    private final Map<String, String> query;

    PageParams(Map<String, String> query) {
        this.query = Collections.unmodifiableMap(new LinkedHashMap<>(query));
    }

    /** @return the query parameters that will be sent */
    public Map<String, String> toQuery() {
        return query;
    }

    /** @return the starting cursor, or {@code null} */
    public String getCursor() {
        return query.get("cursor");
    }

    /**
     * Shared paging setters.
     *
     * @param <B> the concrete builder type
     */
    @SuppressWarnings("unchecked")
    public abstract static class AbstractBuilder<B extends AbstractBuilder<B>> {
        final Map<String, String> query = new LinkedHashMap<>();

        AbstractBuilder() {
        }

        B set(String key, String value) {
            if (value == null || value.isEmpty()) {
                query.remove(key);
            } else {
                query.put(key, value);
            }
            return (B) this;
        }

        /**
         * @param limit page size 1..200 (larger values are clamped to 200 by the server)
         * @return this builder
         */
        public B limit(Integer limit) {
            return set("limit", limit == null ? null : limit.toString());
        }

        /**
         * @param cursor an opaque {@code next_cursor} from a previous page
         * @return this builder
         */
        public B cursor(String cursor) {
            return set("cursor", cursor);
        }
    }
}
