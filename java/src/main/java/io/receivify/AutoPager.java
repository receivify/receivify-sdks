package io.receivify;

import java.util.HashSet;
import java.util.Iterator;
import java.util.NoSuchElementException;
import java.util.Set;
import java.util.Spliterator;
import java.util.Spliterators;
import java.util.function.Function;
import java.util.stream.Stream;
import java.util.stream.StreamSupport;

/**
 * A lazy {@link Iterable} over every item of a list endpoint. Pages are fetched on demand as you
 * iterate, following {@code next_cursor} until {@code has_more} is false. Each call to
 * {@link #iterator()} starts again from the first page.
 *
 * <p>A repeated cursor, or {@code has_more: true} with no cursor, throws {@link ReceivifyException}
 * instead of looping forever.
 *
 * @param <T> the resource type
 */
public final class AutoPager<T> implements Iterable<T> {
    private final Function<String, Page<T>> fetch;
    private final String startCursor;

    AutoPager(Function<String, Page<T>> fetch, String startCursor) {
        this.fetch = fetch;
        this.startCursor = startCursor;
    }

    /** @return a sequential stream over every item (pages fetched lazily) */
    public Stream<T> stream() {
        return StreamSupport.stream(Spliterators.spliteratorUnknownSize(iterator(), Spliterator.ORDERED), false);
    }

    @Override
    public Iterator<T> iterator() {
        return new Iterator<T>() {
            private Iterator<T> current;
            private String cursor = startCursor;
            private boolean fetchedFirst;
            private boolean more = true;
            private final Set<String> seen = new HashSet<>();

            @Override
            public boolean hasNext() {
                while (current == null || !current.hasNext()) {
                    if (fetchedFirst && !more) {
                        return false;
                    }
                    Page<T> page = fetch.apply(cursor);
                    fetchedFirst = true;
                    current = page.getData().iterator();
                    more = page.hasMore();
                    if (more) {
                        String next = page.getNextCursor();
                        if (next == null || next.isEmpty()) {
                            throw new ReceivifyException("Receivify: has_more is true but next_cursor is empty; cannot continue paging.");
                        }
                        if (!seen.add(next)) {
                            throw new ReceivifyException("Receivify: pagination cursor repeated (" + next + "); refusing to loop.");
                        }
                        cursor = next;
                    }
                }
                return true;
            }

            @Override
            public T next() {
                if (!hasNext()) {
                    throw new NoSuchElementException();
                }
                return current.next();
            }
        };
    }
}
