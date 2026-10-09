package io.receivify;

/** 409 {@code already_exists}: a resource with that unique identity already exists. */
public class AlreadyExistsException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public AlreadyExistsException(Context context) {
        super(context);
    }
}
