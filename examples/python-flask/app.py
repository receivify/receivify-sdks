"""Receivify + Flask: a complete, tiny shop.

    GET  /                       product page with a "Pay with crypto" form
    POST /checkout               creates the order + a Receivify invoice, redirects to checkout
    GET  /orders/<id>            order status page
    POST /webhooks/receivify     signed events from Receivify; the ONLY place an order is marked paid

Run:  cp .env.example .env (fill it in) -> pip install -r requirements.txt -> flask --app app run --port 5000
"""

import json
import os
import threading
import uuid
from html import escape
from pathlib import Path

from dotenv import load_dotenv
from flask import Flask, abort, redirect, request

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

# One client for the app. webhook_secrets is a LIST: during a rotation put the new secret
# first and the old one second ("whsec_new,whsec_old") so no delivery is rejected.
receivify = Receivify(
    API_URL,
    API_KEY,
    webhook_secrets=[s.strip() for s in WEBHOOK_SECRET.split(",") if s.strip()],
)

# What we sell. Prices are strings: money never goes through a float.
PRODUCT = {"name": "Receivify T-shirt", "price": "25.00", "currency": "GBP"}

# `chain` picks the network for tokens that live on several. GET /api/v1/assets lists what's live.
COINS = {
    "btc": {"label": "Bitcoin", "asset": "btc"},
    "ltc": {"label": "Litecoin", "asset": "ltc"},
    "eth": {"label": "Ethereum", "asset": "eth", "chain": "ethereum"},
    "usdc-base": {"label": "USDC on Base", "asset": "usdc", "chain": "base"},
    "usdt-tron": {"label": "USDT on Tron", "asset": "usdt", "chain": "tron"},
    "sol": {"label": "Solana", "asset": "sol", "chain": "solana"},
}

# invoice.paid is THE signal: the full amount has its confirmations and can't be reversed.
# invoice.swept comes after it (funds forwarded to your wallet), so it also means paid.
STATUS_FOR_EVENT = {
    "invoice.confirming": "confirming",  # seen on-chain, not final yet: don't ship
    "invoice.partial": "partially_paid",
    "invoice.paid": "paid",
    "invoice.swept": "paid",
    "invoice.expired": "expired",
    "invoice.partial_expired": "underpaid",  # time ran out on an under-payment
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

# --- a deliberately tiny order store ------------------------------------------------------
# A JSON file so orders survive a restart while you test. In a real shop this is your
# database table. The lock matters: Flask can serve requests on several threads.
STORE = Path(__file__).with_name("orders.json")
_lock = threading.Lock()


def _load():
    return json.loads(STORE.read_text()) if STORE.exists() else {}


def create_order():
    with _lock:
        orders = _load()
        # Prefixed so it can't collide with another shop on the same Receivify account:
        # `reference` is also a de-duplication key on Receivify's side.
        order_id = "flask-" + uuid.uuid4().hex[:8]
        orders[order_id] = {
            "id": order_id,
            "product": PRODUCT["name"],
            "amount": PRODUCT["price"],
            "status": "pending",
            "invoice_id": None,
            "last_seq": 0,
        }
        STORE.write_text(json.dumps(orders, indent=2))
        return orders[order_id]


def get_order(order_id):
    return _load().get(order_id)


def update_order(order_id, **changes):
    with _lock:
        orders = _load()
        if order_id in orders:
            orders[order_id].update(changes)
            STORE.write_text(json.dumps(orders, indent=2))


# --- the app --------------------------------------------------------------------------------
app = Flask(__name__)


@app.get("/")
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


@app.post("/checkout")
def checkout():
    coin = COINS.get(request.form.get("coin"), COINS["btc"])
    order = create_order()
    try:
        invoice = receivify.create_invoice(
            amount_fiat=PRODUCT["price"],  # converted at today's rate and locked on the invoice
            currency=PRODUCT["currency"],
            asset=coin["asset"],
            chain=coin.get("chain"),
            # Comes back on every webhook as data["reference"]: that's how we find the order.
            reference=order["id"],
            metadata={"product": PRODUCT["name"], "source": "flask-example"},
            # True = the customer covers the fee and you receive the full price.
            # None (the default) uses your account setting.
            add_fee_ontop=True if request.form.get("fee_ontop") else None,
            # Same key = same invoice, so a double submit or a retry can't create two.
            idempotency_key=f"checkout-{order['id']}",
        )
    except ReceivifyApiError as err:
        # err.code is stable (amount_too_low, asset_unavailable, ...); err.detail is for humans.
        return page("Could not start payment", f"<p>{escape(err.detail or err.code or 'error')}</p><p><a href='/'>Back</a></p>"), 400

    update_order(order["id"], invoice_id=invoice.id)
    # 303: the browser follows with a GET to Receivify's hosted checkout.
    return redirect(invoice.checkout_url, code=303)


@app.post("/webhooks/receivify")
def webhook():
    # get_data() is the body EXACTLY as sent. The signature is an HMAC over these bytes;
    # request.json + json.dumps() would re-serialise it and the check would fail.
    raw_body = request.get_data()
    try:
        # Constant-time HMAC check + 5-minute replay window + JSON parse.
        event = receivify.parse_webhook(raw_body, request.headers.get("X-Receivify-Signature"))
    except (SignatureVerificationError, WebhookPayloadError) as err:
        app.logger.warning("Rejected webhook: %s", err)
        return "bad signature", 400

    if event.event == "test":
        app.logger.info("Test webhook received and verified.")
        return "ok"

    # Ignore anything that isn't about an invoice, including event names added in future.
    if not event.is_invoice_event:
        return "ok"

    data = event.data  # {"id", "reference", "status", "amount_expected", "amount_received", ...}
    order = get_order(data.get("reference")) if data.get("reference") else None
    if order is None:
        # Not one of ours (e.g. made in the dashboard): 200 so it isn't retried forever.
        return "ok"

    # At-least-once delivery, possibly out of order. `sequence` only increases, so apply an
    # event only if it's newer than the last one applied to this order.
    if event.sequence <= order["last_seq"]:
        return "ok"

    if order["status"] == "paid":
        # Paid is final as far as fulfilment is concerned.
        update_order(order["id"], last_seq=event.sequence)
        return "ok"

    new_status = STATUS_FOR_EVENT.get(event.event)
    if new_status:
        update_order(order["id"], status=new_status, last_seq=event.sequence, invoice_id=data.get("id"))
        app.logger.info("Order %s: %s -> %s (%s)", order["id"], order["status"], new_status, event.event)
        if new_status == "paid":
            pass  # fulfil here (email, download, shipping); keep it fast or put it on a queue

    # Answer quickly: anything other than 2xx is retried with backoff.
    return "ok"


@app.get("/orders/<order_id>")
def order_page(order_id):
    order = get_order(order_id)
    if order is None:
        abort(404)
    # This page only SHOWS what the signed webhook recorded. Never mark an order paid
    # because someone opened a URL.
    refresh = "" if order["status"] == "paid" else '<meta http-equiv="refresh" content="5"><p class="muted">Refreshes every 5 seconds.</p>'
    return page(
        f"Order {order_id}",
        f"""<h1>Order {escape(order_id)}</h1><p>{escape(order["product"])}: £{escape(order["amount"])}</p>
        <p class="status {order["status"]}">{escape(STATUS_TEXT.get(order["status"], order["status"]))}</p>
        {refresh}<p><a href="/">Back to the shop</a></p>""",
    )


def page(title, body):
    return f"""<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>{escape(title)}</title><style>body{{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}}
.price{{font-size:2rem;font-weight:700}}label{{display:block;margin:1rem 0}}button{{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}}
.check{{font-size:.9rem}}.status{{font-weight:700}}.paid{{color:#4d7c0f}}.muted{{color:#71717a;font-size:.9rem}}</style></head><body>{body}</body></html>"""


if __name__ == "__main__":
    app.run(port=int(os.environ.get("PORT", 5000)))
