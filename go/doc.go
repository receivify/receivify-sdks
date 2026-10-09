// Package receivify is the official Go SDK for the Receivify crypto payments API.
//
// It uses the standard library only (net/http, encoding/json, crypto/hmac, math/big).
//
//	client, err := receivify.NewClient("https://receivify.io", os.Getenv("RECEIVIFY_API_KEY"),
//		receivify.WithWebhookSecrets(os.Getenv("RECEIVIFY_WEBHOOK_SECRET")),
//	)
//	inv, err := client.CreateInvoice(ctx, &receivify.CreateInvoiceParams{
//		Asset:  "btc",
//		Amount: "0.001",
//	})
//	http.Redirect(w, r, inv.CheckoutURL, http.StatusSeeOther)
//
// Three rules the type system cannot enforce for you:
//
//  1. Money is a string. AmountExpected, AmountReceived, AmountNetRequested, FeeOntop and
//     every other amount is an exact-integer base-unit string (satoshis, wei, ...). Never parse
//     one into a float64. Use [FormatUnits], [ParseUnits], [CompareAmounts] and [ToBigInt],
//     which are backed by math/big.
//  2. Verify webhooks against the RAW request body. Re-encoding a decoded struct produces
//     different bytes and the HMAC will not match.
//  3. Branch on error codes, never on the human-readable detail. Use errors.Is with the
//     sentinel values (for example [ErrNotFound]) or errors.As with [*APIError].
//
// # Errors
//
// Every failed API call returns an [*APIError] carrying the raw Code, Detail and StatusCode.
// It matches the sentinel for its code with errors.Is:
//
//	inv, err := client.GetInvoice(ctx, id)
//	switch {
//	case errors.Is(err, receivify.ErrNotFound):
//		// 404
//	case errors.Is(err, receivify.ErrUnauthorized):
//		// 401
//	case err != nil:
//		var apiErr *receivify.APIError
//		if errors.As(err, &apiErr) {
//			log.Printf("receivify %s (HTTP %d): %s", apiErr.Code, apiErr.StatusCode, apiErr.Detail)
//		}
//	}
//
// A code this SDK does not know (added by a newer server) still yields an *APIError with the
// Code preserved; it simply matches no sentinel. Requests that never produced an HTTP response
// return a [*ConnectionError].
package receivify
