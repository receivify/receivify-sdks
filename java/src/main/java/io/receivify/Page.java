package io.receivify;

import java.util.Collections;
import java.util.Iterator;
import java.util.List;

/**
 * One page of a list: the {@code {data, has_more, next_cursor}} envelope.
 *
 * <p>{@link #hasMore()} is a fact reported by the server, never inferred from the page size. Pass
 * {@link #getNextCursor()} back as {@code cursor} to fetch the next page, or use the
 * {@code autoPaging...} methods on the client to iterate every item.
 *
 * @param <T> the resource type
 */
public final class Page<T> implements Iterable<T> {
    private final List<T> data;
    private final boolean hasMore;
    private final String nextCursor;

    public Page(List<T> data, boolean hasMore, String nextCursor) {
        this.data = data == null ? Collections.<T>emptyList() : Collections.unmodifiableList(data);
        this.hasMore = hasMore;
        this.nextCursor = nextCursor;
    }

    /** @return the items on this page, newest first; never {@code null} */
    public List<T> getData() {
        return data;
    }

    /** @return whether another page exists */
    public boolean hasMore() {
        return hasMore;
    }

    /** @return the cursor for the next page; {@code null} exactly when {@link #hasMore()} is false */
    public String getNextCursor() {
        return nextCursor;
    }

    @Override
    public Iterator<T> iterator() {
        return data.iterator();
    }

    @Override
    public String toString() {
        return "Page{size=" + data.size() + ", hasMore=" + hasMore + ", nextCursor=" + nextCursor + "}";
    }
}
