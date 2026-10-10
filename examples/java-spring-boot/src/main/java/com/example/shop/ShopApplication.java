package com.example.shop;

import io.receivify.ReceivifyClient;
import java.util.Arrays;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;
import org.springframework.context.annotation.Bean;

/**
 * Receivify + Spring Boot: a complete, tiny shop.
 *
 * <pre>
 *   GET  /                      product page with a "Pay with crypto" form      (ShopController)
 *   POST /checkout              creates the order + invoice, redirects to checkout (ShopController)
 *   GET  /orders/{id}           order status page                               (ShopController)
 *   POST /webhooks/receivify    signed events; the ONLY place an order is paid   (WebhookController)
 * </pre>
 */
@SpringBootApplication
public class ShopApplication {

    public static void main(String[] args) {
        SpringApplication.run(ShopApplication.class, args);
    }

    /** One client for the whole app; it is immutable and thread-safe. */
    @Bean
    ReceivifyClient receivifyClient(
            @Value("${receivify.api-url}") String apiUrl,
            @Value("${receivify.api-key}") String apiKey,
            @Value("${receivify.webhook-secret}") String webhookSecret) {
        if (apiKey.isBlank() || webhookSecret.isBlank()) {
            throw new IllegalStateException("Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET (see .env.example).");
        }
        return ReceivifyClient.builder()
                .apiUrl(apiUrl)
                .apiKey(apiKey)
                // A LIST: during a rotation, the new secret first and the old one second
                // ("whsec_new,whsec_old") so no delivery is rejected mid-switch.
                .webhookSecrets(Arrays.stream(webhookSecret.split(",")).map(String::trim).filter(s -> !s.isEmpty()).toList())
                .build();
    }
}
