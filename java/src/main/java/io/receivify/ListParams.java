package io.receivify;

import java.util.Collections;
import java.util.Map;

/** Plain paging arguments (limit and cursor) for list endpoints without filters. */
public final class ListParams extends PageParams {
    private ListParams(Map<String, String> query) {
        super(query);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** @return no arguments: the server default page size, from the first page */
    public static ListParams none() {
        return new ListParams(Collections.<String, String>emptyMap());
    }

    /**
     * @param limit page size 1..200
     * @return paging arguments with just a limit
     */
    public static ListParams limit(int limit) {
        return builder().limit(limit).build();
    }

    /** Builder for {@link ListParams}. */
    public static final class Builder extends AbstractBuilder<Builder> {
        private Builder() {
        }

        public ListParams build() {
            return new ListParams(query);
        }
    }
}
