package io.receivify;

/** 400 {@code limit_exceeded}: a per-account quota for this resource is already at its maximum. */
public class LimitExceededException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public LimitExceededException(Context context) {
        super(context);
    }
}
