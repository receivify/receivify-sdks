package io.receivify;

/** The request never produced an HTTP response: DNS, TLS, a reset connection, or a timeout. */
public final class ReceivifyConnectionException extends ReceivifyException {
    private static final long serialVersionUID = 1L;

    private final boolean timeout;

    public ReceivifyConnectionException(String message, boolean timeout, Throwable cause) {
        super(message, cause);
        this.timeout = timeout;
    }

    /** @return true when the configured timeout fired, rather than a transport error */
    public boolean isTimeout() {
        return timeout;
    }
}
