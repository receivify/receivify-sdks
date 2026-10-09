package io.receivify;

import static io.receivify.T.eq;
import static io.receivify.T.ok;
import static io.receivify.T.test;
import static io.receivify.T.throwsType;

import java.nio.charset.StandardCharsets;
import java.time.Clock;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.Arrays;
import java.util.Collections;
import java.util.List;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

final class WebhookTests {
    private WebhookTests() {
    }

    static final String CURRENT = "whsec_current_0123456789";
    static final String PREVIOUS = "whsec_previous_9876543210";
    static final String BODY = "{\"event\":\"invoice.paid\",\"sequence\":42,\"created_at\":\"2026-10-10T12:00:00.000Z\","
            + "\"data\":{\"id\":\"8f14e45f-ceea-4e1a-9b3c-1d2e3f4a5b6c\",\"reference\":\"order-1\",\"status\":\"paid\","
            + "\"amount_expected\":\"100000\",\"amount_received\":\"100000\",\"address\":\"bc1qtest\","
            + "\"paid_at\":\"2026-10-10T12:00:00.000Z\"}}";

    /** Signs independently of the SDK so the test checks the SDK, not itself. */
    static String independentSign(String body, String secret, long t) throws Exception {
        Mac mac = Mac.getInstance("HmacSHA256");
        mac.init(new SecretKeySpec(secret.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
        byte[] sig = mac.doFinal((t + "." + body).getBytes(StandardCharsets.UTF_8));
        StringBuilder hex = new StringBuilder();
        for (byte b : sig) {
            hex.append(String.format("%02x", b));
        }
        return "t=" + t + ",v1=" + hex;
    }

    static long now() {
        return Instant.now().getEpochSecond();
    }

    static void run() {
        T.section("Webhook signature verification");

        test("a delivery signed with the CURRENT secret verifies", () -> {
            String header = independentSign(BODY, CURRENT, now());
            ok(Webhooks.verify(BODY.getBytes(StandardCharsets.UTF_8), header, Collections.singletonList(CURRENT)), "bytes");
            ok(Webhooks.verify(BODY, header, Arrays.asList(CURRENT, PREVIOUS)), "string, list of two");
        });

        test("a delivery signed with the PREVIOUS secret verifies when both are supplied", () -> {
            String header = independentSign(BODY, PREVIOUS, now());
            ok(Webhooks.verify(BODY, header, Arrays.asList(CURRENT, PREVIOUS)), "grace window");
            ok(!Webhooks.verify(BODY, header, Collections.singletonList(CURRENT)), "but not with the current alone");
        });

        test("a TAMPERED body fails", () -> {
            String header = independentSign(BODY, CURRENT, now());
            String tampered = BODY.replace("\"100000\",\"address\"", "\"999999\",\"address\"");
            ok(!tampered.equals(BODY), "the body really changed");
            VerificationResult r = Webhooks.describe(tampered, header, Collections.singletonList(CURRENT), null);
            ok(!r.isOk(), "rejected");
            eq(WebhookFailureReason.NO_MATCH, r.getReason(), "reason");
        });

        test("a re-serialised body (same JSON, different bytes) fails", () -> {
            String header = independentSign(BODY, CURRENT, now());
            String reserialised = Json.write(Json.parse(BODY)).replace(",", ", ");
            ok(!Webhooks.verify(reserialised, header, Collections.singletonList(CURRENT)), "raw bytes matter");
        });

        test("a timestamp an hour old fails the 300s default tolerance", () -> {
            String header = independentSign(BODY, CURRENT, now() - 3600);
            VerificationResult r = Webhooks.describe(BODY, header, Collections.singletonList(CURRENT), null);
            eq(WebhookFailureReason.STALE_TIMESTAMP, r.getReason(), "stale");
            ok(Webhooks.verify(BODY, header, Collections.singletonList(CURRENT), Duration.ofHours(2)), "a wider tolerance accepts it");
            ok(Webhooks.verify(BODY, header, Collections.singletonList(CURRENT), Duration.ZERO), "zero disables the check");
        });

        test("a timestamp far in the future also fails", () -> {
            String header = independentSign(BODY, CURRENT, now() + 3600);
            ok(!Webhooks.verify(BODY, header, Collections.singletonList(CURRENT)), "future rejected");
        });

        test("an injected clock drives the tolerance check", () -> {
            long t = 1_700_000_000L;
            String header = independentSign(BODY, CURRENT, t);
            WebhookVerifyOptions at = WebhookVerifyOptions.defaults()
                    .withClock(Clock.fixed(Instant.ofEpochSecond(t + 299), ZoneOffset.UTC));
            ok(Webhooks.verify(BODY, header, Collections.singletonList(CURRENT), at), "inside the window");
            WebhookVerifyOptions late = at.withClock(Clock.fixed(Instant.ofEpochSecond(t + 301), ZoneOffset.UTC));
            ok(!Webhooks.verify(BODY, header, Collections.singletonList(CURRENT), late), "outside the window");
        });

        test("malformed or missing headers are reported, not thrown", () -> {
            List<String> s = Collections.singletonList(CURRENT);
            String[] bad = {null, "", "garbage", "t=123", "v1=abcd", "t=abc,v1=00", "t=-5,v1=00", "t=,v1=00"};
            for (String h : bad) {
                VerificationResult r = Webhooks.describe(BODY, h, s, null);
                eq(WebhookFailureReason.BAD_HEADER, r.getReason(), "header " + h);
            }
            long t = now();
            String good = independentSign(BODY, CURRENT, t);
            String sig = good.substring(good.indexOf("v1=") + 3);
            ok(!Webhooks.verify(BODY, "t=" + t + ",v1=zz" + sig.substring(2), s), "non-hex signature");
            ok(!Webhooks.verify(BODY, "t=" + t + ",v1=" + sig.substring(1), s), "odd-length signature");
            ok(Webhooks.verify(BODY, " t=" + t + " , v1=deadbeef , v1=" + sig.toUpperCase(), s), "spaces, multiple v1, upper-case hex");
        });

        test("no configured secret is its own reason", () -> {
            String header = independentSign(BODY, CURRENT, now());
            eq(WebhookFailureReason.NO_SECRETS, Webhooks.describe(BODY, header, null, null).getReason(), "null list");
            eq(WebhookFailureReason.NO_SECRETS,
                    Webhooks.describe(BODY, header, Arrays.asList("", null), null).getReason(), "blank entries dropped");
        });

        test("sign() produces what an independent HMAC produces", () -> {
            eq(independentSign(BODY, CURRENT, 1700000000L), Webhooks.sign(BODY, CURRENT, 1700000000L), "same header");
        });

        test("the client verifies against its configured secret list", () -> {
            ReceivifyClient c = ReceivifyClient.builder().webhookSecrets(Arrays.asList(CURRENT, PREVIOUS))
                    .transport(new FakeTransport()).build();
            ok(c.verifyWebhook(BODY, independentSign(BODY, PREVIOUS, now())), "previous accepted");
            ok(!c.verifyWebhook(BODY, independentSign(BODY, "whsec_other", now())), "unknown rejected");
            eq(2, c.getWebhookSecrets().size(), "two secrets");
        });

        T.section("Webhook envelope");

        test("parse returns the envelope with typed invoice data", () -> {
            WebhookEvent e = Webhooks.parse(BODY, independentSign(BODY, CURRENT, now()), Collections.singletonList(CURRENT));
            eq("invoice.paid", e.getEvent(), "event");
            eq(42L, e.getSequence(), "sequence");
            eq(Instant.parse("2026-10-10T12:00:00Z"), e.getCreatedAt(), "created_at");
            ok(e.isInvoiceEvent() && !e.isForwardEvent(), "family");
            InvoiceEventData d = e.getInvoiceData();
            eq("order-1", d.getReference(), "reference");
            eq("100000", d.getAmountReceived(), "money stays a string");
            eq(null, e.getForwardData(), "not a forward event");
            eq("paid", e.getData().get("status"), "raw data map");
        });

        test("a forward event decodes the CryptAPI-shaped payload with reason and detail", () -> {
            String body = "{\"event\":\"forward.deferred\",\"sequence\":7,\"created_at\":\"2026-10-10T12:00:00.000Z\",\"data\":"
                    + "{\"forward_id\":\"f1\",\"ticker\":\"usdc_base\",\"address_in\":\"0xin\",\"address_out\":\"0xout\","
                    + "\"txid_in\":\"0xtx\",\"txid_out\":null,\"value\":\"115792089237316195423570985008687907853269984665640564039457\","
                    + "\"value_forwarded\":\"0\",\"fee\":\"0\",\"confirmations\":3,\"pending\":false,"
                    + "\"reason\":\"below_minimum\",\"detail\":\"Below min_forward\"}}";
            WebhookEvent e = Webhooks.parse(body, independentSign(body, CURRENT, now()), Collections.singletonList(CURRENT));
            ForwardEventData d = e.getForwardData();
            eq("115792089237316195423570985008687907853269984665640564039457", d.getValue(), "huge value exact");
            eq("below_minimum", d.getReason(), "reason");
            eq(null, d.getTxidOut(), "null txid_out");
            eq(3, d.getConfirmations(), "confirmations");
            eq(null, e.getInvoiceData(), "not invoice-shaped");
        });

        test("an unknown future event name does not crash the parser", () -> {
            String body = "{\"event\":\"invoice.refunded\",\"sequence\":1,\"created_at\":\"2026-01-01T00:00:00.000Z\",\"data\":{\"id\":\"x\",\"new\":1}}";
            WebhookEvent e = Webhooks.parse(body, independentSign(body, CURRENT, now()), Collections.singletonList(CURRENT));
            eq("invoice.refunded", e.getEvent(), "kept");
            eq("1", String.valueOf(e.getData().get("new")), "unknown field reachable");
        });

        test("parse throws SignatureVerificationException with a reason on a bad signature", () -> {
            SignatureVerificationException ex = throwsType(SignatureVerificationException.class,
                    () -> Webhooks.parse(BODY, independentSign(BODY, "whsec_wrong", now()), Collections.singletonList(CURRENT)),
                    "bad signature");
            eq(WebhookFailureReason.NO_MATCH, ex.getReason(), "reason");
            ok(!ex.getMessage().contains(CURRENT), "the secret is never in the message");
        });

        test("parse throws on a verified body that is not an envelope", () -> {
            String notJson = "not json";
            ReceivifyWebhookException a = throwsType(ReceivifyWebhookException.class,
                    () -> Webhooks.parse(notJson, independentSign(notJson, CURRENT, now()), Collections.singletonList(CURRENT)), "bad json");
            eq(WebhookFailureReason.BAD_JSON, a.getReason(), "BAD_JSON");
            String arr = "[1,2]";
            ReceivifyWebhookException b = throwsType(ReceivifyWebhookException.class,
                    () -> Webhooks.parse(arr, independentSign(arr, CURRENT, now()), Collections.singletonList(CURRENT)), "not envelope");
            eq(WebhookFailureReason.BAD_ENVELOPE, b.getReason(), "BAD_ENVELOPE");
        });
    }
}
