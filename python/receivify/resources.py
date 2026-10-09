"""Dataclasses for every resource in the frozen v1 contract.

Three rules run through this module:

1. **Money never becomes a number.**  Every monetary field is typed ``str`` and is handed
   back exactly as the wire carried it.  ``amount_expected`` is base units (satoshis, wei,
   ...); ``amount_fiat``, ``min_forward`` and ``amount_btc`` are decimal display strings.
   Parsing any of them into a ``float`` would silently corrupt a 256-bit wei value -- if
   you need arithmetic, opt in explicitly via :mod:`receivify.money`.
2. **Unknown fields are never lost.**  Every dataclass keeps the decoded body in ``.raw``,
   so a field the API adds tomorrow is readable from a build shipped today.
3. **Missing fields never raise.**  Every field has a default.  A resource is a view over a
   response, not a validator.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from decimal import Decimal
from typing import Any, Dict, List, Mapping, Optional

__all__ = [
    "Invoice",
    "PublicInvoice",
    "Forward",
    "ForwardDetail",
    "ForwardPayment",
    "ForwardCallback",
    "Ticker",
    "Asset",
    "PaymentLink",
    "RecurringSchedule",
    "ApiKey",
    "ApiKeyCreated",
    "WebhookEndpoint",
    "WebhookDelivery",
    "WebhookSecretState",
    "WebhookSecretRotated",
    "PayoutChange",
    "PayoutState",
    "Settings",
    "INVOICE_STATUSES",
    "FORWARD_STATUSES",
    "EVENT_NAMES",
]

# Open enums.  Keep a default branch when you switch on these: adding a value is an
# additive change under docs/HANDBOOK.md part 14 and must not break an older SDK.
INVOICE_STATUSES = (
    "pending",
    "partial",
    "confirming",
    "paid",
    "swept",
    "expired",
    "partial_expired",
    "cancelled",
    "failed",
)

FORWARD_STATUSES = ("active", "paused", "disabled", "expired")

EVENT_NAMES = (
    "invoice.confirming",
    "invoice.partial",
    "invoice.paid",
    "invoice.swept",
    "invoice.expired",
    "invoice.partial_expired",
    "invoice.cancelled",
    "invoice.failed",
    "sweep.sent",
    "forward.detected",
    "forward.confirmed",
    "forward.deferred",
    "forward.sent",
    "forward.reversed",
    "forward.failed",
    "test",
)


# --- decoding helpers ----------------------------------------------------------------


def _str(data: Mapping[str, Any], key: str, default: str = "") -> str:
    value = data.get(key)
    return value if isinstance(value, str) else default


def _opt_str(data: Mapping[str, Any], key: str) -> Optional[str]:
    value = data.get(key)
    return value if isinstance(value, str) else None


def _int(data: Mapping[str, Any], key: str, default: int = 0) -> int:
    value = data.get(key)
    if isinstance(value, bool):
        return default
    if isinstance(value, int):
        return value
    if isinstance(value, str):
        try:
            return int(value)
        except ValueError:
            return default
    return default


def _opt_int(data: Mapping[str, Any], key: str) -> Optional[int]:
    value = data.get(key)
    if isinstance(value, bool) or value is None:
        return None
    if isinstance(value, int):
        return value
    return None


def _bool(data: Mapping[str, Any], key: str, default: bool = False) -> bool:
    value = data.get(key)
    return value if isinstance(value, bool) else default


def _obj(data: Mapping[str, Any], key: str) -> Dict[str, Any]:
    value = data.get(key)
    return dict(value) if isinstance(value, Mapping) else {}


def _money(data: Mapping[str, Any], key: str, default: str = "0") -> str:
    """A required money field, always returned as a string, byte-for-byte as sent."""
    value = _opt_money(data, key)
    return default if value is None else value


def _opt_money(data: Mapping[str, Any], key: str) -> Optional[str]:
    """A nullable money field.

    The API types every amount as a JSON string precisely so no client ever has to make
    this choice.  If a value ever arrives as a JSON number anyway, it is stringified
    without going through binary floating point twice -- ``Decimal(repr(x))`` keeps the
    shortest round-trip form rather than printing ``0.30000000000000004``.
    """
    value = data.get(key)
    if value is None or isinstance(value, bool):
        return None
    if isinstance(value, str):
        return value
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        return format(Decimal(repr(value)), "f")
    return str(value)


# --- invoices -------------------------------------------------------------------------


@dataclass(frozen=True)
class Invoice:
    """A one-off payment request with its own derived deposit address.

    Identical on create, retrieve, list, cancel and ``POST /api/public/links/{id}``.
    """

    id: str = ""
    reference: Optional[str] = None
    asset: str = ""
    #: Exact-integer base units, as a STRING (satoshis for BTC, wei for ETH).
    amount_expected: str = "0"
    #: Exact-integer base units, as a STRING.  ``"0"`` until something arrives.
    amount_received: str = "0"
    #: A DECIMAL string such as ``"25.00"`` -- a display price, never settle against it.
    amount_fiat: Optional[str] = None
    fiat_currency: Optional[str] = None
    chain: Optional[str] = None
    provider: Optional[str] = None
    address: str = ""
    status: str = ""
    expires_at: Optional[str] = None
    paid_at: Optional[str] = None
    created_at: Optional[str] = None
    metadata: Dict[str, Any] = field(default_factory=dict)
    checkout_url: Optional[str] = None
    #: Decimal places of this invoice's asset -- the divisor for ``amount_expected``.
    decimals: Optional[int] = None
    #: ``amount_expected`` already divided by ``10 ** decimals``, as a decimal STRING. Display only.
    amount_decimal: Optional[str] = None
    #: Wallet-scannable URI for a QR code, or ``None`` when the chain has no safe one.
    payment_uri: Optional[str] = None
    #: Whether the commission was added on top of the requested amount.
    add_fee_ontop: bool = False
    #: Exact-integer base units, as a STRING: what you asked to RECEIVE.
    amount_net_requested: Optional[str] = None
    #: Exact-integer base units, as a STRING: what the gross-up added. ``"0"`` when off.
    fee_ontop: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "Invoice":
        return cls(
            id=_str(data, "id"),
            reference=_opt_str(data, "reference"),
            asset=_str(data, "asset"),
            amount_expected=_money(data, "amount_expected"),
            amount_received=_money(data, "amount_received"),
            amount_fiat=_opt_money(data, "amount_fiat"),
            fiat_currency=_opt_str(data, "fiat_currency"),
            chain=_opt_str(data, "chain"),
            provider=_opt_str(data, "provider"),
            address=_str(data, "address"),
            status=_str(data, "status"),
            expires_at=_opt_str(data, "expires_at"),
            paid_at=_opt_str(data, "paid_at"),
            created_at=_opt_str(data, "created_at"),
            metadata=_obj(data, "metadata"),
            checkout_url=_opt_str(data, "checkout_url"),
            decimals=_opt_int(data, "decimals"),
            amount_decimal=_opt_str(data, "amount_decimal"),
            payment_uri=_opt_str(data, "payment_uri"),
            add_fee_ontop=_bool(data, "add_fee_ontop"),
            amount_net_requested=_opt_money(data, "amount_net_requested"),
            fee_ontop=_opt_money(data, "fee_ontop"),
            raw=dict(data),
        )

    @property
    def is_settled(self) -> bool:
        """``True`` once the money is ours: ``paid`` or ``swept``."""
        return self.status in ("paid", "swept")


@dataclass(frozen=True)
class PublicInvoice:
    """The unauthenticated checkout-page projection -- deliberately narrower."""

    id: str = ""
    status: str = ""
    address: str = ""
    amount_expected: str = "0"
    amount_received: str = "0"
    expires_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "PublicInvoice":
        return cls(
            id=_str(data, "id"),
            status=_str(data, "status"),
            address=_str(data, "address"),
            amount_expected=_money(data, "amount_expected"),
            amount_received=_money(data, "amount_received"),
            expires_at=_opt_str(data, "expires_at"),
            raw=dict(data),
        )


# --- forwarding -----------------------------------------------------------------------


@dataclass(frozen=True)
class Forward:
    """A standing deposit address that sweeps everything it receives to ``destination``."""

    #: The canonical identifier.  The wire also carries a deprecated ``forward_id`` with
    #: the identical value; read ``id``.
    id: str = ""
    ticker: str = ""
    provider: str = ""
    chain: Optional[str] = None
    address: str = ""
    destination: str = ""
    callback_url: Optional[str] = None
    confirmations_required: int = 0
    #: The one money field here that is NOT base units: a DISPLAY-unit decimal string.
    #: Multiply by ``10 ** decimals`` before comparing it with a ``ForwardPayment.value``.
    min_forward: str = "0"
    status: str = ""
    expires_at: Optional[str] = None
    created_at: Optional[str] = None
    updated_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "Forward":
        # `id` is canonical; fall back to the deprecated alias only if a very old
        # deployment omits it.
        identifier = _str(data, "id") or _str(data, "forward_id")
        return cls(
            id=identifier,
            ticker=_str(data, "ticker"),
            provider=_str(data, "provider"),
            chain=_opt_str(data, "chain"),
            address=_str(data, "address"),
            destination=_str(data, "destination"),
            callback_url=_opt_str(data, "callback_url"),
            confirmations_required=_int(data, "confirmations_required"),
            min_forward=_money(data, "min_forward"),
            status=_str(data, "status"),
            expires_at=_opt_str(data, "expires_at"),
            created_at=_opt_str(data, "created_at"),
            updated_at=_opt_str(data, "updated_at"),
            raw=dict(data),
        )

    @property
    def forward_id(self) -> str:
        """Deprecated alias for :attr:`id`, kept because the wire keeps it.

        ``forward_id`` is emitted for the whole life of v1 (removing a response field
        would be breaking), but new code should read ``id``.  Note that ``forward_id``
        *inside a forward webhook payload* is a different thing and is NOT deprecated --
        there it names a relationship, not the payload's own identity.
        """
        return self.id


@dataclass(frozen=True)
class ForwardPayment:
    """One deposit credited to a forwarding address."""

    txid_in: str = ""
    #: Gross received, exact-integer base units, as a string.
    value: str = "0"
    status: str = ""
    txid_out: Optional[str] = None
    confirmations: int = 0
    created_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "ForwardPayment":
        return cls(
            txid_in=_str(data, "txid_in"),
            value=_money(data, "value"),
            status=_str(data, "status"),
            txid_out=_opt_str(data, "txid_out"),
            confirmations=_int(data, "confirmations"),
            created_at=_opt_str(data, "created_at"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class ForwardCallback:
    """One webhook delivery attempt for a forward.  Outcome only -- never the body."""

    event: str = ""
    url: str = ""
    status: str = ""
    attempts: int = 0
    last_status: Optional[int] = None
    last_error: Optional[str] = None
    created_at: Optional[str] = None
    delivered_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "ForwardCallback":
        return cls(
            event=_str(data, "event"),
            url=_str(data, "url"),
            status=_str(data, "status"),
            attempts=_int(data, "attempts"),
            last_status=_opt_int(data, "last_status"),
            last_error=_opt_str(data, "last_error"),
            created_at=_opt_str(data, "created_at"),
            delivered_at=_opt_str(data, "delivered_at"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class ForwardDetail(Forward):
    """``GET /api/v1/address/{id}``: a :class:`Forward` plus two capped sub-collections.

    The extra keys are SPREAD at the top level on the wire, which is why this subclasses
    ``Forward`` rather than nesting it.  Neither sub-collection is paginated -- when
    ``payments_has_more`` is ``True`` there is no cursor to reach the rest.
    """

    payments: List[ForwardPayment] = field(default_factory=list)
    payments_has_more: bool = False
    callbacks: List[ForwardCallback] = field(default_factory=list)
    callbacks_has_more: bool = False

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "ForwardDetail":
        base = Forward.from_dict(data)
        payments = data.get("payments")
        callbacks = data.get("callbacks")
        return cls(
            id=base.id,
            ticker=base.ticker,
            provider=base.provider,
            chain=base.chain,
            address=base.address,
            destination=base.destination,
            callback_url=base.callback_url,
            confirmations_required=base.confirmations_required,
            min_forward=base.min_forward,
            status=base.status,
            expires_at=base.expires_at,
            created_at=base.created_at,
            updated_at=base.updated_at,
            raw=base.raw,
            payments=[ForwardPayment.from_dict(p) for p in payments or [] if isinstance(p, Mapping)],
            payments_has_more=_bool(data, "payments_has_more"),
            callbacks=[ForwardCallback.from_dict(c) for c in callbacks or [] if isinstance(c, Mapping)],
            callbacks_has_more=_bool(data, "callbacks_has_more"),
        )


# --- catalogue ------------------------------------------------------------------------


@dataclass(frozen=True)
class Ticker:
    """One ``(asset, chain)`` pair offered for forwarding."""

    ticker: str = ""
    provider: str = ""
    chain: Optional[str] = None
    name: str = ""
    #: Base units per whole coin as a power of ten -- the number to divide by for display.
    decimals: int = 0
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "Ticker":
        return cls(
            ticker=_str(data, "ticker"),
            provider=_str(data, "provider"),
            chain=_opt_str(data, "chain"),
            name=_str(data, "name"),
            decimals=_int(data, "decimals"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class Asset:
    """An invoiceable coin.  DISPLAY metadata -- two of these fields are not machine data."""

    symbol: str = ""
    name: str = ""
    #: A human sentence, e.g. ``"ETH - Base - Arbitrum"``.  Not a chain list.
    network: str = ""
    color: str = ""
    #: Either a decimal string (``"0.00008000"``) or the literal word ``"network"``.
    #: Check for the word before trying to compute with it.
    min: str = ""
    #: English prose (``"~0.000001 BTC"``, ``"dynamic gas"``).  Display only.
    network_fee: str = ""
    live: bool = False
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "Asset":
        return cls(
            symbol=_str(data, "symbol"),
            name=_str(data, "name"),
            network=_str(data, "network"),
            color=_str(data, "color"),
            min=_str(data, "min"),
            network_fee=_str(data, "network_fee"),
            live=_bool(data, "live"),
            raw=dict(data),
        )


# --- payment links & recurring --------------------------------------------------------


@dataclass(frozen=True)
class PaymentLink:
    """One shape for every payment-link operation: create, list, get, update, deactivate.

    Note the asymmetry: you SET the amount with ``amount_btc`` (a decimal BTC string) and
    READ it as :attr:`amount_sats` (base units).
    """

    id: str = ""
    title: Optional[str] = None
    amount_sats: Optional[str] = None
    open_amount: bool = False
    active: bool = True
    uses: int = 0
    created_at: Optional[str] = None
    updated_at: Optional[str] = None
    url: str = ""
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "PaymentLink":
        return cls(
            id=_str(data, "id"),
            title=_opt_str(data, "title"),
            amount_sats=_opt_money(data, "amount_sats"),
            open_amount=_bool(data, "open_amount"),
            active=_bool(data, "active", True),
            uses=_int(data, "uses"),
            created_at=_opt_str(data, "created_at"),
            updated_at=_opt_str(data, "updated_at"),
            url=_str(data, "url"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class RecurringSchedule:
    """Scheduled billing.  Crypto has no pull mechanism: each cycle MINTS a new invoice."""

    id: str = ""
    title: Optional[str] = None
    asset: str = ""
    #: A DECIMAL string like ``"25.00"``.  Null when priced in the coin.
    amount_fiat: Optional[str] = None
    currency: Optional[str] = None
    #: A DECIMAL amount in the coin -- not base units.  Null when priced in fiat.
    amount: Optional[str] = None
    interval_days: int = 0
    active: bool = False
    runs: int = 0
    next_run: Optional[str] = None
    last_invoice_id: Optional[str] = None
    created_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "RecurringSchedule":
        return cls(
            id=_str(data, "id"),
            title=_opt_str(data, "title"),
            asset=_str(data, "asset"),
            amount_fiat=_opt_money(data, "amount_fiat"),
            currency=_opt_str(data, "currency"),
            amount=_opt_money(data, "amount"),
            interval_days=_int(data, "interval_days"),
            active=_bool(data, "active"),
            runs=_int(data, "runs"),
            next_run=_opt_str(data, "next_run"),
            last_invoice_id=_opt_str(data, "last_invoice_id"),
            created_at=_opt_str(data, "created_at"),
            raw=dict(data),
        )


# --- API keys -------------------------------------------------------------------------


@dataclass(frozen=True)
class ApiKey:
    """A listed key.  The secret is never stored, so it can never be listed."""

    id: str = ""
    prefix: str = ""
    label: Optional[str] = None
    created_at: Optional[str] = None
    last_used_at: Optional[str] = None
    revoked: bool = False
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "ApiKey":
        return cls(
            id=_str(data, "id"),
            prefix=_str(data, "prefix") or _str(data, "api_key_prefix"),
            label=_opt_str(data, "label"),
            created_at=_opt_str(data, "created_at"),
            last_used_at=_opt_str(data, "last_used_at"),
            revoked=_bool(data, "revoked"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class ApiKeyCreated:
    """A freshly issued key.  :attr:`api_key` is shown exactly once, here.

    On an ``Idempotency-Key`` replay :attr:`api_key` is ``None`` -- the secret is
    deliberately not persisted for the replay window -- while :attr:`id` and
    :attr:`prefix` still identify what was created, so you can still revoke it.
    """

    id: str = ""
    api_key: Optional[str] = None
    prefix: str = ""
    label: Optional[str] = None
    created_at: Optional[str] = None
    message: str = ""
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "ApiKeyCreated":
        return cls(
            id=_str(data, "id"),
            api_key=_opt_str(data, "api_key"),
            prefix=_str(data, "prefix") or _str(data, "api_key_prefix"),
            label=_opt_str(data, "label"),
            created_at=_opt_str(data, "created_at"),
            message=_str(data, "message"),
            raw=dict(data),
        )

    def __repr__(self) -> str:
        """Masks the secret: an SDK object must not print a live credential."""
        return "ApiKeyCreated(id={!r}, prefix={!r}, api_key={})".format(
            self.id, self.prefix, "'<redacted>'" if self.api_key else None
        )

    @property
    def is_replay(self) -> bool:
        """``True`` when this was an ``Idempotency-Key`` replay (no secret returned)."""
        return self.api_key is None


# --- webhooks -------------------------------------------------------------------------


@dataclass(frozen=True)
class WebhookEndpoint:
    """An account-level endpoint.  Receives every INVOICE event -- never ``forward.*``."""

    id: str = ""
    url: str = ""
    active: bool = True
    created_at: Optional[str] = None
    updated_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "WebhookEndpoint":
        return cls(
            id=_str(data, "id"),
            url=_str(data, "url"),
            active=_bool(data, "active", True),
            created_at=_opt_str(data, "created_at"),
            updated_at=_opt_str(data, "updated_at"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class WebhookDelivery:
    """One row of the delivery log.  Exactly one of the two ids is non-null."""

    #: A 24-character hex ObjectId -- the only identifier here that is not a UUID.
    id: str = ""
    invoice_id: Optional[str] = None
    forward_id: Optional[str] = None
    event: str = ""
    status: str = ""
    attempts: int = 0
    url: str = ""
    last_status: Optional[int] = None
    last_error: Optional[str] = None
    created_at: Optional[str] = None
    delivered_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "WebhookDelivery":
        return cls(
            id=_str(data, "id"),
            invoice_id=_opt_str(data, "invoice_id"),
            forward_id=_opt_str(data, "forward_id"),
            event=_str(data, "event"),
            status=_str(data, "status"),
            attempts=_int(data, "attempts"),
            url=_str(data, "url"),
            last_status=_opt_int(data, "last_status"),
            last_error=_opt_str(data, "last_error"),
            created_at=_opt_str(data, "created_at"),
            delivered_at=_opt_str(data, "delivered_at"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class WebhookSecretState:
    """Rotation metadata.  Carries no secret material of any kind."""

    rotated_at: Optional[str] = None
    prev_expires_at: Optional[str] = None
    #: While ``True``, your receiver must accept EITHER secret.
    prev_active: bool = False
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "WebhookSecretState":
        return cls(
            rotated_at=_opt_str(data, "rotated_at"),
            prev_expires_at=_opt_str(data, "prev_expires_at"),
            prev_active=_bool(data, "prev_active"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class WebhookSecretRotated:
    """The result of a rotation.  :attr:`secret` is shown ONCE and never read back.

    Until :attr:`prev_expires_at`, verify each delivery against the new secret and fall
    back to the previous one -- which is exactly why ``verify_webhook`` takes a *list*.
    """

    secret: Optional[str] = None
    rotated_at: Optional[str] = None
    prev_expires_at: Optional[str] = None
    grace_seconds: int = 0
    #: ``True`` when this rotation retired a previous secret still inside its window.
    replaced_previous: bool = False
    message: str = ""
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "WebhookSecretRotated":
        return cls(
            secret=_opt_str(data, "secret"),
            rotated_at=_opt_str(data, "rotated_at"),
            prev_expires_at=_opt_str(data, "prev_expires_at"),
            grace_seconds=_int(data, "grace_seconds"),
            replaced_previous=_bool(data, "replaced_previous"),
            message=_str(data, "message"),
            raw=dict(data),
        )

    def __repr__(self) -> str:
        """Masks the secret: an SDK object must not print a live credential."""
        return "WebhookSecretRotated(rotated_at={!r}, prev_expires_at={!r}, secret={})".format(
            self.rotated_at, self.prev_expires_at, "'<redacted>'" if self.secret else None
        )

    @property
    def is_replay(self) -> bool:
        """``True`` when this was an ``Idempotency-Key`` replay (no secret returned)."""
        return self.secret is None


# --- payouts & settings ---------------------------------------------------------------


@dataclass(frozen=True)
class PayoutChange:
    """A staged payout-address change, waiting out its timelock."""

    id: str = ""
    asset: str = ""
    #: An empty string means "clear this asset's payout address".
    new_address: str = ""
    old_address: Optional[str] = None
    status: str = ""
    requested_at: Optional[str] = None
    effective_at: Optional[str] = None
    confirmed_at: Optional[str] = None
    applied_at: Optional[str] = None
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "PayoutChange":
        return cls(
            id=_str(data, "id"),
            asset=_str(data, "asset"),
            new_address=_str(data, "new_address"),
            old_address=_opt_str(data, "old_address"),
            status=_str(data, "status"),
            requested_at=_opt_str(data, "requested_at"),
            effective_at=_opt_str(data, "effective_at"),
            confirmed_at=_opt_str(data, "confirmed_at"),
            applied_at=_opt_str(data, "applied_at"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class PayoutState:
    """The LIVE payout map plus every open change.

    ``pending`` is a plain array with no list envelope and no bound -- the sweeper only
    ever reads :attr:`payout_addresses`.
    """

    payout_addresses: Dict[str, str] = field(default_factory=dict)
    pending: List[PayoutChange] = field(default_factory=list)
    timelock_hours: int = 0
    email_confirmation: bool = False
    #: ``True`` only when the timelock is zero AND email confirmation is off.
    instant: bool = False
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "PayoutState":
        pending = data.get("pending")
        return cls(
            payout_addresses={
                str(k): str(v) for k, v in _obj(data, "payout_addresses").items()
            },
            pending=[PayoutChange.from_dict(c) for c in pending or [] if isinstance(c, Mapping)],
            timelock_hours=_int(data, "timelock_hours"),
            email_confirmation=_bool(data, "email_confirmation"),
            instant=_bool(data, "instant"),
            raw=dict(data),
        )


@dataclass(frozen=True)
class Settings:
    """Merchant preferences.  ``fee_bps`` is an integer rate, not money."""

    display_currency: str = ""
    email: str = ""
    fee_bps: int = 0
    raw: Mapping[str, Any] = field(default_factory=dict, repr=False, compare=False)

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> "Settings":
        return cls(
            display_currency=_str(data, "display_currency"),
            email=_str(data, "email"),
            fee_bps=_int(data, "fee_bps"),
            raw=dict(data),
        )
