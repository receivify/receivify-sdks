package io.receivify;

import java.util.Map;

/**
 * {@code data} for the {@code forward.*} events. Field names follow the CryptAPI convention:
 * {@link #getAddressIn()} is the deposit address and {@link #getAddressOut()} is your destination.
 */
public final class ForwardEventData extends ReceivifyObject {
    public ForwardEventData(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the forward this event is about */
    public String getForwardId() {
        return stringOr("forward_id", "");
    }

    /** @return the ticker as supplied at create time */
    public String getTicker() {
        return stringOr("ticker", "");
    }

    /** @return the deposit address */
    public String getAddressIn() {
        return stringOr("address_in", "");
    }

    /** @return the destination */
    public String getAddressOut() {
        return stringOr("address_out", "");
    }

    /** @return the inbound transaction id */
    public String getTxidIn() {
        return stringOr("txid_in", "");
    }

    /** @return the outbound transaction id on {@code forward.sent}, otherwise {@code null} */
    public String getTxidOut() {
        return string("txid_out");
    }

    /** @return gross received, exact-integer base units as a string */
    public String getValue() {
        String v = money("value");
        return v == null ? "0" : v;
    }

    /** @return net sent to the destination, exact-integer base units as a string */
    public String getValueForwarded() {
        String v = money("value_forwarded");
        return v == null ? "0" : v;
    }

    /** @return fee retained, exact-integer base units as a string */
    public String getFee() {
        String v = money("fee");
        return v == null ? "0" : v;
    }

    /** @return confirmations on the inbound transaction */
    public int getConfirmations() {
        return intOr("confirmations", 0);
    }

    /** @return true on {@code forward.detected} only (seen, not yet credited) */
    public boolean isPending() {
        return bool("pending");
    }

    /**
     * @return machine-readable cause on {@code forward.deferred}, {@code forward.reversed} and
     *     {@code forward.failed} (e.g. {@code below_minimum}, {@code reorg}); {@code null} otherwise
     */
    public String getReason() {
        return string("reason");
    }

    /** @return a human sentence explaining {@link #getReason()}, or {@code null} */
    public String getDetail() {
        return string("detail");
    }
}
