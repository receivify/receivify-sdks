package io.receivify;

/** The outcome of a signature check, with a reason worth logging. */
public final class VerificationResult {
    private final boolean ok;
    private final WebhookFailureReason reason;
    private final String message;
    private final Long timestamp;

    VerificationResult(boolean ok, WebhookFailureReason reason, String message, Long timestamp) {
        this.ok = ok;
        this.reason = reason;
        this.message = message;
        this.timestamp = timestamp;
    }

    /** @return true when the delivery is authentic */
    public boolean isOk() {
        return ok;
    }

    /** @return why it failed; {@link WebhookFailureReason#NONE} on success */
    public WebhookFailureReason getReason() {
        return reason;
    }

    /** @return a human sentence for your logs (never contains a secret) */
    public String getMessage() {
        return message;
    }

    /** @return the {@code t=} value once the header parsed, or {@code null} */
    public Long getTimestamp() {
        return timestamp;
    }

    @Override
    public String toString() {
        return "VerificationResult{ok=" + ok + ", reason=" + reason + ", message=" + message + "}";
    }
}
