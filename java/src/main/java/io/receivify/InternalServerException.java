package io.receivify;

/** 500 {@code internal}: an unhandled server error. Safe to retry. */
public class InternalServerException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public InternalServerException(Context context) {
        super(context);
    }
}
