package io.receivify;

/** 400 {@code no_setup}: two-factor enrolment has not been started yet. */
public class NoSetupException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public NoSetupException(Context context) {
        super(context);
    }
}
