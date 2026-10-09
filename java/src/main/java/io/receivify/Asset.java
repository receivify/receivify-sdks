package io.receivify;

import java.util.Map;

/** An invoiceable coin. Display metadata, not a machine contract. */
public final class Asset extends ReceivifyObject {
    public Asset(Map<String, Object> raw) {
        super(raw);
    }

    /** @return lower-case symbol */
    public String getSymbol() {
        return stringOr("symbol", "");
    }

    /** @return display name */
    public String getName() {
        return string("name");
    }

    /** @return a human sentence naming the networks; not machine-readable */
    public String getNetwork() {
        return string("network");
    }

    /** @return brand hex colour */
    public String getColor() {
        return string("color");
    }

    /** @return a display-unit decimal string OR the literal word {@code "network"} */
    public String getMin() {
        return string("min");
    }

    /** @return English prose such as {@code "dynamic gas"}; display only */
    public String getNetworkFee() {
        return string("network_fee");
    }

    /** @return base units per whole coin, or {@code null} when not sent */
    public Integer getDecimals() {
        return integer("decimals");
    }

    /** @return whether the coin is fully wired end to end */
    public boolean isLive() {
        return bool("live");
    }
}
