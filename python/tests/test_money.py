"""Money stays a string, end to end -- and the opt-in Decimal helpers are exact."""

from __future__ import annotations

import unittest
from decimal import Decimal

from receivify import money

from .support import FORWARD, INVOICE, json_response, make_client


class MoneyStaysAStringTests(unittest.TestCase):
    def test_base_unit_amounts_are_never_parsed_into_numbers(self):
        client, _ = make_client([json_response(INVOICE)])
        invoice = client.get_invoice(INVOICE["id"])

        for value in (invoice.amount_expected, invoice.amount_received):
            self.assertIsInstance(value, str)
            self.assertNotIsInstance(value, float)
        # 2.1e15 satoshis survives byte for byte; float(x) would already have lost it.
        self.assertEqual("2100000000000000", invoice.amount_expected)

    def test_a_wei_scale_value_round_trips_untouched(self):
        payload = dict(INVOICE)
        payload["amount_expected"] = "123456789012345678901234567890"
        client, _ = make_client([json_response(payload)])
        self.assertEqual(
            "123456789012345678901234567890", client.get_invoice(INVOICE["id"]).amount_expected
        )

    def test_fiat_amounts_are_decimal_strings_not_floats(self):
        client, _ = make_client([json_response(INVOICE)])
        invoice = client.get_invoice(INVOICE["id"])
        self.assertEqual("25.00", invoice.amount_fiat)
        self.assertIsInstance(invoice.amount_fiat, str)

    def test_min_forward_is_a_display_unit_string(self):
        client, _ = make_client([json_response(FORWARD)])
        forward = client.get_forward(FORWARD["id"])
        self.assertEqual("0.000100", forward.min_forward)
        self.assertIsInstance(forward.min_forward, str)

    def test_a_null_fiat_amount_stays_none_not_zero(self):
        payload = dict(INVOICE)
        payload["amount_fiat"] = None
        payload["fiat_currency"] = None
        client, _ = make_client([json_response(payload)])
        self.assertIsNone(client.get_invoice(INVOICE["id"]).amount_fiat)

    def test_a_stray_json_number_is_stringified_in_plain_decimal_notation(self):
        # The API never sends a money field as a number, but a proxy or a future field
        # might. It must arrive as a plain decimal string -- not '1e+16', which no
        # downstream ledger will accept.
        for wire_value, expected in ((25.5, "25.5"), (1e16, "10000000000000000")):
            payload = dict(INVOICE)
            payload["amount_fiat"] = wire_value
            client, _ = make_client([json_response(payload)])
            self.assertEqual(expected, client.get_invoice(INVOICE["id"]).amount_fiat)


class ToDecimalTests(unittest.TestCase):
    def test_satoshis_to_btc(self):
        self.assertEqual(Decimal("0.00042000"), money.to_decimal("42000", 8))
        self.assertEqual(Decimal("21000000.00000000"), money.to_decimal("2100000000000000", 8))

    def test_wei_conversion_is_exact_beyond_double_precision(self):
        # 1.000000000000000001 ETH: 19 significant digits, past what a float can hold and
        # past the default Decimal context precision of 28 if you divided instead.
        self.assertEqual(
            Decimal("1.000000000000000001"), money.to_decimal("1000000000000000001", 18)
        )

    def test_zero_decimals_is_the_identity(self):
        self.assertEqual(Decimal("42"), money.to_decimal("42", 0))

    def test_negative_amounts_are_supported(self):
        self.assertEqual(Decimal("-0.00042000"), money.to_decimal("-42000", 8))

    def test_a_non_integer_string_is_refused_rather_than_guessed(self):
        with self.assertRaises(ValueError):
            money.to_decimal("0.00042", 8)  # a display amount, not base units
        with self.assertRaises(ValueError):
            money.to_decimal("", 8)

    def test_negative_decimals_are_refused(self):
        with self.assertRaises(ValueError):
            money.to_decimal("42000", -1)


class ToBaseUnitsTests(unittest.TestCase):
    def test_btc_to_satoshis(self):
        self.assertEqual("42000", money.to_base_units("0.00042", 8))
        self.assertEqual("2100000000000000", money.to_base_units("21000000", 8))

    def test_zero(self):
        self.assertEqual("0", money.to_base_units("0", 8))
        self.assertEqual("0", money.to_base_units("0.00000000", 8))

    def test_negative(self):
        self.assertEqual("-42000", money.to_base_units("-0.00042", 8))

    def test_decimal_input_is_accepted(self):
        self.assertEqual("42000", money.to_base_units(Decimal("0.00042"), 8))

    def test_a_float_is_refused(self):
        with self.assertRaises(TypeError):
            money.to_base_units(0.00042, 8)

    def test_excess_precision_raises_instead_of_rounding_money_away(self):
        with self.assertRaises(ValueError):
            money.to_base_units("0.000000001", 8)
        # Trailing zeros beyond the precision are not a loss, so they are fine.
        self.assertEqual("42000", money.to_base_units("0.000420000", 8))

    def test_round_trip(self):
        for base, decimals in (("42000", 8), ("1", 18), ("1000000000000000001", 18), ("0", 6)):
            self.assertEqual(base, money.to_base_units(money.to_decimal(base, decimals), decimals))


class FormatAmountTests(unittest.TestCase):
    def test_pads_to_the_full_precision_by_default(self):
        self.assertEqual("0.00042000", money.format_amount("42000", 8))

    def test_trim_drops_trailing_zeros(self):
        self.assertEqual("0.00042", money.format_amount("42000", 8, trim=True))
        self.assertEqual("1", money.format_amount("100000000", 8, trim=True))

    def test_money_helpers_are_not_exported_at_the_top_level(self):
        # Opt-in and explicit: `from receivify import money`, never an implicit import.
        import receivify

        self.assertNotIn("to_decimal", dir(receivify))
        self.assertNotIn("to_base_units", dir(receivify))


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
