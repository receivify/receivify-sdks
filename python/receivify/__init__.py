"""Receivify -- the official Python SDK for the Receivify crypto payment gateway.

Zero runtime dependencies: standard library only (``urllib.request``, ``hmac``,
``hashlib``, ``json``).  Requires Python 3.9+.

Two calls carry almost every integration::

    from receivify import Receivify

    rcv = Receivify("https://receivify.io", "rcv_live_...", webhook_secrets=["whsec_..."])

    # 1. turn an order into a hosted checkout
    invoice = rcv.create_invoice(amount_fiat="25.00", currency="USD", reference="order-1001")

    # 2. authenticate the signed callback before trusting a word of it
    event = rcv.parse_webhook(request.body, request.headers["X-Receivify-Signature"])

``receivify.money`` holds opt-in ``Decimal`` helpers; it is deliberately NOT imported
here, because every amount in this SDK is a string and stays one until you say otherwise.
"""

from ._version import __version__
from .client import UNSET, Receivify
from .errors import (
    ERROR_CODES,
    AlreadyEnabledError,
    AlreadyExistsError,
    AmountTooLowError,
    AssetUnavailableError,
    BadDestinationError,
    BadRequestError,
    BadSignatureError,
    BadTickerError,
    InternalError,
    InvalidCodeError,
    InvalidTokenError,
    LimitExceededError,
    NoSetupError,
    NoTargetError,
    NotAllowedError,
    NotEnabledError,
    NotFoundError,
    RateLimitedError,
    RateUnavailableError,
    ReceivifyApiError,
    ReceivifyError,
    ReceivifyTimeoutError,
    ReceivifyTransportError,
    SignatureVerificationError,
    SignupDisabledError,
    SuspendedError,
    UnauthorizedError,
    UpstreamError,
    WebhookPayloadError,
    error_class_for_code,
)
from .http import Request, Response, Transport, UrllibTransport
from .pagination import Page, auto_paginate
from .resources import (
    EVENT_NAMES,
    FORWARD_STATUSES,
    INVOICE_STATUSES,
    ApiKey,
    ApiKeyCreated,
    Asset,
    Forward,
    ForwardCallback,
    ForwardDetail,
    ForwardPayment,
    Invoice,
    PaymentLink,
    PayoutChange,
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
    SIGNATURE_HEADER,
    ForwardEventData,
    InvoiceEventData,
    WebhookEvent,
    compute_signature,
    parse_signature_header,
    parse_webhook,
    verify_webhook,
)

__all__ = [
    "__version__",
    "Receivify",
    "UNSET",
    # transport
    "Request",
    "Response",
    "Transport",
    "UrllibTransport",
    # pagination
    "Page",
    "auto_paginate",
    # resources
    "ApiKey",
    "ApiKeyCreated",
    "Asset",
    "Forward",
    "ForwardCallback",
    "ForwardDetail",
    "ForwardPayment",
    "Invoice",
    "PaymentLink",
    "PayoutChange",
    "PayoutState",
    "PublicInvoice",
    "RecurringSchedule",
    "Settings",
    "Ticker",
    "WebhookDelivery",
    "WebhookEndpoint",
    "WebhookSecretRotated",
    "WebhookSecretState",
    "INVOICE_STATUSES",
    "FORWARD_STATUSES",
    "EVENT_NAMES",
    # webhooks
    "SIGNATURE_HEADER",
    "DEFAULT_TOLERANCE_SECONDS",
    "WebhookEvent",
    "InvoiceEventData",
    "ForwardEventData",
    "verify_webhook",
    "parse_webhook",
    "parse_signature_header",
    "compute_signature",
    # errors
    "ReceivifyError",
    "ReceivifyApiError",
    "ReceivifyTransportError",
    "ReceivifyTimeoutError",
    "SignatureVerificationError",
    "WebhookPayloadError",
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
]
