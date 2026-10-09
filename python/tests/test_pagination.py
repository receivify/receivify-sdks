"""The list envelope, the auto-paginator, and the retry policy that guards mutations."""

from __future__ import annotations

import unittest
import urllib.parse

from receivify import Invoice, Page, ReceivifyError, auto_paginate
from receivify.errors import ReceivifyTransportError

from .support import INVOICE, json_response, make_client


def envelope(items, has_more=False, next_cursor=None):
    return {"data": items, "has_more": has_more, "next_cursor": next_cursor}


def invoice(identifier):
    payload = dict(INVOICE)
    payload["id"] = identifier
    return payload


class ListEnvelopeTests(unittest.TestCase):
    def test_list_returns_the_envelope_not_a_bare_array(self):
        client, _ = make_client([json_response(envelope([invoice("a"), invoice("b")], True, "cur_2"))])
        page = client.list_invoices(limit=2)

        self.assertIsInstance(page, Page)
        self.assertEqual(2, len(page))
        self.assertTrue(page.has_more)
        self.assertEqual("cur_2", page.next_cursor)
        self.assertIsInstance(page[0], Invoice)
        self.assertEqual(["a", "b"], [i.id for i in page])

    def test_empty_page_still_carries_has_more_and_next_cursor(self):
        client, _ = make_client([json_response(envelope([]))])
        page = client.list_invoices()
        self.assertEqual([], page.data)
        self.assertFalse(page.has_more)
        self.assertIsNone(page.next_cursor)

    def test_has_more_is_read_never_inferred_from_page_length(self):
        # A full page with has_more=False must not trigger another request.
        client, transport = make_client([json_response(envelope([invoice("a"), invoice("b")], False))])
        self.assertEqual(["a", "b"], [i.id for i in client.iter_invoices(limit=2)])
        self.assertEqual(1, len(transport.requests))


class AutoPaginationTests(unittest.TestCase):
    def test_iter_follows_next_cursor_until_has_more_is_false(self):
        client, transport = make_client(
            [
                json_response(envelope([invoice("a"), invoice("b")], True, "cur_2")),
                json_response(envelope([invoice("c")], True, "cur_3")),
                json_response(envelope([invoice("d")], False, None)),
            ]
        )

        ids = [inv.id for inv in client.iter_invoices(limit=2, status="pending")]

        self.assertEqual(["a", "b", "c", "d"], ids)
        self.assertEqual(3, len(transport.requests))
        queries = [urllib.parse.parse_qs(urllib.parse.urlparse(r.url).query) for r in transport.requests]
        self.assertNotIn("cursor", queries[0])
        self.assertEqual(["cur_2"], queries[1]["cursor"])
        self.assertEqual(["cur_3"], queries[2]["cursor"])
        # Filters carry across every page, or page 2 would be a different query.
        for query in queries:
            self.assertEqual(["pending"], query["status"])
            self.assertEqual(["2"], query["limit"])

    def test_iter_is_lazy(self):
        client, transport = make_client(
            [json_response(envelope([invoice("a")], True, "cur_2")), json_response(envelope([invoice("b")]))]
        )
        stream = client.iter_invoices()
        self.assertEqual(0, len(transport.requests))
        self.assertEqual("a", next(stream).id)
        self.assertEqual(1, len(transport.requests))

    def test_has_more_without_a_cursor_stops_instead_of_looping(self):
        client, transport = make_client([json_response(envelope([invoice("a")], True, None))])
        self.assertEqual(["a"], [i.id for i in client.iter_invoices()])
        self.assertEqual(1, len(transport.requests))

    def test_a_repeated_cursor_raises_rather_than_spinning_forever(self):
        client, _ = make_client(
            [
                json_response(envelope([invoice("a")], True, "same")),
                json_response(envelope([invoice("b")], True, "same")),
            ]
        )
        with self.assertRaises(ReceivifyError):
            list(client.iter_invoices())

    def test_auto_paginate_works_with_any_page_returning_callable(self):
        pages = [
            Page(data=[1, 2], has_more=True, next_cursor="c2"),
            Page(data=[3], has_more=False, next_cursor=None),
        ]
        seen = []

        def fetch(cursor=None):
            seen.append(cursor)
            return pages.pop(0)

        self.assertEqual([1, 2, 3], list(auto_paginate(fetch)))
        self.assertEqual([None, "c2"], seen)

    def test_forwards_paginate_too(self):
        from .support import FORWARD

        first = dict(FORWARD)
        second = dict(FORWARD)
        second["id"] = "22222222-2222-4333-8444-555555555555"
        client, _ = make_client(
            [
                json_response(envelope([first], True, "cur_2")),
                json_response(envelope([second], False, None)),
            ]
        )
        self.assertEqual(2, len(list(client.iter_forwards(ticker="btc"))))


class RetryPolicyTests(unittest.TestCase):
    def test_a_get_is_retried_on_429_and_honours_retry_after(self):
        client, transport = make_client(
            [
                json_response({"error": "rate_limited", "detail": "Slow down."}, status=429,
                              headers={"Retry-After": "7"}),
                json_response(INVOICE),
            ]
        )
        invoice_obj = client.get_invoice(INVOICE["id"])

        self.assertEqual(INVOICE["id"], invoice_obj.id)
        self.assertEqual(2, len(transport.requests))
        self.assertGreaterEqual(client.slept[0], 7.0)

    def test_a_get_is_retried_on_500(self):
        client, transport = make_client(
            [json_response({"error": "internal", "detail": "Unexpected error."}, status=500),
             json_response(INVOICE)]
        )
        client.get_invoice(INVOICE["id"])
        self.assertEqual(2, len(transport.requests))

    def test_a_post_without_an_idempotency_key_is_never_retried(self):
        # This is the double-charge guard: a blind retry could mint a second invoice.
        client, transport = make_client(
            [json_response({"error": "internal", "detail": "Unexpected error."}, status=500)]
        )
        with self.assertRaises(ReceivifyError):
            client.create_invoice(amount_sats="1000")
        self.assertEqual(1, len(transport.requests))
        self.assertEqual([], client.slept)

    def test_a_post_with_an_idempotency_key_is_retried(self):
        client, transport = make_client(
            [
                json_response({"error": "upstream_error", "detail": "Node down."}, status=502),
                json_response(INVOICE, status=201),
            ]
        )
        client.create_invoice(amount_sats="1000", idempotency_key="attempt-1")
        self.assertEqual(2, len(transport.requests))
        self.assertEqual(
            "attempt-1", transport.requests[1].headers["Idempotency-Key"], "the key must survive the retry"
        )

    def test_retries_are_bounded_by_max_retries(self):
        error = json_response({"error": "internal", "detail": "Unexpected error."}, status=500)
        client, transport = make_client([error, error, error, error], max_retries=2)
        with self.assertRaises(ReceivifyError):
            client.get_invoice(INVOICE["id"])
        self.assertEqual(3, len(transport.requests))  # 1 attempt + 2 retries

    def test_a_transport_failure_on_a_get_is_retried(self):
        client, transport = make_client(
            [ReceivifyTransportError("connection reset"), json_response(INVOICE)]
        )
        client.get_invoice(INVOICE["id"])
        self.assertEqual(2, len(transport.requests))

    def test_a_400_is_never_retried(self):
        client, transport = make_client(
            [json_response({"error": "bad_request", "detail": "Nope."}, status=400)]
        )
        with self.assertRaises(ReceivifyError):
            client.get_invoice(INVOICE["id"])
        self.assertEqual(1, len(transport.requests))


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
