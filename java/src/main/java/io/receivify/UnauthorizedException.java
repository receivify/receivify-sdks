package io.receivify;

/** 401 {@code unauthorized}: missing, malformed, revoked or unknown API key. */
public class UnauthorizedException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public UnauthorizedException(Context context) {
        super(context);
    }
}
