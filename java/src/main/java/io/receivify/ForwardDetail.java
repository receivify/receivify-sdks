package io.receivify;

import java.util.List;
import java.util.Map;

/**
 * {@code GET /api/v1/address/{id}}: the {@link Forward} fields plus two capped, unpaginated logs.
 */
public final class ForwardDetail extends Forward {
    public ForwardDetail(Map<String, Object> raw) {
        super(raw);
    }

    /** @return credited deposits, newest first, capped at 200; never {@code null} */
    public List<ForwardPayment> getPayments() {
        return list("payments", ForwardPayment::new);
    }

    /** @return true when the 200 cap truncated {@link #getPayments()} */
    public boolean isPaymentsHasMore() {
        return bool("payments_has_more");
    }

    /** @return the webhook delivery log for this forward, newest first, capped at 50 */
    public List<ForwardCallback> getCallbacks() {
        return list("callbacks", ForwardCallback::new);
    }

    /** @return true when the 50 cap truncated {@link #getCallbacks()} */
    public boolean isCallbacksHasMore() {
        return bool("callbacks_has_more");
    }
}
