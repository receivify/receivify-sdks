package io.receivify;

import static io.receivify.FakeTransport.headers;
import static io.receivify.T.eq;
import static io.receivify.T.ok;
import static io.receivify.T.test;
import static io.receivify.T.throwsType;

import java.net.ConnectException;
import java.net.http.HttpTimeoutException;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.Map;

final class ClientTests {
    private ClientTests() {
    }

    static final String INVOICE = "{\"id\":\"8f14e45f-ceea-4e1a-9b3c-1d2e3f4a5b6c\",\"reference\":\"order-1\",\"asset\":\"usdc\","
            + "\"amount_expected\":\"25062656\",\"amount_received\":\"0\",\"amount_fiat\":null,\"fiat_currency\":null,"
            + "\"chain\":\"base\",\"provider\":\"evm\",\"address\":\"0xabc\",\"status\":\"pending\","
            + "\"expires_at\":\"2026-10-10T13:00:00.000Z\",\"paid_at\":null,\"created_at\":\"2026-10-10T12:00:00.000Z\","
            + "\"metadata\":{\"cart\":7},\"checkout_url\":\"https://api.test/pay/8f14\",\"decimals\":6,"
            + "\"amount_decimal\":\"25.062656\",\"payment_uri\":null,\"add_fee_ontop\":true,"
            + "\"amount_net_requested\":\"25000000\",\"fee_ontop\":\"62656\",\"future_field\":\"kept\"}";

    static String page(String items, boolean hasMore, String cursor) {
        return "{\"data\":[" + items + "],\"has_more\":" + hasMore + ",\"next_cursor\":"
                + (cursor == null ? "null" : "\"" + cursor + "\"") + "}";
    }

    static String inv(String id) {
        return "{\"id\":\"" + id + "\",\"amount_expected\":\"1\",\"amount_received\":\"0\",\"status\":\"pending\",\"address\":\"a\"}";
    }

    static void run() {
        T.section("HTTP, auth and headers");

        test("the API key travels in the Authorization header and never in the URL", () -> {
            FakeTransport f = new FakeTransport().reply(200, INVOICE);
            f.client().getInvoice("8f14e45f-ceea-4e1a-9b3c-1d2e3f4a5b6c");
            Transport.Request r = f.last();
            eq("Bearer rcv_live_secretkey123", r.getHeader("Authorization"), "bearer");
            ok(!r.getUrl().contains("rcv_live"), "not in URL");
            eq("https://api.test/api/v1/invoices/8f14e45f-ceea-4e1a-9b3c-1d2e3f4a5b6c", r.getUrl(), "trailing slash trimmed");
            eq("GET", r.getMethod(), "method");
            ok(!r.toString().contains("rcv_live"), "request toString hides the key");
        });

        test("User-Agent is receivify-java/<version> and the default timeout is 30s", () -> {
            FakeTransport f = new FakeTransport().reply(200, page("", false, null));
            f.client().listTickers();
            eq("receivify-java/1.0.0", f.last().getHeader("User-Agent"), "UA");
            eq(Duration.ofSeconds(30), f.last().getTimeout(), "timeout");
            eq("application/json", f.last().getHeader("Accept"), "accept");
        });

        test("public catalogue routes send no Authorization header", () -> {
            FakeTransport f = new FakeTransport().reply(200, page("{\"ticker\":\"btc\",\"provider\":\"bitcoind\",\"chain\":null,\"name\":\"Bitcoin\",\"decimals\":8}", false, null));
            Page<Ticker> p = f.client().listTickers();
            eq(null, f.last().getHeader("Authorization"), "no auth");
            eq(8, p.getData().get(0).getDecimals(), "decimals");
            eq(null, p.getData().get(0).getChain(), "null chain");
        });

        test("client toString and exceptions never expose the key", () -> {
            FakeTransport f = new FakeTransport().reply(401, "{\"error\":\"unauthorized\",\"detail\":\"Invalid API key\"}");
            ReceivifyClient c = f.client();
            ok(!c.toString().contains("secretkey"), "toString");
            UnauthorizedException e = throwsType(UnauthorizedException.class, () -> c.getSettings(), "401");
            ok(!e.getMessage().contains("secretkey"), "message");
            ok(!e.getHeaders().toString().contains("secretkey"), "headers");
        });

        test("Idempotency-Key and custom headers are sent; per-call timeout overrides", () -> {
            FakeTransport f = new FakeTransport().reply(201, INVOICE);
            f.client().createInvoice(CreateInvoiceParams.builder().amount("25").asset("usdc").chain("base").build(),
                    RequestOptions.builder().idempotencyKey("order-1").header("X-Trace", "t1").timeout(Duration.ofSeconds(5)).build());
            eq("order-1", f.last().getHeader("Idempotency-Key"), "idempotency key");
            eq("t1", f.last().getHeader("X-Trace"), "custom header");
            eq(Duration.ofSeconds(5), f.last().getTimeout(), "timeout override");
            eq("application/json", f.last().getHeader("Content-Type"), "content type");
            throwsType(IllegalArgumentException.class, () -> RequestOptions.builder().header("Authorization", "x"), "cannot override auth");
        });

        test("query parameters and path segments are encoded", () -> {
            FakeTransport f = new FakeTransport().reply(200, page("", false, null)).reply(200, INVOICE);
            ReceivifyClient c = f.client();
            c.listInvoices(ListInvoicesParams.builder().limit(2).status("paid").cursor("a+b/c=").build());
            eq("https://api.test/api/v1/invoices?limit=2&status=paid&cursor=a%2Bb%2Fc%3D", f.last().getUrl(), "query");
            c.getInvoice("../x y");
            eq("https://api.test/api/v1/invoices/..%2Fx%20y", f.last().getUrl(), "segment encoded");
        });

        T.section("Money stays a string; new invoice fields");

        test("the invoice decodes, including decimals, amount_decimal, payment_uri and fee fields", () -> {
            Invoice i = new FakeTransport().reply(200, INVOICE).client().getInvoice("x");
            eq("25062656", i.getAmountExpected(), "amount_expected");
            eq(Integer.valueOf(6), i.getDecimals(), "decimals");
            eq("25.062656", i.getAmountDecimal(), "amount_decimal");
            eq(null, i.getPaymentUri(), "payment_uri null");
            ok(i.isAddFeeOntop(), "add_fee_ontop");
            eq("25000000", i.getAmountNetRequested(), "amount_net_requested");
            eq("62656", i.getFeeOntop(), "fee_ontop");
            eq(Money.add(i.getAmountNetRequested(), i.getFeeOntop()), i.getAmountExpected(), "net + fee = expected");
            eq(null, i.getAmountFiat(), "amount_fiat null");
            eq(Instant.parse("2026-10-10T13:00:00Z"), i.getExpiresAt(), "expires_at");
            eq(null, i.getPaidAt(), "paid_at null");
            eq("kept", i.get("future_field"), "unknown field reachable via get");
            eq("7", String.valueOf(i.getMetadata().get("cart")), "metadata");
        });

        test("an older invoice without the new fields reads safely", () -> {
            Invoice i = new FakeTransport().reply(200, inv("old")).client().getInvoice("old");
            eq(null, i.getDecimals(), "no decimals");
            ok(!i.isAddFeeOntop(), "add_fee_ontop defaults false");
            eq(null, i.getFeeOntop(), "fee_ontop absent");
            eq(0, i.getMetadata().size(), "metadata never null");
        });

        test("a 78-digit wei amount round-trips exactly", () -> {
            String wei = "115792089237316195423570985008687907853269984665640564039457584007913129639935";
            Invoice i = new FakeTransport().reply(200, "{\"id\":\"x\",\"amount_expected\":\"" + wei + "\"}").client().getInvoice("x");
            eq(wei, i.getAmountExpected(), "exact");
            eq(new java.math.BigInteger(wei), Money.toBigInteger(i.getAmountExpected()), "BigInteger");
        });

        test("a money field arriving as a JSON number keeps its exact digits", () -> {
            Invoice i = new FakeTransport().reply(200, "{\"id\":\"x\",\"amount_expected\":12345678901234567890123,\"amount_fiat\":25.10}")
                    .client().getInvoice("x");
            eq("12345678901234567890123", i.getAmountExpected(), "no double");
            eq("25.10", i.getAmountFiat(), "trailing zero kept");
        });

        test("add_fee_ontop is OMITTED when not set (account default)", () -> {
            FakeTransport f = new FakeTransport().reply(201, INVOICE);
            f.client().createInvoice(CreateInvoiceParams.builder().amountFiat("25.00").currency("USD").asset("btc").reference("o1").build());
            eq("{\"amount_fiat\":\"25.00\",\"currency\":\"USD\",\"asset\":\"btc\",\"reference\":\"o1\"}", f.lastBody(), "body");
            ok(!f.lastBody().contains("add_fee_ontop"), "omitted");
        });

        test("add_fee_ontop(null) also omits it", () -> {
            FakeTransport f = new FakeTransport().reply(201, INVOICE);
            f.client().createInvoice(CreateInvoiceParams.builder().amount("1").addFeeOntop(true).addFeeOntop(null).build());
            ok(!f.lastBody().contains("add_fee_ontop"), "null clears it");
        });

        test("add_fee_ontop false and true are sent as JSON booleans", () -> {
            FakeTransport f = new FakeTransport().reply(201, INVOICE).reply(201, INVOICE);
            ReceivifyClient c = f.client();
            c.createInvoice(CreateInvoiceParams.builder().amount("25").addFeeOntop(false).build());
            ok(f.lastBody().contains("\"add_fee_ontop\":false"), "false sent: " + f.lastBody());
            c.createInvoice(CreateInvoiceParams.builder().amount("25").addFeeOntop(true).build());
            ok(f.lastBody().contains("\"add_fee_ontop\":true"), "true sent");
        });

        test("outgoing amounts are JSON strings, BigDecimal in plain notation", () -> {
            FakeTransport f = new FakeTransport().reply(201, INVOICE);
            f.client().createInvoice(CreateInvoiceParams.builder().amount(new java.math.BigDecimal("1E-8")).asset("btc")
                    .amountSats("100000").build());
            eq("{\"amount\":\"0.00000001\",\"asset\":\"btc\",\"amount_sats\":\"100000\"}", f.lastBody(), "strings");
        });

        T.section("List envelope and auto-pagination");

        test("a list decodes into data, hasMore and nextCursor", () -> {
            Page<Invoice> p = new FakeTransport().reply(200, page(inv("a") + "," + inv("b"), true, "c1")).client()
                    .listInvoices(ListInvoicesParams.builder().limit(2).build());
            eq(2, p.getData().size(), "size");
            ok(p.hasMore(), "hasMore");
            eq("c1", p.getNextCursor(), "cursor");
            int n = 0;
            for (Invoice ignored : p) {
                n++;
            }
            eq(2, n, "a page is Iterable");
        });

        test("auto-pagination follows next_cursor until has_more is false, lazily", () -> {
            FakeTransport f = new FakeTransport()
                    .reply(200, page(inv("a") + "," + inv("b"), true, "c1"))
                    .reply(200, page(inv("c"), true, "c2"))
                    .reply(200, page(inv("d"), false, null));
            ReceivifyClient c = f.client();
            AutoPager<Invoice> all = c.autoPagingInvoices(ListInvoicesParams.builder().limit(2).status("pending").build());
            eq(0, f.requests.size(), "nothing fetched until iteration");
            List<String> ids = new ArrayList<>();
            for (Invoice i : all) {
                ids.add(i.getId());
            }
            eq(Arrays.asList("a", "b", "c", "d"), ids, "every item, in order");
            eq(3, f.requests.size(), "three pages");
            eq("https://api.test/api/v1/invoices?limit=2&status=pending&cursor=c2", f.last().getUrl(), "cursor + filters kept");
        });

        test("auto-pagination stops early without fetching more pages", () -> {
            FakeTransport f = new FakeTransport().reply(200, page(inv("a") + "," + inv("b"), true, "c1"));
            long count = f.client().autoPagingInvoices().stream().limit(2).count();
            eq(2L, count, "two items");
            eq(1, f.requests.size(), "only one page fetched");
        });

        test("auto-pagination starts from a supplied cursor", () -> {
            FakeTransport f = new FakeTransport().reply(200, page(inv("z"), false, null));
            for (Invoice ignored : f.client().autoPagingInvoices(ListInvoicesParams.builder().cursor("start").build())) {
                // drain
            }
            eq("https://api.test/api/v1/invoices?cursor=start", f.last().getUrl(), "start cursor");
        });

        test("a repeated cursor throws instead of looping forever", () -> {
            FakeTransport f = new FakeTransport()
                    .reply(200, page(inv("a"), true, "same"))
                    .reply(200, page(inv("b"), true, "same"));
            throwsType(ReceivifyException.class, () -> {
                for (Invoice ignored : f.client().autoPagingInvoices()) {
                    // drain
                }
            }, "repeated cursor");
        });

        test("has_more true with no cursor throws", () -> {
            FakeTransport f = new FakeTransport().reply(200, page(inv("a"), true, null));
            throwsType(ReceivifyException.class, () -> f.client().autoPagingInvoices().iterator().hasNext(), "no cursor");
        });

        T.section("Typed errors");

        test("each known code maps to its exception, with code, detail and status", () -> {
            String[][] cases = {
                {"400", "bad_request", "BadRequestException"}, {"401", "unauthorized", "UnauthorizedException"},
                {"404", "not_found", "NotFoundException"}, {"409", "not_allowed", "NotAllowedException"},
                {"400", "bad_ticker", "BadTickerException"}, {"400", "amount_too_low", "AmountTooLowException"},
                {"409", "already_exists", "AlreadyExistsException"}, {"502", "upstream_error", "UpstreamException"},
                {"503", "rate_unavailable", "RateUnavailableException"}, {"500", "internal", "InternalServerException"},
            };
            for (String[] c : cases) {
                FakeTransport f = new FakeTransport();
                for (int i = 0; i < 3; i++) {
                    f.reply(Integer.parseInt(c[0]), "{\"error\":\"" + c[1] + "\",\"detail\":\"d-" + c[1] + "\"}");
                }
                ReceivifyApiException e = throwsType(ReceivifyApiException.class,
                        () -> f.client().cancelInvoice("x"), c[1]);
                eq(c[2], e.getClass().getSimpleName(), "type for " + c[1]);
                eq(c[1], e.getCode(), "code");
                eq("d-" + c[1], e.getDetail(), "detail");
                eq(Integer.parseInt(c[0]), e.getStatusCode(), "status");
            }
        });

        test("an UNKNOWN code degrades to ReceivifyApiException with the code intact", () -> {
            FakeTransport f = new FakeTransport().reply(418, "{\"error\":\"brand_new_code\",\"detail\":\"later API\"}");
            ReceivifyApiException e = throwsType(ReceivifyApiException.class, () -> f.client().cancelInvoice("x"), "unknown");
            eq(ReceivifyApiException.class, e.getClass(), "base class exactly");
            eq("brand_new_code", e.getCode(), "code kept");
            eq(418, e.getStatusCode(), "status");
        });

        test("a non-JSON error body still throws with the status", () -> {
            FakeTransport f = new FakeTransport().reply(502, "<html>Bad gateway</html>");
            ReceivifyApiException e = throwsType(ReceivifyApiException.class, () -> f.client().cancelInvoice("x"), "html");
            eq(null, e.getCode(), "no code");
            eq(502, e.getStatusCode(), "status");
            eq("<html>Bad gateway</html>", e.getRawBody(), "raw body kept");
        });

        test("a 429 carries the rate-limit headers", () -> {
            FakeTransport f = new FakeTransport().reply(429, "{\"error\":\"rate_limited\",\"detail\":\"slow down\"}",
                    headers("Retry-After", "3", "X-RateLimit-Reset", "1700000000", "X-RateLimit-Limit", "60", "X-RateLimit-Remaining", "0"));
            RateLimitedException e = throwsType(RateLimitedException.class, () -> f.client().cancelInvoice("x"), "429");
            eq(3L, e.getRetryAfterSeconds(), "retry-after");
            eq(1700000000L, e.getRateLimitReset(), "reset");
            eq(60L, e.getRateLimitLimit(), "limit");
            eq(0L, e.getRateLimitRemaining(), "remaining");
        });

        T.section("Retry safety and timeouts");

        test("a GET is retried on 500 and then succeeds", () -> {
            List<Duration> sleeps = new ArrayList<>();
            FakeTransport f = new FakeTransport().reply(500, "{\"error\":\"internal\",\"detail\":\"x\"}").reply(200, INVOICE);
            Invoice i = f.client(sleeps).getInvoice("x");
            eq("25062656", i.getAmountExpected(), "succeeded");
            eq(2, f.requests.size(), "two attempts");
            eq(1, sleeps.size(), "one backoff");
        });

        test("a GET gives up after maxRetries and throws the last error", () -> {
            FakeTransport f = new FakeTransport();
            for (int i = 0; i < 3; i++) {
                f.reply(503, "{\"error\":\"rate_unavailable\",\"detail\":\"x\"}");
            }
            throwsType(RateUnavailableException.class, () -> f.client().getInvoice("x"), "after 3 attempts");
            eq(3, f.requests.size(), "1 + 2 retries");
        });

        test("a 4xx is never retried", () -> {
            FakeTransport f = new FakeTransport().reply(404, "{\"error\":\"not_found\",\"detail\":\"x\"}");
            throwsType(NotFoundException.class, () -> f.client().getInvoice("x"), "404");
            eq(1, f.requests.size(), "single attempt");
        });

        test("a POST WITHOUT an Idempotency-Key is never retried", () -> {
            FakeTransport f = new FakeTransport().reply(500, "{\"error\":\"internal\",\"detail\":\"x\"}");
            throwsType(InternalServerException.class,
                    () -> f.client().createInvoice(CreateInvoiceParams.builder().amount("1").build()), "500");
            eq(1, f.requests.size(), "one attempt");
        });

        test("a POST WITH an Idempotency-Key on a supporting endpoint is retried with the same key", () -> {
            FakeTransport f = new FakeTransport().reply(502, "{\"error\":\"upstream_error\",\"detail\":\"x\"}").reply(201, INVOICE);
            f.client().createInvoice(CreateInvoiceParams.builder().amount("1").build(), RequestOptions.idempotencyKey("k1"));
            eq(2, f.requests.size(), "retried");
            eq("k1", f.requests.get(0).getHeader("Idempotency-Key"), "first");
            eq("k1", f.requests.get(1).getHeader("Idempotency-Key"), "same key");
            eq(f.requests.get(0).getBody().length, f.requests.get(1).getBody().length, "same body");
        });

        test("a key on an endpoint that does NOT support it does not unlock retries", () -> {
            FakeTransport f = new FakeTransport().reply(500, "{\"error\":\"internal\",\"detail\":\"x\"}");
            throwsType(InternalServerException.class, () -> f.client().cancelInvoice("x", RequestOptions.idempotencyKey("k")), "500");
            eq(1, f.requests.size(), "not retried");
            eq("k", f.last().getHeader("Idempotency-Key"), "but still sent");
        });

        test("backoff honours Retry-After, then X-RateLimit-Reset, capped by maxRetryDelay", () -> {
            List<Duration> sleeps = new ArrayList<>();
            FakeTransport f = new FakeTransport()
                    .reply(429, "{\"error\":\"rate_limited\",\"detail\":\"x\"}", headers("Retry-After", "2"))
                    .reply(429, "{\"error\":\"rate_limited\",\"detail\":\"x\"}", headers("X-RateLimit-Reset", "1700000007"))
                    .reply(200, INVOICE);
            ReceivifyClient c = ReceivifyClient.builder().apiUrl("https://api.test").apiKey("k").transport(f)
                    .clock(Clock.fixed(Instant.ofEpochSecond(1700000000L), ZoneOffset.UTC))
                    .sleeper(sleeps::add).build();
            c.getInvoice("x");
            eq(Arrays.asList(Duration.ofSeconds(2), Duration.ofSeconds(7)), sleeps, "waits");

            List<Duration> capped = new ArrayList<>();
            FakeTransport g = new FakeTransport()
                    .reply(429, "{\"error\":\"rate_limited\",\"detail\":\"x\"}", headers("X-RateLimit-Reset", "1700009999"))
                    .reply(200, INVOICE);
            ReceivifyClient c2 = ReceivifyClient.builder().apiUrl("https://api.test").apiKey("k").transport(g)
                    .clock(Clock.fixed(Instant.ofEpochSecond(1700000000L), ZoneOffset.UTC))
                    .maxRetryDelay(Duration.ofSeconds(20)).sleeper(capped::add).build();
            c2.getInvoice("x");
            eq(Arrays.asList(Duration.ofSeconds(20)), capped, "capped");
        });

        test("exponential backoff without headers grows and stays under the cap", () -> {
            ReceivifyClient c = ReceivifyClient.builder().transport(new FakeTransport())
                    .retryBaseDelay(Duration.ofMillis(100)).maxRetryDelay(Duration.ofMillis(350)).build();
            Duration d1 = c.backoff(1, null);
            Duration d3 = c.backoff(3, null);
            ok(d1.toMillis() >= 100 && d1.toMillis() < 200, "first step with jitter: " + d1);
            eq(Duration.ofMillis(350), d3, "capped");
        });

        test("a timeout becomes ReceivifyConnectionException(isTimeout) and a GET is retried", () -> {
            FakeTransport f = new FakeTransport().fail(new HttpTimeoutException("timed out")).reply(200, INVOICE);
            f.client().getInvoice("x");
            eq(2, f.requests.size(), "retried after timeout");

            FakeTransport g = new FakeTransport().fail(new HttpTimeoutException("timed out"));
            ReceivifyConnectionException e = throwsType(ReceivifyConnectionException.class,
                    () -> g.client().createInvoice(CreateInvoiceParams.builder().amount("1").build()), "timeout");
            ok(e.isTimeout(), "flagged as timeout");
            eq(1, g.requests.size(), "unsafe POST not retried");
        });

        test("a connection failure is a non-timeout ReceivifyConnectionException", () -> {
            FakeTransport f = new FakeTransport().fail(new ConnectException("refused"));
            ReceivifyConnectionException e = throwsType(ReceivifyConnectionException.class,
                    () -> f.client().cancelInvoice("x"), "refused");
            ok(!e.isTimeout(), "not a timeout");
        });

        T.section("Endpoints");

        test("cancel, bulk cancel and public invoice hit the right routes", () -> {
            FakeTransport f = new FakeTransport()
                    .reply(200, INVOICE.replace("\"pending\"", "\"cancelled\""))
                    .reply(200, "{\"ok\":true,\"action\":\"cancel\",\"requested\":2,\"cancelled\":1}")
                    .reply(200, "{\"id\":\"p\",\"status\":\"pending\",\"address\":\"a\",\"amount_expected\":\"5\",\"amount_received\":\"0\",\"expires_at\":null}");
            ReceivifyClient c = f.client();
            eq("cancelled", c.cancelInvoice("i1").getStatus(), "cancelled");
            eq("POST https://api.test/api/v1/invoices/i1/cancel", f.last().getMethod() + " " + f.last().getUrl(), "cancel route");
            BulkResult b = c.bulkCancelInvoices(Arrays.asList("a", "b"));
            eq("{\"ids\":[\"a\",\"b\"],\"action\":\"cancel\"}", f.lastBody(), "bulk body");
            eq(1, b.getCancelled(), "cancelled count");
            PublicInvoice p = c.getPublicInvoice("p");
            eq(null, f.last().getHeader("Authorization"), "public route has no auth");
            eq("5", p.getAmountExpected(), "public amount");
        });

        test("forwarding: create (200 vs 201), get detail, list, update, disable", () -> {
            String fwd = "{\"id\":\"f1\",\"forward_id\":\"f1\",\"ticker\":\"usdc_base\",\"provider\":\"evm\",\"chain\":\"base\","
                    + "\"address\":\"0xdep\",\"destination\":\"0xdest\",\"callback_url\":null,\"confirmations_required\":3,"
                    + "\"min_forward\":\"0.500000\",\"status\":\"active\",\"expires_at\":null,\"created_at\":\"2026-10-10T12:00:00.000Z\",\"updated_at\":null}";
            String detail = fwd.substring(0, fwd.length() - 1) + ",\"payments\":[{\"txid_in\":\"0xt\",\"value\":\"1000000\",\"status\":\"sent\","
                    + "\"txid_out\":\"0xo\",\"confirmations\":5,\"created_at\":null}],\"payments_has_more\":true,\"callbacks\":[],\"callbacks_has_more\":false}";
            FakeTransport f = new FakeTransport().reply(200, fwd).reply(200, detail).reply(200, page(fwd, false, null))
                    .reply(200, fwd.replace("\"active\"", "\"paused\"")).reply(200, fwd.replace("\"active\"", "\"disabled\""));
            ReceivifyClient c = f.client();
            ReceivifyResponse<Forward> created = c.createAddressWithResponse(CreateAddressParams.builder()
                    .ticker("usdc_base").destination("0xdest").callback("https://h.test/cb").build(), RequestOptions.idempotencyKey("f-1"));
            eq(200, created.getStatusCode(), "200 means existing");
            eq("{\"ticker\":\"usdc_base\",\"destination\":\"0xdest\",\"callback\":\"https://h.test/cb\"}", f.lastBody(), "create body");
            eq("f1", created.getValue().getId(), "id");
            eq("0.500000", created.getValue().getMinForward(), "min_forward is a display decimal");
            ForwardDetail d = c.getAddress("f1");
            eq("1000000", d.getPayments().get(0).getValue(), "payment value");
            ok(d.isPaymentsHasMore(), "truncated flag");
            eq(1, c.listAddresses(ListAddressesParams.builder().status("active").ticker("usdc_base").build()).getData().size(), "list");
            eq("https://api.test/api/v1/address?status=active&ticker=usdc_base", f.last().getUrl(), "list query");
            eq("paused", c.updateAddress("f1", UpdateAddressParams.pause()).getStatus(), "paused");
            eq("{\"status\":\"paused\"}", f.lastBody(), "patch body");
            eq("PATCH", f.last().getMethod(), "patch");
            eq("disabled", c.disableAddress("f1").getStatus(), "disabled");
            eq("DELETE", f.last().getMethod(), "delete");
            throwsType(IllegalArgumentException.class, () -> CreateAddressParams.builder().ticker("btc").build(), "destination required");
        });

        test("updateAddress sends an explicit null to clear the callback, and only what was set", () -> {
            FakeTransport f = new FakeTransport().reply(200, "{\"id\":\"f1\"}");
            f.client().updateAddress("f1", UpdateAddressParams.builder().clearCallbackUrl().build());
            eq("{\"callback_url\":null}", f.lastBody(), "explicit null");
        });

        test("rotateWebhookSecret is a POST that may carry an Idempotency-Key", () -> {
            FakeTransport f = new FakeTransport().reply(200, "{\"secret\":\"whsec_new\",\"rotated_at\":\"2026-10-10T12:00:00.000Z\","
                    + "\"prev_expires_at\":\"2026-10-11T12:00:00.000Z\",\"grace_seconds\":86400,\"replaced_previous\":false}");
            WebhookSecretRotated r = f.client().rotateWebhookSecret(RequestOptions.idempotencyKey("rot-1"));
            eq("POST https://api.test/api/v1/webhooks/secret/rotate", f.last().getMethod() + " " + f.last().getUrl(), "route");
            eq("whsec_new", r.getSecret(), "secret");
            eq(86400, r.getGraceSeconds(), "grace");
            ok(!r.toString().contains("whsec_new"), "toString hides the secret");
        });

        test("keys, settings and the health report decode", () -> {
            FakeTransport f = new FakeTransport()
                    .reply(200, page("{\"id\":\"k1\",\"prefix\":\"rcv_live_ab\",\"label\":\"default\",\"revoked\":false}", false, null))
                    .reply(200, "{\"ok\":true,\"revoked\":\"k1\"}")
                    .reply(200, "{\"display_currency\":\"USD\",\"email\":\"a@b.c\",\"fee_bps\":100,\"add_fee_ontop\":true}")
                    .reply(503, "{\"status\":\"degraded\",\"time\":\"2026-10-10T12:00:00.000Z\",\"checks\":{\"mongo\":{\"ok\":false}},\"alerts\":[]}");
            ReceivifyClient c = f.client();
            eq("k1", c.listApiKeys(null).getData().get(0).getId(), "key id");
            eq("k1", c.revokeApiKey("k1").getString("revoked"), "revoked ack");
            Settings s = c.getSettings();
            ok(s.isAddFeeOntop(), "account default add_fee_ontop");
            eq(100, s.getFeeBps(), "fee bps");
            HealthReport h = c.getHealth();
            ok(h.isDegraded(), "503 health returned, not thrown");
        });

        test("the request() escape hatch reaches unmodelled endpoints", () -> {
            FakeTransport f = new FakeTransport().reply(200, "{\"tier\":\"gold\",\"fee_bps\":50}");
            ReceivifyResponse<Object> r = f.client().request("get", "/api/v1/fees/tier", null, null, null);
            eq(200, r.getStatusCode(), "status");
            eq("gold", ((Map<?, ?>) r.getValue()).get("tier"), "decoded tree");
            eq("Bearer rcv_live_secretkey123", f.last().getHeader("Authorization"), "auth applied");
        });

        test("an Idempotent-Replay header is surfaced", () -> {
            FakeTransport f = new FakeTransport().reply(201, "{\"id\":\"f1\"}", headers("Idempotent-Replay", "true"));
            ok(f.client().createAddressWithResponse(CreateAddressParams.builder().ticker("btc").destination("bc1q").build(), null)
                    .isIdempotentReplay(), "replay flag");
        });

        test("a 2xx body that is not JSON is a clear ReceivifyException", () -> {
            FakeTransport f = new FakeTransport().reply(200, "<html>proxy</html>");
            throwsType(ReceivifyException.class, () -> f.client().getInvoice("x"), "not json");
        });

        test("builder validation", () -> {
            throwsType(IllegalArgumentException.class, () -> ReceivifyClient.builder().apiUrl("ftp://x").build(), "scheme");
            throwsType(IllegalArgumentException.class, () -> ReceivifyClient.builder().timeout(Duration.ZERO), "timeout");
            eq("https://receivify.io", ReceivifyClient.builder().transport(new FakeTransport()).build().getApiUrl(), "default URL");
            throwsType(IllegalArgumentException.class, () -> new FakeTransport().client().getInvoice(" "), "blank id");
        });
    }
}
