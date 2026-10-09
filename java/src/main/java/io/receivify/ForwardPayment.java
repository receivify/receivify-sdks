package io.receivify;

import java.time.Instant;
import java.util.Map;

/** One deposit credited to a forwarding address. */
public final class ForwardPayment extends ReceivifyObject {
    public ForwardPayment(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the inbound transaction id */
    public String getTxidIn() {
        return stringOr("txid_in", "");
    }

    /** @return gross amount received, exact-integer base units as a string */
    public String getValue() {
        String v = money("value");
        return v == null ? "0" : v;
    }

    /** @return {@code seen}, {@code confirmed}, {@code forwarding}, {@code sent}, ... (open set) */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return the forwarding transaction id once broadcast, or {@code null} */
    public String getTxidOut() {
        return string("txid_out");
    }

    /** @return confirmations on the inbound transaction */
    public int getConfirmations() {
        return intOr("confirmations", 0);
    }

    /** @return when the payment row was created, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }
}
