package io.receivify;

import java.util.Map;

/** The result of a bulk cancel. Compare {@link #getRequested()} with {@link #getCancelled()}. */
public final class BulkResult extends ReceivifyObject {
    public BulkResult(Map<String, Object> raw) {
        super(raw);
    }

    /** @return always true on a 200 */
    public boolean isOk() {
        return bool("ok");
    }

    /** @return the action performed ({@code cancel}) */
    public String getAction() {
        return string("action");
    }

    /** @return how many ids were submitted */
    public int getRequested() {
        return intOr("requested", 0);
    }

    /** @return how many were actually eligible and cancelled */
    public int getCancelled() {
        return intOr("cancelled", 0);
    }
}
