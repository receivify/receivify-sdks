package io.receivify;

/**
 * Invoice status values. An OPEN set: a new value is an additive API change, so always keep a
 * default branch. That is why these are string constants rather than an {@code enum}.
 */
public final class InvoiceStatus {
    private InvoiceStatus() {
    }

    /** The creation state. */
    public static final String PENDING = "pending";
    /** Less than expected arrived. */
    public static final String PARTIAL = "partial";
    /** Seen on-chain but not yet final. */
    public static final String CONFIRMING = "confirming";
    /** Paid in full. */
    public static final String PAID = "paid";
    /** Funds left the deposit address for the payout wallet. */
    public static final String SWEPT = "swept";
    /** The payment window closed with nothing received. */
    public static final String EXPIRED = "expired";
    /** The window closed on an under-payment. */
    public static final String PARTIAL_EXPIRED = "partial_expired";
    /** Cancelled while still pending and unpaid. */
    public static final String CANCELLED = "cancelled";
    /** Terminal failure. */
    public static final String FAILED = "failed";
}
