"""Resource decoding: canonical ids, forward compatibility, and shown-once secrets."""

from __future__ import annotations

import unittest

from receivify import (
    ApiKeyCreated,
    Forward,
    Invoice,
    PayoutState,
    Ticker,
    WebhookDelivery,
    WebhookSecretRotated,
)

from .support import FORWARD, INVOICE, json_response, make_client


class ForwardIdentityTests(unittest.TestCase):
    def test_id_is_canonical_and_forward_id_is_a_deprecated_alias(self):
        forward = Forward.from_dict(FORWARD)
        self.assertEqual(FORWARD["id"], forward.id)
        self.assertEqual(forward.id, forward.forward_id)

    def test_id_wins_when_the_two_ever_disagree(self):
        payload = dict(FORWARD)
        payload["forward_id"] = "stale-value"
        self.assertEqual(FORWARD["id"], Forward.from_dict(payload).id)

    def test_a_payload_carrying_only_the_legacy_key_still_resolves(self):
        payload = dict(FORWARD)
        del payload["id"]
        self.assertEqual(FORWARD["forward_id"], Forward.from_dict(payload).id)

    def test_the_dataclass_field_list_does_not_include_forward_id(self):
        # It is a property, not a field: the canonical identifier is `id`, once.
        import dataclasses

        names = {f.name for f in dataclasses.fields(Forward)}
        self.assertIn("id", names)
        self.assertNotIn("forward_id", names)


class ForwardCompatibilityTests(unittest.TestCase):
    def test_unknown_response_fields_are_preserved_on_raw(self):
        payload = dict(INVOICE)
        payload["settlement_layer"] = "future"
        invoice = Invoice.from_dict(payload)
        self.assertEqual("future", invoice.raw["settlement_layer"])

    def test_missing_fields_do_not_raise(self):
        invoice = Invoice.from_dict({"id": "only-an-id"})
        self.assertEqual("only-an-id", invoice.id)
        self.assertEqual("0", invoice.amount_expected)
        self.assertIsNone(invoice.paid_at)
        self.assertEqual({}, invoice.metadata)

    def test_metadata_is_always_a_dict_never_none(self):
        payload = dict(INVOICE)
        payload["metadata"] = None
        self.assertEqual({}, Invoice.from_dict(payload).metadata)

    def test_an_unknown_status_is_carried_through_not_rejected(self):
        payload = dict(INVOICE)
        payload["status"] = "quantum_settled"
        self.assertEqual("quantum_settled", Invoice.from_dict(payload).status)

    def test_is_settled_covers_paid_and_swept(self):
        for status, expected in (("paid", True), ("swept", True), ("confirming", False)):
            payload = dict(INVOICE)
            payload["status"] = status
            self.assertEqual(expected, Invoice.from_dict(payload).is_settled, status)


class ShownOnceSecretTests(unittest.TestCase):
    def test_api_key_repr_never_prints_the_secret(self):
        created = ApiKeyCreated.from_dict(
            {"id": "key_1", "api_key": "rcv_live_supersecret", "prefix": "rcv_live_abc", "message": "ok"}
        )
        self.assertNotIn("supersecret", repr(created))
        self.assertEqual("rcv_live_supersecret", created.api_key)
        self.assertFalse(created.is_replay)

    def test_a_replay_returns_a_null_secret_and_is_flagged(self):
        created = ApiKeyCreated.from_dict(
            {"id": "key_1", "api_key": None, "api_key_prefix": "rcv_live_abc", "message": "replayed"}
        )
        self.assertIsNone(created.api_key)
        self.assertTrue(created.is_replay)
        # The prefix still identifies what was created, so it can still be revoked.
        self.assertEqual("rcv_live_abc", created.prefix)

    def test_rotated_secret_repr_never_prints_the_secret(self):
        rotated = WebhookSecretRotated.from_dict(
            {
                "secret": "whsec_brandnew",
                "rotated_at": "2026-08-25T11:00:00.000Z",
                "prev_expires_at": "2026-08-26T11:00:00.000Z",
                "grace_seconds": 86400,
                "replaced_previous": False,
                "message": "Store it now.",
            }
        )
        self.assertNotIn("brandnew", repr(rotated))
        self.assertEqual(86400, rotated.grace_seconds)
        self.assertFalse(rotated.is_replay)

    def test_rotation_replay_has_no_secret(self):
        rotated = WebhookSecretRotated.from_dict({"secret": None, "rotated_at": "x"})
        self.assertTrue(rotated.is_replay)


class MiscResourceTests(unittest.TestCase):
    def test_ticker_decimals_is_an_int_for_display_maths(self):
        ticker = Ticker.from_dict(
            {"ticker": "usdc_base", "provider": "evm", "chain": "base", "name": "USD Coin", "decimals": 6}
        )
        self.assertEqual(6, ticker.decimals)
        self.assertEqual("base", ticker.chain)

    def test_delivery_rows_keep_both_nullable_ids(self):
        row = WebhookDelivery.from_dict(
            {
                "id": "a" * 24,
                "invoice_id": None,
                "forward_id": "11111111-2222-4333-8444-555555555555",
                "event": "forward.sent",
                "status": "failed",
                "attempts": 3,
                "url": "https://shop.example/hooks",
                "last_status": 500,
                "last_error": "boom",
            }
        )
        self.assertIsNone(row.invoice_id)
        self.assertEqual("11111111-2222-4333-8444-555555555555", row.forward_id)
        self.assertEqual(500, row.last_status)

    def test_payout_state_decodes_the_live_map_and_open_changes(self):
        state = PayoutState.from_dict(
            {
                "payout_addresses": {"btc": "bc1qlive"},
                "pending": [
                    {"id": "chg_1", "asset": "btc", "new_address": "bc1qnew", "status": "confirmed"}
                ],
                "timelock_hours": 24,
                "email_confirmation": True,
                "instant": False,
            }
        )
        self.assertEqual({"btc": "bc1qlive"}, state.payout_addresses)
        self.assertEqual(1, len(state.pending))
        self.assertEqual("bc1qnew", state.pending[0].new_address)
        self.assertFalse(state.instant)

    def test_tickers_come_back_in_a_one_page_envelope(self):
        client, _ = make_client(
            [
                json_response(
                    {
                        "data": [
                            {"ticker": "btc", "provider": "bitcoind", "chain": None, "name": "Bitcoin", "decimals": 8}
                        ],
                        "has_more": False,
                        "next_cursor": None,
                    }
                )
            ]
        )
        page = client.list_tickers()
        self.assertFalse(page.has_more)
        self.assertIsNone(page.next_cursor)
        self.assertEqual(8, page[0].decimals)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
