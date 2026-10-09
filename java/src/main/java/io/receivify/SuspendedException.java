package io.receivify;

/** 400 {@code suspended}: the merchant account is suspended and cannot create new resources. */
public class SuspendedException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public SuspendedException(Context context) {
        super(context);
    }
}
