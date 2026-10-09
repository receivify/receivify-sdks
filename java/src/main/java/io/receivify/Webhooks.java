package io.receivify;

import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.time.Duration;
import java.util.ArrayList;
import java.util.Collection;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/**
 * Webhook signature verification and envelope parsing. Pure static functions: no network, no
 * client needed.
 *
 * <p>Every delivery carries {@code X-Receivify-Signature: t=<unix>,v1=<hex>}, where the hex is
 * {@code HMAC-SHA256(secret, t + "." + rawBody)}.
 *
 * <p><b>Use the RAW body bytes.</b> Re-serialising a parsed object changes key order and whitespace
 * and the HMAC will not match a genuine delivery. In Spring take {@code @RequestBody byte[]} (or
 * {@code String}), never a bound DTO.
 *
 * <p><b>Verify against a LIST of secrets.</b> After a rotation the previous secret stays valid for a
 * grace window (24h by default), so two secrets are legitimately valid at once. Pass
 * {@code List.of(current, previous)} until the window closes.
 */
public final class Webhooks {
    /** The header Receivify signs every delivery with. */
    public static final String SIGNATURE_HEADER = "X-Receivify-Signature";

    /** The default replay window, in seconds. */
    public static final long DEFAULT_TOLERANCE_SECONDS = 300;

    private Webhooks() {
    }

    /* ---------------- verify (boolean) ---------------- */

    /**
     * Verify a delivery with the default 300-second tolerance.
     *
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the value of {@code X-Receivify-Signature} (may be {@code null})
     * @param secrets current secret first, then the previous one during a rotation
     * @return true when the signature matches one of the secrets and the timestamp is fresh
     */
    public static boolean verify(byte[] rawBody, String signatureHeader, Collection<String> secrets) {
        return describe(rawBody, signatureHeader, secrets, WebhookVerifyOptions.defaults()).isOk();
    }

    /**
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param tolerance the replay window; {@link Duration#ZERO} disables it (tests only)
     * @return true when authentic
     */
    public static boolean verify(byte[] rawBody, String signatureHeader, Collection<String> secrets, Duration tolerance) {
        return describe(rawBody, signatureHeader, secrets, WebhookVerifyOptions.tolerance(tolerance)).isOk();
    }

    /**
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param options tolerance and clock
     * @return true when authentic
     */
    public static boolean verify(byte[] rawBody, String signatureHeader, Collection<String> secrets, WebhookVerifyOptions options) {
        return describe(rawBody, signatureHeader, secrets, options).isOk();
    }

    /**
     * Verify a body you hold as a String. Only safe when it is the untouched request body; it is
     * encoded back to UTF-8 bytes.
     *
     * @param rawBody the EXACT body text
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @return true when authentic
     */
    public static boolean verify(String rawBody, String signatureHeader, Collection<String> secrets) {
        return verify(utf8(rawBody), signatureHeader, secrets);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param tolerance the replay window
     * @return true when authentic
     */
    public static boolean verify(String rawBody, String signatureHeader, Collection<String> secrets, Duration tolerance) {
        return verify(utf8(rawBody), signatureHeader, secrets, tolerance);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param options tolerance and clock
     * @return true when authentic
     */
    public static boolean verify(String rawBody, String signatureHeader, Collection<String> secrets, WebhookVerifyOptions options) {
        return verify(utf8(rawBody), signatureHeader, secrets, options);
    }

    /* ---------------- describe (reason) ---------------- */

    /**
     * The same check as {@code verify}, returning a reason you can log.
     *
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param options tolerance and clock; {@code null} means defaults
     * @return the outcome
     */
    public static VerificationResult describe(byte[] rawBody, String signatureHeader, Collection<String> secrets, WebhookVerifyOptions options) {
        if (rawBody == null) {
            throw new IllegalArgumentException("rawBody must not be null");
        }
        WebhookVerifyOptions opts = options == null ? WebhookVerifyOptions.defaults() : options;
        List<String> list = normaliseSecrets(secrets);
        if (list.isEmpty()) {
            return new VerificationResult(false, WebhookFailureReason.NO_SECRETS,
                    "No webhook secret configured, so nothing could be verified.", null);
        }

        Parsed parsed = parseHeader(signatureHeader);
        if (parsed == null) {
            return new VerificationResult(false, WebhookFailureReason.BAD_HEADER,
                    "Missing or malformed " + SIGNATURE_HEADER + " header (expected \"t=<unix>,v1=<hex>\").", null);
        }

        long toleranceSeconds = opts.getTolerance().getSeconds();
        if (toleranceSeconds > 0) {
            long now = opts.getClock().instant().getEpochSecond();
            if (Math.abs(now - parsed.timestamp) > toleranceSeconds) {
                return new VerificationResult(false, WebhookFailureReason.STALE_TIMESTAMP,
                        "Webhook timestamp " + parsed.timestamp + " is outside the " + toleranceSeconds
                                + "s tolerance window.", parsed.timestamp);
            }
        }

        byte[] signed = signedPayload(parsed.rawTimestamp, rawBody);

        // No early exit: every secret and every v1 candidate is compared in constant time.
        boolean matched = false;
        for (String secret : list) {
            byte[] expected = hmac(secret, signed);
            for (String candidateHex : parsed.signatures) {
                byte[] candidate = decodeHex(candidateHex);
                if (candidate != null && MessageDigest.isEqual(expected, candidate)) {
                    matched = true;
                }
            }
        }

        return matched
                ? new VerificationResult(true, WebhookFailureReason.NONE, "ok", parsed.timestamp)
                : new VerificationResult(false, WebhookFailureReason.NO_MATCH,
                        "Webhook signature did not match any configured secret.", parsed.timestamp);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param options tolerance and clock; {@code null} means defaults
     * @return the outcome
     */
    public static VerificationResult describe(String rawBody, String signatureHeader, Collection<String> secrets, WebhookVerifyOptions options) {
        return describe(utf8(rawBody), signatureHeader, secrets, options);
    }

    /* ---------------- parse ---------------- */

    /**
     * Verify, then decode the envelope.
     *
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     * @throws ReceivifyWebhookException when the verified body is not a valid envelope
     */
    public static WebhookEvent parse(byte[] rawBody, String signatureHeader, Collection<String> secrets) {
        return parse(rawBody, signatureHeader, secrets, WebhookVerifyOptions.defaults());
    }

    /**
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param tolerance the replay window
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     */
    public static WebhookEvent parse(byte[] rawBody, String signatureHeader, Collection<String> secrets, Duration tolerance) {
        return parse(rawBody, signatureHeader, secrets, WebhookVerifyOptions.tolerance(tolerance));
    }

    /**
     * @param rawBody the EXACT request body bytes
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param options tolerance and clock
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     * @throws ReceivifyWebhookException when the verified body is not a valid envelope
     */
    @SuppressWarnings("unchecked")
    public static WebhookEvent parse(byte[] rawBody, String signatureHeader, Collection<String> secrets, WebhookVerifyOptions options) {
        VerificationResult result = describe(rawBody, signatureHeader, secrets, options);
        if (!result.isOk()) {
            throw new SignatureVerificationException(result.getMessage(), result.getReason());
        }
        Object parsed;
        try {
            parsed = Json.parse(rawBody);
        } catch (Json.JsonException e) {
            throw new ReceivifyWebhookException("Webhook body is not valid JSON: " + e.getMessage(),
                    WebhookFailureReason.BAD_JSON, e);
        }
        if (!(parsed instanceof Map) || !(((Map<String, Object>) parsed).get("event") instanceof String)
                || ((String) ((Map<String, Object>) parsed).get("event")).isEmpty()) {
            throw new ReceivifyWebhookException("Webhook body is not a {event, sequence, created_at, data} envelope.",
                    WebhookFailureReason.BAD_ENVELOPE, null);
        }
        return new WebhookEvent((Map<String, Object>) parsed);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     */
    public static WebhookEvent parse(String rawBody, String signatureHeader, Collection<String> secrets) {
        return parse(utf8(rawBody), signatureHeader, secrets);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param tolerance the replay window
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     */
    public static WebhookEvent parse(String rawBody, String signatureHeader, Collection<String> secrets, Duration tolerance) {
        return parse(utf8(rawBody), signatureHeader, secrets, tolerance);
    }

    /**
     * @param rawBody the EXACT body text
     * @param signatureHeader the value of {@code X-Receivify-Signature}
     * @param secrets current first, then previous
     * @param options tolerance and clock
     * @return the event
     * @throws SignatureVerificationException when the signature check fails
     */
    public static WebhookEvent parse(String rawBody, String signatureHeader, Collection<String> secrets, WebhookVerifyOptions options) {
        return parse(utf8(rawBody), signatureHeader, secrets, options);
    }

    /* ---------------- signing (fixtures) ---------------- */

    /**
     * Sign a body exactly as Receivify does, producing a full {@code t=<unix>,v1=<hex>} header value.
     * For tests and local fixtures; you never need it to receive a webhook.
     *
     * @param rawBody the body bytes
     * @param secret the signing secret
     * @param timestamp Unix seconds to embed as {@code t=}
     * @return the header value
     */
    public static String sign(byte[] rawBody, String secret, long timestamp) {
        String t = Long.toString(timestamp);
        return "t=" + t + ",v1=" + encodeHex(hmac(secret, signedPayload(t, rawBody)));
    }

    /**
     * @param rawBody the body text
     * @param secret the signing secret
     * @param timestamp Unix seconds
     * @return the header value
     */
    public static String sign(String rawBody, String secret, long timestamp) {
        return sign(utf8(rawBody), secret, timestamp);
    }

    /* ---------------- internals ---------------- */

    static List<String> normaliseSecrets(Collection<String> secrets) {
        if (secrets == null) {
            return Collections.emptyList();
        }
        List<String> out = new ArrayList<>();
        for (String s : secrets) {
            if (s != null && !s.isEmpty()) {
                out.add(s);
            }
        }
        return Collections.unmodifiableList(out);
    }

    private static final class Parsed {
        final String rawTimestamp;
        final long timestamp;
        final List<String> signatures;

        Parsed(String rawTimestamp, long timestamp, List<String> signatures) {
            this.rawTimestamp = rawTimestamp;
            this.timestamp = timestamp;
            this.signatures = signatures;
        }
    }

    private static Parsed parseHeader(String header) {
        if (header == null || header.isEmpty()) {
            return null;
        }
        String rawTimestamp = null;
        List<String> signatures = new ArrayList<>();
        for (String part : header.split(",")) {
            int eq = part.indexOf('=');
            if (eq < 0) {
                continue;
            }
            String key = part.substring(0, eq).trim();
            String value = part.substring(eq + 1).trim();
            if ("t".equals(key) && rawTimestamp == null) {
                rawTimestamp = value;
            } else if ("v1".equals(key) && !value.isEmpty()) {
                signatures.add(value);
            }
        }
        if (rawTimestamp == null || rawTimestamp.isEmpty() || signatures.isEmpty()) {
            return null;
        }
        for (int i = 0; i < rawTimestamp.length(); i++) {
            char c = rawTimestamp.charAt(i);
            if (c < '0' || c > '9') {
                return null;
            }
        }
        long ts;
        try {
            ts = Long.parseLong(rawTimestamp);
        } catch (NumberFormatException e) {
            return null;
        }
        return new Parsed(rawTimestamp, ts, signatures);
    }

    private static byte[] signedPayload(String rawTimestamp, byte[] rawBody) {
        // The RAW timestamp characters are signed, exactly as the server sent them.
        byte[] prefix = (rawTimestamp + ".").getBytes(StandardCharsets.UTF_8);
        byte[] out = new byte[prefix.length + rawBody.length];
        System.arraycopy(prefix, 0, out, 0, prefix.length);
        System.arraycopy(rawBody, 0, out, prefix.length, rawBody.length);
        return out;
    }

    static byte[] hmac(String secret, byte[] data) {
        try {
            Mac mac = Mac.getInstance("HmacSHA256");
            mac.init(new SecretKeySpec(secret.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
            return mac.doFinal(data);
        } catch (GeneralSecurityException e) {
            throw new IllegalStateException("HmacSHA256 is unavailable in this JVM", e);
        }
    }

    private static final char[] HEX = "0123456789abcdef".toCharArray();

    static String encodeHex(byte[] bytes) {
        char[] out = new char[bytes.length * 2];
        for (int i = 0; i < bytes.length; i++) {
            out[i * 2] = HEX[(bytes[i] >> 4) & 0xF];
            out[i * 2 + 1] = HEX[bytes[i] & 0xF];
        }
        return new String(out);
    }

    private static byte[] decodeHex(String hex) {
        if (hex == null || hex.isEmpty() || (hex.length() % 2) != 0) {
            return null;
        }
        byte[] out = new byte[hex.length() / 2];
        for (int i = 0; i < out.length; i++) {
            int hi = Character.digit(hex.charAt(i * 2), 16);
            int lo = Character.digit(hex.charAt(i * 2 + 1), 16);
            if (hi < 0 || lo < 0) {
                return null;
            }
            out[i] = (byte) ((hi << 4) | lo);
        }
        return out;
    }

    private static byte[] utf8(String s) {
        if (s == null) {
            throw new IllegalArgumentException("rawBody must not be null");
        }
        return s.getBytes(StandardCharsets.UTF_8);
    }
}
