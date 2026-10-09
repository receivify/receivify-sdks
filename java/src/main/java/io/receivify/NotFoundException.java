package io.receivify;

/** 404 {@code not_found}: no such resource, or it does not belong to the caller. */
public class NotFoundException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public NotFoundException(Context context) {
        super(context);
    }
}
