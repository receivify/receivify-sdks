package io.receivify;

/** 400 {@code asset_unavailable}: the asset is enabled but its settlement backend cannot accept it right now. */
public class AssetUnavailableException extends ReceivifyApiException {
    private static final long serialVersionUID = 1L;

    public AssetUnavailableException(Context context) {
        super(context);
    }
}
