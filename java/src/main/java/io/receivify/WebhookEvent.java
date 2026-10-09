package io.receivify;

import java.time.Instant;
import java.util.Map;

/**
 * The webhook envelope: {@code {event, sequence, created_at, data}}.
 *
 * <p>Switch on {@link #getEvent()} first; it decides the shape of {@link #getData()}. Delivery is
 * at-least-once and may arrive out of order, so de-duplicate on {@code (event, sequence)}.
 */
public final class WebhookEvent extends ReceivifyObject {
    public WebhookEvent(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the event name, e.g. {@code invoice.paid}; see {@link EventNames} (open set) */
    public String getEvent() {
        return stringOr("event", "");
    }

    /** @return the global monotonic sequence number; 0 when absent */
    public long getSequence() {
        Long v = longOrNull("sequence");
        return v == null ? 0L : v;
    }

    /** @return when the EVENT happened (frozen across retries and replays), or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return the {@code data} object as raw JSON; never {@code null} */
    public Map<String, Object> getData() {
        return objectOrEmpty("data");
    }

    /** @return true for {@code invoice.*}, {@code sweep.sent} and {@code test} */
    public boolean isInvoiceEvent() {
        return EventNames.isInvoiceEvent(getEvent());
    }

    /** @return true for {@code forward.*} */
    public boolean isForwardEvent() {
        return EventNames.isForwardEvent(getEvent());
    }

    /** @return the typed invoice payload, or {@code null} when this is not an invoice-shaped event */
    public InvoiceEventData getInvoiceData() {
        return isInvoiceEvent() && object("data") != null ? new InvoiceEventData(object("data")) : null;
    }

    /** @return the typed forward payload, or {@code null} when this is not a forward event */
    public ForwardEventData getForwardData() {
        return isForwardEvent() && object("data") != null ? new ForwardEventData(object("data")) : null;
    }
}
