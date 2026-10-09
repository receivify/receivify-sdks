package io.receivify;

/** 400 {@code no_target}: no registered, active webhook endpoint matched the request. */
public class NoTargetException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public NoTargetException(Context context) {
        super(context);
    }
}
