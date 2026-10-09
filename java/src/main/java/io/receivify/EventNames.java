package io.receivify;

import java.util.Arrays;
import java.util.Collections;
import java.util.List;

/**
 * The frozen set of webhook event names. Receivers MUST ignore names they do not recognise: adding
 * one is an additive change.
 */
public final class EventNames {
    private EventNames() {
    }

    public static final String INVOICE_CONFIRMING = "invoice.confirming";
    public static final String INVOICE_PARTIAL = "invoice.partial";
    public static final String INVOICE_PAID = "invoice.paid";
    public static final String INVOICE_SWEPT = "invoice.swept";
    public static final String INVOICE_EXPIRED = "invoice.expired";
    public static final String INVOICE_PARTIAL_EXPIRED = "invoice.partial_expired";
    public static final String INVOICE_CANCELLED = "invoice.cancelled";
    public static final String INVOICE_FAILED = "invoice.failed";
    /** Funds physically left a deposit address. Carries the INVOICE payload. */
    public static final String SWEEP_SENT = "sweep.sent";
    /** First sighting of a forwarding deposit, not yet confirmed ({@code pending: true}). */
    public static final String FORWARD_DETECTED = "forward.detected";
    /** A forwarding deposit was credited. */
    public static final String FORWARD_CONFIRMED = "forward.confirmed";
    /** Credited but not forwardable right now; see {@code reason}. */
    public static final String FORWARD_DEFERRED = "forward.deferred";
    /** A forwarding sweep was broadcast. */
    public static final String FORWARD_SENT = "forward.sent";
    /** The credit was undone (reorg or replace-by-fee); see {@code reason}. */
    public static final String FORWARD_REVERSED = "forward.reversed";
    /** Held for manual review; see {@code reason}. */
    public static final String FORWARD_FAILED = "forward.failed";
    /** Emitted only by the signed test send. Carries the INVOICE payload. */
    public static final String TEST = "test";

    /** Every event name, in spec order. */
    public static final List<String> ALL = Collections.unmodifiableList(Arrays.asList(
            INVOICE_CONFIRMING, INVOICE_PARTIAL, INVOICE_PAID, INVOICE_SWEPT, INVOICE_EXPIRED,
            INVOICE_PARTIAL_EXPIRED, INVOICE_CANCELLED, INVOICE_FAILED, SWEEP_SENT,
            FORWARD_DETECTED, FORWARD_CONFIRMED, FORWARD_DEFERRED, FORWARD_SENT, FORWARD_REVERSED,
            FORWARD_FAILED, TEST));

    /**
     * @param event a raw event name, possibly unknown or {@code null}
     * @return true for {@code invoice.*}, {@code sweep.sent} and {@code test}
     */
    public static boolean isInvoiceEvent(String event) {
        return event != null && (event.startsWith("invoice.") || SWEEP_SENT.equals(event) || TEST.equals(event));
    }

    /**
     * @param event a raw event name, possibly unknown or {@code null}
     * @return true for {@code forward.*}
     */
    public static boolean isForwardEvent(String event) {
        return event != null && event.startsWith("forward.");
    }
}
