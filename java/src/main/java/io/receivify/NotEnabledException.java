package io.receivify;

/** 400 {@code not_enabled}: two-factor authentication is not enabled on this account. */
public class NotEnabledException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public NotEnabledException(Context context) {
        super(context);
    }
}
