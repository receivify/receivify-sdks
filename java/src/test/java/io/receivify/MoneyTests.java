package io.receivify;

import static io.receivify.T.eq;
import static io.receivify.T.ok;
import static io.receivify.T.test;
import static io.receivify.T.throwsType;

import java.math.BigDecimal;
import java.math.BigInteger;

final class MoneyTests {
    private MoneyTests() {
    }

    static void run() {
        T.section("Money helpers");

        test("base units render as a display decimal", () -> {
            eq("1.50000000", Money.format("150000000", 8), "btc");
            eq("0.00000001", Money.format("1", 8), "one sat");
            eq("25.000000", Money.format("25000000", 6), "usdc");
            eq("-0.5", Money.format("-5", 1), "negative");
            eq("7", Money.format("7", 0), "zero decimals");
        });

        test("a display decimal parses back to exact base units", () -> {
            eq("100000", Money.parse("0.001", 8), "btc");
            eq("25000000", Money.parse("25", 6), "usdc");
            eq("50000000000000000", Money.parse("0.05", 18), "eth");
            eq("0", Money.parse("0.000", 8), "zero");
            eq("150000000", Money.parse("1.50000000000", 8), "trailing zeros beyond precision are fine");
        });

        test("parse refuses to round money away", () -> {
            throwsType(NumberFormatException.class, () -> Money.parse("0.000000001", 8), "too precise");
            throwsType(NumberFormatException.class, () -> Money.parse("abc", 8), "garbage");
            throwsType(NumberFormatException.class, () -> Money.parse("1e5", 8), "no exponents");
        });

        test("BigInteger and BigDecimal conversions are exact", () -> {
            String wei = "123456789012345678901234567890";
            eq(new BigInteger(wei), Money.toBigInteger(wei), "BigInteger");
            eq(new BigDecimal("123456789012.345678901234567890"), Money.toBigDecimal(wei, 18), "BigDecimal");
            eq(wei, Money.fromBigDecimal(new BigDecimal("123456789012.34567890123456789"), 18), "back");
            throwsType(NumberFormatException.class, () -> Money.toBigInteger("1.5"), "not an integer");
            throwsType(NumberFormatException.class, () -> Money.toBigInteger(" "), "blank");
        });

        test("compare, add and subtract stay exact beyond 2^64", () -> {
            String a = "340282366920938463463374607431768211456";
            ok(Money.compare(a, "1") > 0, "compare");
            eq("340282366920938463463374607431768211457", Money.add(a, "1"), "add");
            eq("-1", Money.subtract("0", "1"), "subtract");
            ok(Money.isIntegerString("-12") && !Money.isIntegerString("1.0") && !Money.isIntegerString("-"), "isIntegerString");
        });

        T.section("Error-code registry");

        test("the registry matches the spec, in order", () -> {
            eq(23, ErrorCodes.ALL.size(), "23 frozen codes");
            eq("bad_request", ErrorCodes.ALL.get(0), "first");
            eq("rate_unavailable", ErrorCodes.ALL.get(22), "last");
            ok(ErrorCodes.isRetryable("upstream_error") && !ErrorCodes.isRetryable("not_found"), "retryable set");
        });

        test("the event registry includes the forward lifecycle events", () -> {
            eq(16, EventNames.ALL.size(), "16 events");
            ok(EventNames.isForwardEvent(EventNames.FORWARD_DEFERRED), "forward.deferred is a forward event");
            ok(EventNames.isInvoiceEvent(EventNames.SWEEP_SENT) && EventNames.isInvoiceEvent(EventNames.TEST), "invoice-shaped");
        });
    }
}
