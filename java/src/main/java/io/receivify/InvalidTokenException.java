package io.receivify;

/** 400 {@code invalid_token}: a single-use confirmation token is invalid, expired, or already spent. */
public class InvalidTokenException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public InvalidTokenException(Context context) {
        super(context);
    }
}
