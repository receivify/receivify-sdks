package io.receivify;

/** 401 {@code bad_signature}: an inbound provider webhook failed HMAC verification. */
public class BadSignatureException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public BadSignatureException(Context context) {
        super(context);
    }
}
