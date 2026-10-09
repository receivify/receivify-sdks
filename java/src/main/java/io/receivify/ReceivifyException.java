package io.receivify;

/**
 * Base class of every exception this SDK throws. Unchecked, so it composes with lambdas, streams
 * and Kotlin.
 */
public class ReceivifyException extends RuntimeException {
    private static final long serialVersionUID = 1L;

    public ReceivifyException(String message) {
        super(message);
    }

    public ReceivifyException(String message, Throwable cause) {
        super(message, cause);
    }
}
