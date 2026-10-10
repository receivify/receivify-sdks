package com.example.shop;

import io.receivify.CreateInvoiceParams;
import io.receivify.Invoice;
import io.receivify.ReceivifyApiException;
import io.receivify.ReceivifyClient;
import io.receivify.RequestOptions;
import java.net.URI;
import java.util.LinkedHashMap;
import java.util.Map;
import org.springframework.http.HttpStatus;
import org.springframework.http.MediaType;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.util.HtmlUtils;

@RestController
public class ShopController {

    // What we sell. Prices are strings: money never goes through a float or double.
    static final String PRODUCT = "Receivify T-shirt", PRICE = "25.00", CURRENCY = "GBP";

    /** What the customer may pay with: label, asset, chain (null when the asset has one network). */
    static final Map<String, String[]> COINS = new LinkedHashMap<>();
    static {
        COINS.put("btc", new String[] {"Bitcoin", "btc", null});
        COINS.put("ltc", new String[] {"Litecoin", "ltc", null});
        COINS.put("eth", new String[] {"Ethereum", "eth", "ethereum"});
        COINS.put("usdc-base", new String[] {"USDC on Base", "usdc", "base"});
        COINS.put("usdt-tron", new String[] {"USDT on Tron", "usdt", "tron"});
        COINS.put("sol", new String[] {"Solana", "sol", "solana"});
    }

    private final ReceivifyClient receivify;
    private final OrderStore orders;

    ShopController(ReceivifyClient receivify, OrderStore orders) {
        this.receivify = receivify;
        this.orders = orders;
    }

    @GetMapping(value = "/", produces = MediaType.TEXT_HTML_VALUE)
    String shop() {
        StringBuilder options = new StringBuilder();
        COINS.forEach((k, c) -> options.append("<option value=\"").append(k).append("\">").append(c[0]).append("</option>"));
        return Html.page("Shop", "<h1>" + PRODUCT + "</h1><p class=\"price\">£" + PRICE + "</p>"
                + "<form method=\"post\" action=\"/checkout\">"
                + "<label>Pay with <select name=\"coin\">" + options + "</select></label>"
                + "<label><input type=\"checkbox\" name=\"fee_ontop\" value=\"1\"> Customer pays the fee on top</label>"
                + "<button>Pay with crypto</button></form>");
    }

    @PostMapping("/checkout")
    ResponseEntity<String> checkout(@RequestParam(defaultValue = "btc") String coin,
                                    @RequestParam(name = "fee_ontop", required = false) String feeOntop) {
        String[] c = COINS.getOrDefault(coin, COINS.get("btc"));
        OrderStore.Order order = orders.create(PRODUCT, PRICE);

        CreateInvoiceParams params = CreateInvoiceParams.builder()
                .amountFiat(PRICE)          // converted at today's rate and locked on the invoice
                .currency(CURRENCY)
                .asset(c[1])
                .chain(c[2])
                // Comes back on every webhook as data.reference: that's how we find the order.
                .reference(order.id)
                .metadata(Map.of("product", PRODUCT, "source", "spring-boot-example"))
                // true: the customer covers the fee. null (never calling it) = your account default.
                .addFeeOntop(feeOntop != null ? Boolean.TRUE : null)
                .build();
        try {
            // Same idempotency key = same invoice: a double submit or a retry can't create two.
            Invoice invoice = receivify.createInvoice(params, RequestOptions.idempotencyKey("checkout-" + order.id));
            order.invoiceId = invoice.getId();
            // 303: the browser follows with a GET to Receivify's hosted checkout.
            return ResponseEntity.status(HttpStatus.SEE_OTHER).location(URI.create(invoice.getCheckoutUrl())).build();
        } catch (ReceivifyApiException e) {
            // getCode() is stable (amount_too_low, asset_unavailable, ...); getDetail() is for humans.
            String why = e.getDetail() != null ? e.getDetail() : String.valueOf(e.getCode());
            return ResponseEntity.badRequest().contentType(MediaType.TEXT_HTML)
                    .body(Html.page("Could not start payment", "<p>" + HtmlUtils.htmlEscape(why) + "</p><p><a href=\"/\">Back</a></p>"));
        }
    }

    @GetMapping(value = "/orders/{id}", produces = MediaType.TEXT_HTML_VALUE)
    ResponseEntity<String> order(@PathVariable String id) {
        return orders.find(id).map(o -> {
            // Display only. Landing on this URL proves nothing; the webhook is the source of truth.
            String refresh = "paid".equals(o.status) ? ""
                    : "<meta http-equiv=\"refresh\" content=\"5\"><p class=\"muted\">Refreshes every 5 seconds.</p>";
            return ResponseEntity.ok(Html.page("Order " + o.id, "<h1>Order " + HtmlUtils.htmlEscape(o.id) + "</h1>"
                    + "<p>" + o.product + ": £" + o.amount + "</p>"
                    + "<p class=\"status " + o.status + "\">" + o.status + "</p>" + refresh
                    + "<p><a href=\"/\">Back to the shop</a></p>"));
        }).orElse(ResponseEntity.notFound().build());
    }
}
