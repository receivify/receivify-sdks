"""Webhook signature verification and the unified event envelope.

Header::

    X-Receivify-Signature: t=<unix seconds>,v1=<hex>

where ``v1 = HMAC-SHA256(secret, "{t}.{raw_body}")``.

Three things go wrong in the field, and this module is shaped around all three:

1. **Re-serialised JSON.**  Verify against the RAW request body bytes.  ``json.dumps()``
   of a parsed body reorders keys and changes whitespace, so the HMAC will never match.
   In Django that is ``request.body``; in Flask, ``request.get_data()``.  Never
   ``json.dumps(request.json)``.
2. **A single secret.**  ``POST /api/v1/webhooks/secret/rotate`` keeps the previous secret
   valid for a grace window, so during a rotation two secrets are legitimately live.
   :func:`verify_webhook` therefore takes a LIST (a bare string is accepted and
   normalised, so the single-secret case stays a one-liner).
3. **No replay window.**  No tolerance is enforced server-side.  ``tolerance_seconds``
   defaults to 300 here; a captured delivery replayed a day later fails.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import time
from dataclasses import dataclass, field
from typing import Any, Iterable, List, Mapping, Optional, Tuple, Union

from .errors import SignatureVerificationError, WebhookPayloadError
from .resources import _int, _money, _opt_str, _str

__all__ = [
    "SIGNATURE_HEADER",
    "DEFAULT_TOLERANCE_SECONDS",
    "WebhookEvent",
    "InvoiceEventData",
    "ForwardEventData",
    "verify_webhook",
    "parse_webhook",
    "parse_signature_header",
    "compute_signature",
]

#: The header Receivify signs every delivery with.
SIGNATURE_HEADER = "X-Receivify-Signature"

#: Default replay window, in seconds, applied to the ``t=`` component.
DEFAULT_TOLERANCE_SECONDS = 300

SecretsInput = Union[str, bytes, Iterable[Union[str, bytes]], None]
BodyInput = Union[str, bytes, bytearray]


# --- event payloads -------------------------------------------------------------------


@dataclass(frozen=True)
class InvoiceEventData:
    """``data`` for ``invoice.*``, ``sweep.sent`` and ``test``.

    A narrow projection: it carries NO ``asset`` and NO ``chain``, so a multi-coin
    merchant must call ``get_invoice(id)`` to learn which coin the event is about.
    """

    id: str = ""
    reference: Optional[str] = None
    status: str = ""
    #: Exact-integer base units, as a string.
    amount_expected: str = "0"
    #: Exact-integer base units, as a string.
    amount_received: str = "0"
    address: str = ""
    paid_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "InvoiceEventData":
        return cls(
            id=_str(data, "id"),
            reference=_opt_str(data, "reference"),
            status=_str(data, "status"),
            amount_expected=_money(data, "amount_expected"),
            amount_received=_money(data, "amount_received"),
            address=_str(data, "address"),
            paid_at=_opt_str(data, "paid_at"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class ForwardEventData:
    """``data`` for ``forward.confirmed`` and ``forward.sent``.

    CryptAPI-shaped, so the names differ from the REST resource: ``address_in`` is the
    deposit address (``Forward.address``) and ``address_out`` the destination
    (``Forward.destination``).
    """

    #: Names the forward this payment belongs to.  NOT deprecated here -- unlike
    #: ``Forward.forward_id``, this is a relationship, not the payload's own identity.
    forward_id: str = ""
    ticker: str = ""
    address_in: str = ""
    address_out: str = ""
    txid_in: str = ""
    txid_out: Optional[str] = None
    #: Gross received, exact-integer base units, as a string.
    value: str = "0"
    #: Net sent to the destination, exact-integer base units, as a string.
    value_forwarded: str = "0"
    #: Fee retained, exact-integer base units, as a string.
    fee: str = "0"
    confirmations: int = 0
    #: Dead field -- hard-coded ``False`` on every forward event ever sent.
    pending: bool = False
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "ForwardEventData":
        return cls(
            forward_id=_str(data, "forward_id"),
            ticker=_str(data, "ticker"),
            address_in=_str(data, "address_in"),
            address_out=_str(data, "address_out"),
            txid_in=_str(data, "txid_in"),
            txid_out=_opt_str(data, "txid_out"),
            value=_money(data, "value"),
            value_forwarded=_money(data, "value_forwarded"),
            fee=_money(data, "fee"),
            confirmations=_int(data, "confirmations"),
            pending=bool(data.get("pending")),
            raw=dict(data),
        )


@dataclass(frozen=True)
class WebhookEvent:
    """The unified envelope: ``{event, sequence, created_at, data}``.

    Both families ship the same four keys; only ``data`` differs, and ``event`` is the
    discriminator -- switch on it first, and keep a default branch, because receivers MUST
    ignore event names they do not recognise.

    Delivery is at-least-once and can arrive out of order.  ``(event, sequence)`` is the
    de-duplication key; there is no separate event id.  A webhook is a statement about a
    moment, never about now -- poll ``get_invoice()`` / ``get_forward()`` for current state.
    """

    event: str = ""
    #: Globally monotonic, allocated at enqueue time.  NOT per-resource: two consecutive
    #: events for one invoice are not necessarily consecutive numbers.
    sequence: int = 0
    #: When the EVENT happened, not when this delivery was sent.  Frozen across retries.
    created_at: Optional[str] = None
    data: Mapping[str, Any] = field(default_factory=dict)
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, payload: Mapping[str, Any]) -> "WebhookEvent":
        data = payload.get("data")
        if not isinstance(data, Mapping):
            # Tolerate the pre-envelope invoice shape so an integration deployed against
            # an older Receivify keeps working through a rollout (the PHP SDK does the
            # same).
            legacy = payload.get("invoice")
            data = legacy if isinstance(legacy, Mapping) else {}
        return cls(
            event=_str(payload, "event"),
            sequence=_int(payload, "sequence"),
            created_at=_opt_str(payload, "created_at"),
            data=dict(data),
            raw=dict(payload),
        )

    @property
    def is_invoice_event(self) -> bool:
        """``invoice.*``, ``sweep.sent`` and ``test`` all carry the invoice payload."""
        return self.event.startswith("invoice.") or self.event in ("sweep.sent", "test")

    @property
    def is_forward_event(self) -> bool:
        return self.event.startswith("forward.")

    @property
    def invoice(self) -> Optional[InvoiceEventData]:
        """The invoice payload, or ``None`` when this is not an invoice-family event."""
        return InvoiceEventData.from_dict(self.data) if self.is_invoice_event else None

    @property
    def forward(self) -> Optional[ForwardEventData]:
        """The forward payload, or ``None`` when this is not a ``forward.*`` event."""
        return ForwardEventData.from_dict(self.data) if self.is_forward_event else None

    @property
    def dedupe_key(self) -> Tuple[str, int]:
        """``(event, sequence)`` -- store the highest applied value and ignore replays."""
        return (self.event, self.sequence)


# --- signature ------------------------------------------------------------------------


def _as_bytes(body: BodyInput) -> bytes:
    if isinstance(body, (bytes, bytearray)):
        return bytes(body)
    if isinstance(body, str):
        # A str body has already been decoded by the framework; UTF-8 is what was signed.
        return body.encode("utf-8")
    raise TypeError("raw_body must be bytes or str, got {}".format(type(body).__name__))


def normalise_secrets(secrets: SecretsInput) -> List[bytes]:
    """Accept a single secret or an iterable of them; return them as bytes.

    A bare string is a convenience, not the contract: the signature is a LIST from day one
    so that a rotation never forces a code change.
    """
    if secrets is None:
        return []
    if isinstance(secrets, (str, bytes, bytearray)):
        secrets = [secrets]
    out: List[bytes] = []
    for secret in secrets:
        if secret is None:
            continue
        if isinstance(secret, (bytes, bytearray)):
            value = bytes(secret)
        elif isinstance(secret, str):
            value = secret.encode("utf-8")
        else:
            raise TypeError("webhook secrets must be str or bytes")
        if value:
            out.append(value)
    return out


def parse_signature_header(header: Optional[str]) -> Tuple[Optional[int], List[str]]:
    """Split ``t=<unix>,v1=<hex>`` into its timestamp and its signature candidates.

    Returns ``(None, [])`` for anything malformed.  More than one ``v1=`` is tolerated so
    a future multi-signature roll-out does not break an older SDK.
    """
    if not header or not isinstance(header, str):
        return (None, [])
    timestamp: Optional[int] = None
    signatures: List[str] = []
    for part in header.split(","):
        piece = part.strip()
        if "=" not in piece:
            continue
        key, _, value = piece.partition("=")
        key = key.strip()
        value = value.strip()
        if key == "t" and timestamp is None:
            try:
                timestamp = int(value)
            except ValueError:
                return (None, [])
        elif key == "v1" and value:
            signatures.append(value.lower())
    return (timestamp, signatures)


def compute_signature(raw_body: BodyInput, timestamp: Union[int, str], secret: Union[str, bytes]) -> str:
    """The hex ``v1`` value for a body/timestamp/secret triple.

    Exposed mostly so tests -- yours and ours -- can build a valid header without
    reimplementing the scheme.
    """
    key = secret.encode("utf-8") if isinstance(secret, str) else bytes(secret)
    signed_payload = "{}.".format(timestamp).encode("utf-8") + _as_bytes(raw_body)
    return hmac.new(key, signed_payload, hashlib.sha256).hexdigest()


def verify_webhook(
    raw_body: BodyInput,
    signature_header: Optional[str],
    secrets: SecretsInput,
    *,
    tolerance_seconds: Optional[int] = DEFAULT_TOLERANCE_SECONDS,
    now: Optional[int] = None,
) -> bool:
    """Return ``True`` when ``signature_header`` authenticates ``raw_body``.

    :param raw_body: The RAW body bytes, exactly as received.  Never re-serialised JSON.
    :param signature_header: The ``X-Receivify-Signature`` value.
    :param secrets: The current secret, or -- during a rotation -- ``[current, previous]``.
    :param tolerance_seconds: Replay window applied to ``t``.  ``None`` disables it.
    :param now: Unix seconds, for tests.  Defaults to the wall clock.

    Never raises for a bad signature; it returns ``False``.  Use :func:`parse_webhook`
    when you would rather have an exception and the decoded event.
    """
    keys = normalise_secrets(secrets)
    if not keys:
        return False
    timestamp, candidates = parse_signature_header(signature_header)
    if timestamp is None or not candidates:
        return False

    if tolerance_seconds is not None and tolerance_seconds >= 0:
        current = int(time.time()) if now is None else int(now)
        # abs(): a receiver whose clock runs slightly fast must not reject live traffic.
        if abs(current - timestamp) > int(tolerance_seconds):
            return False

    body = _as_bytes(raw_body)
    signed_payload = "{}.".format(timestamp).encode("utf-8") + body

    matched = False
    for key in keys:
        expected = hmac.new(key, signed_payload, hashlib.sha256).hexdigest()
        for candidate in candidates:
            # compare_digest, never ==: a byte-at-a-time comparison leaks the signature
            # one character per request.  No early exit, so the loop is uniform.
            matched = hmac.compare_digest(expected, candidate) or matched
    return matched


def parse_webhook(
    raw_body: BodyInput,
    signature_header: Optional[str],
    secrets: SecretsInput,
    *,
    tolerance_seconds: Optional[int] = DEFAULT_TOLERANCE_SECONDS,
    now: Optional[int] = None,
) -> WebhookEvent:
    """Verify, then decode, in one call.

    :raises SignatureVerificationError: the signature did not verify (wrong secret,
        tampered body, missing header, or a timestamp outside the tolerance window).
    :raises WebhookPayloadError: it verified but the body was not a JSON object.
    """
    if not verify_webhook(
        raw_body,
        signature_header,
        secrets,
        tolerance_seconds=tolerance_seconds,
        now=now,
    ):
        raise SignatureVerificationError(
            "Receivify webhook signature verification failed. Check that you passed the "
            "RAW request body (Django: request.body, Flask: request.get_data()) and, "
            "during a secret rotation, both the current and previous secrets."
        )
    try:
        payload = json.loads(_as_bytes(raw_body).decode("utf-8"))
    except (ValueError, UnicodeDecodeError) as exc:
        raise WebhookPayloadError("Webhook body is not valid JSON: {}".format(exc)) from exc
    if not isinstance(payload, Mapping):
        raise WebhookPayloadError("Webhook body is not a JSON object.")
    return WebhookEvent.from_dict(payload)
