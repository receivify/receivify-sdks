package io.receivify;

import java.util.Map;

/** The unauthenticated view of a payment link. */
public final class PublicPaymentLink extends ReceivifyObject {
    public PublicPaymentLink(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the link id */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return display title, or {@code null} */
    public String getTitle() {
        return string("title");
    }

    /** @return true when the payer chooses the amount */
    public boolean isOpenAmount() {
        return bool("open_amount");
    }

    /** @return a DECIMAL BTC string (not base units), or {@code null} for an open-amount link */
    public String getAmountBtc() {
        return money("amount_btc");
    }
}
