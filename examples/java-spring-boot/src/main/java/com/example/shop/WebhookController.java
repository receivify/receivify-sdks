package com.example.shop;

import io.receivify.InvoiceEventData;
import io.receivify.ReceivifyClient;
import io.receivify.ReceivifyWebhookException;
import io.receivify.WebhookEvent;
import java.util.Map;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.RequestHeader;
import org.springframework.web.bind.annotation.RestController;

/** POST /webhooks/receivify: the ONLY place an order is marked paid. */
@RestController
public class WebhookController {

    private static final Logger log = LoggerFactory.getLogger(WebhookController.class);

    /**
     * invoice.paid is THE signal: the full amount is confirmed and can't be reversed.
     * invoice.swept follows it (funds forwarded to your wallet), so it also means paid.
     */
    private static final Map<String, String> STATUS_FOR_EVENT = Map.of(
            "invoice.confirming", "confirming",
            "invoice.partial", "partially_paid",
            "invoice.paid", "paid",
            "invoice.swept", "paid",
            "invoice.expired", "expired",
            "invoice.partial_expired", "underpaid",
            "invoice.cancelled", "cancelled",
            "invoice.failed", "failed");

    private final ReceivifyClient receivify;
    private final OrderStore orders;

    WebhookController(ReceivifyClient receivify, OrderStore orders) {
        this.receivify = receivify;
        this.orders = orders;
    }

    /**
     * `@RequestBody byte[]` keeps the body EXACTLY as sent. The signature is an HMAC over these
     * bytes; binding to a POJO and re-serialising with Jackson would change them and fail.
     */
    @PostMapping("/webhooks/receivify")
    ResponseEntity<String> receive(@RequestBody byte[] rawBody,
                                   @RequestHeader(name = "X-Receivify-Signature", required = false) String signature) {
        WebhookEvent event;
        try {
            // Constant-time HMAC check + 5-minute replay window + JSON parse.
            event = receivify.parseWebhook(rawBody, signature);
        } catch (ReceivifyWebhookException e) {
            log.warn("Rejected webhook: {}", e.getMessage());
            return ResponseEntity.badRequest().body("bad signature");
        }

        if ("test".equals(event.getEvent())) {
            log.info("Test webhook received and verified.");
            return ResponseEntity.ok("ok");
        }
        // Ignore anything that isn't about an invoice, including event names added later.
        if (!event.getEvent().startsWith("invoice.")) return ResponseEntity.ok("ok");

        InvoiceEventData data = event.getInvoiceData();
        String next = STATUS_FOR_EVENT.get(event.getEvent());
        if (data != null && next != null && orders.apply(data.getReference(), event.getSequence(), next, data.getId())) {
            log.info("Order {} -> {} ({})", data.getReference(), next, event.getEvent());
            if ("paid".equals(next)) {
                // Fulfil here (email, download, shipping). Keep it fast or hand it to a queue.
            }
        }
        // 2xx fast, including for unknown orders (made elsewhere) and duplicates:
        // anything else is retried with backoff.
        return ResponseEntity.ok("ok");
    }
}
