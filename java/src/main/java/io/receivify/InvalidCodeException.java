package io.receivify;

/** 400 {@code invalid_code}: the supplied two-factor (TOTP) code is not valid. */
public class InvalidCodeException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public InvalidCodeException(Context context) {
        super(context);
    }
}
