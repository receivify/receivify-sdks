"""Signature verification: rotation, tampering, replay windows, and the envelope."""

from __future__ import annotations

import hashlib
import hmac
import json
import time
import unittest

from receivify import (
    SIGNATURE_HEADER,
    Receivify,
    SignatureVerificationError,
    WebhookEvent,
    WebhookPayloadError,
    compute_signature,
    parse_signature_header,
    parse_webhook,
    verify_webhook,
)

from .support import FakeTransport

CURRENT = "whsec_current_secret"
PREVIOUS = "whsec_previous_secret"

# The exact bytes Receivify would POST. Note the key order and the spacing: verifying
# against json.dumps(json.loads(body)) would reorder these and break the HMAC, which is
# the whole reason these tests hold raw bytes rather than a dict.
RAW_BODY = (
    b'{"event":"invoice.paid","sequence":12,"created_at":"2026-08-25T12:05:00.000Z",'
    b'"data":{"id":"8d1f3d4e-0000-4000-8000-000000000001","reference":"order-1001",'
    b'"status":"paid","amount_expected":"2100000000000000","amount_received":"2100000000000000",'
    b'"address":"bc1qexampleaddress","paid_at":"2026-08-25T12:05:00.000Z"}}'
)

FORWARD_BODY = (
    b'{"event":"forward.sent","sequence":98,"created_at":"2026-08-25T12:05:00.000Z",'
    b'"data":{"forward_id":"11111111-2222-4333-8444-555555555555","ticker":"btc",'
    b'"address_in":"bc1qdeposit","address_out":"bc1qmywallet","txid_in":"aa","txid_out":"bb",'
    b'"value":"150000","value_forwarded":"149000","fee":"1000","confirmations":3,"pending":false}}'
)


def header_for(body, secret, timestamp):
    signature = hmac.new(
        secret.encode("utf-8"), "{}.".format(timestamp).encode("utf-8") + body, hashlib.sha256
    ).hexdigest()
    return "t={},v1={}".format(timestamp, signature)


class SignatureVerificationTests(unittest.TestCase):
    def setUp(self):
        self.now = int(time.time())

    def test_verifies_with_the_current_secret(self):
        header = header_for(RAW_BODY, CURRENT, self.now)
        self.assertTrue(verify_webhook(RAW_BODY, header, [CURRENT, PREVIOUS], now=self.now))

    def test_verifies_with_the_previous_secret_during_a_rotation(self):
        # A delivery signed just before a rotation must still verify against the previous
        # secret until its grace window closes. This is why the argument is a LIST.
        header = header_for(RAW_BODY, PREVIOUS, self.now)
        self.assertTrue(verify_webhook(RAW_BODY, header, [CURRENT, PREVIOUS], now=self.now))

    def test_fails_when_only_the_wrong_secret_is_offered(self):
        header = header_for(RAW_BODY, PREVIOUS, self.now)
        self.assertFalse(verify_webhook(RAW_BODY, header, [CURRENT], now=self.now))

    def test_a_tampered_body_fails(self):
        header = header_for(RAW_BODY, CURRENT, self.now)
        tampered = RAW_BODY.replace(b'"amount_received":"2100000000000000"', b'"amount_received":"1"')
        self.assertNotEqual(RAW_BODY, tampered)
        self.assertFalse(verify_webhook(tampered, header, [CURRENT, PREVIOUS], now=self.now))

    def test_a_stale_timestamp_fails_the_tolerance_window(self):
        stale = self.now - 3600
        header = header_for(RAW_BODY, CURRENT, stale)
        # The signature itself is perfectly valid -- only the replay window rejects it.
        self.assertTrue(verify_webhook(RAW_BODY, header, [CURRENT], tolerance_seconds=None, now=self.now))
        self.assertFalse(verify_webhook(RAW_BODY, header, [CURRENT], now=self.now))

    def test_a_timestamp_slightly_in_the_future_is_tolerated(self):
        header = header_for(RAW_BODY, CURRENT, self.now + 30)
        self.assertTrue(verify_webhook(RAW_BODY, header, [CURRENT], now=self.now))

    def test_the_timestamp_is_part_of_the_signed_payload(self):
        # Re-stamping a captured body with a fresh t must not resurrect it.
        header = header_for(RAW_BODY, CURRENT, self.now - 3600)
        forged = "t={},v1={}".format(self.now, header.split("v1=")[1])
        self.assertFalse(verify_webhook(RAW_BODY, forged, [CURRENT], now=self.now))

    def test_a_single_secret_string_is_accepted_and_normalised(self):
        header = header_for(RAW_BODY, CURRENT, self.now)
        self.assertTrue(verify_webhook(RAW_BODY, header, CURRENT, now=self.now))

    def test_a_str_body_verifies_identically_to_bytes(self):
        header = header_for(RAW_BODY, CURRENT, self.now)
        self.assertTrue(verify_webhook(RAW_BODY.decode("utf-8"), header, [CURRENT], now=self.now))

    def test_no_secrets_never_verifies(self):
        header = header_for(RAW_BODY, CURRENT, self.now)
        self.assertFalse(verify_webhook(RAW_BODY, header, [], now=self.now))
        self.assertFalse(verify_webhook(RAW_BODY, header, None, now=self.now))
        self.assertFalse(verify_webhook(RAW_BODY, header, [""], now=self.now))

    def test_malformed_headers_fail_closed(self):
        for header in (None, "", "garbage", "t=abc,v1=deadbeef", "v1=deadbeef", "t={}".format(self.now)):
            self.assertFalse(verify_webhook(RAW_BODY, header, [CURRENT], now=self.now), header)

    def test_case_and_whitespace_in_the_header_are_tolerated(self):
        header = header_for(RAW_BODY, CURRENT, self.now)
        timestamp, signatures = parse_signature_header(header)
        spaced = "t = {} , v1 = {}".format(timestamp, signatures[0].upper())
        self.assertTrue(verify_webhook(RAW_BODY, spaced, [CURRENT], now=self.now))

    def test_multiple_v1_values_are_all_considered(self):
        good = parse_signature_header(header_for(RAW_BODY, CURRENT, self.now))[1][0]
        header = "t={},v1={},v1={}".format(self.now, "00" * 32, good)
        self.assertTrue(verify_webhook(RAW_BODY, header, [CURRENT], now=self.now))

    def test_compute_signature_matches_the_documented_scheme(self):
        expected = hmac.new(
            CURRENT.encode("utf-8"), b"1780000000." + RAW_BODY, hashlib.sha256
        ).hexdigest()
        self.assertEqual(expected, compute_signature(RAW_BODY, 1780000000, CURRENT))


class ParseWebhookTests(unittest.TestCase):
    def setUp(self):
        self.now = int(time.time())
        self.header = header_for(RAW_BODY, CURRENT, self.now)

    def test_parse_returns_the_typed_envelope(self):
        event = parse_webhook(RAW_BODY, self.header, [CURRENT], now=self.now)

        self.assertIsInstance(event, WebhookEvent)
        self.assertEqual("invoice.paid", event.event)
        self.assertEqual(12, event.sequence)
        self.assertEqual("2026-08-25T12:05:00.000Z", event.created_at)
        self.assertEqual(("invoice.paid", 12), event.dedupe_key)

    def test_parse_raises_on_a_bad_signature(self):
        with self.assertRaises(SignatureVerificationError):
            parse_webhook(RAW_BODY, self.header, [PREVIOUS], now=self.now)

    def test_parse_raises_on_a_verified_but_non_json_body(self):
        body = b"not json"
        header = header_for(body, CURRENT, self.now)
        with self.assertRaises(WebhookPayloadError):
            parse_webhook(body, header, [CURRENT], now=self.now)

    def test_invoice_family_payload(self):
        event = parse_webhook(RAW_BODY, self.header, [CURRENT], now=self.now)
        self.assertTrue(event.is_invoice_event)
        self.assertFalse(event.is_forward_event)

        data = event.invoice
        self.assertIsNotNone(data)
        self.assertEqual("order-1001", data.reference)
        self.assertEqual("2100000000000000", data.amount_received)
        self.assertIsInstance(data.amount_received, str)
        self.assertIsNone(event.forward)

    def test_forward_family_payload_uses_cryptapi_names(self):
        header = header_for(FORWARD_BODY, CURRENT, self.now)
        event = parse_webhook(FORWARD_BODY, header, [CURRENT], now=self.now)

        self.assertTrue(event.is_forward_event)
        data = event.forward
        self.assertEqual("11111111-2222-4333-8444-555555555555", data.forward_id)
        self.assertEqual("bc1qdeposit", data.address_in)
        self.assertEqual("bc1qmywallet", data.address_out)
        self.assertEqual("149000", data.value_forwarded)
        self.assertEqual("1000", data.fee)
        self.assertFalse(data.pending)
        self.assertIsNone(event.invoice)

    def test_sweep_sent_and_test_carry_the_invoice_shape(self):
        for name in ("sweep.sent", "test"):
            event = WebhookEvent.from_dict({"event": name, "sequence": 1, "data": {"id": "x"}})
            self.assertTrue(event.is_invoice_event, name)
            self.assertIsNotNone(event.invoice)

    def test_an_unknown_event_name_does_not_raise(self):
        # Receivers MUST ignore names they do not recognise; adding one is additive.
        event = WebhookEvent.from_dict({"event": "invoice.teleported", "sequence": 5, "data": {}})
        self.assertEqual("invoice.teleported", event.event)
        self.assertEqual(5, event.sequence)

    def test_unknown_fields_survive_on_raw(self):
        payload = {"event": "invoice.paid", "sequence": 1, "data": {"id": "x"}, "future_field": 42}
        event = WebhookEvent.from_dict(payload)
        self.assertEqual(42, event.raw["future_field"])

    def test_the_legacy_pre_envelope_shape_is_tolerated(self):
        event = WebhookEvent.from_dict({"event": "invoice.paid", "invoice": {"id": "legacy"}})
        self.assertEqual("legacy", event.data["id"])


class ClientWebhookTests(unittest.TestCase):
    def test_client_uses_its_configured_secret_list(self):
        now = int(time.time())
        client = Receivify(
            "https://receivify.io",
            "rcv_live_k",
            webhook_secrets=[CURRENT, PREVIOUS],
            transport=FakeTransport([]),
        )
        self.assertTrue(client.verify_webhook(RAW_BODY, header_for(RAW_BODY, CURRENT, now)))
        self.assertTrue(client.verify_webhook(RAW_BODY, header_for(RAW_BODY, PREVIOUS, now)))
        self.assertFalse(client.verify_webhook(RAW_BODY, header_for(RAW_BODY, "whsec_other", now)))

    def test_explicit_secrets_override_the_configured_ones(self):
        now = int(time.time())
        client = Receivify(
            "https://receivify.io", "rcv_live_k", webhook_secrets=[CURRENT], transport=FakeTransport([])
        )
        header = header_for(RAW_BODY, "whsec_other", now)
        self.assertFalse(client.verify_webhook(RAW_BODY, header))
        self.assertTrue(client.verify_webhook(RAW_BODY, header, ["whsec_other"]))

    def test_single_webhook_secret_kwarg_is_supported(self):
        now = int(time.time())
        client = Receivify(
            "https://receivify.io", "rcv_live_k", webhook_secret=CURRENT, transport=FakeTransport([])
        )
        self.assertTrue(client.verify_webhook(RAW_BODY, header_for(RAW_BODY, CURRENT, now)))

    def test_client_parse_webhook_returns_an_event(self):
        now = int(time.time())
        client = Receivify(
            "https://receivify.io", "rcv_live_k", webhook_secrets=[CURRENT], transport=FakeTransport([])
        )
        event = client.parse_webhook(RAW_BODY, header_for(RAW_BODY, CURRENT, now))
        self.assertEqual("invoice.paid", event.event)

    def test_reserialised_json_is_exactly_what_breaks_verification(self):
        # The documented #1 integration bug, pinned as a test so the README stays honest.
        now = int(time.time())
        header = header_for(RAW_BODY, CURRENT, now)
        reserialised = json.dumps(json.loads(RAW_BODY.decode("utf-8")), indent=2).encode("utf-8")
        self.assertTrue(verify_webhook(RAW_BODY, header, [CURRENT], now=now))
        self.assertFalse(verify_webhook(reserialised, header, [CURRENT], now=now))

    def test_signature_header_name_is_exported(self):
        self.assertEqual("X-Receivify-Signature", SIGNATURE_HEADER)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
