package io.receivify;

import java.math.BigDecimal;
import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The body of {@code POST /api/v1/invoices}.
 *
 * <p>Give exactly one amount: {@link Builder#amountFiat(String)} (+ {@link Builder#currency(String)}),
 * {@link Builder#amount(String)} (a decimal in the coin), or {@link Builder#amountSats(String)}
 * (exact base units). All amounts are strings so nothing is ever rounded.
 *
 * <pre>{@code
 * CreateInvoiceParams.builder()
 *     .amountFiat("25.00").currency("USD")
 *     .asset("btc")
 *     .reference("order-1")
 *     .addFeeOntop(true)
 *     .build();
 * }</pre>
 */
public final class CreateInvoiceParams {
    private final Map<String, Object> body;

    private CreateInvoiceParams(Map<String, Object> body) {
        this.body = Collections.unmodifiableMap(body);
    }

    /** @return a builder */
    public static Builder builder() {
        return new Builder();
    }

    /** @return the JSON body that will be sent (only the fields you set) */
    public Map<String, Object> toMap() {
        return body;
    }

    /** Builder for {@link CreateInvoiceParams}. */
    public static final class Builder {
        private final Map<String, Object> body = new LinkedHashMap<>();

        private Builder() {
        }

        private Builder put(String key, Object value) {
            if (value == null) {
                body.remove(key);
            } else {
                body.put(key, value);
            }
            return this;
        }

        /**
         * @param asset lower-case symbol such as {@code btc}, {@code eth}, {@code usdc}; defaults to btc
         * @return this builder
         */
        public Builder asset(String asset) {
            return put("asset", asset);
        }

        /**
         * @param chain the EVM chain for EVM assets, e.g. {@code ethereum}, {@code base}; defaults
         *     to ethereum
         * @return this builder
         */
        public Builder chain(String chain) {
            return put("chain", chain);
        }

        /**
         * @param amountFiat a fiat price as a decimal string such as {@code "25.00"}, converted at
         *     the current rate and locked onto the invoice
         * @return this builder
         */
        public Builder amountFiat(String amountFiat) {
            return put("amount_fiat", amountFiat);
        }

        /**
         * @param amountFiat a fiat price; sent as its exact plain string
         * @return this builder
         */
        public Builder amountFiat(BigDecimal amountFiat) {
            return put("amount_fiat", amountFiat == null ? null : amountFiat.toPlainString());
        }

        /**
         * @param currency ISO-4217 code for {@link #amountFiat(String)}; defaults to USD
         * @return this builder
         */
        public Builder currency(String currency) {
            return put("currency", currency);
        }

        /**
         * @param amount a decimal amount in the coin's own units, e.g. {@code "0.001"}
         * @return this builder
         */
        public Builder amount(String amount) {
            return put("amount", amount);
        }

        /**
         * @param amount a decimal amount in the coin's own units; sent as its exact plain string
         * @return this builder
         */
        public Builder amount(BigDecimal amount) {
            return put("amount", amount == null ? null : amount.toPlainString());
        }

        /**
         * @param amountSats exact base units as an integer string (any asset, despite the name)
         * @return this builder
         */
        public Builder amountSats(String amountSats) {
            return put("amount_sats", amountSats);
        }

        /**
         * @param amountBtc legacy decimal BTC amount; only honoured when the asset is btc
         * @return this builder
         */
        public Builder amountBtc(String amountBtc) {
            return put("amount_btc", amountBtc);
        }

        /**
         * @param reference your business key; reusing it returns the EXISTING invoice
         * @return this builder
         */
        public Builder reference(String reference) {
            return put("reference", reference);
        }

        /**
         * @param callbackUrl a per-invoice webhook target (https in production)
         * @return this builder
         */
        public Builder callbackUrl(String callbackUrl) {
            return put("callback_url", callbackUrl);
        }

        /**
         * @param metadata arbitrary JSON-serialisable data echoed back on the invoice
         * @return this builder
         */
        public Builder metadata(Map<String, ?> metadata) {
            return put("metadata", metadata == null ? null : new LinkedHashMap<String, Object>(metadata));
        }

        /**
         * Add the commission ON TOP of the amount so you receive the requested amount in full
         * ({@code true}), or force fee-out-of-amount for this invoice ({@code false}). Pass
         * {@code null} (or never call this) to use the account default: omitting the field is NOT
         * the same as {@code false}.
         *
         * @param addFeeOntop true, false, or null for the account default
         * @return this builder
         */
        public Builder addFeeOntop(Boolean addFeeOntop) {
            return put("add_fee_ontop", addFeeOntop);
        }

        /** @return the params */
        public CreateInvoiceParams build() {
            return new CreateInvoiceParams(new LinkedHashMap<>(body));
        }
    }
}
