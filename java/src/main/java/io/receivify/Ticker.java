package io.receivify;

import java.util.Map;

/** One {@code (asset, chain)} pair this deployment offers for forwarding. */
public final class Ticker extends ReceivifyObject {
    public Ticker(Map<String, Object> raw) {
        super(raw);
    }

    /**
     * @return the string to pass as {@code ticker} when creating a forward, e.g. {@code btc} or
     *     {@code usdc_base}
     */
    public String getTicker() {
        return stringOr("ticker", "");
    }

    /** @return the settlement backend (see {@link Providers}) */
    public String getProvider() {
        return string("provider");
    }

    /** @return the chain, or {@code null} where the concept does not apply */
    public String getChain() {
        return string("chain");
    }

    /** @return display name */
    public String getName() {
        return string("name");
    }

    /** @return base units per whole coin, as a power of ten; give this to {@link Money} */
    public int getDecimals() {
        return intOr("decimals", 0);
    }
}
