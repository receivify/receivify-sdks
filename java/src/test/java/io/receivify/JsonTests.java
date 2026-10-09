package io.receivify;

import static io.receivify.T.eq;
import static io.receivify.T.ok;
import static io.receivify.T.test;
import static io.receivify.T.throwsType;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.Arrays;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

final class JsonTests {
    private JsonTests() {
    }

    @SuppressWarnings("unchecked")
    static void run() {
        T.section("JSON reader and writer");

        test("a 78-digit integer stays exact through parse and write", () -> {
            String big = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
            Map<String, Object> m = Json.parseObject("{\"wei\":" + big + "}");
            JsonNumber n = (JsonNumber) m.get("wei");
            eq(big, n.raw(), "raw text kept");
            eq(new BigInteger(big), n.toBigInteger(), "BigInteger exact");
            eq("{\"wei\":" + big + "}", Json.write(m), "written back verbatim");
        });

        test("2^53+1 is not rounded the way a double would round it", () -> {
            JsonNumber n = (JsonNumber) Json.parse("9007199254740993");
            eq("9007199254740993", n.raw(), "raw");
            eq(9007199254740993L, n.longValue(), "long exact");
        });

        test("decimals and exponents keep their exact text", () -> {
            List<Object> l = (List<Object>) Json.parse("[0.1, -0, 1e400, 2.50E-3, 0.30000000000000004]");
            eq("0.1", l.get(0).toString(), "0.1");
            eq("-0", l.get(1).toString(), "-0");
            eq("1e400", l.get(2).toString(), "beyond double range kept");
            eq(new BigDecimal("2.50E-3"), ((JsonNumber) l.get(3)).toBigDecimal(), "BigDecimal");
            eq("0.30000000000000004", l.get(4).toString(), "no float formatting");
        });

        test("standard escapes decode", () -> {
            String s = (String) Json.parse("\"q\\\" b\\\\ s\\/ \\b\\f\\n\\r\\t\"");
            eq("q\" b\\ s/ \b\f\n\r\t", s, "escapes");
        });

        test("unicode escapes, surrogate pairs and raw UTF-8 decode", () -> {
            eq("\u00e9", Json.parse("\"\\u00e9\""), "BMP escape");
            eq("\uD83D\uDE00", Json.parse("\"\\ud83d\\ude00\""), "surrogate pair escape");
            eq("caf\u00e9 \u20ac \uD83D\uDE00", Json.parse("\"caf\u00e9 \u20ac \uD83D\uDE00\""), "raw UTF-8 text");
            byte[] utf8 = "{\"s\":\"\u00fc\u4e2d\"}".getBytes(java.nio.charset.StandardCharsets.UTF_8);
            eq("\u00fc\u4e2d", Json.parseObject(new String(utf8, java.nio.charset.StandardCharsets.UTF_8)).get("s"), "bytes");
            eq("\u00fc\u4e2d", ((Map<String, Object>) Json.parse(utf8)).get("s"), "parse(byte[])");
        });

        test("the writer escapes quotes, backslashes and control characters", () -> {
            String out = Json.write("a\"b\\c\nd\u0001e\u2028");
            eq("\"a\\\"b\\\\c\\nd\\u0001e\\u2028\"", out, "escaped");
            eq("a\"b\\c\nd\u0001e\u2028", Json.parse(out), "round trip");
        });

        test("the writer keeps non-ASCII as UTF-8 and round-trips it", () -> {
            String s = "\u00e9\u4e2d\uD83D\uDE00";
            eq("\"" + s + "\"", Json.write(s), "unescaped");
            eq(s, Json.parse(Json.write(s)), "round trip");
        });

        test("null, booleans, nesting and empty containers", () -> {
            Map<String, Object> m = Json.parseObject(" {\"a\":null,\"b\":true,\"c\":false,\"d\":[],\"e\":{},\"f\":[[1,[2]]]} ");
            ok(m.containsKey("a") && m.get("a") == null, "explicit null is a present key");
            eq(Boolean.TRUE, m.get("b"), "true");
            eq(Boolean.FALSE, m.get("c"), "false");
            eq(0, ((List<Object>) m.get("d")).size(), "empty array");
            eq(0, ((Map<String, Object>) m.get("e")).size(), "empty object");
            eq("{\"a\":null,\"b\":true,\"c\":false,\"d\":[],\"e\":{},\"f\":[[1,[2]]]}", Json.write(m), "compact rewrite keeps key order");
        });

        test("parsed trees are unmodifiable", () -> {
            Map<String, Object> m = Json.parseObject("{\"a\":[1]}");
            throwsType(UnsupportedOperationException.class, () -> m.put("x", 1), "map");
            throwsType(UnsupportedOperationException.class, () -> ((List<Object>) m.get("a")).add(2), "nested list");
        });

        test("invalid documents are rejected", () -> {
            String[] bad = {"", "{", "[1,]", "{\"a\":1,}", "01", "1.", ".5", "-", "+1", "1e", "tru", "nul",
                "\"unterminated", "\"bad \\x escape\"", "\"\\u12G4\"", "{a:1}", "[1] x", "\"tab\there\"",
                "NaN", "Infinity", "// comment\n1", "{\"a\" 1}", "[1 2]"};
            for (String b : bad) {
                throwsType(Json.JsonException.class, () -> Json.parse(b), "should reject: " + b);
            }
        });

        test("deep nesting is bounded instead of overflowing the stack", () -> {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 100000; i++) {
                sb.append('[');
            }
            throwsType(Json.JsonException.class, () -> Json.parse(sb.toString()), "depth limit");
        });

        test("the writer serialises Java types without floating-point surprises", () -> {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("i", 42);
            m.put("l", 9007199254740993L);
            m.put("bi", new BigInteger("123456789012345678901234567890"));
            m.put("bd", new BigDecimal("1E+3"));
            m.put("list", Arrays.asList("x", null, true));
            m.put("arr", new Object[] {1, "y"});
            eq("{\"i\":42,\"l\":9007199254740993,\"bi\":123456789012345678901234567890,\"bd\":1000,"
                    + "\"list\":[\"x\",null,true],\"arr\":[1,\"y\"]}", Json.write(m), "written");
            throwsType(IllegalArgumentException.class, () -> Json.write(Double.NaN), "NaN refused");
            throwsType(IllegalArgumentException.class, () -> Json.write(new Object()), "unknown type refused");
        });

        test("a leading byte-order mark is tolerated", () -> {
            eq(Boolean.TRUE, Json.parse("\uFEFFtrue"), "BOM skipped");
        });
    }
}
