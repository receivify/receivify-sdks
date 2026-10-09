package io.receivify;

/** Why a webhook failed verification or parsing. */
public enum WebhookFailureReason {
    /** Nothing failed (only seen on a successful {@link VerificationResult}). */
    NONE,
    /** No secret was supplied, so nothing could be checked. */
    NO_SECRETS,
    /** The {@code X-Receivify-Signature} header was missing or not {@code t=<unix>,v1=<hex>}. */
    BAD_HEADER,
    /** The {@code t=} timestamp fell outside the tolerance window. */
    STALE_TIMESTAMP,
    /** The signature matched none of the supplied secrets (wrong secret, or a modified body). */
    NO_MATCH,
    /** The body verified but is not valid JSON. */
    BAD_JSON,
    /** The body is JSON but not a {@code {event, sequence, created_at, data}} envelope. */
    BAD_ENVELOPE
}
