package io.receivify;

import java.util.Map;

/** The outcome of a signed test send. Answers 200 even when every delivery failed. */
public final class WebhookTestResult extends ReceivifyObject {
    public WebhookTestResult(Map<String, Object> raw) {
        super(raw);
    }

    /** @return always {@code test} */
    public String getEvent() {
        return stringOr("event", "");
    }

    /** @return targets that answered 2xx */
    public int getDelivered() {
        return intOr("delivered", 0);
    }

    /** @return targets attempted */
    public int getTotal() {
        return intOr("total", 0);
    }
}
