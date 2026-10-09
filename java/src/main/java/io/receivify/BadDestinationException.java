package io.receivify;

/** 400 {@code bad_destination}: the destination address is not valid for that ticker or chain. */
public class BadDestinationException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public BadDestinationException(Context context) {
        super(context);
    }
}
