package receivify

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"testing"
	"time"
)

var ctx = context.Background()

/* ---------------------------------------------------------------- *
 * Construction, headers, secrecy of the key
 * ---------------------------------------------------------------- */

func TestAuthenticatedGetSendsDocumentedHeaders(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: sampleInvoice(nil)}))
	if _, err := c.GetInvoice(ctx, "3f2b8c1e-5a4d-4e8f-9b7a-1c2d3e4f5a6b"); err != nil {
		t.Fatal(err)
	}
	r := rt.only(t)
	if r.Method != "GET" || r.Path != "/api/v1/invoices/3f2b8c1e-5a4d-4e8f-9b7a-1c2d3e4f5a6b" {
		t.Fatalf("got %s %s", r.Method, r.Path)
	}
	if got := r.Header.Get("Authorization"); got != "Bearer rcv_live_secretkey123" {
		t.Fatalf("Authorization = %q", got)
	}
	if got := r.Header.Get("User-Agent"); got != "receivify-go/"+Version {
		t.Fatalf("User-Agent = %q", got)
	}
	if got := r.Header.Get("Accept"); got != "application/json" {
		t.Fatalf("Accept = %q", got)
	}
	if r.Header.Get("Idempotency-Key") != "" || r.Header.Get("Content-Type") != "" {
		t.Fatal("a bodiless GET must not carry Idempotency-Key or Content-Type")
	}
}

func TestAPIKeyNeverInURLOrFmtOutput(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: map[string]any{"data": []any{}, "has_more": false, "next_cursor": nil}}))
	if _, err := c.ListInvoices(ctx, &ListInvoicesParams{Limit: 5}); err != nil {
		t.Fatal(err)
	}
	if strings.Contains(rt.only(t).URL, "secretkey123") {
		t.Fatal("API key leaked into the URL")
	}
	for _, f := range []string{"%v", "%+v", "%#v", "%s"} {
		if s := fmt.Sprintf(f, c); strings.Contains(s, "secretkey123") {
			t.Fatalf("fmt %s leaked the key: %s", f, s)
		}
	}
}

func TestAPIKeyNeverInErrors(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Status: 401, Body: errBody("unauthorized", "Invalid API key")}))
	_, err := c.GetInvoice(ctx, "x")
	if err == nil || strings.Contains(err.Error(), "secretkey123") {
		t.Fatalf("err = %v", err)
	}
	c2, _, _ := newTestClient(t, constant(reply{Err: errors.New("dial tcp: refused")}), WithMaxRetries(0))
	_, err = c2.GetInvoice(ctx, "x")
	if err == nil || strings.Contains(err.Error(), "secretkey123") {
		t.Fatalf("err = %v", err)
	}
}

func TestTrailingSlashTrimmed(t *testing.T) {
	rt := &mockRT{handler: constant(reply{Body: map[string]any{"data": []any{}, "has_more": false, "next_cursor": nil}})}
	c, err := NewClient("https://api.example.test///", "k", WithHTTPClient(&http.Client{Transport: rt}))
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.ListTickers(ctx); err != nil {
		t.Fatal(err)
	}
	if got := rt.only(t).URL; got != "https://api.example.test/api/v1/tickers" {
		t.Fatalf("URL = %s", got)
	}
}

func TestNewClientValidatesURL(t *testing.T) {
	for _, u := range []string{"", "   ", "receivify.io", "ftp://x", "https://user:pw@x.test", "https://x.test?a=1"} {
		if _, err := NewClient(u, "k"); err == nil {
			t.Errorf("NewClient(%q) should fail", u)
		}
	}
}

func TestPathIDsAreEscaped(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: sampleInvoice(nil)}))
	if _, err := c.GetInvoice(ctx, "a/b?c"); err != nil {
		t.Fatal(err)
	}
	if got := rt.only(t).Path; got != "/api/v1/invoices/a%2Fb%3Fc" {
		t.Fatalf("path = %s", got)
	}
}

func TestMissingIDFailsBeforeRequest(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{}))
	if _, err := c.GetInvoice(ctx, ""); !errors.Is(err, ErrMissingID) {
		t.Fatalf("err = %v", err)
	}
	if _, err := c.CancelInvoice(ctx, " "); !errors.Is(err, ErrMissingID) {
		t.Fatalf("err = %v", err)
	}
	if _, err := c.RevokeAPIKey(ctx, ""); !errors.Is(err, ErrMissingID) {
		t.Fatalf("err = %v", err)
	}
	if len(rt.calls) != 0 {
		t.Fatalf("made %d requests", len(rt.calls))
	}
}

func TestUserAgentSuffixAppended(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: map[string]any{"data": []any{}, "has_more": false, "next_cursor": nil}}), WithUserAgent("my-shop/2.1"))
	_, _ = c.ListTickers(ctx)
	if got := rt.only(t).Header.Get("User-Agent"); got != "receivify-go/"+Version+" my-shop/2.1" {
		t.Fatalf("User-Agent = %q", got)
	}
}

func TestPublicRoutesSendNoAuthorization(t *testing.T) {
	c, rt, _ := newTestClient(t, func(r *recorded, n int) reply {
		if strings.HasPrefix(r.Path, "/api/public/invoices") {
			return reply{Body: map[string]any{"id": "i", "status": "pending", "address": "a", "amount_expected": "1", "amount_received": "0", "expires_at": "2026-08-24T12:05:00.000Z"}}
		}
		return reply{Body: map[string]any{"data": []any{}, "has_more": false, "next_cursor": nil}}
	})
	_, _ = c.ListTickers(ctx)
	_, _ = c.ListAssets(ctx)
	if _, err := c.GetPublicInvoice(ctx, "i"); err != nil {
		t.Fatal(err)
	}
	for _, r := range rt.calls {
		if r.Header.Get("Authorization") != "" {
			t.Fatalf("%s sent Authorization", r.Path)
		}
	}
}

func TestClientWithoutKeyCallsPublicRoutes(t *testing.T) {
	rt := &mockRT{handler: constant(reply{Body: map[string]any{"data": []any{map[string]any{"ticker": "btc", "provider": "bitcoind", "chain": nil, "name": "Bitcoin", "decimals": 8}}, "has_more": false, "next_cursor": nil}})}
	c, err := NewClient("https://api.example.test", "", WithHTTPClient(&http.Client{Transport: rt}))
	if err != nil {
		t.Fatal(err)
	}
	l, err := c.ListTickers(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(l.Data) != 1 || l.Data[0].Decimals != 8 || l.Data[0].Chain != "" {
		t.Fatalf("tickers = %+v", l)
	}
}

/* ---------------------------------------------------------------- *
 * Invoices: body, idempotency, add_fee_ontop, new fields, money
 * ---------------------------------------------------------------- */

func TestCreateInvoicePostsJSONWithIdempotencyKey(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Status: 201, Body: sampleInvoice(nil)}))
	inv, err := c.CreateInvoice(ctx, &CreateInvoiceParams{Asset: "btc", Amount: "0.001", Reference: "order-1001", Metadata: map[string]any{"cart": 7}},
		RequestOptions{IdempotencyKey: "order-1001-attempt"})
	if err != nil {
		t.Fatal(err)
	}
	r := rt.only(t)
	if r.Method != "POST" || r.Path != "/api/v1/invoices" {
		t.Fatalf("%s %s", r.Method, r.Path)
	}
	if r.Header.Get("Content-Type") != "application/json" {
		t.Fatal("missing Content-Type")
	}
	if r.Header.Get("Idempotency-Key") != "order-1001-attempt" {
		t.Fatalf("Idempotency-Key = %q", r.Header.Get("Idempotency-Key"))
	}
	body := r.JSON(t)
	if body["asset"] != "btc" || body["amount"] != "0.001" || body["reference"] != "order-1001" {
		t.Fatalf("body = %v", body)
	}
	for _, k := range []string{"chain", "amount_fiat", "amount_sats", "amount_btc", "callback_url", "currency"} {
		if _, ok := body[k]; ok {
			t.Errorf("unset field %q was sent", k)
		}
	}
	if inv.AmountExpected != "100000" || inv.ID == "" {
		t.Fatalf("inv = %+v", inv)
	}
}

func TestAddFeeOntopOmittedFalseTrue(t *testing.T) {
	cases := []struct {
		name    string
		val     *bool
		present bool
		want    bool
	}{
		{"omitted", nil, false, false},
		{"false", Bool(false), true, false},
		{"true", Bool(true), true, true},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c, rt, _ := newTestClient(t, constant(reply{Status: 201, Body: sampleInvoice(nil)}))
			if _, err := c.CreateInvoice(ctx, &CreateInvoiceParams{Asset: "usdc", Chain: "base", Amount: "25", AddFeeOntop: tc.val}); err != nil {
				t.Fatal(err)
			}
			raw := string(rt.only(t).Body)
			body := rt.only(t).JSON(t)
			v, ok := body["add_fee_ontop"]
			if ok != tc.present {
				t.Fatalf("add_fee_ontop present=%v, want %v (body %s)", ok, tc.present, raw)
			}
			if ok {
				b, isBool := v.(bool)
				if !isBool || b != tc.want {
					t.Fatalf("add_fee_ontop = %#v, want real boolean %v", v, tc.want)
				}
			}
		})
	}
}

func TestInvoiceNewFieldsDecode(t *testing.T) {
	body := sampleInvoice(map[string]any{
		"asset": "usdc", "chain": "base", "provider": "evm",
		"amount_expected": "25252526", "decimals": 6, "amount_decimal": "25.252526",
		"payment_uri": nil, "add_fee_ontop": true, "amount_net_requested": "25000000", "fee_ontop": "252526",
		"amount_fiat": "25.00", "fiat_currency": "USD", "paid_at": "2026-08-24T11:30:00.000Z",
	})
	c, _, _ := newTestClient(t, constant(reply{Body: body}))
	inv, err := c.GetInvoice(ctx, "x")
	if err != nil {
		t.Fatal(err)
	}
	if inv.Decimals != 6 || inv.AmountDecimal != "25.252526" || inv.PaymentURI != "" || !inv.AddFeeOntop ||
		inv.AmountNetRequested != "25000000" || inv.FeeOntop != "252526" || inv.Chain != "base" || inv.Provider != "evm" ||
		inv.AmountFiat != "25.00" || inv.FiatCurrency != "USD" {
		t.Fatalf("inv = %+v", inv)
	}
	if inv.PaidAt == nil || inv.PaidAt.UTC().Format(time.RFC3339) != "2026-08-24T11:30:00Z" {
		t.Fatalf("PaidAt = %v", inv.PaidAt)
	}
	// The fee identity holds exactly, without floats.
	exp, _ := ToBigInt(inv.AmountExpected)
	net, _ := ToBigInt(inv.AmountNetRequested)
	fee, _ := ToBigInt(inv.FeeOntop)
	if exp.Cmp(net.Add(net, fee)) != 0 {
		t.Fatal("amount_expected != amount_net_requested + fee_ontop")
	}

	c2, _, _ := newTestClient(t, constant(reply{Body: sampleInvoice(nil)}))
	inv2, _ := c2.GetInvoice(ctx, "x")
	if inv2.PaymentURI != "bitcoin:bc1qexampleaddress?amount=0.00100000" || inv2.AddFeeOntop || inv2.FeeOntop != "0" || inv2.Decimals != 8 {
		t.Fatalf("inv2 = %+v", inv2)
	}
	if inv2.ExpiresAt.IsZero() || inv2.CreatedAt.IsZero() || inv2.PaidAt != nil {
		t.Fatal("timestamps not decoded")
	}
}

func TestHugeBaseUnitAmountsSurviveExactly(t *testing.T) {
	huge := "115792089237316195423570985008687907853269984665640564039457584007913129639935" // 2^256-1 wei
	c, _, _ := newTestClient(t, constant(reply{Body: sampleInvoice(map[string]any{"amount_expected": huge, "amount_received": "9007199254740993"})}))
	inv, err := c.GetInvoice(ctx, "x")
	if err != nil {
		t.Fatal(err)
	}
	if inv.AmountExpected != huge || inv.AmountReceived != "9007199254740993" {
		t.Fatalf("amounts changed: %s %s", inv.AmountExpected, inv.AmountReceived)
	}
}

func TestNumericMoneyFromServerIsRejectedNotFloated(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Body: `{"id":"x","amount_expected":100000.5}`}))
	if _, err := c.GetInvoice(ctx, "x"); err == nil {
		t.Fatal("a JSON number for a base-unit field must not silently decode")
	}
}

func TestMetadataNumbersStayJSONNumbers(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Body: sampleInvoice(map[string]any{"metadata": map[string]any{"order": json.Number("9007199254740993")}})}))
	inv, err := c.GetInvoice(ctx, "x")
	if err != nil {
		t.Fatal(err)
	}
	n, ok := inv.Metadata["order"].(json.Number)
	if !ok || n.String() != "9007199254740993" {
		t.Fatalf("metadata order = %#v", inv.Metadata["order"])
	}
}

func TestListInvoicesQuerySerialisation(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: map[string]any{"data": []any{}, "has_more": false, "next_cursor": nil}}))
	if _, err := c.ListInvoices(ctx, &ListInvoicesParams{Limit: 2, Cursor: "abc", Status: InvoiceStatusPaid}); err != nil {
		t.Fatal(err)
	}
	q := rt.only(t).Query
	if q["limit"] != "2" || q["cursor"] != "abc" || q["status"] != "paid" || len(q) != 3 {
		t.Fatalf("query = %v", q)
	}
	rt.calls = nil
	_, _ = c.ListInvoices(ctx, nil)
	if len(rt.only(t).Query) != 0 {
		t.Fatalf("empty params must send no query, got %v", rt.calls[0].Query)
	}
}

func TestBooleanQueryIncludesFalse(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: map[string]any{"data": []any{}, "has_more": false, "next_cursor": nil}}))
	_, _ = c.ListPaymentLinks(ctx, &ListPaymentLinksParams{Active: Bool(false)})
	if rt.only(t).Query["active"] != "false" {
		t.Fatalf("query = %v", rt.calls[0].Query)
	}
}

func TestCancelInvoiceAndBulkCancel(t *testing.T) {
	c, rt, _ := newTestClient(t, func(r *recorded, n int) reply {
		if r.Path == "/api/v1/invoices/bulk" {
			return reply{Body: map[string]any{"ok": true, "action": "cancel", "requested": 2, "cancelled": 1}}
		}
		return reply{Body: sampleInvoice(map[string]any{"status": "cancelled"})}
	})
	inv, err := c.CancelInvoice(ctx, "abc")
	if err != nil || inv.Status != InvoiceStatusCancelled {
		t.Fatalf("inv=%v err=%v", inv, err)
	}
	if rt.calls[0].Method != "POST" || rt.calls[0].Path != "/api/v1/invoices/abc/cancel" || len(rt.calls[0].Body) != 0 {
		t.Fatalf("cancel request = %+v", rt.calls[0])
	}
	res, err := c.BulkCancelInvoices(ctx, []string{"a", "b"})
	if err != nil || res.Requested != 2 || res.Cancelled != 1 {
		t.Fatalf("res=%+v err=%v", res, err)
	}
	b := rt.calls[1].JSON(t)
	if b["action"] != "cancel" || len(b["ids"].([]any)) != 2 {
		t.Fatalf("bulk body = %v", b)
	}
}

/* ---------------------------------------------------------------- *
 * Forwarding addresses
 * ---------------------------------------------------------------- */

func TestCreateAddressReportsCreatedFrom201vs200(t *testing.T) {
	c, rt, _ := newTestClient(t, sequence(t,
		reply{Status: 201, Body: sampleForward(nil)},
		reply{Status: 200, Body: sampleForward(nil)},
	))
	p := &CreateAddressParams{Ticker: "btc", Destination: "bc1qdestination", Callback: "https://shop.example/hooks/forward"}
	f1, err := c.CreateAddress(ctx, p)
	if err != nil || !f1.Created {
		t.Fatalf("first: %+v %v", f1, err)
	}
	f2, err := c.CreateAddress(ctx, p)
	if err != nil || f2.Created {
		t.Fatalf("second: %+v %v", f2, err)
	}
	if f1.ID != f1.ForwardID || f1.ID == "" {
		t.Fatal("id and forward_id must carry the same value")
	}
	b := rt.calls[0].JSON(t)
	if b["ticker"] != "btc" || b["destination"] != "bc1qdestination" || b["callback"] != "https://shop.example/hooks/forward" {
		t.Fatalf("body = %v", b)
	}
	if _, ok := b["confirmations"]; ok {
		t.Fatal("zero confirmations must be omitted")
	}
	if f1.MinForward != "0.00010000" || f1.ExpiresAt != nil || f1.Chain != "" {
		t.Fatalf("forward = %+v", f1)
	}
}

func TestGetAddressDecodesDetail(t *testing.T) {
	d := sampleForward(map[string]any{
		"payments":           []any{map[string]any{"txid_in": "tx1", "value": "150000", "status": "sent", "txid_out": "tx2", "confirmations": 3, "created_at": "2026-08-24T11:05:00.000Z"}},
		"payments_has_more":  false,
		"callbacks":          []any{},
		"callbacks_has_more": false,
	})
	c, rt, _ := newTestClient(t, constant(reply{Body: d}))
	got, err := c.GetAddress(ctx, "8a1d")
	if err != nil {
		t.Fatal(err)
	}
	if rt.only(t).Path != "/api/v1/address/8a1d" {
		t.Fatal(rt.calls[0].Path)
	}
	if got.ID == "" || len(got.Payments) != 1 || got.Payments[0].Value != "150000" || got.Payments[0].TxidOut != "tx2" {
		t.Fatalf("detail = %+v", got)
	}
}

func TestUpdateAndDisableAddress(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: sampleForward(map[string]any{"status": "paused"})}))
	if _, err := c.UpdateAddress(ctx, "f1", &UpdateAddressParams{Status: ForwardStatusPaused}); err != nil {
		t.Fatal(err)
	}
	if _, err := c.UpdateAddress(ctx, "f1", &UpdateAddressParams{CallbackURL: String("")}); err != nil {
		t.Fatal(err)
	}
	if _, err := c.DisableAddress(ctx, "f1"); err != nil {
		t.Fatal(err)
	}
	if rt.calls[0].Method != "PATCH" || string(rt.calls[0].Body) != `{"status":"paused"}` {
		t.Fatalf("patch 1 = %s %s", rt.calls[0].Method, rt.calls[0].Body)
	}
	if string(rt.calls[1].Body) != `{"callback_url":""}` {
		t.Fatalf("patch 2 = %s", rt.calls[1].Body)
	}
	if rt.calls[2].Method != "DELETE" || rt.calls[2].Path != "/api/v1/address/f1" {
		t.Fatalf("disable = %s %s", rt.calls[2].Method, rt.calls[2].Path)
	}
}

func TestListAddressesQuery(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: map[string]any{"data": []any{sampleForward(nil)}, "has_more": false, "next_cursor": nil}}))
	l, err := c.ListAddresses(ctx, &ListAddressesParams{Status: ForwardStatusActive, Ticker: "btc", Limit: 10})
	if err != nil || len(l.Data) != 1 {
		t.Fatalf("%v %v", l, err)
	}
	q := rt.only(t).Query
	if q["status"] != "active" || q["ticker"] != "btc" || q["limit"] != "10" {
		t.Fatalf("query = %v", q)
	}
}

/* ---------------------------------------------------------------- *
 * Lists and auto-pagination
 * ---------------------------------------------------------------- */

func TestListEnvelopeDecoded(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Body: map[string]any{"data": []any{sampleInvoice(nil), sampleInvoice(nil)}, "has_more": true, "next_cursor": "cur_2"}}))
	l, err := c.ListInvoices(ctx, &ListInvoicesParams{Limit: 2})
	if err != nil {
		t.Fatal(err)
	}
	if len(l.Data) != 2 || !l.HasMore || l.NextCursor != "cur_2" {
		t.Fatalf("list = %+v", l)
	}
}

func page(ids []string, more bool, next any) reply {
	data := []any{}
	for _, id := range ids {
		data = append(data, sampleInvoice(map[string]any{"id": id}))
	}
	return reply{Body: map[string]any{"data": data, "has_more": more, "next_cursor": next}}
}

func TestAutoPagingFollowsCursorUntilDone(t *testing.T) {
	c, rt, _ := newTestClient(t, sequence(t,
		page([]string{"a", "b"}, true, "c1"),
		page([]string{"c", "d"}, true, "c2"),
		page([]string{"e"}, false, nil),
	))
	it := c.ListInvoicesAutoPaging(ctx, &ListInvoicesParams{Limit: 2, Status: InvoiceStatusPending})
	var got []string
	for it.Next() {
		got = append(got, it.Current().ID)
	}
	if err := it.Err(); err != nil {
		t.Fatal(err)
	}
	if strings.Join(got, ",") != "a,b,c,d,e" {
		t.Fatalf("got %v", got)
	}
	if len(rt.calls) != 3 {
		t.Fatalf("requests = %d", len(rt.calls))
	}
	if _, ok := rt.calls[0].Query["cursor"]; ok {
		t.Fatal("first page must not send a cursor")
	}
	if rt.calls[1].Query["cursor"] != "c1" || rt.calls[2].Query["cursor"] != "c2" {
		t.Fatal("cursors not followed")
	}
	for _, r := range rt.calls {
		if r.Query["status"] != "pending" || r.Query["limit"] != "2" {
			t.Fatalf("filters not kept across pages: %v", r.Query)
		}
	}
}

func TestAutoPagingSinglePageStops(t *testing.T) {
	c, rt, _ := newTestClient(t, sequence(t, page([]string{"a"}, false, nil)))
	items, err := c.ListInvoicesAutoPaging(ctx, nil).Collect(0)
	if err != nil || len(items) != 1 || len(rt.calls) != 1 {
		t.Fatalf("items=%d err=%v calls=%d", len(items), err, len(rt.calls))
	}
}

func TestAutoPagingEmptyList(t *testing.T) {
	c, _, _ := newTestClient(t, sequence(t, page(nil, false, nil)))
	it := c.ListInvoicesAutoPaging(ctx, nil)
	if it.Next() || it.Err() != nil {
		t.Fatal("empty list should end cleanly")
	}
}

func TestAutoPagingRefusesRepeatedCursor(t *testing.T) {
	c, _, _ := newTestClient(t, sequence(t,
		page([]string{"a"}, true, "same"),
		page([]string{"b"}, true, "same"),
	))
	items, err := c.ListInvoicesAutoPaging(ctx, nil).Collect(0)
	if !errors.Is(err, ErrPaginationLoop) {
		t.Fatalf("err = %v", err)
	}
	if len(items) != 2 {
		t.Fatalf("items already fetched should still be yielded, got %d", len(items))
	}
}

func TestAutoPagingRefusesHasMoreWithoutCursor(t *testing.T) {
	c, _, _ := newTestClient(t, sequence(t, page([]string{"a"}, true, nil)))
	_, err := c.ListInvoicesAutoPaging(ctx, nil).Collect(0)
	if !errors.Is(err, ErrPaginationNoCursor) {
		t.Fatalf("err = %v", err)
	}
}

func TestAutoPagingCollectMaxStopsRequesting(t *testing.T) {
	c, rt, _ := newTestClient(t, sequence(t,
		page([]string{"a", "b"}, true, "c1"),
		page([]string{"c", "d"}, true, "c2"),
	))
	items, err := c.ListInvoicesAutoPaging(ctx, nil).Collect(3)
	if err != nil || len(items) != 3 || len(rt.calls) != 2 {
		t.Fatalf("items=%d err=%v calls=%d", len(items), err, len(rt.calls))
	}
}

func TestAutoPagingSurfacesAPIError(t *testing.T) {
	c, _, _ := newTestClient(t, sequence(t,
		page([]string{"a"}, true, "c1"),
		reply{Status: 400, Body: errBody("bad_request", "Malformed cursor")},
	))
	items, err := c.ListInvoicesAutoPaging(ctx, nil).Collect(0)
	if len(items) != 1 || !errors.Is(err, ErrBadRequest) {
		t.Fatalf("items=%d err=%v", len(items), err)
	}
}

func TestEveryListHasAutoPagingTwin(t *testing.T) {
	empty := reply{Body: map[string]any{"data": []any{}, "has_more": false, "next_cursor": nil}}
	c, rt, _ := newTestClient(t, constant(empty))
	_, _ = c.ListAddressesAutoPaging(ctx, nil).Collect(0)
	_, _ = c.ListWebhookEndpointsAutoPaging(ctx, nil).Collect(0)
	_, _ = c.ListWebhookDeliveriesAutoPaging(ctx, nil).Collect(0)
	_, _ = c.ListPaymentLinksAutoPaging(ctx, nil).Collect(0)
	_, _ = c.ListRecurringAutoPaging(ctx, nil).Collect(0)
	_, _ = c.ListAPIKeysAutoPaging(ctx, nil).Collect(0)
	want := []string{"/api/v1/address", "/api/v1/webhooks/endpoints", "/api/v1/webhooks/deliveries", "/api/v1/payment-links", "/api/v1/recurring", "/api/v1/keys"}
	if len(rt.calls) != len(want) {
		t.Fatalf("calls = %d", len(rt.calls))
	}
	for i, w := range want {
		if rt.calls[i].Path != w {
			t.Errorf("call %d = %s, want %s", i, rt.calls[i].Path, w)
		}
	}
}

/* ---------------------------------------------------------------- *
 * Errors
 * ---------------------------------------------------------------- */

func TestKnownCodeMapsToSentinelWithDetailPreserved(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Status: 404, Body: errBody("not_found", "Invoice not found")}))
	_, err := c.GetInvoice(ctx, "00000000-0000-4000-8000-000000000000")
	if !errors.Is(err, ErrNotFound) {
		t.Fatalf("err = %v", err)
	}
	if errors.Is(err, ErrUnauthorized) {
		t.Fatal("must not match another sentinel")
	}
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatal("not an *APIError")
	}
	if apiErr.Code != CodeNotFound || apiErr.Detail != "Invoice not found" || apiErr.StatusCode != 404 || !apiErr.Known() {
		t.Fatalf("apiErr = %+v", apiErr)
	}
	if apiErr.Method != "GET" || apiErr.Path != "/api/v1/invoices/00000000-0000-4000-8000-000000000000" {
		t.Fatalf("request info = %s %s", apiErr.Method, apiErr.Path)
	}
	if !strings.Contains(err.Error(), "not_found") || !strings.Contains(err.Error(), "404") {
		t.Fatalf("message = %s", err)
	}
}

func TestEveryRegistryCodeMatchesItsOwnSentinelOnly(t *testing.T) {
	sentinels := map[ErrorCode]error{
		CodeBadRequest: ErrBadRequest, CodeBadTicker: ErrBadTicker, CodeBadDestination: ErrBadDestination,
		CodeAssetUnavailable: ErrAssetUnavailable, CodeAmountTooLow: ErrAmountTooLow, CodeSuspended: ErrSuspended,
		CodeLimitExceeded: ErrLimitExceeded, CodeInvalidCode: ErrInvalidCode, CodeNoSetup: ErrNoSetup,
		CodeNotEnabled: ErrNotEnabled, CodeNoTarget: ErrNoTarget, CodeInvalidToken: ErrInvalidToken,
		CodeUnauthorized: ErrUnauthorized, CodeBadSignature: ErrBadSignature, CodeSignupDisabled: ErrSignupDisabled,
		CodeNotFound: ErrNotFound, CodeAlreadyExists: ErrAlreadyExists, CodeAlreadyEnabled: ErrAlreadyEnabled,
		CodeNotAllowed: ErrNotAllowed, CodeRateLimited: ErrRateLimited, CodeInternal: ErrInternal,
		CodeUpstreamError: ErrUpstream, CodeRateUnavailable: ErrRateUnavailable,
	}
	if len(sentinels) != len(ErrorCodes) || len(ErrorCodes) != 23 {
		t.Fatalf("registry size mismatch: %d sentinels, %d codes", len(sentinels), len(ErrorCodes))
	}
	for _, code := range ErrorCodes {
		e := &APIError{Code: code, StatusCode: 400}
		for other, s := range sentinels {
			if got := errors.Is(e, s); got != (other == code) {
				t.Errorf("errors.Is(%s, Err(%s)) = %v", code, other, got)
			}
		}
	}
}

func TestUnknownCodeFallsBackToAPIError(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Status: 418, Body: errBody("teapot_mode", "A future error")}))
	_, err := c.GetInvoice(ctx, "x")
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatalf("err = %#v", err)
	}
	if apiErr.Code != "teapot_mode" || apiErr.Detail != "A future error" || apiErr.StatusCode != 418 || apiErr.Known() {
		t.Fatalf("apiErr = %+v", apiErr)
	}
	for _, s := range []error{ErrBadRequest, ErrNotFound, ErrInternal, ErrRateLimited} {
		if errors.Is(err, s) {
			t.Fatalf("unknown code matched %v", s)
		}
	}
}

func TestNonJSONErrorBodyStillUsable(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Status: 502, Body: "<html>Bad Gateway</html>"}), WithMaxRetries(0))
	_, err := c.GetInvoice(ctx, "x")
	var apiErr *APIError
	if !errors.As(err, &apiErr) || apiErr.StatusCode != 502 || apiErr.Code != "" || !apiErr.Retryable() {
		t.Fatalf("err = %v", err)
	}
	if string(apiErr.Body) != "<html>Bad Gateway</html>" {
		t.Fatalf("body = %q", apiErr.Body)
	}
}

func TestRateLimitedCarriesHeaders(t *testing.T) {
	reset := time.Now().Add(30 * time.Second).Unix()
	c, _, _ := newTestClient(t, constant(reply{Status: 429, Body: errBody("rate_limited", "Slow down"),
		Header: map[string]string{"Retry-After": "7", "X-RateLimit-Reset": fmt.Sprint(reset), "X-RateLimit-Limit": "120", "X-RateLimit-Remaining": "0"}}),
		WithMaxRetries(0))
	_, err := c.GetInvoice(ctx, "x")
	var apiErr *APIError
	if !errors.Is(err, ErrRateLimited) || !errors.As(err, &apiErr) {
		t.Fatalf("err = %v", err)
	}
	if apiErr.RetryAfter() != 7*time.Second || apiErr.RateLimitReset().Unix() != reset || apiErr.RateLimitLimit() != 120 || apiErr.RateLimitRemaining() != 0 || !apiErr.Retryable() {
		t.Fatalf("headers = %v %v %v %v", apiErr.RetryAfter(), apiErr.RateLimitReset(), apiErr.RateLimitLimit(), apiErr.RateLimitRemaining())
	}
}

func TestBare429MatchesErrRateLimited(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Status: 429, Body: "too many"}), WithMaxRetries(0))
	_, err := c.GetInvoice(ctx, "x")
	if !errors.Is(err, ErrRateLimited) {
		t.Fatalf("err = %v", err)
	}
}

/* ---------------------------------------------------------------- *
 * Retries
 * ---------------------------------------------------------------- */

func TestGetRetriedOn500ThenSucceeds(t *testing.T) {
	c, rt, sleeps := newTestClient(t, sequence(t,
		reply{Status: 500, Body: errBody("internal", "boom")},
		reply{Body: sampleInvoice(nil)},
	))
	if _, err := c.GetInvoice(ctx, "x"); err != nil {
		t.Fatal(err)
	}
	if len(rt.calls) != 2 || len(*sleeps) != 1 {
		t.Fatalf("calls=%d sleeps=%d", len(rt.calls), len(*sleeps))
	}
}

func TestGetRetriedOn429HonouringRetryAfter(t *testing.T) {
	c, rt, sleeps := newTestClient(t, sequence(t,
		reply{Status: 429, Body: errBody("rate_limited", "slow"), Header: map[string]string{"Retry-After": "3"}},
		reply{Body: sampleInvoice(nil)},
	))
	if _, err := c.GetInvoice(ctx, "x"); err != nil {
		t.Fatal(err)
	}
	if len(rt.calls) != 2 || (*sleeps)[0] != 3*time.Second {
		t.Fatalf("calls=%d sleeps=%v", len(rt.calls), *sleeps)
	}
}

func TestRetryHonoursXRateLimitReset(t *testing.T) {
	now := time.Unix(1_800_000_000, 0)
	c, _, sleeps := newTestClient(t, sequence(t,
		reply{Status: 429, Body: errBody("rate_limited", "slow"), Header: map[string]string{"X-RateLimit-Reset": fmt.Sprint(now.Unix() + 5)}},
		reply{Body: sampleInvoice(nil)},
	))
	c.now = func() time.Time { return now }
	if _, err := c.GetInvoice(ctx, "x"); err != nil {
		t.Fatal(err)
	}
	if (*sleeps)[0] != 5*time.Second {
		t.Fatalf("sleeps = %v", *sleeps)
	}
}

func TestRetryDelayIsCapped(t *testing.T) {
	c, _, sleeps := newTestClient(t, sequence(t,
		reply{Status: 429, Body: errBody("rate_limited", "slow"), Header: map[string]string{"Retry-After": "3600"}},
		reply{Body: sampleInvoice(nil)},
	))
	_, _ = c.GetInvoice(ctx, "x")
	if (*sleeps)[0] != maxRetryDelay {
		t.Fatalf("sleeps = %v", *sleeps)
	}
}

func TestMutatingCallWithoutIdempotencyKeyNeverRetried(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Status: 503, Body: errBody("rate_unavailable", "no rate")}))
	_, err := c.CreateInvoice(ctx, &CreateInvoiceParams{AmountFiat: "25.00", Currency: "USD"})
	if !errors.Is(err, ErrRateUnavailable) || len(rt.calls) != 1 {
		t.Fatalf("err=%v calls=%d", err, len(rt.calls))
	}
}

func TestMutatingCallWithIdempotencyKeyIsRetried(t *testing.T) {
	c, rt, _ := newTestClient(t, sequence(t,
		reply{Status: 502, Body: errBody("upstream_error", "node down")},
		reply{Status: 201, Body: sampleInvoice(nil)},
	))
	if _, err := c.CreateInvoice(ctx, &CreateInvoiceParams{Amount: "0.001"}, RequestOptions{IdempotencyKey: "k1"}); err != nil {
		t.Fatal(err)
	}
	if len(rt.calls) != 2 {
		t.Fatalf("calls = %d", len(rt.calls))
	}
	if rt.calls[0].Header.Get("Idempotency-Key") != "k1" || rt.calls[1].Header.Get("Idempotency-Key") != "k1" {
		t.Fatal("same key must be sent on every attempt")
	}
	if string(rt.calls[0].Body) != string(rt.calls[1].Body) || len(rt.calls[1].Body) == 0 {
		t.Fatal("retried body must be identical")
	}
}

func TestIdempotencyKeyOnNonDeclaringEndpointDoesNotUnlockRetries(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Status: 500, Body: errBody("internal", "x")}))
	_, err := c.CancelInvoice(ctx, "abc", RequestOptions{IdempotencyKey: "k"})
	if !errors.Is(err, ErrInternal) || len(rt.calls) != 1 {
		t.Fatalf("err=%v calls=%d", err, len(rt.calls))
	}
}

func TestFourXXNeverRetried(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Status: 400, Body: errBody("bad_request", "nope")}))
	_, err := c.GetInvoice(ctx, "x")
	if !errors.Is(err, ErrBadRequest) || len(rt.calls) != 1 {
		t.Fatalf("err=%v calls=%d", err, len(rt.calls))
	}
}

func TestMaxRetriesBoundsAttempts(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Status: 500, Body: errBody("internal", "x")}), WithMaxRetries(4))
	_, err := c.GetInvoice(ctx, "x")
	if !errors.Is(err, ErrInternal) || len(rt.calls) != 5 {
		t.Fatalf("err=%v calls=%d", err, len(rt.calls))
	}
	c2, rt2, _ := newTestClient(t, constant(reply{Status: 500, Body: errBody("internal", "x")}))
	_, _ = c2.GetInvoice(ctx, "x")
	if len(rt2.calls) != DefaultMaxRetries+1 {
		t.Fatalf("default attempts = %d", len(rt2.calls))
	}
}

func TestTransportFailureRetriedThenConnectionError(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Err: errors.New("read: connection reset by peer")}))
	_, err := c.GetInvoice(ctx, "x")
	var ce *ConnectionError
	if !errors.As(err, &ce) || ce.Timeout || !strings.Contains(err.Error(), "connection reset") {
		t.Fatalf("err = %v", err)
	}
	if len(rt.calls) != 3 {
		t.Fatalf("calls = %d", len(rt.calls))
	}
	var apiErr *APIError
	if errors.As(err, &apiErr) {
		t.Fatal("a transport failure is not an APIError")
	}
}

type blockingRT struct{}

func (blockingRT) RoundTrip(req *http.Request) (*http.Response, error) {
	<-req.Context().Done()
	return nil, req.Context().Err()
}

func TestTimeoutSurfacesAsConnectionError(t *testing.T) {
	c, err := NewClient("https://api.example.test", "k", WithHTTPClient(&http.Client{Transport: blockingRT{}}), WithTimeout(10*time.Millisecond), WithMaxRetries(0))
	if err != nil {
		t.Fatal(err)
	}
	_, err = c.GetInvoice(ctx, "x")
	var ce *ConnectionError
	if !errors.As(err, &ce) || !ce.Timeout {
		t.Fatalf("err = %v", err)
	}
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("should unwrap to DeadlineExceeded: %v", err)
	}
}

func TestCancelledContextIsNotRetried(t *testing.T) {
	rt := &mockRT{}
	cctx, cancel := context.WithCancel(ctx)
	rt.handler = func(r *recorded, n int) reply {
		cancel()
		return reply{Err: context.Canceled}
	}
	c, _ := NewClient("https://api.example.test", "k", WithHTTPClient(&http.Client{Transport: rt}))
	_, err := c.GetInvoice(cctx, "x")
	var ce *ConnectionError
	if !errors.As(err, &ce) || ce.Timeout || !errors.Is(err, context.Canceled) || len(rt.calls) != 1 {
		t.Fatalf("err=%v calls=%d", err, len(rt.calls))
	}
}

func TestDefaultTimeoutIs30s(t *testing.T) {
	c, _ := NewClient("https://api.example.test", "k")
	if c.timeout != 30*time.Second || DefaultTimeout != 30*time.Second {
		t.Fatalf("timeout = %v", c.timeout)
	}
}

/* ---------------------------------------------------------------- *
 * Remaining endpoints
 * ---------------------------------------------------------------- */

func TestRotateWebhookSecret(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Body: map[string]any{
		"secret": "whsec_new", "rotated_at": "2026-08-24T12:00:00.000Z", "prev_expires_at": "2026-08-25T12:00:00.000Z",
		"grace_seconds": 86400, "replaced_previous": false, "message": "Rotated",
	}}))
	res, err := c.RotateWebhookSecret(ctx, RequestOptions{IdempotencyKey: "rot-1"})
	if err != nil {
		t.Fatal(err)
	}
	r := rt.only(t)
	if r.Method != "POST" || r.Path != "/api/v1/webhooks/secret/rotate" || r.Header.Get("Idempotency-Key") != "rot-1" {
		t.Fatalf("%s %s", r.Method, r.Path)
	}
	if res.Secret != "whsec_new" || res.GraceSeconds != 86400 || res.PrevExpiresAt == nil {
		t.Fatalf("res = %+v", res)
	}
}

func TestRotateReplayHasEmptySecret(t *testing.T) {
	c, _, _ := newTestClient(t, constant(reply{Body: map[string]any{"secret": nil, "rotated_at": "2026-08-24T12:00:00.000Z", "prev_expires_at": nil, "grace_seconds": 86400, "replaced_previous": false, "message": "replay"}, Header: map[string]string{"Idempotent-Replay": "true"}}))
	res, err := c.RotateWebhookSecret(ctx)
	if err != nil || res.Secret != "" {
		t.Fatalf("res=%+v err=%v", res, err)
	}
}

func TestHealthReturnsReportOn503(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Status: 503, Body: map[string]any{"status": "degraded", "time": "2026-08-24T12:00:00.000Z", "checks": map[string]any{"mongo": map[string]any{"ok": false}}, "alerts": []any{}}}))
	h, err := c.GetHealth(ctx)
	if err != nil || h.Status != "degraded" || h.Checks["mongo"]["ok"] != false {
		t.Fatalf("h=%+v err=%v", h, err)
	}
	if len(rt.calls) != 1 {
		t.Fatalf("a 503 health report must not be retried, got %d calls", len(rt.calls))
	}
}

func TestSmallerEndpointsBuildDocumentedRequests(t *testing.T) {
	c, rt, _ := newTestClient(t, func(r *recorded, n int) reply { return reply{Body: map[string]any{"ok": true}} })
	_, _ = c.StagePayoutChanges(ctx, map[string]string{"btc": "bc1qnew"})
	_, _ = c.CreateAPIKey(ctx, "ci")
	_, _ = c.RevokeAPIKey(ctx, "key-id")
	_, _ = c.CreateWebhookEndpoint(ctx, "https://shop.example/hooks")
	_, _ = c.SendTestWebhook(ctx, "")
	_, _ = c.UpdateSettings(ctx, &UpdateSettingsParams{AddFeeOntop: Bool(false)})
	_, _ = c.CancelRecurring(ctx, "r1")
	_, _ = c.CancelPayoutChange(ctx, "p1")
	_, _ = c.DeleteWebhookEndpoint(ctx, "w1")
	_, _ = c.ReplayWebhookDelivery(ctx, "d1")
	want := []struct{ m, p, body string }{
		{"PATCH", "/api/v1/payouts", `{"addresses":{"btc":"bc1qnew"}}`},
		{"POST", "/api/v1/keys", `{"label":"ci"}`},
		{"POST", "/api/v1/keys/key-id/revoke", ``},
		{"POST", "/api/v1/webhooks/endpoints", `{"url":"https://shop.example/hooks"}`},
		{"POST", "/api/v1/webhooks/test", ``},
		{"PATCH", "/api/v1/settings", `{"add_fee_ontop":false}`},
		{"POST", "/api/v1/recurring/r1/cancel", ``},
		{"POST", "/api/v1/payouts/changes/p1/cancel", ``},
		{"DELETE", "/api/v1/webhooks/endpoints/w1", ``},
		{"POST", "/api/v1/webhooks/deliveries/d1/replay", ``},
	}
	if len(rt.calls) != len(want) {
		t.Fatalf("calls = %d", len(rt.calls))
	}
	for i, w := range want {
		r := rt.calls[i]
		if r.Method != w.m || r.Path != w.p || string(r.Body) != w.body {
			t.Errorf("call %d = %s %s %s, want %s %s %s", i, r.Method, r.Path, r.Body, w.m, w.p, w.body)
		}
	}
}

func TestDoEscapeHatch(t *testing.T) {
	c, rt, _ := newTestClient(t, constant(reply{Status: 201, Body: map[string]any{"x": 1}, Header: map[string]string{"Idempotent-Replay": "true"}}))
	var out map[string]any
	resp, err := c.Do(ctx, "get", "/api/v1/fees/tier?x=1", nil, &out)
	if err != nil || resp.StatusCode != 201 || !resp.IdempotentReplay() || out["x"].(json.Number).String() != "1" {
		t.Fatalf("resp=%+v out=%v err=%v", resp, out, err)
	}
	if rt.only(t).Path != "/api/v1/fees/tier" || rt.calls[0].Query["x"] != "1" {
		t.Fatalf("request = %+v", rt.calls[0])
	}
	if _, err := c.Do(ctx, "GET", "no-slash", nil, nil); err == nil {
		t.Fatal("relative path should fail")
	}
}
