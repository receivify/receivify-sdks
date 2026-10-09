package io.receivify;

import java.util.Map;

/** The live payout map plus every open staged change ({@code pending} is a bare array). */
public final class PayoutState extends ReceivifyObject {
    public PayoutState(Map<String, Object> raw) {
        super(raw);
    }

    /** @return asset symbol to live payout address; never {@code null} */
    public Map<String, Object> getPayoutAddresses() {
        return objectOrEmpty("payout_addresses");
    }

    /** @return the deployment-wide timelock in hours */
    public int getTimelockHours() {
        return intOr("timelock_hours", 0);
    }

    /** @return whether an emailed confirmation is required */
    public boolean isEmailConfirmation() {
        return bool("email_confirmation");
    }

    /** @return true only when the timelock is zero and email confirmation is off */
    public boolean isInstant() {
        return bool("instant");
    }
}
