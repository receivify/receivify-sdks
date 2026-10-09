// Command quickstart is a complete Receivify integration in one file: a checkout route that
// creates an invoice and redirects the payer to the hosted checkout page, and a webhook route
// that verifies the signature over the RAW body before trusting anything in it.
//
//	export RECEIVIFY_URL=https://receivify.io
//	export RECEIVIFY_API_KEY=rcv_live_...
//	export RECEIVIFY_WEBHOOK_SECRET=whsec_...
//	go run ./examples/quickstart
//
// Then open http://localhost:8080/checkout?order=1001
package main

import (
	"errors"
	"io"
	"log"
	"net/http"
	"os"

	receivify "github.com/receivify/receivify-sdks/go"
)

func main() {
	client, err := receivify.NewClient(
		envOr("RECEIVIFY_URL", "https://receivify.io"),
		os.Getenv("RECEIVIFY_API_KEY"),
		// Current secret first. During a rotation, add the previous one second.
		receivify.WithWebhookSecrets(os.Getenv("RECEIVIFY_WEBHOOK_SECRET"), os.Getenv("RECEIVIFY_WEBHOOK_SECRET_PREVIOUS")),
	)
	if err != nil {
		log.Fatal(err)
	}

	http.HandleFunc("/checkout", func(w http.ResponseWriter, r *http.Request) {
		order := r.URL.Query().Get("order")
		if order == "" {
			http.Error(w, "missing ?order=", http.StatusBadRequest)
			return
		}
		inv, err := client.CreateInvoice(r.Context(), &receivify.CreateInvoiceParams{
			Asset:      "btc",
			AmountFiat: "25.00",
			Currency:   "USD",
			Reference:  "myshop-order-" + order,
			Metadata:   map[string]any{"order_id": order},
		}, receivify.RequestOptions{IdempotencyKey: "myshop-order-" + order})
		if err != nil {
			var apiErr *receivify.APIError
			if errors.As(err, &apiErr) {
				log.Printf("receivify %s (HTTP %d): %s", apiErr.Code, apiErr.StatusCode, apiErr.Detail)
			} else {
				log.Printf("receivify: %v", err)
			}
			http.Error(w, "could not create invoice", http.StatusBadGateway)
			return
		}
		log.Printf("invoice %s: pay %s %s (%s base units) to %s", inv.ID, inv.AmountDecimal, inv.Asset, inv.AmountExpected, inv.Address)
		http.Redirect(w, r, inv.CheckoutURL, http.StatusSeeOther)
	})

	http.HandleFunc("/webhooks/receivify", func(w http.ResponseWriter, r *http.Request) {
		// Read the RAW body first. Never decode and re-encode before verifying.
		raw, err := io.ReadAll(http.MaxBytesReader(w, r.Body, 1<<20))
		if err != nil {
			http.Error(w, "bad body", http.StatusBadRequest)
			return
		}
		event, err := client.ParseWebhook(raw, r.Header.Get(receivify.SignatureHeader))
		if err != nil {
			log.Printf("rejected webhook: %v", err)
			http.Error(w, "invalid signature", http.StatusBadRequest)
			return
		}

		switch event.Event {
		case receivify.EventInvoicePaid:
			data, err := event.InvoiceData()
			if err != nil {
				http.Error(w, "bad data", http.StatusBadRequest)
				return
			}
			// Deliveries are at-least-once: make this idempotent on (event, sequence).
			log.Printf("paid: invoice %s (reference %s) received %s base units", data.ID, data.Reference, data.AmountReceived)
		case receivify.EventInvoiceExpired, receivify.EventInvoiceCancelled:
			log.Printf("%s (sequence %d)", event.Event, event.Sequence)
		default:
			// Ignore event names you do not recognise; new ones are added over time.
		}
		w.WriteHeader(http.StatusOK)
	})

	log.Println("listening on :8080")
	log.Fatal(http.ListenAndServe(":8080", nil))
}

func envOr(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}
