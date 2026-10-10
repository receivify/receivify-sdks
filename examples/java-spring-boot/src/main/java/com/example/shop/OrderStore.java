package com.example.shop;

import java.util.Map;
import java.util.Optional;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;
import org.springframework.stereotype.Component;

/** A deliberately tiny in-memory order store. Use your database (JPA etc.) in a real shop. */
@Component
public class OrderStore {

    public static final class Order {
        public final String id;
        public final String product;
        public final String amount;
        public volatile String status = "pending";
        public volatile String invoiceId;
        public long lastSeq; // highest webhook sequence applied, guarded by synchronized(this)

        Order(String id, String product, String amount) {
            this.id = id;
            this.product = product;
            this.amount = amount;
        }
    }

    private final Map<String, Order> orders = new ConcurrentHashMap<>();

    public Order create(String product, String amount) {
        // Prefixed so it can't collide with another shop on the same Receivify account:
        // `reference` is also a de-duplication key on Receivify's side.
        Order o = new Order("java-" + UUID.randomUUID().toString().substring(0, 8), product, amount);
        orders.put(o.id, o);
        return o;
    }

    public Optional<Order> find(String id) {
        return Optional.ofNullable(id == null ? null : orders.get(id));
    }

    /**
     * Applies a webhook event if it is newer than the last one applied to this order.
     * Delivery is at-least-once and can be out of order, but `sequence` only increases,
     * so this check alone makes duplicates and stale retries harmless. Paid is final.
     *
     * @return true if the status changed
     */
    public boolean apply(String id, long sequence, String status, String invoiceId) {
        Order o = orders.get(id);
        if (o == null) return false;
        synchronized (o) {
            if (sequence <= o.lastSeq) return false;
            o.lastSeq = sequence;
            if ("paid".equals(o.status)) return false;
            o.status = status;
            o.invoiceId = invoiceId;
            return true;
        }
    }
}
