package io.receivify;

/** 400 {@code amount_too_low}: below the economic floor. */
public class AmountTooLowException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public AmountTooLowException(Context context) {
        super(context);
    }
}
