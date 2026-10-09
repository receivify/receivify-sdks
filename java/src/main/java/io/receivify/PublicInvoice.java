package io.receivify;

import java.time.Instant;
import java.util.Map;

/** The browser-safe checkout projection from {@code GET /api/public/invoices/{id}}. */
public final class PublicInvoice extends ReceivifyObject {
    public PublicInvoice(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the invoice id */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return the status; see {@link InvoiceStatus} */
    public String getStatus() {
        return stringOr("status", "");
    }

    /** @return the deposit address */
    public String getAddress() {
        return stringOr("address", "");
    }

    /** @return exact-integer base units, as a string */
    public String getAmountExpected() {
        String v = money("amount_expected");
        return v == null ? "0" : v;
    }

    /** @return exact-integer base units, as a string */
    public String getAmountReceived() {
        String v = money("amount_received");
        return v == null ? "0" : v;
    }

    /** @return when the payment window closes, or {@code null} */
    public Instant getExpiresAt() {
        return instant("expires_at");
    }
}
