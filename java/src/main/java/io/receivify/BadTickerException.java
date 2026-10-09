package io.receivify;

/** 400 {@code bad_ticker}: the forwarding ticker is unknown, misspelled, or not offered by this deployment. */
public class BadTickerException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public BadTickerException(Context context) {
        super(context);
    }
}
