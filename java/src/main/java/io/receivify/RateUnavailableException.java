package io.receivify;

/** 503 {@code rate_unavailable}: no fresh FX rate is available to price the request. Retryable. */
public class RateUnavailableException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public RateUnavailableException(Context context) {
        super(context);
    }
}
