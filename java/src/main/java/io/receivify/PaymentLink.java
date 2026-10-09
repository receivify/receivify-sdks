package io.receivify;

import java.time.Instant;
import java.util.Map;

/**
 * A shareable link that mints an invoice when opened. You SET the amount as {@code amount_btc} (a
 * decimal BTC string) and READ it as {@link #getAmountSats()} (base units).
 */
public final class PaymentLink extends ReceivifyObject {
    public PaymentLink(Map<String, Object> raw) {
        super(raw);
    }

    /** @return eight base64url characters, not a UUID */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return display title, or {@code null} */
    public String getTitle() {
        return string("title");
    }

    /** @return base units as a string for a fixed link; {@code null} for an open-amount link */
    public String getAmountSats() {
        return money("amount_sats");
    }

    /** @return true when the payer chooses the amount */
    public boolean isOpenAmount() {
        return bool("open_amount");
    }

    /** @return whether the link still mints invoices */
    public boolean isActive() {
        return bool("active");
    }

    /** @return invoices minted from this link */
    public int getUses() {
        return intOr("uses", 0);
    }

    /** @return when it was created, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return when it was last updated, or {@code null} */
    public Instant getUpdatedAt() {
        return instant("updated_at");
    }

    /** @return the shareable URL */
    public String getUrl() {
        return string("url");
    }
}
