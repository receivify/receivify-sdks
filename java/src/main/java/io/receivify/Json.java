package io.receivify;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/**
 * A small, strict JSON reader and writer — the SDK's only JSON dependency is this file.
 *
 * <p>The tree uses plain Java types:
 *
 * <ul>
 *   <li>object &rarr; {@code Map<String, Object>} (insertion-ordered)
 *   <li>array &rarr; {@code List<Object>}
 *   <li>string &rarr; {@code String}
 *   <li>number &rarr; {@link JsonNumber} (the raw text, so nothing is ever rounded)
 *   <li>{@code true}/{@code false} &rarr; {@code Boolean}
 *   <li>{@code null} &rarr; Java {@code null} (use {@code containsKey} to tell it from an absent key)
 * </ul>
 *
 * <p>The reader follows RFC 8259 strictly: no comments, no trailing commas, no leading zeros, no
 * unescaped control characters, no trailing garbage. Trees returned by {@link #parse(String)} are
 * deeply unmodifiable.
 */
public final class Json {
    private static final int MAX_DEPTH = 512;

    private Json() {
    }

    /** Thrown when text is not valid JSON. */
    public static final class JsonException extends RuntimeException {
        private static final long serialVersionUID = 1L;

        JsonException(String message) {
            super(message);
        }
    }

    /* ============================================================ *
     * Reading
     * ============================================================ */

    /**
     * Parse UTF-8 bytes.
     *
     * @param utf8 the document
     * @return the (unmodifiable) tree
     * @throws JsonException on any syntax error
     */
    public static Object parse(byte[] utf8) {
        return parse(new String(utf8, StandardCharsets.UTF_8));
    }

    /**
     * Parse a JSON document.
     *
     * @param text the document
     * @return the (unmodifiable) tree
     * @throws JsonException on any syntax error
     */
    public static Object parse(String text) {
        if (text == null) {
            throw new JsonException("JSON input is null");
        }
        Reader reader = new Reader(text);
        reader.skipBom();
        reader.skipWhitespace();
        Object value = reader.readValue(0);
        reader.skipWhitespace();
        if (reader.pos != text.length()) {
            throw reader.error("Unexpected trailing content");
        }
        return value;
    }

    /**
     * Parse a document that must be an object.
     *
     * @param text the document
     * @return the object
     * @throws JsonException when it is not valid JSON or not an object
     */
    @SuppressWarnings("unchecked")
    public static Map<String, Object> parseObject(String text) {
        Object value = parse(text);
        if (!(value instanceof Map)) {
            throw new JsonException("Expected a JSON object");
        }
        return (Map<String, Object>) value;
    }

    static boolean isNumberText(String s) {
        int i = 0;
        int n = s.length();
        if (i < n && s.charAt(i) == '-') {
            i++;
        }
        if (i >= n) {
            return false;
        }
        if (s.charAt(i) == '0') {
            i++;
        } else if (s.charAt(i) >= '1' && s.charAt(i) <= '9') {
            while (i < n && isDigit(s.charAt(i))) {
                i++;
            }
        } else {
            return false;
        }
        if (i < n && s.charAt(i) == '.') {
            i++;
            int start = i;
            while (i < n && isDigit(s.charAt(i))) {
                i++;
            }
            if (i == start) {
                return false;
            }
        }
        if (i < n && (s.charAt(i) == 'e' || s.charAt(i) == 'E')) {
            i++;
            if (i < n && (s.charAt(i) == '+' || s.charAt(i) == '-')) {
                i++;
            }
            int start = i;
            while (i < n && isDigit(s.charAt(i))) {
                i++;
            }
            if (i == start) {
                return false;
            }
        }
        return i == n;
    }

    private static boolean isDigit(char c) {
        return c >= '0' && c <= '9';
    }

    private static final class Reader {
        private final String s;
        private int pos;

        Reader(String s) {
            this.s = s;
        }

        JsonException error(String message) {
            return new JsonException(message + " at offset " + pos);
        }

        void skipBom() {
            if (pos < s.length() && s.charAt(pos) == '﻿') {
                pos++;
            }
        }

        void skipWhitespace() {
            while (pos < s.length()) {
                char c = s.charAt(pos);
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') {
                    pos++;
                } else {
                    break;
                }
            }
        }

        Object readValue(int depth) {
            if (depth > MAX_DEPTH) {
                throw error("Nesting deeper than " + MAX_DEPTH);
            }
            if (pos >= s.length()) {
                throw error("Unexpected end of input");
            }
            char c = s.charAt(pos);
            switch (c) {
                case '{':
                    return readObject(depth);
                case '[':
                    return readArray(depth);
                case '"':
                    return readString();
                case 't':
                    expectLiteral("true");
                    return Boolean.TRUE;
                case 'f':
                    expectLiteral("false");
                    return Boolean.FALSE;
                case 'n':
                    expectLiteral("null");
                    return null;
                default:
                    if (c == '-' || isDigit(c)) {
                        return readNumber();
                    }
                    throw error("Unexpected character '" + c + "'");
            }
        }

        void expectLiteral(String literal) {
            if (!s.startsWith(literal, pos)) {
                throw error("Invalid literal");
            }
            pos += literal.length();
        }

        Map<String, Object> readObject(int depth) {
            pos++; // {
            Map<String, Object> map = new LinkedHashMap<>();
            skipWhitespace();
            if (pos < s.length() && s.charAt(pos) == '}') {
                pos++;
                return Collections.unmodifiableMap(map);
            }
            while (true) {
                skipWhitespace();
                if (pos >= s.length() || s.charAt(pos) != '"') {
                    throw error("Expected a string key");
                }
                String key = readString();
                skipWhitespace();
                if (pos >= s.length() || s.charAt(pos) != ':') {
                    throw error("Expected ':'");
                }
                pos++;
                skipWhitespace();
                Object value = readValue(depth + 1);
                map.put(key, value);
                skipWhitespace();
                if (pos >= s.length()) {
                    throw error("Unterminated object");
                }
                char c = s.charAt(pos++);
                if (c == '}') {
                    return Collections.unmodifiableMap(map);
                }
                if (c != ',') {
                    pos--;
                    throw error("Expected ',' or '}'");
                }
            }
        }

        List<Object> readArray(int depth) {
            pos++; // [
            List<Object> list = new ArrayList<>();
            skipWhitespace();
            if (pos < s.length() && s.charAt(pos) == ']') {
                pos++;
                return Collections.unmodifiableList(list);
            }
            while (true) {
                skipWhitespace();
                list.add(readValue(depth + 1));
                skipWhitespace();
                if (pos >= s.length()) {
                    throw error("Unterminated array");
                }
                char c = s.charAt(pos++);
                if (c == ']') {
                    return Collections.unmodifiableList(list);
                }
                if (c != ',') {
                    pos--;
                    throw error("Expected ',' or ']'");
                }
            }
        }

        String readString() {
            pos++; // opening quote
            StringBuilder sb = null;
            int start = pos;
            while (true) {
                if (pos >= s.length()) {
                    throw error("Unterminated string");
                }
                char c = s.charAt(pos);
                if (c == '"') {
                    String out = sb == null ? s.substring(start, pos) : sb.append(s, start, pos).toString();
                    pos++;
                    return out;
                }
                if (c < 0x20) {
                    throw error("Unescaped control character in string");
                }
                if (c == '\\') {
                    if (sb == null) {
                        sb = new StringBuilder();
                    }
                    sb.append(s, start, pos);
                    pos++;
                    if (pos >= s.length()) {
                        throw error("Unterminated escape");
                    }
                    char e = s.charAt(pos++);
                    switch (e) {
                        case '"':
                            sb.append('"');
                            break;
                        case '\\':
                            sb.append('\\');
                            break;
                        case '/':
                            sb.append('/');
                            break;
                        case 'b':
                            sb.append('\b');
                            break;
                        case 'f':
                            sb.append('\f');
                            break;
                        case 'n':
                            sb.append('\n');
                            break;
                        case 'r':
                            sb.append('\r');
                            break;
                        case 't':
                            sb.append('\t');
                            break;
                        case 'u':
                            sb.append(readHex4());
                            break;
                        default:
                            pos--;
                            throw error("Invalid escape '\\" + e + "'");
                    }
                    start = pos;
                    continue;
                }
                pos++;
            }
        }

        char readHex4() {
            if (pos + 4 > s.length()) {
                throw error("Truncated \\u escape");
            }
            int value = 0;
            for (int i = 0; i < 4; i++) {
                char h = s.charAt(pos++);
                int d = Character.digit(h, 16);
                if (d < 0) {
                    pos--;
                    throw error("Invalid hex digit in \\u escape");
                }
                value = (value << 4) | d;
            }
            // A lone surrogate is passed through as-is: it is legal JSON, and refusing it would
            // reject a document that other parsers accept.
            return (char) value;
        }

        JsonNumber readNumber() {
            int start = pos;
            if (s.charAt(pos) == '-') {
                pos++;
            }
            if (pos >= s.length()) {
                throw error("Truncated number");
            }
            char c = s.charAt(pos);
            if (c == '0') {
                pos++;
                if (pos < s.length() && isDigit(s.charAt(pos))) {
                    throw error("Leading zeros are not allowed");
                }
            } else if (c >= '1' && c <= '9') {
                while (pos < s.length() && isDigit(s.charAt(pos))) {
                    pos++;
                }
            } else {
                throw error("Invalid number");
            }
            if (pos < s.length() && s.charAt(pos) == '.') {
                pos++;
                int fracStart = pos;
                while (pos < s.length() && isDigit(s.charAt(pos))) {
                    pos++;
                }
                if (pos == fracStart) {
                    throw error("Expected a digit after '.'");
                }
            }
            if (pos < s.length() && (s.charAt(pos) == 'e' || s.charAt(pos) == 'E')) {
                pos++;
                if (pos < s.length() && (s.charAt(pos) == '+' || s.charAt(pos) == '-')) {
                    pos++;
                }
                int expStart = pos;
                while (pos < s.length() && isDigit(s.charAt(pos))) {
                    pos++;
                }
                if (pos == expStart) {
                    throw error("Expected a digit in the exponent");
                }
            }
            return JsonNumber.trusted(s.substring(start, pos));
        }
    }

    /* ============================================================ *
     * Writing
     * ============================================================ */

    /**
     * Serialise a value to compact JSON.
     *
     * <p>Accepted: {@code null}, {@link Map} with string keys, {@link Iterable}, {@code Object[]},
     * {@link String}, {@link Character}, {@link Boolean}, {@link JsonNumber}, the integral boxed
     * types, {@link BigInteger}, {@link BigDecimal} (written in plain notation), finite
     * {@code Double}/{@code Float}, and any {@link ReceivifyObject} (its raw JSON).
     *
     * @param value the value
     * @return compact JSON text
     * @throws IllegalArgumentException for an unsupported type or a non-finite double
     */
    public static String write(Object value) {
        StringBuilder sb = new StringBuilder();
        writeValue(sb, value, 0);
        return sb.toString();
    }

    private static void writeValue(StringBuilder sb, Object value, int depth) {
        if (depth > MAX_DEPTH) {
            throw new IllegalArgumentException("Nesting deeper than " + MAX_DEPTH);
        }
        if (value == null) {
            sb.append("null");
        } else if (value instanceof String) {
            writeString(sb, (String) value);
        } else if (value instanceof Character) {
            writeString(sb, value.toString());
        } else if (value instanceof Boolean) {
            sb.append(((Boolean) value) ? "true" : "false");
        } else if (value instanceof JsonNumber) {
            sb.append(((JsonNumber) value).raw());
        } else if (value instanceof Integer || value instanceof Long || value instanceof Short
                || value instanceof Byte || value instanceof BigInteger) {
            sb.append(value.toString());
        } else if (value instanceof BigDecimal) {
            sb.append(((BigDecimal) value).toPlainString());
        } else if (value instanceof Double || value instanceof Float) {
            double d = ((Number) value).doubleValue();
            if (Double.isNaN(d) || Double.isInfinite(d)) {
                throw new IllegalArgumentException("JSON cannot represent " + d);
            }
            sb.append(value.toString());
        } else if (value instanceof ReceivifyObject) {
            writeValue(sb, ((ReceivifyObject) value).raw(), depth + 1);
        } else if (value instanceof Map) {
            sb.append('{');
            boolean first = true;
            for (Map.Entry<?, ?> entry : ((Map<?, ?>) value).entrySet()) {
                if (!(entry.getKey() instanceof String)) {
                    throw new IllegalArgumentException("JSON object keys must be strings");
                }
                if (!first) {
                    sb.append(',');
                }
                first = false;
                writeString(sb, (String) entry.getKey());
                sb.append(':');
                writeValue(sb, entry.getValue(), depth + 1);
            }
            sb.append('}');
        } else if (value instanceof Iterable) {
            sb.append('[');
            Iterator<?> it = ((Iterable<?>) value).iterator();
            boolean first = true;
            while (it.hasNext()) {
                if (!first) {
                    sb.append(',');
                }
                first = false;
                writeValue(sb, it.next(), depth + 1);
            }
            sb.append(']');
        } else if (value instanceof Object[]) {
            sb.append('[');
            Object[] array = (Object[]) value;
            for (int i = 0; i < array.length; i++) {
                if (i > 0) {
                    sb.append(',');
                }
                writeValue(sb, array[i], depth + 1);
            }
            sb.append(']');
        } else {
            throw new IllegalArgumentException(
                    "Cannot serialise " + value.getClass().getName() + " as JSON");
        }
    }

    private static final char[] HEX = "0123456789abcdef".toCharArray();

    private static void writeString(StringBuilder sb, String s) {
        sb.append('"');
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"':
                    sb.append("\\\"");
                    break;
                case '\\':
                    sb.append("\\\\");
                    break;
                case '\n':
                    sb.append("\\n");
                    break;
                case '\r':
                    sb.append("\\r");
                    break;
                case '\t':
                    sb.append("\\t");
                    break;
                case '\b':
                    sb.append("\\b");
                    break;
                case '\f':
                    sb.append("\\f");
                    break;
                default:
                    if (c < 0x20 || c == ' ' || c == ' ') {
                        sb.append("\\u")
                                .append(HEX[(c >> 12) & 0xF])
                                .append(HEX[(c >> 8) & 0xF])
                                .append(HEX[(c >> 4) & 0xF])
                                .append(HEX[c & 0xF]);
                    } else {
                        sb.append(c);
                    }
            }
        }
        sb.append('"');
    }
}
