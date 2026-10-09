package io.receivify;

import java.time.Instant;
import java.util.Map;

/** A billing schedule that generates a fresh invoice every cycle. */
public final class RecurringSchedule extends ReceivifyObject {
    public RecurringSchedule(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the schedule id */
    public String getId() {
        return stringOr("id", "");
    }

    /** @return display title */
    public String getTitle() {
        return string("title");
    }

    /** @return lower-case asset symbol */
    public String getAsset() {
        return string("asset");
    }

    /** @return a DECIMAL fiat string such as {@code "25.00"}, or {@code null} */
    public String getAmountFiat() {
        return money("amount_fiat");
    }

    /** @return ISO-4217 for the fiat amount, or {@code null} */
    public String getCurrency() {
        return string("currency");
    }

    /** @return a DECIMAL coin amount (NOT base units), or {@code null} */
    public String getAmount() {
        return money("amount");
    }

    /** @return days between cycles */
    public int getIntervalDays() {
        return intOr("interval_days", 0);
    }

    /** @return false once cancelled */
    public boolean isActive() {
        return bool("active");
    }

    /** @return invoices generated so far */
    public int getRuns() {
        return intOr("runs", 0);
    }

    /** @return the next run, or {@code null} */
    public Instant getNextRun() {
        return instant("next_run");
    }

    /** @return the most recent generated invoice id, or {@code null} */
    public String getLastInvoiceId() {
        return string("last_invoice_id");
    }

    /** @return when created, or {@code null} */
    public Instant getCreatedAt() {
        return instant("created_at");
    }
}
