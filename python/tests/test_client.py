"""How the client builds requests, and what it refuses to do."""

from __future__ import annotations

import unittest
import urllib.parse

from receivify import Forward, Invoice, Receivify, ReceivifyError, __version__
from receivify.errors import ReceivifyTransportError

from .support import FORWARD, INVOICE, FakeTransport, json_response, make_client


class RequestBuildingTests(unittest.TestCase):
    def test_create_invoice_sends_bearer_key_json_body_and_user_agent(self):
        client, transport = make_client([json_response(INVOICE, status=201)])

        invoice = client.create_invoice(
            amount_fiat="25.00", currency="usd", reference="order-1001", metadata={"cart": "abc"}
        )

        request = transport.last_request
        self.assertEqual("POST", request.method)
        self.assertEqual("https://receivify.io/api/v1/invoices", request.url)
        self.assertEqual("Bearer rcv_live_secretkey", request.headers["Authorization"])
        self.assertEqual("application/json", request.headers["Content-Type"])
        self.assertEqual("receivify-python/{}".format(__version__), request.headers["User-Agent"])
        self.assertEqual(
            {
                "amount_fiat": "25.00",
                "currency": "USD",
                "reference": "order-1001",
                "metadata": {"cart": "abc"},
            },
            transport.body_of(),
        )
        self.assertIsInstance(invoice, Invoice)
        self.assertEqual("order-1001", invoice.reference)

    def test_add_fee_ontop_is_sent_only_when_given_and_must_be_a_bool(self):
        client, transport = make_client([json_response(INVOICE, status=201)] * 2)
        client.create_invoice(amount_sats="1000")
        self.assertNotIn("add_fee_ontop", transport.body_of())
        client.create_invoice(amount_sats="1000", add_fee_ontop=False)
        self.assertIs(False, transport.body_of()["add_fee_ontop"])
        with self.assertRaises(TypeError):
            client.create_invoice(amount_sats="1000", add_fee_ontop="true")

    def test_invoice_exposes_checkout_and_fee_fields(self):
        payload = dict(INVOICE, decimals=8, amount_decimal="0.00100000", payment_uri="bitcoin:bc1q?amount=0.001",
                       add_fee_ontop=True, amount_net_requested="99000", fee_ontop="1000")
        client, _ = make_client([json_response(payload)])
        invoice = client.get_invoice(INVOICE["id"])
        self.assertEqual(8, invoice.decimals)
        self.assertEqual("0.00100000", invoice.amount_decimal)
        self.assertTrue(invoice.add_fee_ontop)
        self.assertEqual("99000", invoice.amount_net_requested)
        self.assertEqual("1000", invoice.fee_ontop)

    def test_api_key_never_appears_in_a_url(self):
        client, transport = make_client(
            [json_response(INVOICE), json_response({"data": [], "has_more": False, "next_cursor": None})]
        )
        client.get_invoice(INVOICE["id"])
        client.list_invoices(limit=10, status="pending")

        for request in transport.requests:
            self.assertNotIn("rcv_live", request.url)

    def test_repr_masks_the_api_key(self):
        client, _ = make_client()
        self.assertNotIn("secretkey", repr(client))
        self.assertIn("rcv_live_sec", repr(client))

    def test_request_repr_redacts_authorization(self):
        client, transport = make_client([json_response(INVOICE)])
        client.get_invoice(INVOICE["id"])
        self.assertNotIn("rcv_live_secretkey", repr(transport.last_request))
        self.assertIn("<redacted>", repr(transport.last_request))

    def test_idempotency_key_is_sent_as_a_header(self):
        client, transport = make_client([json_response(INVOICE, status=201)])
        client.create_invoice(amount_sats="42000", idempotency_key="order-1001-attempt-1")
        self.assertEqual("order-1001-attempt-1", transport.last_request.headers["Idempotency-Key"])

    def test_query_params_skip_none_and_encode_booleans(self):
        client, transport = make_client(
            [json_response({"data": [], "has_more": False, "next_cursor": None})]
        )
        client.list_payment_links(limit=25, cursor=None, active=True)

        query = urllib.parse.parse_qs(urllib.parse.urlparse(transport.last_request.url).query)
        self.assertEqual({"limit": ["25"], "active": ["true"]}, query)

    def test_path_ids_are_percent_encoded(self):
        client, transport = make_client([json_response(INVOICE)])
        client.get_invoice("weird id/../../admin")
        self.assertTrue(
            transport.last_request.url.endswith("/api/v1/invoices/weird%20id%2F..%2F..%2Fadmin"),
            transport.last_request.url,
        )

    def test_public_endpoints_send_no_authorization_header(self):
        client, transport = make_client(
            [json_response({"data": [], "has_more": False, "next_cursor": None})]
        )
        client.list_tickers()
        self.assertNotIn("Authorization", dict(transport.last_request.headers))

    def test_trailing_slash_in_api_url_is_normalised(self):
        transport = FakeTransport([json_response(INVOICE)])
        client = Receivify("https://receivify.io/", "rcv_live_k", transport=transport)
        client.get_invoice(INVOICE["id"])
        self.assertNotIn("//api", transport.last_request.url.replace("https://", ""))

    def test_context_manager_leaves_an_injected_transport_alone(self):
        # An injected transport belongs to the caller; only one the client created is
        # ours to close.
        transport = FakeTransport([])
        with Receivify("https://receivify.io", "k", transport=transport) as client:
            self.assertIsInstance(client, Receivify)
        self.assertFalse(transport.closed)

    def test_context_manager_closes_a_transport_it_created(self):
        closed = []

        class OwnedTransport:
            def send(self, request):  # pragma: no cover - never sent
                raise AssertionError("no request expected")

            def close(self):
                closed.append(True)

        client = Receivify("https://receivify.io", "k")
        client._transport = OwnedTransport()  # still flagged as client-owned
        with client:
            pass
        self.assertEqual([True], closed)

    def test_timeout_is_carried_on_every_request(self):
        client, transport = make_client([json_response(INVOICE)], timeout=5.5)
        client.get_invoice(INVOICE["id"])
        self.assertEqual(5.5, transport.last_request.timeout)


class AmountGuardTests(unittest.TestCase):
    def test_float_amounts_are_refused_loudly(self):
        client, _ = make_client([])
        with self.assertRaises(TypeError) as ctx:
            client.create_invoice(amount_sats=0.1 + 0.2)
        self.assertIn("float", str(ctx.exception))

    def test_missing_amount_is_caught_before_the_request(self):
        client, transport = make_client([])
        with self.assertRaises(ValueError):
            client.create_invoice(reference="order-1")
        self.assertEqual([], transport.requests)

    def test_decimal_amount_is_serialised_without_exponent(self):
        from decimal import Decimal

        client, transport = make_client([json_response(INVOICE, status=201)])
        client.create_invoice(amount_fiat=Decimal("2.5E+1"), currency="USD")
        self.assertEqual("25", transport.body_of()["amount_fiat"])

    def test_int_base_units_are_stringified(self):
        client, transport = make_client([json_response(INVOICE, status=201)])
        client.create_invoice(amount_sats=42000)
        self.assertEqual("42000", transport.body_of()["amount_sats"])


class ForwardMethodTests(unittest.TestCase):
    def test_create_forward_posts_the_alias_tolerant_body(self):
        client, transport = make_client([json_response(FORWARD, status=201)])
        forward = client.create_forward(
            ticker="btc", destination="bc1qmywallet", callback="https://shop.example/hooks/receivify"
        )
        self.assertEqual("/api/v1/address", urllib.parse.urlparse(transport.last_request.url).path)
        self.assertEqual(
            {
                "ticker": "btc",
                "destination": "bc1qmywallet",
                "callback": "https://shop.example/hooks/receivify",
            },
            transport.body_of(),
        )
        self.assertIsInstance(forward, Forward)

    def test_address_aliases_point_at_the_same_methods(self):
        self.assertIs(Receivify.create_address, Receivify.create_forward)
        self.assertIs(Receivify.disable_address, Receivify.disable_forward)
        self.assertIs(Receivify.update_address, Receivify.update_forward)

    def test_update_forward_can_clear_the_callback_but_not_send_an_empty_patch(self):
        client, transport = make_client([json_response(FORWARD)])
        client.update_forward(FORWARD["id"], callback_url=None)
        self.assertEqual({"callback_url": None}, transport.body_of())

        with self.assertRaises(ValueError):
            client.update_forward(FORWARD["id"])

    def test_update_forward_omits_untouched_fields(self):
        client, transport = make_client([json_response(FORWARD)])
        client.update_forward(FORWARD["id"], status="paused")
        self.assertEqual({"status": "paused"}, transport.body_of())
        self.assertEqual("PATCH", transport.last_request.method)

    def test_get_forward_returns_the_detail_view_with_sub_collections(self):
        detail_payload = dict(FORWARD)
        detail_payload.update(
            {
                "payments": [
                    {
                        "txid_in": "aa" * 32,
                        "value": "150000",
                        "status": "sent",
                        "txid_out": "bb" * 32,
                        "confirmations": 3,
                        "created_at": "2026-08-25T11:30:00.000Z",
                    }
                ],
                "payments_has_more": True,
                "callbacks": [],
                "callbacks_has_more": False,
            }
        )
        client, _ = make_client([json_response(detail_payload)])
        detail = client.get_forward(FORWARD["id"])

        self.assertEqual(FORWARD["id"], detail.id)
        self.assertEqual(1, len(detail.payments))
        self.assertEqual("150000", detail.payments[0].value)
        self.assertTrue(detail.payments_has_more)


class BulkAndHealthTests(unittest.TestCase):
    def test_bulk_cancel_enforces_the_500_cap_client_side(self):
        client, transport = make_client([])
        with self.assertRaises(ValueError):
            client.bulk_cancel_invoices([])
        with self.assertRaises(ValueError):
            client.bulk_cancel_invoices(["id"] * 501)
        self.assertEqual([], transport.requests)

    def test_health_503_is_not_mapped_to_an_api_error(self):
        # GET /api/health documents itself as NOT conforming to {error, detail}: a 503
        # carries the same body as a 200. Raising a typed API error would be a lie.
        degraded = {"status": "degraded", "time": "2026-08-25T11:00:00.000Z", "checks": {}, "alerts": []}
        client, _ = make_client([json_response(degraded, status=503)])
        self.assertEqual("degraded", client.get_health()["status"])


class TransportErrorTests(unittest.TestCase):
    def test_non_json_body_raises_a_receivify_error_not_a_json_error(self):
        client, _ = make_client([json_response(status=200, body="<html>proxy</html>")])
        with self.assertRaises(ReceivifyError):
            client.get_invoice(INVOICE["id"])

    def test_transport_failure_on_a_post_without_idempotency_key_is_not_retried(self):
        boom = ReceivifyTransportError("connection reset")
        client, transport = make_client([boom])
        with self.assertRaises(ReceivifyTransportError):
            client.create_invoice(amount_sats="1000")
        self.assertEqual(1, len(transport.requests))


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
