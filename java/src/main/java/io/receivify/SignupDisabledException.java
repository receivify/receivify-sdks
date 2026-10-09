package io.receivify;

/** 403 {@code signup_disabled}: self-serve signup is turned off on this deployment. */
public class SignupDisabledException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public SignupDisabledException(Context context) {
        super(context);
    }
}
