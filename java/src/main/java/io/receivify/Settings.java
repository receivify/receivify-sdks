package io.receivify;

import java.util.Map;

/** Merchant preferences. */
public final class Settings extends ReceivifyObject {
    public Settings(Map<String, Object> raw) {
        super(raw);
    }

    /** @return ISO-4217 display currency */
    public String getDisplayCurrency() {
        return string("display_currency");
    }

    /** @return the account email */
    public String getEmail() {
        return string("email");
    }

    /** @return the default fee for the account in basis points (a rate, not money) */
    public int getFeeBps() {
        return intOr("fee_bps", 0);
    }

    /** @return the ACCOUNT default for {@code add_fee_ontop} on new invoices */
    public boolean isAddFeeOntop() {
        return bool("add_fee_ontop");
    }
}
