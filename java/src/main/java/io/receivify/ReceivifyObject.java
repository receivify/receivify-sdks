package io.receivify;

import java.time.Instant;
import java.time.format.DateTimeParseException;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.function.Function;

/**
 * Base class of every response model: an immutable view over the decoded JSON object.
 *
 * <p>Typed getters cover every field the spec documents. Anything else — a field added by a newer
 * deployment, say — is still reachable through {@link #raw()} or {@link #get(String)}, because the
 * API's versioning policy allows additive changes and nothing is ever dropped on decode.
 *
 * <p>Getters returning an object type ({@code String}, {@code Integer}, {@code Instant}) may return
 * {@code null} when the server sends {@code null} or omits the field; their Javadoc says when.
 * Getters returning a {@code List} never return {@code null}.
 */
public abstract class ReceivifyObject {
    private final Map<String, Object> raw;

    protected ReceivifyObject(Map<String, Object> raw) {
        this.raw = raw == null ? Collections.<String, Object>emptyMap() : Collections.unmodifiableMap(raw);
    }

    /** @return the decoded JSON object, unmodifiable, exactly as received */
    public Map<String, Object> raw() {
        return raw;
    }

    /**
     * @param key a JSON property name
     * @return the raw value: {@code String}, {@link JsonNumber}, {@code Boolean}, {@code Map},
     *     {@code List} or {@code null}
     */
    public Object get(String key) {
        return raw.get(key);
    }

    /** @return the object as compact JSON */
    public String toJson() {
        return Json.write(raw);
    }

    @Override
    public String toString() {
        return getClass().getSimpleName() + toJson();
    }

    @Override
    public boolean equals(Object other) {
        return other != null && other.getClass() == getClass() && ((ReceivifyObject) other).raw.equals(raw);
    }

    @Override
    public int hashCode() {
        return raw.hashCode();
    }

    /* ---------------- typed accessors ---------------- */

    protected final String string(String key) {
        Object v = raw.get(key);
        if (v == null) {
            return null;
        }
        if (v instanceof String) {
            return (String) v;
        }
        if (v instanceof JsonNumber || v instanceof Boolean) {
            return v.toString();
        }
        return null;
    }

    protected final String stringOr(String key, String fallback) {
        String v = string(key);
        return v == null ? fallback : v;
    }

    /**
     * A money field. The server sends a string; if a number ever turns up instead its exact raw
     * digits are returned. Never routed through a double.
     */
    protected final String money(String key) {
        Object v = raw.get(key);
        if (v instanceof String) {
            return (String) v;
        }
        if (v instanceof JsonNumber) {
            return ((JsonNumber) v).raw();
        }
        return null;
    }

    protected final boolean bool(String key) {
        return Boolean.TRUE.equals(raw.get(key));
    }

    protected final Boolean boolOrNull(String key) {
        Object v = raw.get(key);
        return v instanceof Boolean ? (Boolean) v : null;
    }

    protected final Integer integer(String key) {
        Object v = raw.get(key);
        if (v instanceof JsonNumber) {
            try {
                return ((JsonNumber) v).intValue();
            } catch (ArithmeticException e) {
                return null;
            }
        }
        if (v instanceof String) {
            try {
                return Integer.valueOf((String) v);
            } catch (NumberFormatException e) {
                return null;
            }
        }
        return null;
    }

    protected final int intOr(String key, int fallback) {
        Integer v = integer(key);
        return v == null ? fallback : v;
    }

    protected final Long longOrNull(String key) {
        Object v = raw.get(key);
        if (v instanceof JsonNumber) {
            try {
                return ((JsonNumber) v).longValue();
            } catch (ArithmeticException e) {
                return null;
            }
        }
        return null;
    }

    protected final Instant instant(String key) {
        String v = string(key);
        if (v == null || v.isEmpty()) {
            return null;
        }
        try {
            return Instant.parse(v);
        } catch (DateTimeParseException e) {
            return null;
        }
    }

    @SuppressWarnings("unchecked")
    protected final Map<String, Object> object(String key) {
        Object v = raw.get(key);
        return v instanceof Map ? (Map<String, Object>) v : null;
    }

    protected final Map<String, Object> objectOrEmpty(String key) {
        Map<String, Object> v = object(key);
        return v == null ? Collections.<String, Object>emptyMap() : v;
    }

    @SuppressWarnings("unchecked")
    protected final <T> List<T> list(String key, Function<Map<String, Object>, T> factory) {
        Object v = raw.get(key);
        if (!(v instanceof List)) {
            return Collections.emptyList();
        }
        List<T> out = new ArrayList<>();
        for (Object item : (List<Object>) v) {
            if (item instanceof Map) {
                out.add(factory.apply((Map<String, Object>) item));
            }
        }
        return Collections.unmodifiableList(out);
    }

    @SuppressWarnings("unchecked")
    protected final List<String> stringList(String key) {
        Object v = raw.get(key);
        if (!(v instanceof List)) {
            return Collections.emptyList();
        }
        List<String> out = new ArrayList<>();
        for (Object item : (List<Object>) v) {
            if (item instanceof String) {
                out.add((String) item);
            }
        }
        return Collections.unmodifiableList(out);
    }
}
