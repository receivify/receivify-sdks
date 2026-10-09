"""Error-code mapping: known codes get a class, unknown codes must not crash."""

from __future__ import annotations

import unittest

from receivify import (
    ERROR_CODES,
    AlreadyExistsError,
    AmountTooLowError,
    BadTickerError,
    NotAllowedError,
    NotFoundError,
    RateLimitedError,
    RateUnavailableError,
    ReceivifyApiError,
    ReceivifyError,
    UnauthorizedError,
    UpstreamError,
    error_class_for_code,
)

from .support import INVOICE, json_response, make_client


class ErrorMappingTests(unittest.TestCase):
    def test_a_known_code_raises_its_own_exception_class(self):
        client, _ = make_client(
            [json_response({"error": "not_found", "detail": "No such invoice."}, status=404)]
        )
        with self.assertRaises(NotFoundError) as ctx:
            client.get_invoice(INVOICE["id"])

        error = ctx.exception
        self.assertEqual("not_found", error.code)
        self.assertEqual("No such invoice.", error.detail)
        self.assertEqual(404, error.status)
        self.assertIsInstance(error, ReceivifyApiError)
        self.assertIsInstance(error, ReceivifyError)

    def test_an_unknown_code_falls_back_to_the_base_class_with_the_raw_code(self):
        # A code added to the API after this SDK shipped must degrade, not explode.
        client, _ = make_client(
            [json_response({"error": "quantum_flux", "detail": "Something new."}, status=400)]
        )
        with self.assertRaises(ReceivifyApiError) as ctx:
            client.get_invoice(INVOICE["id"])

        error = ctx.exception
        self.assertIs(ReceivifyApiError, type(error), "must not be silently coerced to a subclass")
        self.assertEqual("quantum_flux", error.code)
        self.assertEqual("Something new.", error.detail)

    def test_a_body_that_is_not_an_error_object_still_produces_a_usable_exception(self):
        client, _ = make_client(
            [json_response(status=502, body="<html>bad gateway</html>")], max_retries=0
        )
        with self.assertRaises(ReceivifyApiError) as ctx:
            client.get_invoice(INVOICE["id"])

        error = ctx.exception
        self.assertIsNone(error.code)
        self.assertEqual(502, error.status)
        self.assertIn("bad gateway", error.body or "")

    def test_every_frozen_code_from_the_spec_has_a_class(self):
        # The enum in openapi.yaml -> components.schemas.Error.error, in order.
        frozen = [
            "bad_request",
            "bad_ticker",
            "bad_destination",
            "asset_unavailable",
            "amount_too_low",
            "suspended",
            "limit_exceeded",
            "invalid_code",
            "no_setup",
            "not_enabled",
            "no_target",
            "invalid_token",
            "unauthorized",
            "bad_signature",
            "signup_disabled",
            "not_found",
            "already_exists",
            "already_enabled",
            "not_allowed",
            "rate_limited",
            "internal",
            "upstream_error",
            "rate_unavailable",
        ]
        self.assertEqual(sorted(frozen), sorted(ERROR_CODES))
        for code in frozen:
            self.assertIsNot(ReceivifyApiError, error_class_for_code(code), code)

    def test_error_class_for_an_unknown_or_missing_code(self):
        self.assertIs(ReceivifyApiError, error_class_for_code("nope"))
        self.assertIs(ReceivifyApiError, error_class_for_code(None))
        self.assertIs(ReceivifyApiError, error_class_for_code(""))

    def test_retryable_flags_match_the_spec(self):
        self.assertTrue(RateLimitedError("rate_limited").retryable)
        self.assertTrue(UpstreamError("upstream_error").retryable)
        self.assertTrue(RateUnavailableError("rate_unavailable").retryable)
        self.assertFalse(NotFoundError("not_found").retryable)
        self.assertFalse(AmountTooLowError("amount_too_low").retryable)

    def test_rate_limit_headers_are_readable_case_insensitively(self):
        client, _ = make_client(
            [
                json_response(
                    {"error": "rate_limited", "detail": "Slow down."},
                    status=429,
                    headers={"retry-after": "12", "x-ratelimit-reset": "1780000000"},
                )
            ],
            max_retries=0,
        )
        with self.assertRaises(RateLimitedError) as ctx:
            client.get_invoice(INVOICE["id"])
        self.assertEqual(12, ctx.exception.retry_after)
        self.assertEqual(1780000000, ctx.exception.rate_limit_reset)

    def test_the_message_carries_code_status_and_detail(self):
        error = BadTickerError("bad_ticker", "Unknown ticker btk; did you mean btc?", status=400)
        self.assertIn("bad_ticker", str(error))
        self.assertIn("400", str(error))
        self.assertIn("did you mean btc?", str(error))

    def test_409s_are_distinguishable_by_class(self):
        client, _ = make_client(
            [
                json_response({"error": "not_allowed", "detail": "Paused."}, status=409),
                json_response({"error": "already_exists", "detail": "Collides."}, status=409),
            ]
        )
        with self.assertRaises(NotAllowedError):
            client.create_forward(ticker="btc", destination="bc1q")
        with self.assertRaises(AlreadyExistsError):
            client.create_forward(ticker="btc", destination="bc1q")

    def test_unauthorized_is_typed_so_a_bad_key_is_obvious(self):
        client, _ = make_client(
            [json_response({"error": "unauthorized", "detail": "Unknown API key."}, status=401)]
        )
        with self.assertRaises(UnauthorizedError):
            client.list_invoices()

    def test_an_error_repr_does_not_leak_the_request(self):
        error = NotFoundError("not_found", "No such invoice.", status=404)
        self.assertEqual(
            "NotFoundError(code='not_found', status=404, detail='No such invoice.')", repr(error)
        )


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
