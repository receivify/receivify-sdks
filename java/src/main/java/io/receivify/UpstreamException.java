package io.receivify;

/** 502 {@code upstream_error}: a node or payment provider we depend on failed. Retryable. */
public class UpstreamException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public UpstreamException(Context context) {
        super(context);
    }
}
