package io.receivify;

import java.math.BigDecimal;
import java.math.BigInteger;

/**
 * Exact arithmetic and formatting for the API's money strings.
 *
 * <p>Money on the wire is always a {@code String} of exact-integer base units (satoshis, wei, ...)
 * and this SDK never changes that. Everything here routes through {@link BigInteger} and
 * {@link BigDecimal}; nothing parses a float. Take {@code decimals} from
 * {@link Invoice#getDecimals()} or {@link Ticker#getDecimals()}; never hard-code 8.
 */
public final class Money {
    private Money() {
    }

    /**
     * @param baseUnits an integer string such as {@code "1000000000000000000"}
     * @return the exact value
     * @throws NumberFormatException when it is not an optionally-signed run of digits
     */
    public static BigInteger toBigInteger(String baseUnits) {
        if (baseUnits == null) {
            throw new NumberFormatException("Receivify: amount is null");
        }
        String t = baseUnits.trim();
        if (!isIntegerString(t)) {
            throw new NumberFormatException("Receivify: \"" + baseUnits + "\" is not an exact-integer base-unit amount.");
        }
        return new BigInteger(t);
    }

    /**
     * Base units to an exact display {@link BigDecimal}, e.g. {@code ("150000000", 8)} gives
     * {@code 1.50000000}.
     *
     * @param baseUnits exact-integer base units
     * @param decimals base units per whole coin, as a power of ten
     * @return the display value (scale = decimals)
     */
    public static BigDecimal toBigDecimal(String baseUnits, int decimals) {
        requireDecimals(decimals);
        return new BigDecimal(toBigInteger(baseUnits), decimals);
    }

    /**
     * Render base units as a display decimal string, e.g. {@code format("150000000", 8)} is
     * {@code "1.50000000"}. Display only.
     *
     * @param baseUnits exact-integer base units
     * @param decimals base units per whole coin, as a power of ten
     * @return the display string
     */
    public static String format(String baseUnits, int decimals) {
        return toBigDecimal(baseUnits, decimals).toPlainString();
    }

    /**
     * Convert a display decimal into exact base units, e.g. {@code parse("1.5", 8)} is
     * {@code "150000000"}. Refuses to round: more precision than the asset holds throws.
     *
     * @param displayAmount a plain decimal string such as {@code "0.0012"}
     * @param decimals base units per whole coin
     * @return exact-integer base units, as a string
     * @throws NumberFormatException when not a plain decimal, or too precise
     */
    public static String parse(String displayAmount, int decimals) {
        requireDecimals(decimals);
        if (displayAmount == null) {
            throw new NumberFormatException("Receivify: amount is null");
        }
        String text = displayAmount.trim();
        if (!text.matches("[+-]?([0-9]+(\\.[0-9]*)?|\\.[0-9]+)")) {
            throw new NumberFormatException("Receivify: \"" + displayAmount + "\" is not a plain decimal amount.");
        }
        return fromBigDecimal(new BigDecimal(text), decimals);
    }

    /**
     * Convert an exact display {@link BigDecimal} to base units. Refuses to round.
     *
     * @param display the display amount, e.g. {@code new BigDecimal("25.5")}
     * @param decimals base units per whole coin
     * @return exact-integer base units, as a string
     * @throws NumberFormatException when the value carries more precision than {@code decimals}
     */
    public static String fromBigDecimal(BigDecimal display, int decimals) {
        requireDecimals(decimals);
        BigDecimal scaled = display.movePointRight(decimals);
        try {
            return scaled.toBigIntegerExact().toString();
        } catch (ArithmeticException e) {
            throw new NumberFormatException("Receivify: " + display.toPlainString()
                    + " carries more precision than an asset with " + decimals
                    + " decimals can hold. Rounding money silently is not something this SDK does.");
        }
    }

    /**
     * @param left base units
     * @param right base units
     * @return negative, zero or positive, exactly
     */
    public static int compare(String left, String right) {
        return toBigInteger(left).compareTo(toBigInteger(right));
    }

    /**
     * @param left base units
     * @param right base units
     * @return the exact sum, as base units
     */
    public static String add(String left, String right) {
        return toBigInteger(left).add(toBigInteger(right)).toString();
    }

    /**
     * @param left base units
     * @param right base units
     * @return the exact difference, as base units
     */
    public static String subtract(String left, String right) {
        return toBigInteger(left).subtract(toBigInteger(right)).toString();
    }

    /**
     * @param value any string
     * @return true when it is an optionally-negative run of decimal digits and nothing else
     */
    public static boolean isIntegerString(String value) {
        if (value == null || value.isEmpty()) {
            return false;
        }
        int start = value.charAt(0) == '-' ? 1 : 0;
        if (value.length() == start) {
            return false;
        }
        for (int i = start; i < value.length(); i++) {
            char c = value.charAt(i);
            if (c < '0' || c > '9') {
                return false;
            }
        }
        return true;
    }

    private static void requireDecimals(int decimals) {
        if (decimals < 0) {
            throw new IllegalArgumentException("decimals must be >= 0");
        }
    }
}
