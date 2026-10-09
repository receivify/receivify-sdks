package io.receivify;

import java.math.BigDecimal;
import java.math.BigInteger;

/**
 * A JSON number, kept as the exact characters that appeared on the wire.
 *
 * <p>Java has no standard JSON parser, and the obvious shortcut of reading every number as a
 * {@code double} is exactly how a payment amount gets corrupted: a double cannot hold a 256-bit wei
 * value, nor even a satoshi count above 2<sup>53</sup>. So this SDK never converts a number on its
 * own. You choose the conversion, and every conversion offered here is exact (or throws).
 *
 * <p>Receivify sends every money field as a JSON <em>string</em>, so in practice you only meet this
 * type for counts, decimals, sequences and the advisory FX rates.
 */
public final class JsonNumber {
    private final String raw;

    private JsonNumber(String raw) {
        this.raw = raw;
    }

    /**
     * Wrap already-validated number text.
     *
     * @param raw text that matches the JSON number grammar
     * @return the number
     * @throws IllegalArgumentException when {@code raw} is not a JSON number
     */
    public static JsonNumber of(String raw) {
        if (raw == null || !Json.isNumberText(raw)) {
            throw new IllegalArgumentException("Not a JSON number: " + raw);
        }
        return new JsonNumber(raw);
    }

    static JsonNumber trusted(String raw) {
        return new JsonNumber(raw);
    }

    /** @return the exact characters from the wire, e.g. {@code "123456789012345678901234567890"} */
    public String raw() {
        return raw;
    }

    /** @return the exact value as a {@link BigDecimal} (never lossy) */
    public BigDecimal toBigDecimal() {
        return new BigDecimal(raw);
    }

    /**
     * @return the exact value as a {@link BigInteger}
     * @throws ArithmeticException when the number has a non-zero fractional part
     */
    public BigInteger toBigInteger() {
        return toBigDecimal().toBigIntegerExact();
    }

    /**
     * @return the value as a {@code long}
     * @throws ArithmeticException when it is fractional or out of range
     */
    public long longValue() {
        return toBigDecimal().longValueExact();
    }

    /**
     * @return the value as an {@code int}
     * @throws ArithmeticException when it is fractional or out of range
     */
    public int intValue() {
        return toBigDecimal().intValueExact();
    }

    /**
     * A lossy floating-point view. Only appropriate for advisory values such as FX rates — never for
     * money.
     *
     * @return the nearest double
     */
    public double doubleValue() {
        return Double.parseDouble(raw);
    }

    @Override
    public String toString() {
        return raw;
    }

    @Override
    public boolean equals(Object other) {
        return other instanceof JsonNumber && ((JsonNumber) other).raw.equals(raw);
    }

    @Override
    public int hashCode() {
        return raw.hashCode();
    }
}
