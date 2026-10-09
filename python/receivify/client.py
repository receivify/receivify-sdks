"""The Receivify client.

    from receivify import Receivify

    with Receivify("https://receivify.io", "rcv_live_...", webhook_secrets=["whsec_..."]) as rcv:
        invoice = rcv.create_invoice(amount_fiat="25.00", currency="USD", asset="btc")
        print(invoice.checkout_url)

Design notes worth knowing before you read the methods:

* **The API key is a header, never a URL parameter**, and never appears in a ``repr()``.
* **Retries are conservative.**  A GET is safe to repeat.  A mutating call is repeated
  ONLY when you gave it an ``Idempotency-Key`` -- blind-retrying a POST is how a merchant
  ends up with two invoices, two API keys, or two burned HD derivation indexes.
* **Amounts are strings.**  Passing a ``float`` to an amount argument raises: ``0.1 + 0.2``
  is not ``0.3``, and a satoshi is not divisible.
"""

from __future__ import annotations

import json
import random
import time
import urllib.parse
from decimal import Decimal
from typing import Any, Callable, Dict, Iterator, List, Mapping, Optional, Sequence, Union

from ._version import __version__
from .errors import ReceivifyError, ReceivifyTransportError, error_from_response
from .http import Request, Response, Transport, UrllibTransport
from .pagination import Page, auto_paginate
from .resources import (
    ApiKey,
    ApiKeyCreated,
    Asset,
    Forward,
    ForwardDetail,
    Invoice,
    PaymentLink,
    PayoutState,
    PublicInvoice,
    RecurringSchedule,
    Settings,
    Ticker,
    WebhookDelivery,
    WebhookEndpoint,
    WebhookSecretRotated,
    WebhookSecretState,
)
from .webhooks import (
    DEFAULT_TOLERANCE_SECONDS,
    SecretsInput,
    WebhookEvent,
    normalise_secrets,
    parse_webhook,
    verify_webhook,
)

__all__ = ["Receivify", "UNSET"]

AmountInput = Union[str, int, Decimal]
JsonDict = Dict[str, Any]


class _Unset:
    """Sentinel distinguishing "not supplied" from an explicit ``None``.

    ``update_forward(id, callback_url=None)`` CLEARS the callback; omitting the argument
    leaves it alone.  Those are different requests, so they need different values.
    """

    _instance: Optional["_Unset"] = None

    def __new__(cls) -> "_Unset":
        if cls._instance is None:
            cls._instance = super().__new__(cls)
        return cls._instance

    def __repr__(self) -> str:
        return "UNSET"

    def __bool__(self) -> bool:
        return False


UNSET: Any = _Unset()

_DEFAULT_TIMEOUT = 30.0
_DEFAULT_MAX_RETRIES = 2
_DEFAULT_BACKOFF = 0.5
_MAX_BACKOFF = 20.0
#: Hard ceiling on a server-suggested wait, so a bad ``Retry-After`` cannot park a worker.
_MAX_SERVER_WAIT = 60.0

_RETRYABLE_STATUSES = frozenset({429, 500, 502, 503, 504})


class Receivify:
    """A client for one Receivify deployment and one API key.

    :param api_url: Deployment base URL, e.g. ``https://receivify.io``.
    :param api_key: The merchant's ``rcv_live_...`` key.  Optional: the catalogue and
        public endpoints work without one.
    :param timeout: Per-request timeout in seconds (default 30).
    :param webhook_secrets: Default secrets for :meth:`verify_webhook`.  Pass BOTH the
        current and the previous secret during a rotation grace window.
    :param max_retries: Extra attempts for retry-safe requests (default 2).
    :param transport: Injected HTTP layer.  Anything with ``send(Request) -> Response``;
        this is the seam the test suite uses instead of a network.
    :param sleep: Injected sleeper, for tests.
    :param user_agent: Overrides the default ``receivify-python/<version>``.

    Instances are cheap and thread-safe as long as the transport is; the default urllib
    transport keeps no cross-request state.
    """

    def __init__(
        self,
        api_url: str,
        api_key: str = "",
        *,
        timeout: float = _DEFAULT_TIMEOUT,
        webhook_secrets: SecretsInput = None,
        webhook_secret: Optional[str] = None,
        max_retries: int = _DEFAULT_MAX_RETRIES,
        transport: Optional[Transport] = None,
        sleep: Callable[[float], None] = time.sleep,
        user_agent: Optional[str] = None,
    ) -> None:
        if not api_url:
            raise ValueError("api_url is required, e.g. 'https://receivify.io'")
        self._api_url = api_url.rstrip("/")
        self._api_key = api_key or ""
        self._timeout = float(timeout)
        self._max_retries = max(0, int(max_retries))
        self._transport: Transport = transport if transport is not None else UrllibTransport()
        self._owns_transport = transport is None
        self._sleep = sleep
        self._user_agent = user_agent or "receivify-python/{}".format(__version__)
        secrets: List[bytes] = normalise_secrets(webhook_secrets)
        secrets.extend(normalise_secrets(webhook_secret))
        self._webhook_secrets: List[bytes] = secrets

    # -- lifecycle ---------------------------------------------------------------------

    def __repr__(self) -> str:
        """Never prints the API key -- reprs end up in logs and bug reports."""
        return "Receivify(api_url={!r}, api_key={!r})".format(self._api_url, self._masked_key())

    def _masked_key(self) -> str:
        if not self._api_key:
            return ""
        return self._api_key[:12] + "..." if len(self._api_key) > 12 else "..."

    def close(self) -> None:
        """Release the transport.  Only closes a transport this client created."""
        closer = getattr(self._transport, "close", None)
        if self._owns_transport and callable(closer):
            closer()

    def __enter__(self) -> "Receivify":
        return self

    def __exit__(self, exc_type: Any, exc: Any, tb: Any) -> None:
        self.close()

    @property
    def api_url(self) -> str:
        return self._api_url

    @property
    def webhook_secrets(self) -> List[str]:
        """The configured secrets, decoded.  Handle with the same care as the key."""
        return [s.decode("utf-8", "replace") for s in self._webhook_secrets]

    # -- invoices ----------------------------------------------------------------------

    def create_invoice(
        self,
        *,
        amount_fiat: Optional[AmountInput] = None,
        currency: Optional[str] = None,
        amount: Optional[AmountInput] = None,
        amount_sats: Optional[AmountInput] = None,
        amount_btc: Optional[AmountInput] = None,
        asset: Optional[str] = None,
        chain: Optional[str] = None,
        reference: Optional[str] = None,
        callback_url: Optional[str] = None,
        metadata: Optional[Mapping[str, Any]] = None,
        add_fee_ontop: Optional[bool] = None,
        idempotency_key: Optional[str] = None,
    ) -> Invoice:
        """Create an invoice -- ``POST /api/v1/invoices``.

        Exactly one amount is required.  They are tried in this order and the first
        present one wins: ``amount_fiat`` (+ ``currency``), ``amount``, ``amount_sats``,
        ``amount_btc``.

        Two things return an EXISTING invoice rather than creating one, both with a 201:
        an ``Idempotency-Key`` replay, and a ``reference`` this merchant has used before.
        ``reference`` is a de-duplication key as well as your business key -- there is no
        way to ask for a second invoice under one reference.

        ``add_fee_ontop``: ``True`` grosses the amount up so the payer covers the commission
        and you receive the amount you asked for; ``False`` forces fee-out-of-amount for this
        invoice.  Leave it ``None`` (omitted) to use the account default -- omitting it is NOT
        the same as sending ``False``.
        """
        body: JsonDict = {}
        if amount_fiat is not None:
            body["amount_fiat"] = _amount(amount_fiat, "amount_fiat")
        if currency is not None:
            body["currency"] = currency.upper()
        if amount is not None:
            body["amount"] = _amount(amount, "amount")
        if amount_sats is not None:
            body["amount_sats"] = _amount(amount_sats, "amount_sats")
        if amount_btc is not None:
            body["amount_btc"] = _amount(amount_btc, "amount_btc")
        if not any(k in body for k in ("amount_fiat", "amount", "amount_sats", "amount_btc")):
            raise ValueError(
                "One of amount_fiat, amount, amount_sats or amount_btc is required."
            )
        if asset is not None:
            body["asset"] = asset
        if chain is not None:
            body["chain"] = chain
        if reference is not None:
            body["reference"] = reference
        if callback_url is not None:
            body["callback_url"] = callback_url
        if metadata is not None:
            body["metadata"] = dict(metadata)
        if add_fee_ontop is not None:
            if not isinstance(add_fee_ontop, bool):
                # The API rejects anything but a real JSON boolean with 400 bad_request.
                raise TypeError("add_fee_ontop must be a bool, got {}.".format(type(add_fee_ontop).__name__))
            body["add_fee_ontop"] = add_fee_ontop
        payload = self._request_json(
            "POST", "/api/v1/invoices", body=body, idempotency_key=idempotency_key
        )
        return Invoice.from_dict(_expect_object(payload, "invoice"))

    def get_invoice(self, invoice_id: str) -> Invoice:
        """Retrieve an invoice -- the authoritative current state.

        Poll this rather than trusting a webhook replay, which re-sends a frozen snapshot
        of the moment the event happened.
        """
        payload = self._request_json("GET", "/api/v1/invoices/{}".format(_seg(invoice_id)))
        return Invoice.from_dict(_expect_object(payload, "invoice"))

    def list_invoices(
        self,
        *,
        limit: Optional[int] = None,
        cursor: Optional[str] = None,
        status: Optional[str] = None,
    ) -> Page[Invoice]:
        """One page of invoices, newest first."""
        payload = self._request_json(
            "GET", "/api/v1/invoices", params={"limit": limit, "cursor": cursor, "status": status}
        )
        return Page.from_dict(payload, Invoice.from_dict)

    def iter_invoices(
        self, *, limit: Optional[int] = None, status: Optional[str] = None
    ) -> Iterator[Invoice]:
        """Every invoice across every page, following ``next_cursor`` lazily."""
        return auto_paginate(self.list_invoices, limit=limit, status=status)

    def cancel_invoice(self, invoice_id: str) -> Invoice:
        """Cancel a pending, unpaid invoice.

        NOT idempotent: a second call on an already-cancelled invoice raises
        :class:`~receivify.errors.NotAllowedError` (409).  A payment landing mid-request
        wins -- the match is atomic on ``{status: pending, amount_received: 0}``.
        """
        payload = self._request_json("POST", "/api/v1/invoices/{}/cancel".format(_seg(invoice_id)))
        return Invoice.from_dict(_expect_object(payload, "invoice"))

    def bulk_cancel_invoices(self, invoice_ids: Sequence[str]) -> JsonDict:
        """Cancel up to 500 invoices.  Returns ``{ok, action, requested, cancelled}``.

        Naturally idempotent -- the filter only matches pending, unpaid invoices of yours,
        so a stray or already-paid id is skipped rather than force-cancelled.  Compare
        ``requested`` with ``cancelled``; the response does not say which succeeded.
        Does not accept ``Idempotency-Key``.
        """
        ids = [str(i) for i in invoice_ids]
        if not ids:
            raise ValueError("invoice_ids must not be empty.")
        if len(ids) > 500:
            raise ValueError("At most 500 ids per bulk call, got {}.".format(len(ids)))
        payload = self._request_json(
            "POST", "/api/v1/invoices/bulk", body={"ids": ids, "action": "cancel"}
        )
        return _expect_object(payload, "bulk result")

    # -- forwarding addresses ----------------------------------------------------------

    def create_forward(
        self,
        *,
        ticker: str,
        destination: str,
        callback: Optional[str] = None,
        confirmations: Optional[int] = None,
        metadata: Optional[Mapping[str, Any]] = None,
        idempotency_key: Optional[str] = None,
    ) -> Forward:
        """Create -- or return -- a standing forwarding address.

        **Deterministic.**  Identity is ``(merchant, asset, destination, callback)``, so
        repeating the call returns the same forward (200) instead of burning a second HD
        derivation index.  The single-session BTCPay tickers (``ltc``, ``doge``, ``bch``,
        ``xmr``, ``zec``) are never deduped and always mint a fresh address.

        Note ``callback`` is part of that identity: a different callback URL creates a
        DIFFERENT forward.

        A forward that exists but is ``paused``/``disabled`` raises
        :class:`~receivify.errors.NotAllowedError` rather than being resurrected.
        """
        body: JsonDict = {"ticker": ticker, "destination": destination}
        if callback is not None:
            body["callback"] = callback
        if confirmations is not None:
            body["confirmations"] = int(confirmations)
        if metadata is not None:
            body["metadata"] = dict(metadata)
        payload = self._request_json(
            "POST", "/api/v1/address", body=body, idempotency_key=idempotency_key
        )
        return Forward.from_dict(_expect_object(payload, "forward"))

    def get_forward(self, forward_id: str) -> ForwardDetail:
        """Retrieve a forward with its payment and callback log.

        ``payments`` is capped at 200 and ``callbacks`` at 50, with ``*_has_more`` flags.
        Neither sub-collection is paginated -- there is no cursor to reach the rest.
        """
        payload = self._request_json("GET", "/api/v1/address/{}".format(_seg(forward_id)))
        return ForwardDetail.from_dict(_expect_object(payload, "forward"))

    def list_forwards(
        self,
        *,
        limit: Optional[int] = None,
        cursor: Optional[str] = None,
        status: Optional[str] = None,
        ticker: Optional[str] = None,
    ) -> Page[Forward]:
        """One page of forwards, newest first (without the detail sub-collections)."""
        payload = self._request_json(
            "GET",
            "/api/v1/address",
            params={"limit": limit, "cursor": cursor, "status": status, "ticker": ticker},
        )
        return Page.from_dict(payload, Forward.from_dict)

    def iter_forwards(
        self,
        *,
        limit: Optional[int] = None,
        status: Optional[str] = None,
        ticker: Optional[str] = None,
    ) -> Iterator[Forward]:
        """Every forward across every page."""
        return auto_paginate(self.list_forwards, limit=limit, status=status, ticker=ticker)

    def update_forward(
        self,
        forward_id: str,
        *,
        status: Any = UNSET,
        callback_url: Any = UNSET,
    ) -> Forward:
        """The merchant kill switch -- ``PATCH /api/v1/address/{id}``.

        Only ``status`` (``active`` <-> ``paused``) and ``callback_url`` are mutable.
        ``destination`` and ``ticker`` are immutable and refused with a 409 *before* the
        forward is looked up: repointing a live deposit address is exactly what a leaked
        key would be used for.

        Pass ``callback_url=None`` to clear it; omit the argument to leave it alone.
        Changing it recomputes the dedupe identity, so it can collide (409
        ``already_exists``).  Does not accept ``Idempotency-Key``.
        """
        body: JsonDict = {}
        if not isinstance(status, _Unset):
            body["status"] = status
        if not isinstance(callback_url, _Unset):
            body["callback_url"] = callback_url
        if not body:
            raise ValueError("Supply status and/or callback_url; a patch with neither is a 400.")
        payload = self._request_json(
            "PATCH", "/api/v1/address/{}".format(_seg(forward_id)), body=body
        )
        return Forward.from_dict(_expect_object(payload, "forward"))

    def disable_forward(self, forward_id: str) -> Forward:
        """Soft-delete a forward (``status: disabled``).  Idempotent.

        The row is never removed: money can still arrive at a deposit address after you
        stop using it, and a deleted row would make that money unattributable.  Already
        credited funds still forward; no NEW deposit is credited.
        """
        payload = self._request_json("DELETE", "/api/v1/address/{}".format(_seg(forward_id)))
        return Forward.from_dict(_expect_object(payload, "forward"))

    # The forwarding resource lives at /api/v1/address, so both vocabularies are in use
    # in the wild.  These aliases mean neither one is wrong.
    create_address = create_forward
    get_address = get_forward
    list_addresses = list_forwards
    iter_addresses = iter_forwards
    update_address = update_forward
    disable_address = disable_forward

    # -- catalogue (public) ------------------------------------------------------------

    def list_tickers(self) -> Page[Ticker]:
        """Every ``(asset, chain)`` pair offered for forwarding.  No API key needed.

        A finite catalogue in ONE page: ``has_more`` is honestly ``False``.  A ticker
        listed here can still be refused at create time with ``bad_ticker``.
        """
        payload = self._request_json("GET", "/api/v1/tickers", auth=False)
        return Page.from_dict(payload, Ticker.from_dict)

    def list_assets(self) -> Page[Asset]:
        """The coins enabled for invoicing.  Display metadata -- see :class:`Asset`."""
        payload = self._request_json("GET", "/api/v1/assets", auth=False)
        return Page.from_dict(payload, Asset.from_dict)

    # -- payment links -----------------------------------------------------------------

    def create_payment_link(
        self,
        *,
        title: Optional[str] = None,
        amount_btc: Optional[AmountInput] = None,
        idempotency_key: Optional[str] = None,
    ) -> PaymentLink:
        """Create a payment link.  Omit ``amount_btc`` for an open-amount link."""
        body: JsonDict = {}
        if title is not None:
            body["title"] = title
        if amount_btc is not None:
            body["amount_btc"] = _amount(amount_btc, "amount_btc")
        payload = self._request_json(
            "POST", "/api/v1/payment-links", body=body, idempotency_key=idempotency_key
        )
        return PaymentLink.from_dict(_expect_object(payload, "payment link"))

    def get_payment_link(self, link_id: str) -> PaymentLink:
        """The merchant-side read -- unlike the public one, it shows inactive links."""
        payload = self._request_json("GET", "/api/v1/payment-links/{}".format(_seg(link_id)))
        return PaymentLink.from_dict(_expect_object(payload, "payment link"))

    def list_payment_links(
        self,
        *,
        limit: Optional[int] = None,
        cursor: Optional[str] = None,
        active: Optional[bool] = None,
    ) -> Page[PaymentLink]:
        payload = self._request_json(
            "GET",
            "/api/v1/payment-links",
            params={"limit": limit, "cursor": cursor, "active": active},
        )
        return Page.from_dict(payload, PaymentLink.from_dict)

    def iter_payment_links(
        self, *, limit: Optional[int] = None, active: Optional[bool] = None
    ) -> Iterator[PaymentLink]:
        return auto_paginate(self.list_payment_links, limit=limit, active=active)

    def update_payment_link(
        self,
        link_id: str,
        *,
        active: Any = UNSET,
        title: Any = UNSET,
        amount_btc: Any = UNSET,
    ) -> PaymentLink:
        """Update a link.  ``amount_btc=None`` converts a fixed link to open-amount.

        Setting ``active=False`` stops the link minting further invoices; invoices already
        created from it remain payable.  Deactivating is not a way to cancel money in
        flight.
        """
        body: JsonDict = {}
        if not isinstance(active, _Unset):
            body["active"] = bool(active)
        if not isinstance(title, _Unset):
            body["title"] = title
        if not isinstance(amount_btc, _Unset):
            body["amount_btc"] = None if amount_btc is None else _amount(amount_btc, "amount_btc")
        if not body:
            raise ValueError("Supply at least one of active, title or amount_btc.")
        payload = self._request_json(
            "PATCH", "/api/v1/payment-links/{}".format(_seg(link_id)), body=body
        )
        return PaymentLink.from_dict(_expect_object(payload, "payment link"))

    def deactivate_payment_link(self, link_id: str) -> PaymentLink:
        """Soft-deactivate a link.  Idempotent; the row is kept for attribution."""
        payload = self._request_json("DELETE", "/api/v1/payment-links/{}".format(_seg(link_id)))
        return PaymentLink.from_dict(_expect_object(payload, "payment link"))

    # -- recurring ---------------------------------------------------------------------

    def create_recurring(
        self,
        *,
        interval_days: int,
        title: Optional[str] = None,
        asset: Optional[str] = None,
        amount_fiat: Optional[AmountInput] = None,
        currency: Optional[str] = None,
        amount: Optional[AmountInput] = None,
        callback_url: Optional[str] = None,
        idempotency_key: Optional[str] = None,
    ) -> RecurringSchedule:
        """Create a billing schedule.  Provide EITHER ``amount_fiat`` OR ``amount``.

        Crypto has no pull mechanism, so this is scheduled billing, not auto-debit: each
        cycle GENERATES a fresh invoice and fires your webhook.  The first invoice appears
        on the next worker tick.
        """
        body: JsonDict = {"interval_days": int(interval_days)}
        if title is not None:
            body["title"] = title
        if asset is not None:
            body["asset"] = asset
        if amount_fiat is not None:
            body["amount_fiat"] = _amount(amount_fiat, "amount_fiat")
        if currency is not None:
            body["currency"] = currency.upper()
        if amount is not None:
            body["amount"] = _amount(amount, "amount")
        if amount_fiat is None and amount is None:
            raise ValueError("One of amount_fiat or amount is required.")
        if callback_url is not None:
            body["callback_url"] = callback_url
        payload = self._request_json(
            "POST", "/api/v1/recurring", body=body, idempotency_key=idempotency_key
        )
        return RecurringSchedule.from_dict(_expect_object(payload, "schedule"))

    def list_recurring(
        self, *, limit: Optional[int] = None, cursor: Optional[str] = None
    ) -> Page[RecurringSchedule]:
        payload = self._request_json(
            "GET", "/api/v1/recurring", params={"limit": limit, "cursor": cursor}
        )
        return Page.from_dict(payload, RecurringSchedule.from_dict)

    def iter_recurring(self, *, limit: Optional[int] = None) -> Iterator[RecurringSchedule]:
        return auto_paginate(self.list_recurring, limit=limit)

    def cancel_recurring(self, schedule_id: str) -> JsonDict:
        """Cancel a schedule.  Returns an ACK ``{ok, cancelled}``, not the resource."""
        payload = self._request_json(
            "POST", "/api/v1/recurring/{}/cancel".format(_seg(schedule_id))
        )
        return _expect_object(payload, "ack")

    # -- API keys ----------------------------------------------------------------------

    def list_api_keys(
        self, *, limit: Optional[int] = None, cursor: Optional[str] = None
    ) -> Page[ApiKey]:
        """Prefixes only -- the secret is never stored, so it can never be listed."""
        payload = self._request_json("GET", "/api/v1/keys", params={"limit": limit, "cursor": cursor})
        return Page.from_dict(payload, ApiKey.from_dict)

    def iter_api_keys(self, *, limit: Optional[int] = None) -> Iterator[ApiKey]:
        return auto_paginate(self.list_api_keys, limit=limit)

    def create_api_key(
        self, *, label: Optional[str] = None, idempotency_key: Optional[str] = None
    ) -> ApiKeyCreated:
        """Issue a key.  ``api_key`` is returned ONCE -- store it now.

        On an ``Idempotency-Key`` replay ``api_key`` is ``None`` (see
        :attr:`ApiKeyCreated.is_replay`); the ``id`` and ``prefix`` still identify what
        was created, so you can revoke it.
        """
        body: JsonDict = {}
        if label is not None:
            body["label"] = label
        payload = self._request_json(
            "POST", "/api/v1/keys", body=body, idempotency_key=idempotency_key
        )
        return ApiKeyCreated.from_dict(_expect_object(payload, "api key"))

    def revoke_api_key(self, key_id: str) -> JsonDict:
        """Revoke a key by its ``id`` -- NOT its prefix.  Returns ``{ok, revoked}``."""
        payload = self._request_json("POST", "/api/v1/keys/{}/revoke".format(_seg(key_id)))
        return _expect_object(payload, "ack")

    # -- webhooks: endpoints, deliveries, secret ---------------------------------------

    def list_webhook_endpoints(
        self, *, limit: Optional[int] = None, cursor: Optional[str] = None
    ) -> Page[WebhookEndpoint]:
        """Account-level endpoints.  They receive every INVOICE event -- never ``forward.*``,
        which go only to a forward's own ``callback_url``."""
        payload = self._request_json(
            "GET", "/api/v1/webhooks/endpoints", params={"limit": limit, "cursor": cursor}
        )
        return Page.from_dict(payload, WebhookEndpoint.from_dict)

    def iter_webhook_endpoints(self, *, limit: Optional[int] = None) -> Iterator[WebhookEndpoint]:
        return auto_paginate(self.list_webhook_endpoints, limit=limit)

    def create_webhook_endpoint(
        self, url: str, *, idempotency_key: Optional[str] = None
    ) -> WebhookEndpoint:
        """Register an endpoint (max 20 per account; the 21st is a 400 ``limit_exceeded``).

        The URL is normalised through the WHATWG parser, so what comes back may differ
        from what you sent -- a bare origin gains a trailing slash.
        """
        payload = self._request_json(
            "POST", "/api/v1/webhooks/endpoints", body={"url": url}, idempotency_key=idempotency_key
        )
        return WebhookEndpoint.from_dict(_expect_object(payload, "endpoint"))

    def update_webhook_endpoint(
        self, endpoint_id: str, *, active: Any = UNSET, url: Any = UNSET
    ) -> WebhookEndpoint:
        """Update an endpoint.  ``active=False`` is the pause switch."""
        body: JsonDict = {}
        if not isinstance(active, _Unset):
            body["active"] = bool(active)
        if not isinstance(url, _Unset):
            body["url"] = url
        if not body:
            raise ValueError("Supply active and/or url.")
        payload = self._request_json(
            "PATCH", "/api/v1/webhooks/endpoints/{}".format(_seg(endpoint_id)), body=body
        )
        return WebhookEndpoint.from_dict(_expect_object(payload, "endpoint"))

    def delete_webhook_endpoint(self, endpoint_id: str) -> JsonDict:
        """HARD delete -- the row is removed and a second call is a 404.

        Prefer ``update_webhook_endpoint(id, active=False)`` if you may want it back.
        """
        payload = self._request_json(
            "DELETE", "/api/v1/webhooks/endpoints/{}".format(_seg(endpoint_id))
        )
        return _expect_object(payload, "ack")

    def list_webhook_deliveries(
        self,
        *,
        limit: Optional[int] = None,
        cursor: Optional[str] = None,
        event: Optional[str] = None,
        status: Optional[str] = None,
    ) -> Page[WebhookDelivery]:
        """The delivery log for both families.  Exactly one id per row is non-null.

        ``event`` and ``status`` are validated server-side, so a typo is a 400 rather than
        a silently empty page.  The delivered body itself is not returned here.
        """
        payload = self._request_json(
            "GET",
            "/api/v1/webhooks/deliveries",
            params={"limit": limit, "cursor": cursor, "event": event, "status": status},
        )
        return Page.from_dict(payload, WebhookDelivery.from_dict)

    def iter_webhook_deliveries(
        self,
        *,
        limit: Optional[int] = None,
        event: Optional[str] = None,
        status: Optional[str] = None,
    ) -> Iterator[WebhookDelivery]:
        return auto_paginate(self.list_webhook_deliveries, limit=limit, event=event, status=status)

    def replay_webhook_delivery(self, delivery_id: str) -> JsonDict:
        """Re-queue a delivery.  The body is FROZEN.

        A replay re-sends the exact bytes built at enqueue time, with the same
        ``sequence`` and ``created_at``.  It does NOT reflect current state.
        """
        payload = self._request_json(
            "POST", "/api/v1/webhooks/deliveries/{}/replay".format(_seg(delivery_id))
        )
        return _expect_object(payload, "ack")

    def get_webhook_secret_state(self) -> WebhookSecretState:
        """Rotation metadata -- no secret material.

        The one question a running receiver needs answered: is the previous secret still
        acceptable (``prev_active``), and until when (``prev_expires_at``)?
        """
        payload = self._request_json("GET", "/api/v1/webhooks/secret")
        return WebhookSecretState.from_dict(_expect_object(payload, "secret state"))

    def rotate_webhook_secret(self, *, idempotency_key: Optional[str] = None) -> WebhookSecretRotated:
        """Mint a new signing secret.  Returned ONCE -- there is no read-back path.

        The previous secret stays acceptable until ``prev_expires_at``.  Receivify keeps
        SIGNING with the current secret only; the grace window is an instruction to your
        receiver, which is why you should feed both secrets to :meth:`verify_webhook`
        until the window closes.

        On an ``Idempotency-Key`` replay ``secret`` is ``None`` -- but the double-rotation
        the header exists to prevent is prevented regardless.
        """
        payload = self._request_json(
            "POST", "/api/v1/webhooks/secret/rotate", idempotency_key=idempotency_key
        )
        return WebhookSecretRotated.from_dict(_expect_object(payload, "rotation"))

    def send_test_webhook(self, *, url: Optional[str] = None) -> JsonDict:
        """Deliver a signed ``test`` event to your own registered, active endpoints.

        SSRF-safe by construction: only URLs already registered on the account are
        reachable.  Returns 200 even when every delivery failed -- compare ``delivered``
        with ``total``.
        """
        body: JsonDict = {}
        if url is not None:
            body["url"] = url
        payload = self._request_json("POST", "/api/v1/webhooks/test", body=body)
        return _expect_object(payload, "test result")

    # -- payouts, settings, summary ----------------------------------------------------

    def get_payouts(self) -> PayoutState:
        """The LIVE payout map plus every open (staged) change."""
        payload = self._request_json("GET", "/api/v1/payouts")
        return PayoutState.from_dict(_expect_object(payload, "payout state"))

    def stage_payout_changes(self, addresses: Mapping[str, str]) -> JsonDict:
        """Stage payout-address changes.  **Nothing is applied inline.**

        A change is staged, may require an emailed confirmation, and then waits out a
        timelock before the sweeper will send to it.  An empty string CLEARS an asset's
        address.  ``applied`` is non-empty only on a deployment with a zero timelock and
        email confirmation off.
        """
        payload = self._request_json(
            "PATCH", "/api/v1/payouts", body={"addresses": dict(addresses)}
        )
        return _expect_object(payload, "ack")

    def cancel_payout_change(self, change_id: str) -> JsonDict:
        """Cancel a staged change.  Already applied/cancelled is a 409."""
        payload = self._request_json(
            "POST", "/api/v1/payouts/changes/{}/cancel".format(_seg(change_id))
        )
        return _expect_object(payload, "ack")

    def get_settings(self) -> Settings:
        payload = self._request_json("GET", "/api/v1/settings")
        return Settings.from_dict(_expect_object(payload, "settings"))

    def update_settings(self, *, display_currency: Any = UNSET) -> JsonDict:
        """Update settings.  Returns an ACK carrying only what changed -- not ``Settings``."""
        body: JsonDict = {}
        if not isinstance(display_currency, _Unset):
            body["display_currency"] = display_currency
        if not body:
            raise ValueError("Supply at least one updatable setting.")
        payload = self._request_json("PATCH", "/api/v1/settings", body=body)
        return _expect_object(payload, "ack")

    def get_summary(self) -> JsonDict:
        """The dashboard composite.  An ad-hoc shape, not a resource -- returned as a dict.

        Note ``fx`` holds floating-point RATES, deliberately.  A rate is not money: nothing
        is ever settled against it.  Every actual amount in here is still a string.
        """
        payload = self._request_json("GET", "/api/v1/summary")
        return _expect_object(payload, "summary")

    # -- public / unauthenticated ------------------------------------------------------

    def get_public_invoice(self, invoice_id: str) -> PublicInvoice:
        """The checkout-page projection.  No API key sent.

        Safe to poll from a thank-you page.  Server-side, prefer :meth:`get_invoice`.
        """
        payload = self._request_json(
            "GET", "/api/public/invoices/{}".format(_seg(invoice_id)), auth=False
        )
        return PublicInvoice.from_dict(_expect_object(payload, "invoice"))

    def get_public_payment_link(self, link_id: str) -> JsonDict:
        """The public view of a link plus its payable coins.  A deactivated link is a 404."""
        payload = self._request_json("GET", "/api/public/links/{}".format(_seg(link_id)), auth=False)
        return _expect_object(payload, "payment link")

    def get_health(self) -> JsonDict:
        """The operator health report.

        **Does not raise on 503.**  A degraded health check answers with the same
        ``{status, time, checks, alerts}`` body as a 200 and no ``error`` key, so mapping
        it to an API exception would be a lie.  Read ``status`` yourself.
        """
        response = self.request("GET", "/api/health", auth=False, retry=False, raise_on_error=False)
        if response.status not in (200, 503):
            raise error_from_response(response.status, response.headers, _safe_json(response), response.text())
        return _expect_object(_safe_json(response), "health")

    # -- webhooks ----------------------------------------------------------------------

    def verify_webhook(
        self,
        raw_body: Union[str, bytes, bytearray],
        signature_header: Optional[str],
        secrets: SecretsInput = None,
        *,
        tolerance_seconds: Optional[int] = DEFAULT_TOLERANCE_SECONDS,
    ) -> bool:
        """Verify a delivery against the client's secrets (or the ones you pass).

        **Pass the RAW body**, not re-serialised JSON -- ``json.dumps()`` of a parsed body
        reorders keys and the HMAC will never match.  Django: ``request.body``.  Flask:
        ``request.get_data()``.
        """
        keys = normalise_secrets(secrets) or self._webhook_secrets
        return verify_webhook(
            raw_body, signature_header, keys, tolerance_seconds=tolerance_seconds
        )

    def parse_webhook(
        self,
        raw_body: Union[str, bytes, bytearray],
        signature_header: Optional[str],
        secrets: SecretsInput = None,
        *,
        tolerance_seconds: Optional[int] = DEFAULT_TOLERANCE_SECONDS,
    ) -> WebhookEvent:
        """Verify and decode in one step.

        :raises SignatureVerificationError: if it does not verify.
        """
        keys = normalise_secrets(secrets) or self._webhook_secrets
        return parse_webhook(
            raw_body, signature_header, keys, tolerance_seconds=tolerance_seconds
        )

    # -- low-level ---------------------------------------------------------------------

    def request(
        self,
        method: str,
        path: str,
        *,
        params: Optional[Mapping[str, Any]] = None,
        body: Any = None,
        idempotency_key: Optional[str] = None,
        auth: bool = True,
        retry: Optional[bool] = None,
        raise_on_error: bool = True,
        extra_headers: Optional[Mapping[str, str]] = None,
    ) -> Response:
        """Escape hatch: issue any request against this deployment.

        Useful for an endpoint added after this SDK build.  Returns the raw
        :class:`~receivify.http.Response`, including headers (``Idempotent-Replay``,
        ``X-RateLimit-*``).
        """
        url = self._api_url + path
        query = _encode_params(params)
        if query:
            url = "{}?{}".format(url, query)

        headers: Dict[str, str] = {
            "Accept": "application/json",
            "User-Agent": self._user_agent,
        }
        if auth and self._api_key:
            # Header, never a query parameter: URLs are logged, headers are not.
            headers["Authorization"] = "Bearer " + self._api_key
        if idempotency_key:
            headers["Idempotency-Key"] = idempotency_key
        if extra_headers:
            headers.update(extra_headers)

        encoded: Optional[bytes] = None
        if body is not None:
            encoded = json.dumps(body, separators=(",", ":"), allow_nan=False).encode("utf-8")
            headers["Content-Type"] = "application/json"

        request = Request(
            method=method.upper(),
            url=url,
            headers=headers,
            body=encoded,
            timeout=self._timeout,
        )
        retryable = self._is_retry_safe(request.method, idempotency_key) if retry is None else bool(retry)
        response = self._execute(request, retryable=retryable)
        if raise_on_error and response.status >= 400:
            raise error_from_response(
                response.status, response.headers, _safe_json(response), response.text()
            )
        return response

    # -- internals ---------------------------------------------------------------------

    def _request_json(
        self,
        method: str,
        path: str,
        *,
        params: Optional[Mapping[str, Any]] = None,
        body: Any = None,
        idempotency_key: Optional[str] = None,
        auth: bool = True,
    ) -> Any:
        response = self.request(
            method,
            path,
            params=params,
            body=body,
            idempotency_key=idempotency_key,
            auth=auth,
        )
        if not response.body:
            return None
        try:
            return response.json()
        except ValueError as exc:
            raise ReceivifyError(
                "Receivify returned a non-JSON body for {} {} (status {}).".format(
                    method.upper(), path, response.status
                )
            ) from exc

    @staticmethod
    def _is_retry_safe(method: str, idempotency_key: Optional[str]) -> bool:
        """Which requests may be repeated.

        A GET changes nothing.  A mutating call is safe ONLY with an ``Idempotency-Key``:
        without one, a retry after a timeout can create a second invoice or mint a second
        live API key.  That is not a hypothetical -- it is the bug the header exists for.
        """
        if method in ("GET", "HEAD"):
            return True
        return bool(idempotency_key)

    def _execute(self, request: Request, *, retryable: bool) -> Response:
        attempt = 0
        while True:
            try:
                response = self._transport.send(request)
            except ReceivifyTransportError:
                if retryable and attempt < self._max_retries:
                    self._sleep(self._backoff(attempt, None))
                    attempt += 1
                    continue
                raise
            if (
                response.status not in _RETRYABLE_STATUSES
                or not retryable
                or attempt >= self._max_retries
            ):
                return response
            self._sleep(self._backoff(attempt, response))
            attempt += 1

    def _backoff(self, attempt: int, response: Optional[Response]) -> float:
        """Exponential backoff with jitter, overridden by what the server tells us."""
        delay = min(_MAX_BACKOFF, _DEFAULT_BACKOFF * (2 ** attempt))
        server = _server_requested_wait(response)
        if server is not None:
            delay = min(_MAX_SERVER_WAIT, max(delay, server))
        # Full-ish jitter: many workers hitting one rate limit must not retry in lockstep.
        return delay + random.uniform(0.0, delay * 0.25)


# --- module helpers -------------------------------------------------------------------


def _seg(value: Any) -> str:
    """Percent-encode one path segment.  An id never gets to inject a path."""
    text = str(value)
    if not text:
        raise ValueError("An id is required.")
    return urllib.parse.quote(text, safe="")


def _encode_params(params: Optional[Mapping[str, Any]]) -> str:
    if not params:
        return ""
    pairs: List[tuple] = []
    for key, value in params.items():
        if value is None:
            continue
        if isinstance(value, bool):
            pairs.append((key, "true" if value else "false"))
        else:
            pairs.append((key, str(value)))
    return urllib.parse.urlencode(pairs)


def _amount(value: AmountInput, field_name: str) -> str:
    """Normalise a money input to a string, refusing floats.

    A ``float`` cannot hold 21 million BTC in satoshis, let alone a wei value, and
    ``json.dumps(0.1 + 0.2)`` is ``0.30000000000000004``.  Rejecting it here turns a
    silent truncation into a loud, fixable ``TypeError``.
    """
    if isinstance(value, float):
        raise TypeError(
            "{} must not be a float -- pass a string like '25.00' (or a Decimal). "
            "Floats cannot represent base-unit amounts exactly.".format(field_name)
        )
    if isinstance(value, Decimal):
        return format(value, "f")
    if isinstance(value, int) and not isinstance(value, bool):
        return str(value)
    if isinstance(value, str):
        text = value.strip()
        if not text:
            raise ValueError("{} must not be empty.".format(field_name))
        return text
    raise TypeError("{} must be a string, int or Decimal, got {}.".format(field_name, type(value).__name__))


def _expect_object(payload: Any, what: str) -> JsonDict:
    if not isinstance(payload, Mapping):
        raise ReceivifyError(
            "Expected a {} object in the response, got {}.".format(what, type(payload).__name__)
        )
    return dict(payload)


def _safe_json(response: Response) -> Any:
    try:
        return response.json()
    except ValueError:
        return None


def _server_requested_wait(response: Optional[Response]) -> Optional[float]:
    """Honour ``Retry-After``, else ``X-RateLimit-Reset`` (unix seconds)."""
    if response is None:
        return None
    retry_after = response.headers.get("Retry-After")
    if retry_after:
        try:
            return max(0.0, float(str(retry_after).strip()))
        except ValueError:
            pass
    reset = response.headers.get("X-RateLimit-Reset")
    if reset:
        try:
            return max(0.0, float(str(reset).strip()) - time.time())
        except ValueError:
            pass
    return None
