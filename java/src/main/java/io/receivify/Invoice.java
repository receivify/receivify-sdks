package io.receivify;

import java.time.Instant;
import java.util.Map;

/**
 * An invoice, exactly as create, retrieve, list and cancel all return it.
 *
 * <p><b>Money is a string.</b> {@link #getAmountExpected()} and friends are exact-integer base units
 * (satoshis, wei, ...). Use {@link Money} for arithmetic or display; never parse them into a
 * {@code double}.
 */
public final class Invoice extends ReceivifyObject {
    public Invoice(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the invoice id (a UUID) */
    public String getId() {
        return stringOr("id", "");
    }

    /**
     * Your own business key. It is ALSO a de-duplication key: creating an invoice with a reference
     * you have already used returns the EXISTING invoice.
     *
     * @return the reference, or {@code null}
     */
    public String getReference() {
        return string("reference");
    }

    /** @return lower-case asset symbol, e.g. {@code btc}, {@code usdc} */
    public String getAsset() {
        return string("asset");
    }

    /** @return exact-integer base units, as a string — what the payer must send */
    public String getAmountExpected() {
        return stringOrMoney("amount_expected");
    }

    /** @return exact-integer base units, as a string; {@code "0"} until something arrives */
    public String getAmountReceived() {
        return stringOrMoney("amount_received");
    }

    /**
     * A DECIMAL display price such as {@code "25.00"} captured at create time — not base units.
     *
     * @return the fiat price, or {@code null} when the invoice was not priced in fiat
     */
    public String getAmountFiat() {
        return money("amount_fiat");
    }

    /** @return the ISO-4217 code of {@link #getAmountFiat()}, or {@code null} */
    public String getFiatCurrency() {
        return string("fiat_currency");
    }

    /** @return the EVM chain, {@code solana}/{@code tron}, or {@code null} for BTC and BTCPay coins */
    public String getChain() {
        return string("chain");
    }

    /** @return the settlement backend (see {@link Providers}), or {@code null} */
    public String getProvider() {
        return string("provider");
    }

    /** @return the deposit address — send exactly this asset on exactly this chain */
    public String getAddress() {
        return stringOr("address", "");
    }

    /** @return the status; see {@link InvoiceStatus}, and treat it as an open set */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return when the payment window closes, or {@code null} */
    public Instant getExpiresAt() {
        return instant("expires_at");
    }

    /** @return when it was paid, or {@code null} */
    public Instant getPaidAt() {
        return instant("paid_at");
    }

    /** @return when it was created, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }

    /** @return whatever metadata you stored; never {@code null} */
    public Map<String, Object> getMetadata() {
        return objectOrEmpty("metadata");
    }

    /** @return the hosted checkout page — redirect the payer here */
    public String getCheckoutUrl() {
        return string("checkout_url");
    }

    /**
     * Decimal places of this invoice's asset — the divisor for {@link #getAmountExpected()}.
     * Per-token on EVM (USDC 6, DAI 18).
     *
     * @return the decimals, or {@code null} on a deployment that predates the field
     */
    public Integer getDecimals() {
        return integer("decimals");
    }

    /**
     * {@link #getAmountExpected()} already divided by {@code 10^decimals}, as a decimal string.
     * Display only.
     *
     * @return the display amount, or {@code null}
     */
    public String getAmountDecimal() {
        return money("amount_decimal");
    }

    /**
     * A wallet-scannable URI for a QR code.
     *
     * @return the URI, or {@code null} when the chain has no safe one (show the bare address)
     */
    public String getPaymentUri() {
        return string("payment_uri");
    }

    /** @return whether the commission was added on top of the requested amount */
    public boolean isAddFeeOntop() {
        return bool("add_fee_ontop");
    }

    /**
     * Exact-integer base units: what you asked to RECEIVE. Equal to {@link #getAmountExpected()}
     * when {@link #isAddFeeOntop()} is false.
     *
     * @return the net requested amount, or {@code null} on an older deployment
     */
    public String getAmountNetRequested() {
        return money("amount_net_requested");
    }

    /**
     * Exact-integer base units: what the gross-up added. {@code "0"} when the flag is off.
     *
     * @return the fee added on top, or {@code null} on an older deployment
     */
    public String getFeeOntop() {
        return money("fee_ontop");
    }

    private String stringOrMoney(String key) {
        String v = money(key);
        return v == null ? "0" : v;
    }
}
