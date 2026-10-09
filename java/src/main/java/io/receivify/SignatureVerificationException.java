package io.receivify;

/**
 * The {@code X-Receivify-Signature} check failed: no secret configured, a missing or malformed
 * header, a stale timestamp, or a signature that matches none of the secrets. Answer such a
 * request with HTTP 400 (or 401) and do not act on it.
 */
public final class SignatureVerificationException extends ReceivifyWebhookException {
    private static final long serialVersionUID = 1L;

    public SignatureVerificationException(String message, WebhookFailureReason reason) {
        super(message, reason, null);
    }
}
