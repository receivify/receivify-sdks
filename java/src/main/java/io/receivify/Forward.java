package io.receivify;

import java.time.Instant;
import java.util.Map;

/**
 * A forwarding address: a standing deposit address that sweeps everything it receives to your
 * destination wallet.
 */
public class Forward extends ReceivifyObject {
    public Forward(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the canonical identifier; read this, not {@link #getForwardId()} */
    public String getId() {
        String id = string("id");
        return id != null ? id : stringOr("forward_id", "");
    }

    /**
     * @return the deprecated alias of {@link #getId()}, carrying the same value
     * @deprecated read {@link #getId()}; {@code forward_id} is kept for the life of v1 only for
     *     backwards compatibility
     */
    @Deprecated
    public String getForwardId() {
        return string("forward_id");
    }

    /** @return the ticker exactly as supplied at create time */
    public String getTicker() {
        return stringOr("ticker", "");
    }

    /** @return the settlement backend (see {@link Providers}), or {@code null} */
    public String getProvider() {
        return string("provider");
    }

    /** @return the chain, or {@code null} for btc and the BTCPay-settled coins */
    public String getChain() {
        return string("chain");
    }

    /** @return the deposit address to hand to payers */
    public String getAddress() {
        return stringOr("address", "");
    }

    /** @return your wallet; immutable once created */
    public String getDestination() {
        return stringOr("destination", "");
    }

    /** @return where {@code forward.*} events go, or {@code null} */
    public String getCallbackUrl() {
        return string("callback_url");
    }

    /** @return confirmations required before a payment is credited */
    public int getConfirmationsRequired() {
        return intOr("confirmations_required", 0);
    }

    /**
     * <b>NOT base units.</b> A display-unit decimal string such as {@code "0.500000"}. Use
     * {@link Money#parse(String, int)} before comparing it with a base-unit amount.
     *
     * @return the minimum forwarded amount in display units, or {@code null}
     */
    public String getMinForward() {
        return money("min_forward");
    }

    /** @return the status; see {@link ForwardStatus}, and treat it as an open set */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return {@code null} for permanent addresses; set only for single-session BTCPay coins */
    public Instant getExpiresAt() {
        return instant("expires_at");
    }

    /** @return when it was created, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return when it was last patched, or {@code null} */
    public Instant getUpdatedAt() {
        return instant("updated_at");
    }
}
