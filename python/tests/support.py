"""Test doubles.  No network, no monkeypatching -- just the injected transport seam."""

from __future__ import annotations

import json
from typing import Any, Dict, List, Mapping, Optional, Sequence, Union

from receivify import Receivify, Request, Response


def json_response(
    payload: Any = None,
    *,
    status: int = 200,
    headers: Optional[Mapping[str, str]] = None,
    body: Optional[str] = None,
) -> Response:
    """Build a canned JSON response.

    ``body`` wins over ``payload`` so a test can hand back deliberately broken bytes.
    """
    raw = body if body is not None else ("" if payload is None else json.dumps(payload))
    merged: Dict[str, str] = {"Content-Type": "application/json"}
    merged.update(dict(headers or {}))
    return Response(status, merged, raw.encode("utf-8"))


class FakeTransport:
    """Records every request and replays a scripted queue of responses.

    A queued entry may be an ``Exception``, which is raised instead -- that is how the
    retry tests simulate a connection failure.
    """

    def __init__(self, responses: Optional[Sequence[Union[Response, Exception]]] = None) -> None:
        self.requests: List[Request] = []
        self.responses: List[Union[Response, Exception]] = list(responses or [])
        self.closed = False

    def send(self, request: Request) -> Response:
        self.requests.append(request)
        if not self.responses:
            raise AssertionError(
                "FakeTransport ran out of canned responses at {} {}".format(request.method, request.url)
            )
        item = self.responses.pop(0)
        if isinstance(item, Exception):
            raise item
        return item

    def close(self) -> None:
        self.closed = True

    # -- assertions helpers ------------------------------------------------------------

    @property
    def last_request(self) -> Request:
        return self.requests[-1]

    def body_of(self, index: int = -1) -> Any:
        raw = self.requests[index].body
        return None if raw is None else json.loads(raw.decode("utf-8"))


def make_client(
    responses: Optional[Sequence[Union[Response, Exception]]] = None,
    **kwargs: Any,
) -> "tuple":
    """Return ``(client, transport)`` wired together, with sleeping disabled."""
    transport = FakeTransport(responses)
    slept: List[float] = []
    client = Receivify(
        kwargs.pop("api_url", "https://receivify.io"),
        kwargs.pop("api_key", "rcv_live_secretkey"),
        transport=transport,
        sleep=slept.append,
        **kwargs,
    )
    client.slept = slept  # type: ignore[attr-defined]
    return client, transport


# --- canned payloads ------------------------------------------------------------------

INVOICE: Dict[str, Any] = {
    "id": "8d1f3d4e-0000-4000-8000-000000000001",
    "reference": "order-1001",
    "asset": "btc",
    # 21 million BTC in satoshis; a float would round this.
    "amount_expected": "2100000000000000",
    "amount_received": "0",
    "amount_fiat": "25.00",
    "fiat_currency": "USD",
    "chain": None,
    "provider": "bitcoind",
    "address": "bc1qexampleaddress",
    "status": "pending",
    "expires_at": "2026-08-25T12:00:00.000Z",
    "paid_at": None,
    "created_at": "2026-08-25T11:00:00.000Z",
    "metadata": {"cart": "abc"},
    "checkout_url": "https://receivify.io/pay/8d1f3d4e-0000-4000-8000-000000000001",
}

FORWARD: Dict[str, Any] = {
    "id": "11111111-2222-4333-8444-555555555555",
    "forward_id": "11111111-2222-4333-8444-555555555555",
    "ticker": "btc",
    "provider": "bitcoind",
    "chain": None,
    "address": "bc1qdepositaddress",
    "destination": "bc1qmywallet",
    "callback_url": "https://shop.example/hooks/receivify",
    "confirmations_required": 2,
    "min_forward": "0.000100",
    "status": "active",
    "expires_at": None,
    "created_at": "2026-08-25T11:00:00.000Z",
    "updated_at": None,
}
