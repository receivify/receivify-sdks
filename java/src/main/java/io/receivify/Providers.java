package io.receivify;

/** The settlement backends that can derive an address. */
public final class Providers {
    private Providers() {
    }

    /** Bitcoin Core. */
    public static final String BITCOIND = "bitcoind";
    /** BTCPay Server (single-session altcoins). */
    public static final String BTCPAY = "btcpay";
    /** EVM chains. */
    public static final String EVM = "evm";
    /** Solana. */
    public static final String SOLANA = "solana";
    /** Tron. */
    public static final String TRON = "tron";
}
