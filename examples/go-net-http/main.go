// Receivify + Go net/http: a complete, tiny shop with no framework.
//
//	GET  /                      product page with a "Pay with crypto" form
//	POST /checkout              creates the order + a Receivify invoice, redirects to checkout
//	GET  /orders/{id}           order status page
//	POST /webhooks/receivify    signed events from Receivify; the ONLY place an order is marked paid
//
// Run: export the variables from .env.example, then `go run .` (Go 1.22+ for the route patterns).
package main

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"html/template"
	"io"
	"log"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"

	receivify "github.com/receivify/receivify-sdks/go"
)

// What we sell. Prices are strings: money never goes through a float.
var product = struct{ Name, Price, Currency string }{"Receivify T-shirt", "25.00", "GBP"}

// What the customer may pay with. Chain picks the network for tokens that live on several.
type coin struct{ Key, Label, Asset, Chain string }

var coins = []coin{
	{"btc", "Bitcoin", "btc", ""},
	{"ltc", "Litecoin", "ltc", ""},
	{"eth", "Ethereum", "eth", "ethereum"},
	{"usdc-base", "USDC on Base", "usdc", "base"},
	{"usdt-tron", "USDT on Tron", "usdt", "tron"},
	{"sol", "Solana", "sol", "solana"},
}

// invoice.paid is THE signal: the full amount is confirmed and can't be reversed.
// invoice.swept follows it (funds forwarded to your wallet), so it also means paid.
var statusForEvent = map[string]string{
	"invoice.confirming":      "confirming", // seen on-chain, not final: don't ship yet
	"invoice.partial":         "partially_paid",
	"invoice.paid":            "paid",
	"invoice.swept":           "paid",
	"invoice.expired":         "expired",
	"invoice.partial_expired": "underpaid", // time ran out on an under-payment
	"invoice.cancelled":       "cancelled",
	"invoice.failed":          "failed",
}

// ---- a deliberately tiny, in-memory order store. Use your database in a real shop. ----

type order struct {
	ID, Product, Amount, Status, InvoiceID string
	LastSeq                                int64 // highest webhook sequence applied (see the handler)
}

type store struct {
	mu     sync.Mutex
	orders map[string]*order
}

func (s *store) create() *order {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	// Prefixed so it can't collide with another shop on the same Receivify account:
	// `reference` is also a de-duplication key on Receivify's side.
	o := &order{ID: "go-" + hex.EncodeToString(b), Product: product.Name, Amount: product.Price, Status: "pending"}
	s.mu.Lock()
	s.orders[o.ID] = o
	s.mu.Unlock()
	return o
}

func (s *store) get(id string) (order, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	o, ok := s.orders[id]
	if !ok {
		return order{}, false
	}
	return *o, true
}

type server struct {
	rv     *receivify.Client
	orders *store
}

func main() {
	apiKey, secret := os.Getenv("RECEIVIFY_API_KEY"), os.Getenv("RECEIVIFY_WEBHOOK_SECRET")
	if apiKey == "" || secret == "" {
		log.Fatal("Set RECEIVIFY_API_KEY and RECEIVIFY_WEBHOOK_SECRET (see .env.example).")
	}
	apiURL := os.Getenv("RECEIVIFY_API_URL")
	if apiURL == "" {
		apiURL = "https://receivify.io"
	}

	// The webhook secrets are a LIST: during a rotation pass the new one first and the old
	// one second ("whsec_new,whsec_old") so no delivery is rejected mid-switch.
	var secrets []string
	for _, s := range strings.Split(secret, ",") {
		if s = strings.TrimSpace(s); s != "" {
			secrets = append(secrets, s)
		}
	}
	rv, err := receivify.NewClient(apiURL, apiKey, receivify.WithWebhookSecrets(secrets...))
	if err != nil {
		log.Fatal(err)
	}

	s := &server{rv: rv, orders: &store{orders: map[string]*order{}}}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", s.shop)
	mux.HandleFunc("POST /checkout", s.checkout)
	mux.HandleFunc("GET /orders/{id}", s.orderPage)
	mux.HandleFunc("POST /webhooks/receivify", s.webhook)

	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	// Localhost only by default, so nothing on your network can reach the dev server
	// (and Windows/macOS won't show a firewall prompt). Set HOST=0.0.0.0 in a container.
	host := os.Getenv("HOST")
	if host == "" {
		host = "127.0.0.1"
	}
	log.Printf("Shop running on http://localhost:%s", port)
	log.Fatal(http.ListenAndServe(host+":"+port, mux))
}

func (s *server) shop(w http.ResponseWriter, r *http.Request) {
	render(w, shopTmpl, map[string]any{"Product": product, "Coins": coins})
}

func (s *server) checkout(w http.ResponseWriter, r *http.Request) {
	c := coins[0]
	for _, x := range coins {
		if x.Key == r.FormValue("coin") {
			c = x
		}
	}
	o := s.orders.create()

	params := &receivify.CreateInvoiceParams{
		AmountFiat: product.Price, // converted at today's rate and locked on the invoice
		Currency:   product.Currency,
		Asset:      c.Asset,
		Chain:      c.Chain,
		// Comes back on every webhook as data.reference: that's how we find the order.
		Reference: o.ID,
		Metadata:  map[string]any{"product": product.Name, "source": "go-example"},
	}
	if r.FormValue("fee_ontop") != "" {
		// Customer covers the fee so you receive the full price. Leave nil for your account default.
		params.AddFeeOntop = receivify.Bool(true)
	}

	ctx, cancel := context.WithTimeout(r.Context(), 20*time.Second)
	defer cancel()
	// Same key = same invoice: a double submit or a retry can't create two.
	inv, err := s.rv.CreateInvoice(ctx, params, receivify.RequestOptions{IdempotencyKey: "checkout-" + o.ID})
	if err != nil {
		var apiErr *receivify.APIError
		if errors.As(err, &apiErr) {
			// Code is stable (amount_too_low, asset_unavailable, ...); Detail is for humans.
			http.Error(w, fmt.Sprintf("Could not start payment: %s", apiErr.Detail), http.StatusBadRequest)
			return
		}
		http.Error(w, "Receivify is unreachable, try again", http.StatusBadGateway)
		return
	}

	s.orders.mu.Lock()
	s.orders.orders[o.ID].InvoiceID = inv.ID
	s.orders.mu.Unlock()

	// 303: the browser follows with a GET to Receivify's hosted checkout.
	http.Redirect(w, r, inv.CheckoutURL, http.StatusSeeOther)
}

func (s *server) webhook(w http.ResponseWriter, r *http.Request) {
	// Read the body EXACTLY as sent. The signature is an HMAC over these bytes; decoding
	// into a struct and re-encoding would change them and fail verification.
	raw, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 1<<20))
	if err != nil {
		http.Error(w, "bad body", http.StatusBadRequest)
		return
	}

	// Constant-time HMAC check + 5-minute replay window + JSON decode.
	event, err := s.rv.ParseWebhook(raw, r.Header.Get(receivify.SignatureHeader))
	if err != nil {
		log.Printf("Rejected webhook: %v", err)
		http.Error(w, "bad signature", http.StatusBadRequest)
		return
	}

	if event.Event == "test" {
		log.Print("Test webhook received and verified.")
		w.WriteHeader(http.StatusOK)
		return
	}
	// Ignore anything that isn't about an invoice, including event names added later.
	if !strings.HasPrefix(event.Event, "invoice.") {
		w.WriteHeader(http.StatusOK)
		return
	}

	data, err := event.InvoiceData()
	if err != nil {
		w.WriteHeader(http.StatusOK) // verified but unexpected shape: acknowledge, log, move on
		log.Printf("Unexpected invoice payload: %v", err)
		return
	}

	s.orders.mu.Lock()
	defer s.orders.mu.Unlock()
	o, ok := s.orders.orders[data.Reference]
	// Unknown (made elsewhere), a duplicate, or an older event arriving late: do nothing.
	// Delivery is at-least-once and can be out of order, and `sequence` only increases,
	// so "only apply newer sequences" is all the de-duplication this needs.
	if !ok || event.Sequence <= o.LastSeq {
		w.WriteHeader(http.StatusOK)
		return
	}
	o.LastSeq = event.Sequence
	if next, known := statusForEvent[event.Event]; known && o.Status != "paid" { // paid is final
		log.Printf("Order %s: %s -> %s (%s)", o.ID, o.Status, next, event.Event)
		o.Status, o.InvoiceID = next, data.ID
		if next == "paid" {
			// Fulfil here (email, download, shipping). Keep it fast or enqueue it.
		}
	}
	w.WriteHeader(http.StatusOK) // 2xx fast; anything else is retried with backoff
}

func (s *server) orderPage(w http.ResponseWriter, r *http.Request) {
	o, ok := s.orders.get(r.PathValue("id"))
	if !ok {
		http.NotFound(w, r)
		return
	}
	// Display only. Landing on this URL proves nothing; the webhook is the source of truth.
	render(w, orderTmpl, o)
}

func render(w http.ResponseWriter, t *template.Template, data any) {
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	if err := t.Execute(w, data); err != nil {
		log.Print(err)
	}
}

const css = `<style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}
.price{font-size:2rem;font-weight:700}label{display:block;margin:1rem 0}button{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}
.status{font-weight:700}.paid{color:#4d7c0f}.muted{color:#71717a;font-size:.9rem}</style>`

var shopTmpl = template.Must(template.New("shop").Parse(`<!doctype html><meta charset="utf-8"><title>Shop</title>` + css + `
<h1>{{.Product.Name}}</h1><p class="price">£{{.Product.Price}}</p>
<form method="post" action="/checkout">
  <label>Pay with <select name="coin">{{range .Coins}}<option value="{{.Key}}">{{.Label}}</option>{{end}}</select></label>
  <label><input type="checkbox" name="fee_ontop" value="1"> Customer pays the fee on top</label>
  <button>Pay with crypto</button>
</form>`))

var orderTmpl = template.Must(template.New("order").Parse(`<!doctype html><meta charset="utf-8"><title>Order {{.ID}}</title>` + css + `
{{if ne .Status "paid"}}<meta http-equiv="refresh" content="5">{{end}}
<h1>Order {{.ID}}</h1><p>{{.Product}}: £{{.Amount}}</p>
<p class="status {{.Status}}">{{.Status}}</p>
{{if ne .Status "paid"}}<p class="muted">Refreshes every 5 seconds.</p>{{end}}
<p><a href="/">Back to the shop</a></p>`))
