package io.receivify;

/** 409 {@code already_enabled}: the feature being enabled is already on. */
public class AlreadyEnabledException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public AlreadyEnabledException(Context context) {
        super(context);
    }
}
