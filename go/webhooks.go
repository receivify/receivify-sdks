package receivify

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// Webhook verification.
//
// Use the RAW body. The signature is HMAC-SHA256(secret, "<t>.<rawBody>") over the exact bytes
// Receivify sent. Re-encoding a decoded struct changes key order and whitespace, and the HMAC
// will not match even for a genuine delivery. In net/http, read r.Body with io.ReadAll before
// anything else touches it.
//
// Verify against a LIST of secrets. RotateWebhookSecret keeps the previous secret valid for a
// grace window (24h by default), so during a rotation two secrets are legitimately valid.
// Pass []string{current, previous} and drop the old one once the window closes.

// SignatureHeader is the header Receivify signs every delivery with.
const SignatureHeader = "X-Receivify-Signature"

// DefaultTolerance is the default replay window for webhook timestamps.
const DefaultTolerance = 300 * time.Second

// Event names. Receivers MUST ignore names they do not recognise.
const (
	EventInvoiceConfirming     = "invoice.confirming"
	EventInvoicePartial        = "invoice.partial"
	EventInvoicePaid           = "invoice.paid"
	EventInvoiceSwept          = "invoice.swept"
	EventInvoiceExpired        = "invoice.expired"
	EventInvoicePartialExpired = "invoice.partial_expired"
	EventInvoiceCancelled      = "invoice.cancelled"
	EventInvoiceFailed         = "invoice.failed"
	EventSweepSent             = "sweep.sent"
	EventForwardDetected       = "forward.detected"
	EventForwardConfirmed      = "forward.confirmed"
	EventForwardDeferred       = "forward.deferred"
	EventForwardSent           = "forward.sent"
	EventForwardReversed       = "forward.reversed"
	EventForwardFailed         = "forward.failed"
	EventTest                  = "test"
)

// Events is the frozen event registry.
var Events = []string{
	EventInvoiceConfirming, EventInvoicePartial, EventInvoicePaid, EventInvoiceSwept,
	EventInvoiceExpired, EventInvoicePartialExpired, EventInvoiceCancelled, EventInvoiceFailed,
	EventSweepSent, EventForwardDetected, EventForwardConfirmed, EventForwardDeferred,
	EventForwardSent, EventForwardReversed, EventForwardFailed, EventTest,
}

// WebhookErrorReason says why a delivery was rejected.
type WebhookErrorReason string

// Webhook rejection reasons.
const (
	ReasonNoSecrets      WebhookErrorReason = "no_secrets"      // no secret configured
	ReasonBadHeader      WebhookErrorReason = "bad_header"      // missing or malformed signature header
	ReasonStaleTimestamp WebhookErrorReason = "stale_timestamp" // outside the tolerance window
	ReasonNoMatch        WebhookErrorReason = "no_match"        // signature matched no secret
	ReasonBadJSON        WebhookErrorReason = "bad_json"        // verified, but not valid JSON
	ReasonBadEnvelope    WebhookErrorReason = "bad_envelope"    // valid JSON, but not an event envelope
)

// WebhookError is returned by CheckWebhook and ParseWebhook. VerifyWebhook never returns one;
// it returns false.
type WebhookError struct {
	Reason  WebhookErrorReason
	Message string
}

func (e *WebhookError) Error() string { return "receivify: webhook rejected: " + e.Message }

// VerifyOption tunes webhook verification.
type VerifyOption func(*verifyConfig)

type verifyConfig struct {
	tolerance time.Duration
	now       time.Time
}

// WithTolerance sets the replay window. Zero or negative disables the timestamp check.
// Default 300s.
func WithTolerance(d time.Duration) VerifyOption {
	return func(v *verifyConfig) { v.tolerance = d }
}

// WithVerifyTime sets the "current" time used for the replay window. For tests.
func WithVerifyTime(t time.Time) VerifyOption {
	return func(v *verifyConfig) { v.now = t }
}

// WebhookEvent is the unified envelope every delivery carries. Switch on Event first; the
// shape of Data depends on it.
type WebhookEvent struct {
	Event string `json:"event"`
	// Sequence is globally monotonic. (Event, Sequence) is the de-duplication key. Delivery is
	// at-least-once and may be out of order: store the highest sequence applied per resource.
	Sequence int64 `json:"sequence"`
	// CreatedAt is when the event happened (frozen across retries and replays).
	CreatedAt time.Time `json:"created_at"`
	// Data is the raw resource payload. Decode it with InvoiceData or ForwardData.
	Data json.RawMessage `json:"data"`
}

// InvoiceEventData is Data for invoice.*, sweep.sent and test. It carries no asset or chain:
// call GetInvoice for the full invoice.
type InvoiceEventData struct {
	ID             string        `json:"id"`
	Reference      string        `json:"reference"`
	Status         InvoiceStatus `json:"status"`
	AmountExpected string        `json:"amount_expected"`
	AmountReceived string        `json:"amount_received"`
	Address        string        `json:"address"`
	PaidAt         *time.Time    `json:"paid_at"`
}

// ForwardEventData is Data for forward.* events.
type ForwardEventData struct {
	ForwardID  string `json:"forward_id"`
	Ticker     string `json:"ticker"`
	AddressIn  string `json:"address_in"`
	AddressOut string `json:"address_out"`
	TxidIn     string `json:"txid_in"`
	TxidOut    string `json:"txid_out"`
	// Value, ValueForwarded and Fee are base units.
	Value          string `json:"value"`
	ValueForwarded string `json:"value_forwarded"`
	Fee            string `json:"fee"`
	Confirmations  int    `json:"confirmations"`
	Pending        bool   `json:"pending"`
	// Reason is machine-readable on forward.deferred, forward.reversed and forward.failed.
	Reason string `json:"reason"`
	// Detail is prose explaining Reason. Never branch on it.
	Detail string `json:"detail"`
}

// IsInvoiceEvent reports whether Data is invoice-shaped (invoice.*, sweep.sent, test).
func (e *WebhookEvent) IsInvoiceEvent() bool {
	return strings.HasPrefix(e.Event, "invoice.") || e.Event == EventSweepSent || e.Event == EventTest
}

// IsForwardEvent reports whether Data is forward-payment-shaped (forward.*).
func (e *WebhookEvent) IsForwardEvent() bool { return strings.HasPrefix(e.Event, "forward.") }

// InvoiceData decodes Data as an invoice payload.
func (e *WebhookEvent) InvoiceData() (*InvoiceEventData, error) {
	var d InvoiceEventData
	if err := json.Unmarshal(e.Data, &d); err != nil {
		return nil, fmt.Errorf("receivify: decoding invoice event data: %w", err)
	}
	return &d, nil
}

// ForwardData decodes Data as a forward-payment payload.
func (e *WebhookEvent) ForwardData() (*ForwardEventData, error) {
	var d ForwardEventData
	if err := json.Unmarshal(e.Data, &d); err != nil {
		return nil, fmt.Errorf("receivify: decoding forward event data: %w", err)
	}
	return &d, nil
}

// VerifyWebhook reports whether rawBody carries a valid signature from any of secrets and a
// timestamp inside the tolerance window. It never panics and never returns an error.
//
// rawBody must be the exact request bytes; header is the X-Receivify-Signature value.
func VerifyWebhook(rawBody []byte, header string, secrets []string, opts ...VerifyOption) bool {
	return CheckWebhook(rawBody, header, secrets, opts...) == nil
}

// CheckWebhook is VerifyWebhook with a reason: it returns nil when the delivery is genuine,
// otherwise a *WebhookError whose Reason you can log.
func CheckWebhook(rawBody []byte, header string, secrets []string, opts ...VerifyOption) error {
	cfg := verifyConfig{tolerance: DefaultTolerance}
	for _, o := range opts {
		if o != nil {
			o(&cfg)
		}
	}
	list := normaliseSecrets(secrets)
	if len(list) == 0 {
		return &WebhookError{Reason: ReasonNoSecrets, Message: "no webhook secret configured"}
	}
	rawTS, ts, sigs, ok := parseSignatureHeader(header)
	if !ok {
		return &WebhookError{Reason: ReasonBadHeader, Message: "missing or malformed " + SignatureHeader + ` header (expected "t=<unix>,v1=<hex>")`}
	}
	if cfg.tolerance > 0 {
		now := cfg.now
		if now.IsZero() {
			now = time.Now()
		}
		diff := now.Unix() - ts
		if diff < 0 {
			diff = -diff
		}
		if diff > int64(cfg.tolerance/time.Second) {
			return &WebhookError{Reason: ReasonStaleTimestamp, Message: fmt.Sprintf("timestamp %d is outside the %s tolerance window", ts, cfg.tolerance)}
		}
	}

	// No early exit: every secret and every candidate is compared in constant time, so neither
	// the number of secrets nor which one matched is observable from timing.
	matched := 0
	for _, secret := range list {
		mac := hmac.New(sha256.New, []byte(secret))
		mac.Write([]byte(rawTS))
		mac.Write([]byte{'.'})
		mac.Write(rawBody)
		expected := mac.Sum(nil)
		for _, cand := range sigs {
			got, err := hex.DecodeString(cand)
			if err != nil || len(got) != len(expected) {
				continue
			}
			matched |= subtle.ConstantTimeCompare(expected, got)
		}
	}
	if matched != 1 {
		return &WebhookError{Reason: ReasonNoMatch, Message: "signature did not match any configured secret"}
	}
	return nil
}

// ParseWebhook verifies the delivery and decodes the envelope in one step. On failure it
// returns a *WebhookError.
func ParseWebhook(rawBody []byte, header string, secrets []string, opts ...VerifyOption) (*WebhookEvent, error) {
	if err := CheckWebhook(rawBody, header, secrets, opts...); err != nil {
		return nil, err
	}
	var env struct {
		Event     *string         `json:"event"`
		Sequence  json.Number     `json:"sequence"`
		CreatedAt *time.Time      `json:"created_at"`
		Data      json.RawMessage `json:"data"`
		Invoice   json.RawMessage `json:"invoice"` // pre-envelope shape
	}
	trimmed := bytes.TrimSpace(rawBody)
	if len(trimmed) == 0 || trimmed[0] != '{' {
		if json.Valid(trimmed) {
			return nil, &WebhookError{Reason: ReasonBadEnvelope, Message: "body is not a {event, sequence, created_at, data} envelope"}
		}
		return nil, &WebhookError{Reason: ReasonBadJSON, Message: "body is not valid JSON"}
	}
	dec := json.NewDecoder(bytes.NewReader(trimmed))
	dec.UseNumber()
	if err := dec.Decode(&env); err != nil {
		if json.Valid(trimmed) {
			return nil, &WebhookError{Reason: ReasonBadEnvelope, Message: "body is not a {event, sequence, created_at, data} envelope: " + err.Error()}
		}
		return nil, &WebhookError{Reason: ReasonBadJSON, Message: "body is not valid JSON: " + err.Error()}
	}
	if env.Event == nil || *env.Event == "" {
		return nil, &WebhookError{Reason: ReasonBadEnvelope, Message: "body is not a {event, sequence, created_at, data} envelope"}
	}
	ev := &WebhookEvent{Event: *env.Event, Data: env.Data}
	if env.Sequence != "" {
		if n, err := strconv.ParseInt(env.Sequence.String(), 10, 64); err == nil {
			ev.Sequence = n
		}
	}
	if env.CreatedAt != nil {
		ev.CreatedAt = *env.CreatedAt
	}
	if (len(ev.Data) == 0 || string(ev.Data) == "null") && len(env.Invoice) > 0 {
		ev.Data = env.Invoice
	}
	if len(ev.Data) == 0 {
		ev.Data = json.RawMessage("{}")
	}
	return ev, nil
}

// SignWebhook signs a body the way Receivify does and returns the full header value
// "t=<unix>,v1=<hex>". You never need it to RECEIVE webhooks; it exists for tests and fixtures.
func SignWebhook(rawBody []byte, secret string, t time.Time) string {
	ts := strconv.FormatInt(t.Unix(), 10)
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(ts + "."))
	mac.Write(rawBody)
	return "t=" + ts + ",v1=" + hex.EncodeToString(mac.Sum(nil))
}

// VerifyWebhook verifies against the secrets configured with WithWebhookSecrets.
func (c *Client) VerifyWebhook(rawBody []byte, header string, opts ...VerifyOption) bool {
	return VerifyWebhook(rawBody, header, c.webhookSecrets, opts...)
}

// CheckWebhook is CheckWebhook against the configured secrets.
func (c *Client) CheckWebhook(rawBody []byte, header string, opts ...VerifyOption) error {
	return CheckWebhook(rawBody, header, c.webhookSecrets, opts...)
}

// ParseWebhook verifies against the configured secrets and decodes the envelope.
func (c *Client) ParseWebhook(rawBody []byte, header string, opts ...VerifyOption) (*WebhookEvent, error) {
	return ParseWebhook(rawBody, header, c.webhookSecrets, opts...)
}

func normaliseSecrets(secrets []string) []string {
	out := make([]string, 0, len(secrets))
	for _, s := range secrets {
		if s != "" {
			out = append(out, s)
		}
	}
	return out
}

// parseSignatureHeader returns the raw timestamp text (which is what was signed), its value,
// and every v1 candidate.
func parseSignatureHeader(header string) (rawTS string, ts int64, sigs []string, ok bool) {
	if header == "" {
		return "", 0, nil, false
	}
	haveTS := false
	for _, part := range strings.Split(header, ",") {
		k, v, found := strings.Cut(part, "=")
		if !found {
			continue
		}
		k, v = strings.TrimSpace(k), strings.TrimSpace(v)
		switch k {
		case "t":
			if !haveTS {
				rawTS, haveTS = v, true
			}
		case "v1":
			if v != "" {
				sigs = append(sigs, v)
			}
		}
	}
	if !haveTS || len(sigs) == 0 || rawTS == "" {
		return "", 0, nil, false
	}
	for _, r := range rawTS {
		if r < '0' || r > '9' {
			return "", 0, nil, false
		}
	}
	n, err := strconv.ParseInt(rawTS, 10, 64)
	if err != nil {
		return "", 0, nil, false
	}
	return rawTS, n, sigs, true
}
