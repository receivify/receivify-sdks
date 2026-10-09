package io.receivify;

/**
 * A webhook could not be authenticated or parsed. Thrown only by {@code Webhooks.parse}; the
 * {@code verify} methods return a boolean instead. Signature failures arrive as the
 * {@link SignatureVerificationException} subclass.
 */
public class ReceivifyWebhookException extends ReceivifyException {
    private static final long serialVersionUID = 1L;

    private final WebhookFailureReason reason;

    public ReceivifyWebhookException(String message, WebhookFailureReason reason, Throwable cause) {
        super(message, cause);
        this.reason = reason;
    }

    /** @return the machine-readable cause; branch on this, not on the message */
    public WebhookFailureReason getReason() {
        return reason;
    }
}
