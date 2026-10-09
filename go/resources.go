package receivify

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
)

func pageQuery(limit int, cursor string) url.Values {
	q := url.Values{}
	if limit != 0 {
		q.Set("limit", strconv.Itoa(limit))
	}
	if cursor != "" {
		q.Set("cursor", cursor)
	}
	return q
}

func setIf(q url.Values, k, v string) {
	if v != "" {
		q.Set(k, v)
	}
}

/* ------------------------------------------------------------------ *
 * Invoices
 * ------------------------------------------------------------------ */

// CreateInvoice creates an invoice (POST /api/v1/invoices, always 201).
//
// An Idempotency-Key replay, and a Reference already used with the same terms, both return
// the EXISTING invoice. Pass RequestOptions{IdempotencyKey: ...} to make the call safe to retry.
func (c *Client) CreateInvoice(ctx context.Context, params *CreateInvoiceParams, opts ...RequestOptions) (*Invoice, error) {
	if params == nil {
		params = &CreateInvoiceParams{}
	}
	var out Invoice
	_, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/invoices", body: params, idempotent: true, opts: opts}, &out)
	if err != nil {
		return nil, err
	}
	return &out, nil
}

// GetInvoice retrieves the authoritative current state of an invoice.
// A non-UUID id is 400 bad_request; an unknown one is 404 not_found.
func (c *Client) GetInvoice(ctx context.Context, id string, opts ...RequestOptions) (*Invoice, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out Invoice
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/invoices/" + s, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListInvoices returns one page of invoices, newest first.
func (c *Client) ListInvoices(ctx context.Context, params *ListInvoicesParams, opts ...RequestOptions) (*List[Invoice], error) {
	if params == nil {
		params = &ListInvoicesParams{}
	}
	q := pageQuery(params.Limit, params.Cursor)
	setIf(q, "status", string(params.Status))
	var out List[Invoice]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/invoices", query: q, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListInvoicesAutoPaging iterates every invoice matching params, following next_cursor.
//
//	it := client.ListInvoicesAutoPaging(ctx, &receivify.ListInvoicesParams{Status: "paid"})
//	for it.Next() {
//		inv := it.Current()
//	}
//	if err := it.Err(); err != nil { ... }
func (c *Client) ListInvoicesAutoPaging(ctx context.Context, params *ListInvoicesParams, opts ...RequestOptions) *Iter[Invoice] {
	p := ListInvoicesParams{}
	if params != nil {
		p = *params
	}
	return newIter(ctx, p.Cursor, func(ctx context.Context, cursor string) (*List[Invoice], error) {
		p.Cursor = cursor
		return c.ListInvoices(ctx, &p, opts...)
	})
}

// CancelInvoice cancels a pending, unpaid invoice and returns it. Not idempotent: cancelling
// an already-cancelled invoice is 409 not_allowed.
func (c *Client) CancelInvoice(ctx context.Context, id string, opts ...RequestOptions) (*Invoice, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out Invoice
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/invoices/" + s + "/cancel", opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// BulkCancelInvoices cancels up to 500 invoices. Ids that are not pending-and-unpaid are
// skipped; compare Requested with Cancelled.
func (c *Client) BulkCancelInvoices(ctx context.Context, ids []string, opts ...RequestOptions) (*BulkResult, error) {
	body := map[string]any{"ids": ids, "action": "cancel"}
	var out BulkResult
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/invoices/bulk", body: body, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetPublicInvoice reads the unauthenticated checkout projection. No API key is sent.
func (c *Client) GetPublicInvoice(ctx context.Context, id string, opts ...RequestOptions) (*PublicInvoice, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out PublicInvoice
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/public/invoices/" + s, noAuth: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

/* ------------------------------------------------------------------ *
 * Forwarding addresses
 * ------------------------------------------------------------------ */

// CreateAddress creates, or returns, a forwarding address. The identity is
// (merchant, asset, destination, callback): repeating the call returns the same address with
// Created=false instead of deriving a new one.
func (c *Client) CreateAddress(ctx context.Context, params *CreateAddressParams, opts ...RequestOptions) (*Forward, error) {
	if params == nil {
		params = &CreateAddressParams{}
	}
	var out Forward
	resp, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/address", body: params, idempotent: true, opts: opts}, &out)
	if err != nil {
		return nil, err
	}
	out.Created = resp.StatusCode == http.StatusCreated
	return &out, nil
}

// GetAddress returns the forward plus its capped payments and callbacks logs.
func (c *Client) GetAddress(ctx context.Context, id string, opts ...RequestOptions) (*ForwardDetail, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out ForwardDetail
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/address/" + s, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListAddresses returns one page of forwarding addresses.
func (c *Client) ListAddresses(ctx context.Context, params *ListAddressesParams, opts ...RequestOptions) (*List[Forward], error) {
	if params == nil {
		params = &ListAddressesParams{}
	}
	q := pageQuery(params.Limit, params.Cursor)
	setIf(q, "status", string(params.Status))
	setIf(q, "ticker", params.Ticker)
	var out List[Forward]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/address", query: q, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListAddressesAutoPaging iterates every forwarding address matching params.
func (c *Client) ListAddressesAutoPaging(ctx context.Context, params *ListAddressesParams, opts ...RequestOptions) *Iter[Forward] {
	p := ListAddressesParams{}
	if params != nil {
		p = *params
	}
	return newIter(ctx, p.Cursor, func(ctx context.Context, cursor string) (*List[Forward], error) {
		p.Cursor = cursor
		return c.ListAddresses(ctx, &p, opts...)
	})
}

// UpdateAddress pauses/resumes a forward or changes its callback URL. Destination and ticker
// are immutable.
func (c *Client) UpdateAddress(ctx context.Context, id string, params *UpdateAddressParams, opts ...RequestOptions) (*Forward, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	if params == nil {
		params = &UpdateAddressParams{}
	}
	var out Forward
	if _, err := c.do(ctx, &call{method: http.MethodPatch, path: "/api/v1/address/" + s, body: params, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// DisableAddress soft-deletes a forward (status "disabled"). Idempotent.
func (c *Client) DisableAddress(ctx context.Context, id string, opts ...RequestOptions) (*Forward, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out Forward
	if _, err := c.do(ctx, &call{method: http.MethodDelete, path: "/api/v1/address/" + s, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

/* ------------------------------------------------------------------ *
 * Catalogue (public)
 * ------------------------------------------------------------------ */

// ListTickers returns every (asset, chain) pair offered for forwarding, as one page.
// No API key is sent.
func (c *Client) ListTickers(ctx context.Context, opts ...RequestOptions) (*List[Ticker], error) {
	var out List[Ticker]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/tickers", noAuth: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListAssets returns the invoiceable coins (display metadata). No API key is sent.
func (c *Client) ListAssets(ctx context.Context, opts ...RequestOptions) (*List[Asset], error) {
	var out List[Asset]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/assets", noAuth: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

/* ------------------------------------------------------------------ *
 * Webhooks: secret, endpoints, deliveries, test
 * ------------------------------------------------------------------ */

// GetWebhookSecretState returns rotation metadata (never the secret). While PrevActive is
// true your receiver must accept either secret.
func (c *Client) GetWebhookSecretState(ctx context.Context, opts ...RequestOptions) (*WebhookSecretState, error) {
	var out WebhookSecretState
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/webhooks/secret", opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// RotateWebhookSecret mints a new signing secret and returns it ONCE. The previous secret
// stays valid until PrevExpiresAt; verify against both until then. An Idempotency-Key replay
// returns Secret "".
func (c *Client) RotateWebhookSecret(ctx context.Context, opts ...RequestOptions) (*WebhookSecretRotated, error) {
	var out WebhookSecretRotated
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/webhooks/secret/rotate", idempotent: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListWebhookEndpoints returns one page of account-level webhook endpoints.
func (c *Client) ListWebhookEndpoints(ctx context.Context, params *PageParams, opts ...RequestOptions) (*List[WebhookEndpoint], error) {
	if params == nil {
		params = &PageParams{}
	}
	var out List[WebhookEndpoint]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/webhooks/endpoints", query: pageQuery(params.Limit, params.Cursor), opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListWebhookEndpointsAutoPaging iterates every webhook endpoint.
func (c *Client) ListWebhookEndpointsAutoPaging(ctx context.Context, params *PageParams, opts ...RequestOptions) *Iter[WebhookEndpoint] {
	p := PageParams{}
	if params != nil {
		p = *params
	}
	return newIter(ctx, p.Cursor, func(ctx context.Context, cursor string) (*List[WebhookEndpoint], error) {
		p.Cursor = cursor
		return c.ListWebhookEndpoints(ctx, &p, opts...)
	})
}

// CreateWebhookEndpoint registers an account-level endpoint (max 20). Account endpoints
// receive invoice.* and sweep.sent, never forward.*.
func (c *Client) CreateWebhookEndpoint(ctx context.Context, endpointURL string, opts ...RequestOptions) (*WebhookEndpoint, error) {
	var out WebhookEndpoint
	body := map[string]string{"url": endpointURL}
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/webhooks/endpoints", body: body, idempotent: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// UpdateWebhookEndpoint changes an endpoint's Active flag or URL.
func (c *Client) UpdateWebhookEndpoint(ctx context.Context, id string, params *UpdateWebhookEndpointParams, opts ...RequestOptions) (*WebhookEndpoint, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	if params == nil {
		params = &UpdateWebhookEndpointParams{}
	}
	var out WebhookEndpoint
	if _, err := c.do(ctx, &call{method: http.MethodPatch, path: "/api/v1/webhooks/endpoints/" + s, body: params, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// DeleteWebhookEndpoint hard-deletes an endpoint. A second call is 404.
func (c *Client) DeleteWebhookEndpoint(ctx context.Context, id string, opts ...RequestOptions) (*Ack, error) {
	return c.ack(ctx, http.MethodDelete, "/api/v1/webhooks/endpoints/", id, "", opts)
}

// ListWebhookDeliveries returns one page of the delivery log.
func (c *Client) ListWebhookDeliveries(ctx context.Context, params *ListWebhookDeliveriesParams, opts ...RequestOptions) (*List[WebhookDelivery], error) {
	if params == nil {
		params = &ListWebhookDeliveriesParams{}
	}
	q := pageQuery(params.Limit, params.Cursor)
	setIf(q, "event", params.Event)
	setIf(q, "status", params.Status)
	var out List[WebhookDelivery]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/webhooks/deliveries", query: q, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListWebhookDeliveriesAutoPaging iterates the delivery log.
func (c *Client) ListWebhookDeliveriesAutoPaging(ctx context.Context, params *ListWebhookDeliveriesParams, opts ...RequestOptions) *Iter[WebhookDelivery] {
	p := ListWebhookDeliveriesParams{}
	if params != nil {
		p = *params
	}
	return newIter(ctx, p.Cursor, func(ctx context.Context, cursor string) (*List[WebhookDelivery], error) {
		p.Cursor = cursor
		return c.ListWebhookDeliveries(ctx, &p, opts...)
	})
}

// ReplayWebhookDelivery re-sends the ORIGINAL bytes of a delivery (frozen sequence and
// created_at); it does not reflect current state.
func (c *Client) ReplayWebhookDelivery(ctx context.Context, id string, opts ...RequestOptions) (*Ack, error) {
	return c.ack(ctx, http.MethodPost, "/api/v1/webhooks/deliveries/", id, "/replay", opts)
}

// SendTestWebhook sends a signed "test" event to your registered endpoints. Pass "" to hit
// every active endpoint, or one registered URL to target it.
func (c *Client) SendTestWebhook(ctx context.Context, endpointURL string, opts ...RequestOptions) (*WebhookTestResult, error) {
	var body any
	if endpointURL != "" {
		body = map[string]string{"url": endpointURL}
	}
	var out WebhookTestResult
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/webhooks/test", body: body, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

/* ------------------------------------------------------------------ *
 * Payment links
 * ------------------------------------------------------------------ */

// ListPaymentLinks returns one page of payment links.
func (c *Client) ListPaymentLinks(ctx context.Context, params *ListPaymentLinksParams, opts ...RequestOptions) (*List[PaymentLink], error) {
	if params == nil {
		params = &ListPaymentLinksParams{}
	}
	q := pageQuery(params.Limit, params.Cursor)
	if params.Active != nil {
		q.Set("active", strconv.FormatBool(*params.Active))
	}
	var out List[PaymentLink]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/payment-links", query: q, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListPaymentLinksAutoPaging iterates every payment link.
func (c *Client) ListPaymentLinksAutoPaging(ctx context.Context, params *ListPaymentLinksParams, opts ...RequestOptions) *Iter[PaymentLink] {
	p := ListPaymentLinksParams{}
	if params != nil {
		p = *params
	}
	return newIter(ctx, p.Cursor, func(ctx context.Context, cursor string) (*List[PaymentLink], error) {
		p.Cursor = cursor
		return c.ListPaymentLinks(ctx, &p, opts...)
	})
}

// CreatePaymentLink creates a reusable payment link.
func (c *Client) CreatePaymentLink(ctx context.Context, params *CreatePaymentLinkParams, opts ...RequestOptions) (*PaymentLink, error) {
	if params == nil {
		params = &CreatePaymentLinkParams{}
	}
	var out PaymentLink
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/payment-links", body: params, idempotent: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetPaymentLink reads a payment link (including deactivated ones).
func (c *Client) GetPaymentLink(ctx context.Context, id string, opts ...RequestOptions) (*PaymentLink, error) {
	return c.paymentLink(ctx, http.MethodGet, id, nil, opts)
}

// UpdatePaymentLink changes Active, Title or the BTC amount.
func (c *Client) UpdatePaymentLink(ctx context.Context, id string, params *UpdatePaymentLinkParams, opts ...RequestOptions) (*PaymentLink, error) {
	if params == nil {
		params = &UpdatePaymentLinkParams{}
	}
	return c.paymentLink(ctx, http.MethodPatch, id, params, opts)
}

// DeactivatePaymentLink soft-deactivates a link. Invoices already minted stay payable.
func (c *Client) DeactivatePaymentLink(ctx context.Context, id string, opts ...RequestOptions) (*PaymentLink, error) {
	return c.paymentLink(ctx, http.MethodDelete, id, nil, opts)
}

func (c *Client) paymentLink(ctx context.Context, method, id string, body any, opts []RequestOptions) (*PaymentLink, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out PaymentLink
	if _, err := c.do(ctx, &call{method: method, path: "/api/v1/payment-links/" + s, body: body, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetPublicPaymentLink reads the unauthenticated view of a link. No API key is sent.
func (c *Client) GetPublicPaymentLink(ctx context.Context, id string, opts ...RequestOptions) (*PublicPaymentLink, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out PublicPaymentLink
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/public/links/" + s, noAuth: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

/* ------------------------------------------------------------------ *
 * Recurring schedules
 * ------------------------------------------------------------------ */

// ListRecurring returns one page of recurring schedules.
func (c *Client) ListRecurring(ctx context.Context, params *PageParams, opts ...RequestOptions) (*List[RecurringSchedule], error) {
	if params == nil {
		params = &PageParams{}
	}
	var out List[RecurringSchedule]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/recurring", query: pageQuery(params.Limit, params.Cursor), opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListRecurringAutoPaging iterates every recurring schedule.
func (c *Client) ListRecurringAutoPaging(ctx context.Context, params *PageParams, opts ...RequestOptions) *Iter[RecurringSchedule] {
	p := PageParams{}
	if params != nil {
		p = *params
	}
	return newIter(ctx, p.Cursor, func(ctx context.Context, cursor string) (*List[RecurringSchedule], error) {
		p.Cursor = cursor
		return c.ListRecurring(ctx, &p, opts...)
	})
}

// CreateRecurring creates a schedule that generates an invoice every IntervalDays.
func (c *Client) CreateRecurring(ctx context.Context, params *CreateRecurringParams, opts ...RequestOptions) (*RecurringSchedule, error) {
	if params == nil {
		params = &CreateRecurringParams{}
	}
	var out RecurringSchedule
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/recurring", body: params, idempotent: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// CancelRecurring stops a schedule.
func (c *Client) CancelRecurring(ctx context.Context, id string, opts ...RequestOptions) (*Ack, error) {
	return c.ack(ctx, http.MethodPost, "/api/v1/recurring/", id, "/cancel", opts)
}

/* ------------------------------------------------------------------ *
 * API keys
 * ------------------------------------------------------------------ */

// ListAPIKeys returns one page of API keys (prefixes only; revoked keys included).
func (c *Client) ListAPIKeys(ctx context.Context, params *PageParams, opts ...RequestOptions) (*List[APIKey], error) {
	if params == nil {
		params = &PageParams{}
	}
	var out List[APIKey]
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/keys", query: pageQuery(params.Limit, params.Cursor), opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ListAPIKeysAutoPaging iterates every API key.
func (c *Client) ListAPIKeysAutoPaging(ctx context.Context, params *PageParams, opts ...RequestOptions) *Iter[APIKey] {
	p := PageParams{}
	if params != nil {
		p = *params
	}
	return newIter(ctx, p.Cursor, func(ctx context.Context, cursor string) (*List[APIKey], error) {
		p.Cursor = cursor
		return c.ListAPIKeys(ctx, &p, opts...)
	})
}

// CreateAPIKey mints a new key. The raw secret is returned ONCE in APIKey.
func (c *Client) CreateAPIKey(ctx context.Context, label string, opts ...RequestOptions) (*APIKeyCreated, error) {
	body := map[string]string{}
	if label != "" {
		body["label"] = label
	}
	var out APIKeyCreated
	if _, err := c.do(ctx, &call{method: http.MethodPost, path: "/api/v1/keys", body: body, idempotent: true, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// RevokeAPIKey revokes a key by its ID (not its prefix).
func (c *Client) RevokeAPIKey(ctx context.Context, id string, opts ...RequestOptions) (*Ack, error) {
	return c.ack(ctx, http.MethodPost, "/api/v1/keys/", id, "/revoke", opts)
}

/* ------------------------------------------------------------------ *
 * Settings, summary, payouts, health
 * ------------------------------------------------------------------ */

// GetSettings reads the merchant settings, including the AddFeeOntop account default.
func (c *Client) GetSettings(ctx context.Context, opts ...RequestOptions) (*Settings, error) {
	var out Settings
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/settings", opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// UpdateSettings writes settings and returns an ack echoing only what changed.
func (c *Client) UpdateSettings(ctx context.Context, params *UpdateSettingsParams, opts ...RequestOptions) (*SettingsUpdateAck, error) {
	if params == nil {
		params = &UpdateSettingsParams{}
	}
	var out SettingsUpdateAck
	if _, err := c.do(ctx, &call{method: http.MethodPatch, path: "/api/v1/settings", body: params, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetSummary returns the dashboard composite. Its FX rates are advisory floats.
func (c *Client) GetSummary(ctx context.Context, opts ...RequestOptions) (*Summary, error) {
	var out Summary
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/summary", opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// GetPayouts returns the live payout map plus every open staged change.
func (c *Client) GetPayouts(ctx context.Context, opts ...RequestOptions) (*PayoutState, error) {
	var out PayoutState
	if _, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/v1/payouts", opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// StagePayoutChanges stages payout-address changes (asset symbol to address). They go live
// after the deployment's timelock and, where enabled, an email confirmation.
func (c *Client) StagePayoutChanges(ctx context.Context, addresses map[string]string, opts ...RequestOptions) (*PayoutStageAck, error) {
	var out PayoutStageAck
	body := map[string]any{"addresses": addresses}
	if _, err := c.do(ctx, &call{method: http.MethodPatch, path: "/api/v1/payouts", body: body, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// CancelPayoutChange cancels an open staged payout change.
func (c *Client) CancelPayoutChange(ctx context.Context, id string, opts ...RequestOptions) (*Ack, error) {
	return c.ack(ctx, http.MethodPost, "/api/v1/payouts/changes/", id, "/cancel", opts)
}

// GetHealth returns the operator health report. A 503 carries the same report body (not an
// error body), so it is returned without an error.
func (c *Client) GetHealth(ctx context.Context, opts ...RequestOptions) (*Health, error) {
	var out Health
	_, err := c.do(ctx, &call{method: http.MethodGet, path: "/api/health", noAuth: true, noRetry: true, opts: opts}, &out)
	if err != nil {
		if apiErr, ok := err.(*APIError); ok && apiErr.StatusCode == http.StatusServiceUnavailable && apiErr.Code == "" {
			if decodeJSON(apiErr.Body, &out) == nil {
				return &out, nil
			}
		}
		return nil, err
	}
	return &out, nil
}

func (c *Client) ack(ctx context.Context, method, prefix, id, suffix string, opts []RequestOptions) (*Ack, error) {
	s, err := seg(id)
	if err != nil {
		return nil, err
	}
	var out Ack
	if _, err := c.do(ctx, &call{method: method, path: prefix + s + suffix, opts: opts}, &out); err != nil {
		return nil, err
	}
	return &out, nil
}
