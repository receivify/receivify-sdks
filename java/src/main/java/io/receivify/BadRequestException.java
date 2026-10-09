package io.receivify;

/** 400 {@code bad_request}: malformed request: bad JSON, a missing field, or a value that failed validation. */
public class BadRequestException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public BadRequestException(Context context) {
        super(context);
    }
}
