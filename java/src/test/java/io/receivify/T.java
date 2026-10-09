package io.receivify;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

/** A tiny assertion harness, so the suite needs no test framework at all. */
final class T {
    private static int passed;
    private static int failed;
    private static final List<String> FAILURES = new ArrayList<>();

    private T() {
    }

    interface Body {
        void run() throws Exception;
    }

    static void section(String title) {
        System.out.println();
        System.out.println(title);
    }

    static void test(String name, Body body) {
        try {
            body.run();
            passed++;
            System.out.println("  pass  " + name);
        } catch (Throwable e) {
            failed++;
            FAILURES.add(name + ": " + e);
            System.out.println("  FAIL  " + name);
            System.out.println("        " + e);
        }
    }

    static int summarise() {
        System.out.println();
        System.out.println((passed + failed) + " tests, " + passed + " passed, " + failed + " failed");
        for (String f : FAILURES) {
            System.out.println("  - " + f);
        }
        return failed == 0 ? 0 : 1;
    }

    static void ok(boolean condition, String message) {
        if (!condition) {
            throw new AssertionError(message);
        }
    }

    static void eq(Object expected, Object actual, String message) {
        if (!Objects.equals(expected, actual)) {
            throw new AssertionError(message + " (expected <" + expected + "> but was <" + actual + ">)");
        }
    }

    static <E extends Throwable> E throwsType(Class<E> type, Body body, String message) {
        try {
            body.run();
        } catch (Throwable e) {
            if (type.isInstance(e)) {
                return type.cast(e);
            }
            throw new AssertionError(message + " (expected " + type.getSimpleName() + " but got " + e + ")");
        }
        throw new AssertionError(message + " (expected " + type.getSimpleName() + " but nothing was thrown)");
    }
}
