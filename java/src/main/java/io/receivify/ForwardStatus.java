package io.receivify;

/** Forwarding-address status values. An OPEN set: keep a default branch. */
public final class ForwardStatus {
    private ForwardStatus() {
    }

    /** Watching for deposits and forwarding everything credited. */
    public static final String ACTIVE = "active";
    /** The merchant kill switch; no NEW deposit is credited. Reversible. */
    public static final String PAUSED = "paused";
    /** Soft delete; the row is never removed. */
    public static final String DISABLED = "disabled";
    /** A single-session BTCPay deposit window closed. Set by the system only. */
    public static final String EXPIRED = "expired";
}
