"""Opt-in ``Decimal`` helpers for base-unit amounts.

**Nothing in this module is imported for you.**  Every amount the SDK hands back is a
string and stays a string; converting is a decision you make per call site::

    from receivify import money

    btc = money.to_decimal(invoice.amount_expected, 8)   # Decimal('0.00042000')
    sats = money.to_base_units("0.00042", 8)             # '42000'

Why not just ``float(amount) / 10 ** decimals``?  Because a wei value has up to 78
significant digits and an IEEE-754 double has about 15.  ``float("1000000000000000001")``
is already wrong before you divide.  Even ``Decimal(x) / 10 ** decimals`` rounds at the
context precision (28 digits by default), so the conversions here are done by moving the
decimal point in the digit string -- exact for any magnitude, no context to configure.

``decimals`` comes from ``GET /api/v1/tickers`` (``Ticker.decimals``).
"""

from __future__ import annotations

from decimal import Decimal
from typing import Union

__all__ = ["to_decimal", "to_base_units", "format_amount"]


def _split_sign(text: str) -> tuple:
    text = text.strip()
    if text.startswith("-"):
        return ("-", text[1:])
    if text.startswith("+"):
        return ("", text[1:])
    return ("", text)


def to_decimal(base_units: Union[str, int], decimals: int) -> Decimal:
    """Convert an exact-integer base-unit string into a display-unit :class:`Decimal`.

    >>> to_decimal("42000", 8)
    Decimal('0.00042000')
    >>> to_decimal("1000000000000000001", 18)
    Decimal('1.000000000000000001')

    :raises ValueError: ``base_units`` is not an integer string, or ``decimals`` is
        negative.  Never guesses.
    """
    if decimals < 0:
        raise ValueError("decimals must be >= 0, got {!r}".format(decimals))
    sign, digits = _split_sign(str(base_units))
    if not digits.isdigit():
        raise ValueError(
            "Expected an exact-integer base-unit string, got {!r}. Decimal display "
            "amounts (amount_fiat, min_forward, amount_btc) are already display units.".format(base_units)
        )
    if decimals == 0:
        return Decimal(sign + digits)
    digits = digits.rjust(decimals + 1, "0")
    return Decimal("{}{}.{}".format(sign, digits[:-decimals], digits[-decimals:]))


def to_base_units(amount: Union[str, Decimal, int], decimals: int) -> str:
    """Convert a display-unit amount into an exact-integer base-unit string.

    >>> to_base_units("0.00042", 8)
    '42000'

    :param amount: A decimal string, a :class:`~decimal.Decimal`, or an int.  A ``float``
        is rejected -- by the time it reaches here the value may already be wrong.
    :raises ValueError: the amount carries more precision than ``decimals`` allows, i.e.
        converting it would silently discard money.
    """
    if decimals < 0:
        raise ValueError("decimals must be >= 0, got {!r}".format(decimals))
    if isinstance(amount, float):
        raise TypeError(
            "Refusing to convert a float: pass a decimal string (or a Decimal) so the "
            "value is not already rounded before it gets here."
        )
    text = format(amount, "f") if isinstance(amount, Decimal) else str(amount).strip()
    sign, rest = _split_sign(text)
    whole, _, fraction = rest.partition(".")
    whole = whole or "0"
    if not whole.isdigit() or (fraction and not fraction.isdigit()):
        raise ValueError("Expected a decimal amount, got {!r}".format(amount))
    if len(fraction) > decimals:
        if fraction[decimals:].strip("0"):
            raise ValueError(
                "{!r} has more precision than {} decimals can represent; rounding here "
                "would lose money.".format(amount, decimals)
            )
        fraction = fraction[:decimals]
    combined = (whole + fraction.ljust(decimals, "0")).lstrip("0") or "0"
    return ("-" + combined) if (sign and combined != "0") else combined


def format_amount(base_units: Union[str, int], decimals: int, *, trim: bool = False) -> str:
    """Render base units for display.

    >>> format_amount("42000", 8)
    '0.00042000'
    >>> format_amount("42000", 8, trim=True)
    '0.00042'
    """
    text = format(to_decimal(base_units, decimals), "f")
    if trim and "." in text:
        text = text.rstrip("0").rstrip(".")
    return text or "0"
