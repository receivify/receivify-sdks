package receivify

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

// Offline test transport. Every test injects one of these through WithHTTPClient, so the
// suite never touches a network and can assert on the exact request the SDK built.

type recorded struct {
	Method string
	URL    string
	Path   string
	Query  map[string]string
	Header http.Header
	Body   []byte
}

func (r *recorded) JSON(t *testing.T) map[string]any {
	t.Helper()
	var m map[string]any
	if err := json.Unmarshal(r.Body, &m); err != nil {
		t.Fatalf("request body is not JSON: %v (%q)", err, r.Body)
	}
	return m
}

type reply struct {
	Status int
	Body   any // marshalled as JSON unless it is a string
	Header map[string]string
	Err    error // a transport error instead of a response
}

type mockRT struct {
	mu      sync.Mutex
	calls   []*recorded
	handler func(r *recorded, n int) reply
}

func (m *mockRT) RoundTrip(req *http.Request) (*http.Response, error) {
	var body []byte
	if req.Body != nil {
		body, _ = io.ReadAll(req.Body)
	}
	q := map[string]string{}
	for k, v := range req.URL.Query() {
		q[k] = v[0]
	}
	rec := &recorded{Method: req.Method, URL: req.URL.String(), Path: req.URL.EscapedPath(), Query: q, Header: req.Header.Clone(), Body: body}
	m.mu.Lock()
	m.calls = append(m.calls, rec)
	n := len(m.calls)
	m.mu.Unlock()

	rp := m.handler(rec, n)
	if rp.Err != nil {
		return nil, rp.Err
	}
	if rp.Status == 0 {
		rp.Status = 200
	}
	var payload string
	switch b := rp.Body.(type) {
	case nil:
	case string:
		payload = b
	default:
		raw, err := json.Marshal(b)
		if err != nil {
			panic(err)
		}
		payload = string(raw)
	}
	h := http.Header{}
	h.Set("Content-Type", "application/json")
	for k, v := range rp.Header {
		h.Set(k, v)
	}
	return &http.Response{
		StatusCode: rp.Status,
		Status:     strconv.Itoa(rp.Status) + " " + http.StatusText(rp.Status),
		Header:     h,
		Body:       io.NopCloser(strings.NewReader(payload)),
		Request:    req,
	}, nil
}

func (m *mockRT) only(t *testing.T) *recorded {
	t.Helper()
	if len(m.calls) != 1 {
		t.Fatalf("expected exactly 1 request, got %d", len(m.calls))
	}
	return m.calls[0]
}

// newTestClient builds a client whose transport is handler and whose retry sleeps are no-ops
// (recorded in *sleeps).
func newTestClient(t *testing.T, handler func(r *recorded, n int) reply, opts ...Option) (*Client, *mockRT, *[]time.Duration) {
	t.Helper()
	rt := &mockRT{handler: handler}
	all := append([]Option{WithHTTPClient(&http.Client{Transport: rt})}, opts...)
	c, err := NewClient("https://api.example.test", "rcv_live_secretkey123", all...)
	if err != nil {
		t.Fatal(err)
	}
	var sleeps []time.Duration
	c.sleep = func(ctx context.Context, d time.Duration) error {
		sleeps = append(sleeps, d)
		return ctx.Err()
	}
	return c, rt, &sleeps
}

func constant(rp reply) func(*recorded, int) reply {
	return func(*recorded, int) reply { return rp }
}

func sequence(t *testing.T, rps ...reply) func(*recorded, int) reply {
	return func(_ *recorded, n int) reply {
		if n > len(rps) {
			t.Errorf("unexpected request #%d", n)
			return reply{Status: 500, Body: map[string]string{"error": "internal", "detail": "unexpected"}}
		}
		return rps[n-1]
	}
}

// sampleInvoice is a realistic Invoice body including the newer fields.
func sampleInvoice(over map[string]any) map[string]any {
	inv := map[string]any{
		"id":                   "3f2b8c1e-5a4d-4e8f-9b7a-1c2d3e4f5a6b",
		"reference":            "order-1001",
		"asset":                "btc",
		"amount_expected":      "100000",
		"amount_received":      "0",
		"amount_fiat":          nil,
		"fiat_currency":        nil,
		"chain":                nil,
		"provider":             "bitcoind",
		"address":              "bc1qexampleaddress",
		"status":               "pending",
		"expires_at":           "2026-08-24T12:05:00.000Z",
		"paid_at":              nil,
		"created_at":           "2026-08-24T11:05:00.000Z",
		"metadata":             map[string]any{},
		"checkout_url":         "https://api.example.test/pay/3f2b8c1e-5a4d-4e8f-9b7a-1c2d3e4f5a6b",
		"decimals":             8,
		"amount_decimal":       "0.00100000",
		"payment_uri":          "bitcoin:bc1qexampleaddress?amount=0.00100000",
		"add_fee_ontop":        false,
		"amount_net_requested": "100000",
		"fee_ontop":            "0",
	}
	for k, v := range over {
		inv[k] = v
	}
	return inv
}

func sampleForward(over map[string]any) map[string]any {
	f := map[string]any{
		"id":                     "8a1d2c3b-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
		"forward_id":             "8a1d2c3b-4e5f-4a6b-8c7d-9e0f1a2b3c4d",
		"ticker":                 "btc",
		"provider":               "bitcoind",
		"chain":                  nil,
		"address":                "bc1qdeposit",
		"destination":            "bc1qdestination",
		"callback_url":           "https://shop.example/hooks/forward",
		"confirmations_required": 2,
		"min_forward":            "0.00010000",
		"status":                 "active",
		"expires_at":             nil,
		"created_at":             "2026-08-24T11:05:00.000Z",
		"updated_at":             nil,
	}
	for k, v := range over {
		f[k] = v
	}
	return f
}

func errBody(code, detail string) map[string]string {
	return map[string]string{"error": code, "detail": detail}
}
