"""The HTTP layer: an injectable transport, plus the stdlib implementation of it.

Nothing here imports anything outside the standard library.  :class:`Transport` is a
``typing.Protocol``, so a test double only has to expose ``send()`` -- no subclassing, no
network, no mocking of ``urllib`` internals.

    >>> class Recorder:
    ...     def __init__(self): self.requests = []
    ...     def send(self, request):
    ...         self.requests.append(request)
    ...         return Response(200, {"Content-Type": "application/json"}, b"{}")
    >>> client = Receivify("https://receivify.io", "rcv_live_x", transport=Recorder())
"""

from __future__ import annotations

import json
import socket
import ssl
import urllib.error
import urllib.request
from collections.abc import Mapping as _AbcMapping
from dataclasses import dataclass, field
from typing import Any, Dict, Iterable, Iterator, Mapping, Optional, Tuple

try:  # pragma: no cover - typing_extensions is not a dependency
    from typing import Protocol, runtime_checkable
except ImportError:  # pragma: no cover - Python < 3.8 only
    Protocol = object  # type: ignore[assignment]

    def runtime_checkable(cls):  # type: ignore[misc]
        return cls


from .errors import ReceivifyTimeoutError, ReceivifyTransportError

__all__ = ["Headers", "Request", "Response", "Transport", "UrllibTransport"]


class Headers(_AbcMapping):
    """A read-only, case-insensitive header mapping that remembers original casing."""

    __slots__ = ("_items",)

    def __init__(self, items: Any = None) -> None:
        self._items: Dict[str, Tuple[str, str]] = {}
        if items is None:
            pairs: Iterable[Tuple[str, str]] = ()
        elif hasattr(items, "items"):
            pairs = items.items()
        else:
            pairs = items
        for key, value in pairs:
            self._items[str(key).lower()] = (str(key), str(value))

    def __getitem__(self, key: str) -> str:
        return self._items[str(key).lower()][1]

    def __iter__(self) -> Iterator[str]:
        return iter(original for original, _ in self._items.values())

    def __len__(self) -> int:
        return len(self._items)

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return "Headers({!r})".format(dict(self.items()))


@dataclass(frozen=True)
class Request:
    """One outbound HTTP request, fully built.

    The API key lives in ``headers['Authorization']`` and is NEVER in :attr:`url` -- URLs
    end up in proxy logs, access logs and error trackers.
    """

    method: str
    url: str
    headers: Mapping[str, str] = field(default_factory=dict)
    body: Optional[bytes] = None
    timeout: float = 30.0

    def __repr__(self) -> str:
        """Redacts ``Authorization`` so a traceback can never leak the API key."""
        safe = {k: ("<redacted>" if k.lower() == "authorization" else v) for k, v in self.headers.items()}
        return "Request(method={!r}, url={!r}, headers={!r}, body={!r})".format(
            self.method, self.url, safe, self.body
        )


@dataclass(frozen=True)
class Response:
    """One inbound HTTP response.  ``headers`` is case-insensitive."""

    status: int
    headers: Mapping[str, str] = field(default_factory=Headers)
    body: bytes = b""

    def __post_init__(self) -> None:
        if not isinstance(self.headers, Headers):
            object.__setattr__(self, "headers", Headers(self.headers))

    def text(self) -> str:
        try:
            return self.body.decode("utf-8")
        except UnicodeDecodeError:
            return self.body.decode("utf-8", "replace")

    def json(self) -> Any:
        """Decode the body as JSON.  Raises ``ValueError`` on a non-JSON body."""
        if not self.body:
            return None
        return json.loads(self.text())


@runtime_checkable
class Transport(Protocol):  # pragma: no cover - structural type
    """Anything that can turn a :class:`Request` into a :class:`Response`.

    Implementations MUST NOT raise on a 4xx/5xx -- return the response and let the client
    map it to a typed exception.  Raise :class:`~receivify.errors.ReceivifyTransportError`
    only when no response was produced at all.
    """

    def send(self, request: Request) -> Response:
        ...


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    """Refuse to follow redirects.

    Following a 30x would re-send ``Authorization: Bearer rcv_live_...`` to whatever host
    the ``Location`` header names.  A redirect surfaces as a plain 3xx response instead.
    """

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: D102
        return None


class UrllibTransport:
    """The default transport: :mod:`urllib.request` from the standard library."""

    def __init__(self, *, ssl_context: Optional[ssl.SSLContext] = None) -> None:
        self._opener = urllib.request.build_opener(
            _NoRedirect(),
            urllib.request.HTTPSHandler(context=ssl_context) if ssl_context else urllib.request.HTTPSHandler(),
        )

    def send(self, request: Request) -> Response:
        req = urllib.request.Request(request.url, data=request.body, method=request.method.upper())
        for name, value in request.headers.items():
            req.add_header(name, value)
        try:
            with self._opener.open(req, timeout=request.timeout) as raw:
                return Response(getattr(raw, "status", raw.getcode()), dict(raw.headers.items()), raw.read())
        except urllib.error.HTTPError as exc:  # a real response with a >=400 status
            body = b""
            try:
                body = exc.read()
            except Exception:  # pragma: no cover - body already consumed
                pass
            return Response(exc.code, dict(exc.headers.items()) if exc.headers else {}, body)
        except socket.timeout as exc:  # pragma: no cover - timing dependent
            raise ReceivifyTimeoutError("Receivify request timed out.", cause=exc) from exc
        except urllib.error.URLError as exc:
            reason = getattr(exc, "reason", exc)
            if isinstance(reason, (socket.timeout, TimeoutError)):
                raise ReceivifyTimeoutError("Receivify request timed out.", cause=exc) from exc
            raise ReceivifyTransportError("Receivify request failed: {}".format(reason), cause=exc) from exc
        except (OSError, TimeoutError) as exc:  # pragma: no cover - platform dependent
            raise ReceivifyTransportError("Receivify request failed: {}".format(exc), cause=exc) from exc

    def close(self) -> None:
        closer = getattr(self._opener, "close", None)
        if callable(closer):
            closer()
