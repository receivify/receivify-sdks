package io.receivify;

/** 409 {@code not_allowed}: the resource exists but its state forbids this action. */
public class NotAllowedException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public NotAllowedException(Context context) {
        super(context);
    }
}
