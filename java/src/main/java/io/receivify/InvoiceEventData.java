package io.receivify;

import java.time.Instant;
import java.util.Map;

/**
 * {@code data} for {@code invoice.*}, {@code sweep.sent} and {@code test}. A deliberately narrow
 * projection with no asset or chain: call {@code getInvoice(id)} for the authoritative state.
 */
public final class InvoiceEventData extends ReceivifyObject {
    public InvoiceEventData(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the invoice id */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return your business key, or {@code null} */
    public String getReference() {
        return string("reference");
    }

    /** @return the status at the moment of the event */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return exact-integer base units, as a string */
    public String getAmountExpected() {
        String v = money("amount_expected");
        return v == null ? "0" : v;
    }

    /** @return exact-integer base units, as a string */
    public String getAmountReceived() {
        String v = money("amount_received");
        return v == null ? "0" : v;
    }

    /** @return the deposit address */
    public String getAddress() {
        return stringOr("address", "");
    }

    /** @return when it was paid, or {@code null} */
    public Instant getPaidAt() {
        return instant("paid_at");
    }
}
