"""Typed exceptions for the Receivify API.

Every failure the API can report arrives as ``{"error": "<code>", "detail": "<sentence>"}``
(``openapi.yaml`` -> ``components.schemas.Error``).  The ``error`` code is a frozen enum;
this module maps each known code to its own exception class.

**An unknown code never crashes an older SDK.**  A code this build has never heard of is
raised as the base :class:`ReceivifyApiError` with ``.code`` carrying the raw string, so a
future additive change to the API degrades to a generic-but-informative exception instead
of a ``KeyError``.

Exception hierarchy::

    ReceivifyError                     every error this SDK raises
     +- ReceivifyTransportError        the request never produced an HTTP response
     |   +- ReceivifyTimeoutError
     +- SignatureVerificationError     a webhook signature did not verify
     +- WebhookPayloadError            a verified webhook body was not valid JSON
     +- ReceivifyApiError              the API answered with a 4xx/5xx
         +- BadRequestError            ... one subclass per frozen error code
         +- UnauthorizedError
         +- NotFoundError
         +- ...
"""

from __future__ import annotations

from typing import Any, Dict, Mapping, Optional, Type

__all__ = [
    "ReceivifyError",
    "ReceivifyTransportError",
    "ReceivifyTimeoutError",
    "SignatureVerificationError",
    "WebhookPayloadError",
    "ReceivifyApiError",
    "BadRequestError",
    "BadTickerError",
    "BadDestinationError",
    "AssetUnavailableError",
    "AmountTooLowError",
    "SuspendedError",
    "LimitExceededError",
    "InvalidCodeError",
    "NoSetupError",
    "NotEnabledError",
    "NoTargetError",
    "InvalidTokenError",
    "UnauthorizedError",
    "BadSignatureError",
    "SignupDisabledError",
    "NotFoundError",
    "AlreadyExistsError",
    "AlreadyEnabledError",
    "NotAllowedError",
    "RateLimitedError",
    "InternalError",
    "UpstreamError",
    "RateUnavailableError",
    "ERROR_CODES",
    "error_class_for_code",
    "error_from_response",
]


class ReceivifyError(Exception):
    """Base class for every error raised by this SDK."""


class ReceivifyTransportError(ReceivifyError):
    """The request never produced an HTTP response (DNS, TLS, connection, socket)."""

    def __init__(self, message: str, *, cause: Optional[BaseException] = None) -> None:
        super().__init__(message)
        self.cause = cause


class ReceivifyTimeoutError(ReceivifyTransportError):
    """The request exceeded the configured timeout."""


class SignatureVerificationError(ReceivifyError):
    """A webhook signature did not verify against any of the supplied secrets."""


class WebhookPayloadError(ReceivifyError):
    """A webhook body verified but was not a JSON object."""


class ReceivifyApiError(ReceivifyError):
    """The API answered with a 4xx or 5xx.

    Also the fallback for an ``error`` code this SDK build does not recognise -- the raw
    code is always available as :attr:`code`, so forward compatibility costs nothing.
    """

    #: Codes whose subclass sets this to ``True`` are worth retrying as-is.
    retryable: bool = False

    def __init__(
        self,
        code: Optional[str],
        detail: str = "",
        *,
        status: int = 0,
        headers: Optional[Mapping[str, str]] = None,
        body: Optional[str] = None,
    ) -> None:
        message = "{} ({} {})".format(detail or "Receivify API error", status, code or "no code")
        super().__init__(message)
        #: The raw ``error`` code, or ``None`` when the body was not an ``{error, detail}``
        #: object (``GET /api/health`` documents itself as non-conforming, and an
        #: intermediary proxy can return HTML).
        self.code = code
        #: The human sentence.  For a log or a developer -- never branch on the wording.
        self.detail = detail
        #: The HTTP status code.
        self.status = status
        #: Response headers, for ``Retry-After`` / ``X-RateLimit-Reset``.
        self.headers: Mapping[str, str] = headers or {}
        #: The raw response body, truncated, when it could not be parsed as an error.
        self.body = body

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return "{}(code={!r}, status={!r}, detail={!r})".format(
            type(self).__name__, self.code, self.status, self.detail
        )

    @property
    def retry_after(self) -> Optional[int]:
        """Seconds the server asked us to wait, from ``Retry-After``."""
        return _header_int(self.headers, "Retry-After")

    @property
    def rate_limit_reset(self) -> Optional[int]:
        """Unix seconds at which the current rate-limit window resets."""
        return _header_int(self.headers, "X-RateLimit-Reset")


# --- one class per frozen error code -------------------------------------------------
# Ordered exactly as the enum in openapi.yaml (components.schemas.Error.error).


class BadRequestError(ReceivifyApiError):
    """400 -- malformed request: bad JSON, a missing field, or a failed validation."""


class BadTickerError(ReceivifyApiError):
    """400 -- the forwarding ticker is unknown, misspelled, or not offered here."""


class BadDestinationError(ReceivifyApiError):
    """400 -- the destination address is not valid for that ticker/chain."""


class AssetUnavailableError(ReceivifyApiError):
    """400 -- the asset is enabled but its settlement backend cannot accept it now."""


class AmountTooLowError(ReceivifyApiError):
    """400 -- below the economic floor: forwarding gas would exceed the fee earned."""


class SuspendedError(ReceivifyApiError):
    """400 -- the merchant account is suspended and cannot create new resources."""


class LimitExceededError(ReceivifyApiError):
    """400 -- a per-account quota for this resource is already at its maximum."""


class InvalidCodeError(ReceivifyApiError):
    """400 -- the supplied two-factor (TOTP) code is not valid."""


class NoSetupError(ReceivifyApiError):
    """400 -- two-factor enrolment has not been started yet."""


class NotEnabledError(ReceivifyApiError):
    """400 -- two-factor authentication is not enabled on this account."""


class NoTargetError(ReceivifyApiError):
    """400 -- no registered, active webhook endpoint matched the request."""


class InvalidTokenError(ReceivifyApiError):
    """400 -- a single-use confirmation token is invalid, expired, or already spent."""


class UnauthorizedError(ReceivifyApiError):
    """401 -- missing, malformed, revoked or unknown API key."""


class BadSignatureError(ReceivifyApiError):
    """401 -- an inbound provider webhook failed HMAC verification.

    Never returned to merchant callers; present for completeness of the frozen set.
    """


class SignupDisabledError(ReceivifyApiError):
    """403 -- self-serve signup is turned off on this deployment."""


class NotFoundError(ReceivifyApiError):
    """404 -- no such resource, or it does not belong to the caller.

    The two cases are deliberately indistinguishable.
    """


class AlreadyExistsError(ReceivifyApiError):
    """409 -- a resource with that unique identity already exists."""


class AlreadyEnabledError(ReceivifyApiError):
    """409 -- the feature being enabled is already on."""


class NotAllowedError(ReceivifyApiError):
    """409 -- the resource exists but its state forbids this action."""


class RateLimitedError(ReceivifyApiError):
    """429 -- back off using ``Retry-After`` / ``X-RateLimit-Reset``."""

    retryable = True


class InternalError(ReceivifyApiError):
    """500 -- an unhandled server error.  Safe to retry; report it if it persists."""

    retryable = True


class UpstreamError(ReceivifyApiError):
    """502 -- a node or payment provider we depend on failed.  Retryable."""

    retryable = True


class RateUnavailableError(ReceivifyApiError):
    """503 -- no fresh FX rate is available to price the request.  Retryable."""

    retryable = True


#: Frozen code -> exception class.  Adding a code upstream is an additive change; a code
#: missing from this table falls back to :class:`ReceivifyApiError`.
ERROR_CODES: Dict[str, Type[ReceivifyApiError]] = {
    "bad_request": BadRequestError,
    "bad_ticker": BadTickerError,
    "bad_destination": BadDestinationError,
    "asset_unavailable": AssetUnavailableError,
    "amount_too_low": AmountTooLowError,
    "suspended": SuspendedError,
    "limit_exceeded": LimitExceededError,
    "invalid_code": InvalidCodeError,
    "no_setup": NoSetupError,
    "not_enabled": NotEnabledError,
    "no_target": NoTargetError,
    "invalid_token": InvalidTokenError,
    "unauthorized": UnauthorizedError,
    "bad_signature": BadSignatureError,
    "signup_disabled": SignupDisabledError,
    "not_found": NotFoundError,
    "already_exists": AlreadyExistsError,
    "already_enabled": AlreadyEnabledError,
    "not_allowed": NotAllowedError,
    "rate_limited": RateLimitedError,
    "internal": InternalError,
    "upstream_error": UpstreamError,
    "rate_unavailable": RateUnavailableError,
}


def error_class_for_code(code: Optional[str]) -> Type[ReceivifyApiError]:
    """Return the exception class for ``code``, or the base class if it is unknown."""
    if not code:
        return ReceivifyApiError
    return ERROR_CODES.get(code, ReceivifyApiError)


def error_from_response(
    status: int,
    headers: Optional[Mapping[str, str]],
    payload: Any,
    raw_body: Optional[str] = None,
) -> ReceivifyApiError:
    """Build the right exception for an error response.

    ``payload`` is the decoded JSON body, or ``None`` when it could not be decoded.  A body
    that is not an ``{error, detail}`` object still produces a usable exception, with
    ``code`` set to ``None`` and the (truncated) body attached.
    """
    code: Optional[str] = None
    detail = ""
    if isinstance(payload, Mapping):
        raw_code = payload.get("error")
        if isinstance(raw_code, str):
            code = raw_code
        raw_detail = payload.get("detail")
        if isinstance(raw_detail, str):
            detail = raw_detail
    if not detail:
        detail = _DEFAULT_DETAIL.get(status, "Receivify request failed.")
    cls = error_class_for_code(code)
    return cls(
        code,
        detail,
        status=status,
        headers=headers or {},
        body=(raw_body[:500] if raw_body else None),
    )


_DEFAULT_DETAIL = {
    400: "The request was rejected.",
    401: "Unauthorized.",
    403: "Forbidden.",
    404: "Not found.",
    409: "Not allowed in the resource's current state.",
    429: "Rate limited.",
    500: "Unexpected error.",
    502: "Upstream error.",
    503: "Service unavailable.",
}


def _header_int(headers: Mapping[str, str], name: str) -> Optional[int]:
    for key, value in headers.items():
        if key.lower() == name.lower():
            try:
                return int(str(value).strip())
            except (TypeError, ValueError):
                return None
    return None
