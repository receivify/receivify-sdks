"""Receivify + FastAPI: a complete, tiny shop.

    GET  /                       product page with a "Pay with crypto" form
    POST /checkout               creates the order + a Receivify invoice, redirects to checkout
    GET  /orders/{order_id}      order status page
    POST /webhooks/receivify     signed events from Receivify; the ONLY place an order is marked paid

Run:  cp .env.example .env (fill it in) -> pip install -r requirements.txt -> uvicorn main:app --port 8000
"""

import os
import threading
import uuid
from html import escape

from dotenv import load_dotenv
from fastapi import FastAPI, Form, HTTPException, Request
from fastapi.responses import HTMLResponse, PlainTextResponse, RedirectResponse

from receivify import (
    Receivify,
    ReceivifyApiError,
    SignatureVerificationError,
    WebhookPayloadError,
)

load_dotenv()

API_KEY = os.environ.get("RECEIVIFY_API_KEY", "")
WEBHOOK_SECRET = os.environ.get("RECEIVIFY_WEBHOOK_SECRET", "")
API_URL = os.environ.get("RECEIVIFY_API_URL", "https://receivify.io")
if not API_KEY or not WEBHOOK_SECRET:
    raise SystemExit("Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET in .env (see .env.example).")

# One client for the app. A LIST of secrets: new first, old second during a rotation.
receivify = Receivify(
    API_URL,
    API_KEY,
    webhook_secrets=[s.strip() for s in WEBHOOK_SECRET.split(",") if s.strip()],
)

PRODUCT = {"name": "Receivify T-shirt", "price": "25.00", "currency": "GBP"}  # strings, never floats

# `chain` picks the network for tokens that live on several. GET /api/v1/assets lists what's live.
COINS = {
    "btc": {"label": "Bitcoin", "asset": "btc"},
    "ltc": {"label": "Litecoin", "asset": "ltc"},
    "eth": {"label": "Ethereum", "asset": "eth", "chain": "ethereum"},
    "usdc-base": {"label": "USDC on Base", "asset": "usdc", "chain": "base"},
    "usdt-tron": {"label": "USDT on Tron", "asset": "usdt", "chain": "tron"},
    "sol": {"label": "Solana", "asset": "sol", "chain": "solana"},
}

# invoice.paid is THE signal (full amount, confirmed, irreversible). invoice.swept follows it.
STATUS_FOR_EVENT = {
    "invoice.confirming": "confirming",
    "invoice.partial": "partially_paid",
    "invoice.paid": "paid",
    "invoice.swept": "paid",
    "invoice.expired": "expired",
    "invoice.partial_expired": "underpaid",
    "invoice.cancelled": "cancelled",
    "invoice.failed": "failed",
}
STATUS_TEXT = {
    "pending": "Waiting for payment",
    "confirming": "Payment seen, waiting for confirmations",
    "partially_paid": "Part of the payment has arrived",
    "paid": "Paid. Thank you!",
    "expired": "The payment window closed",
    "underpaid": "Underpaid. We will be in touch",
    "cancelled": "Cancelled",
    "failed": "Payment failed",
}

# --- demo order store: in memory, guarded by a lock. Use your database in a real shop. ---
ORDERS: dict[str, dict] = {}
LOCK = threading.Lock()

app = FastAPI(title="Receivify FastAPI example")


@app.get("/", response_class=HTMLResponse)
def shop():
    options = "".join(f'<option value="{k}">{escape(c["label"])}</option>' for k, c in COINS.items())
    return page(
        "Shop",
        f"""<h1>{PRODUCT["name"]}</h1><p class="price">£{PRODUCT["price"]}</p>
        <form method="post" action="/checkout">
          <label>Pay with <select name="coin">{options}</select></label>
          <label class="check"><input type="checkbox" name="fee_ontop" value="1"> Customer pays the fee on top</label>
          <button>Pay with crypto</button>
        </form>""",
    )


# A plain `def` (not `async def`): the SDK client is synchronous, so FastAPI runs this in
# its thread pool and the event loop never blocks on the HTTP call to Receivify.
@app.post("/checkout")
def checkout(coin: str = Form("btc"), fee_ontop: str | None = Form(None)):
    choice = COINS.get(coin, COINS["btc"])
    order_id = "fastapi-" + uuid.uuid4().hex[:8]  # prefixed: `reference` is also a de-dup key
    with LOCK:
        ORDERS[order_id] = {"id": order_id, "product": PRODUCT["name"], "amount": PRODUCT["price"],
                            "status": "pending", "invoice_id": None, "last_seq": 0}
    try:
        invoice = receivify.create_invoice(
            amount_fiat=PRODUCT["price"],  # converted at today's rate and locked
            currency=PRODUCT["currency"],
            asset=choice["asset"],
            chain=choice.get("chain"),
            reference=order_id,  # comes back on every webhook as data["reference"]
            metadata={"product": PRODUCT["name"], "source": "fastapi-example"},
            add_fee_ontop=True if fee_ontop else None,  # None = your account default
            idempotency_key=f"checkout-{order_id}",  # a retry returns the same invoice
        )
    except ReceivifyApiError as err:
        raise HTTPException(400, detail={"error": err.code, "detail": err.detail})

    with LOCK:
        ORDERS[order_id]["invoice_id"] = invoice.id
    return RedirectResponse(invoice.checkout_url, status_code=303)


@app.post("/webhooks/receivify")
async def webhook(request: Request):
    # The raw bytes, exactly as sent: the signature is an HMAC over them. Declaring a
    # Pydantic body model here would parse and re-serialise it and break verification.
    raw_body = await request.body()
    try:
        # Constant-time HMAC check + 5-minute replay window + JSON parse.
        event = receivify.parse_webhook(raw_body, request.headers.get("x-receivify-signature"))
    except (SignatureVerificationError, WebhookPayloadError):
        return PlainTextResponse("bad signature", status_code=400)

    if event.event == "test":
        print("Test webhook received and verified.")
        return PlainTextResponse("ok")
    if not event.event.startswith("invoice."):
        return PlainTextResponse("ok")  # not about an invoice, or an event added later

    data = event.data
    with LOCK:
        order = ORDERS.get(data.get("reference") or "")
        # Unknown (made elsewhere), a duplicate, or an older event arriving late: acknowledge
        # and do nothing. `sequence` only increases, which makes this check sufficient.
        if order is None or event.sequence <= order["last_seq"]:
            return PlainTextResponse("ok")
        order["last_seq"] = event.sequence
        new_status = STATUS_FOR_EVENT.get(event.event)
        # Paid is final for fulfilment; never move an order back out of it.
        if new_status and order["status"] != "paid":
            order["status"] = new_status
            order["invoice_id"] = data.get("id")
            if new_status == "paid":
                pass  # fulfil here, quickly, or enqueue the work

    return PlainTextResponse("ok")  # 2xx fast; anything else is retried with backoff


@app.get("/orders/{order_id}", response_class=HTMLResponse)
def order_page(order_id: str):
    order = ORDERS.get(order_id)
    if order is None:
        raise HTTPException(404)
    # Display only. Being redirected here proves nothing; the webhook is the source of truth.
    refresh = "" if order["status"] == "paid" else '<meta http-equiv="refresh" content="5"><p class="muted">Refreshes every 5 seconds.</p>'
    return page(
        f"Order {order_id}",
        f"""<h1>Order {escape(order_id)}</h1><p>{escape(order["product"])}: £{escape(order["amount"])}</p>
        <p class="status {order["status"]}">{escape(STATUS_TEXT.get(order["status"], order["status"]))}</p>
        {refresh}<p><a href="/">Back to the shop</a></p>""",
    )


def page(title: str, body: str) -> str:
    return f"""<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{escape(title)}</title><style>body{{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}}
.price{{font-size:2rem;font-weight:700}}label{{display:block;margin:1rem 0}}button{{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}}
.check{{font-size:.9rem}}.status{{font-weight:700}}.paid{{color:#4d7c0f}}.muted{{color:#71717a;font-size:.9rem}}</style></head><body>{body}</body></html>"""
