package receivify

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"
)

const (
	current  = "whsec_current_secret"
	previous = "whsec_previous_secret"
)

var (
	now  = time.Unix(1_800_000_000, 0)
	body = []byte(`{"event":"invoice.paid","sequence":42,"created_at":"2026-08-24T12:05:00.000Z","data":{"id":"3f2b8c1e-5a4d-4e8f-9b7a-1c2d3e4f5a6b","reference":"order-1001","status":"paid","amount_expected":"100000","amount_received":"100000","address":"bc1qexample","paid_at":"2026-08-24T12:05:00.000Z"}}`)
)

// sign is an independent implementation of the documented scheme, deliberately not using
// SignWebhook, so the verifier is checked against the spec rather than against itself.
func sign(b []byte, secret string, ts int64) string {
	mac := hmac.New(sha256.New, []byte(secret))
	mac.Write([]byte(fmt.Sprintf("%d.%s", ts, b)))
	return fmt.Sprintf("t=%d,v1=%s", ts, hex.EncodeToString(mac.Sum(nil)))
}

func at(t time.Time) VerifyOption { return WithVerifyTime(t) }

func reason(err error) WebhookErrorReason {
	var we *WebhookError
	if errors.As(err, &we) {
		return we.Reason
	}
	return ""
}

func TestVerifiesCurrentSecret(t *testing.T) {
	if !VerifyWebhook(body, sign(body, current, now.Unix()), []string{current}, at(now)) {
		t.Fatal("current secret should verify")
	}
}

func TestVerifiesPreviousSecretDuringRotation(t *testing.T) {
	h := sign(body, previous, now.Unix())
	if !VerifyWebhook(body, h, []string{current, previous}, at(now)) {
		t.Fatal("previous secret should verify when listed")
	}
	if VerifyWebhook(body, h, []string{current}, at(now)) {
		t.Fatal("previous secret must fail once dropped from the list")
	}
}

func TestRejectsUnknownSecret(t *testing.T) {
	err := CheckWebhook(body, sign(body, "whsec_other", now.Unix()), []string{current, previous}, at(now))
	if reason(err) != ReasonNoMatch {
		t.Fatalf("err = %v", err)
	}
}

func TestRejectsTamperedBody(t *testing.T) {
	h := sign(body, current, now.Unix())
	tampered := []byte(strings.Replace(string(body), `"100000","address"`, `"999999","address"`, 1))
	if VerifyWebhook(tampered, h, []string{current}, at(now)) {
		t.Fatal("tampered body verified")
	}
}

func TestRejectsReserialisedBody(t *testing.T) {
	h := sign(body, current, now.Unix())
	reser := []byte(strings.ReplaceAll(string(body), `":`, `": `))
	if VerifyWebhook(reser, h, []string{current}, at(now)) {
		t.Fatal("re-serialised body verified")
	}
}

func TestRejectsStaleAndFutureTimestamps(t *testing.T) {
	old := sign(body, current, now.Add(-time.Hour).Unix())
	if err := CheckWebhook(body, old, []string{current}, at(now)); reason(err) != ReasonStaleTimestamp {
		t.Fatalf("hour-old: %v", err)
	}
	future := sign(body, current, now.Add(301*time.Second).Unix())
	if err := CheckWebhook(body, future, []string{current}, at(now)); reason(err) != ReasonStaleTimestamp {
		t.Fatalf("future: %v", err)
	}
}

func TestAcceptsJustInsideTolerance(t *testing.T) {
	h := sign(body, current, now.Add(-300*time.Second).Unix())
	if !VerifyWebhook(body, h, []string{current}, at(now)) {
		t.Fatal("300s old should be inside the default window")
	}
	h = sign(body, current, now.Add(-301*time.Second).Unix())
	if VerifyWebhook(body, h, []string{current}, at(now)) {
		t.Fatal("301s old should be outside the default window")
	}
}

func TestDefaultToleranceUsesWallClock(t *testing.T) {
	if !VerifyWebhook(body, sign(body, current, time.Now().Unix()), []string{current}) {
		t.Fatal("fresh signature should verify without options")
	}
	if VerifyWebhook(body, sign(body, current, time.Now().Add(-time.Hour).Unix()), []string{current}) {
		t.Fatal("hour-old signature should fail without options")
	}
}

func TestZeroToleranceDisablesReplayWindow(t *testing.T) {
	h := sign(body, current, now.Add(-24*time.Hour).Unix())
	if !VerifyWebhook(body, h, []string{current}, at(now), WithTolerance(0)) {
		t.Fatal("tolerance 0 should disable the timestamp check")
	}
}

func TestCustomTolerance(t *testing.T) {
	h := sign(body, current, now.Add(-90*time.Second).Unix())
	if VerifyWebhook(body, h, []string{current}, at(now), WithTolerance(60*time.Second)) {
		t.Fatal("90s old should fail a 60s window")
	}
	if !VerifyWebhook(body, h, []string{current}, at(now), WithTolerance(120*time.Second)) {
		t.Fatal("90s old should pass a 120s window")
	}
}

func TestNoSecretsIsCleanFalse(t *testing.T) {
	h := sign(body, current, now.Unix())
	for _, s := range [][]string{nil, {}, {""}} {
		if VerifyWebhook(body, h, s, at(now)) {
			t.Fatal("verified with no secrets")
		}
		if reason(CheckWebhook(body, h, s, at(now))) != ReasonNoSecrets {
			t.Fatal("reason should be no_secrets")
		}
	}
	// Blank entries are dropped, real ones still work.
	if !VerifyWebhook(body, h, []string{"", current}, at(now)) {
		t.Fatal("blank entry should be ignored")
	}
}

func TestMalformedHeaders(t *testing.T) {
	good := sign(body, current, now.Unix())
	sig := strings.SplitN(good, "v1=", 2)[1]
	for _, h := range []string{
		"", "garbage", "t=,v1=" + sig, fmt.Sprintf("t=%d", now.Unix()), "v1=" + sig,
		fmt.Sprintf("t=-%d,v1=%s", now.Unix(), sig), fmt.Sprintf("t=12abc,v1=%s", sig), fmt.Sprintf("t=%d,v1=", now.Unix()),
	} {
		if err := CheckWebhook(body, h, []string{current}, at(now)); reason(err) != ReasonBadHeader {
			t.Errorf("header %q: %v", h, err)
		}
	}
}

func TestNonHexOrWrongLengthSignatureRejectedWithoutPanic(t *testing.T) {
	for _, v := range []string{"zzzz", "abcd", strings.Repeat("g", 64), strings.Repeat("a", 63)} {
		h := fmt.Sprintf("t=%d,v1=%s", now.Unix(), v)
		if err := CheckWebhook(body, h, []string{current}, at(now)); reason(err) != ReasonNoMatch {
			t.Errorf("v1=%s: %v", v, err)
		}
	}
}

func TestMultipleV1CandidatesAndWhitespace(t *testing.T) {
	good := sign(body, current, now.Unix())
	sig := strings.SplitN(good, "v1=", 2)[1]
	h := fmt.Sprintf(" t=%d , v1=%s , v1=%s ", now.Unix(), strings.Repeat("0", 64), sig)
	if !VerifyWebhook(body, h, []string{current}, at(now)) {
		t.Fatal("any matching v1 should verify")
	}
}

func TestSignWebhookMatchesIndependentSigner(t *testing.T) {
	if SignWebhook(body, current, now) != sign(body, current, now.Unix()) {
		t.Fatal("SignWebhook disagrees with the documented scheme")
	}
}

func TestParseWebhookReturnsEnvelope(t *testing.T) {
	ev, err := ParseWebhook(body, sign(body, current, now.Unix()), []string{current}, at(now))
	if err != nil {
		t.Fatal(err)
	}
	if ev.Event != EventInvoicePaid || ev.Sequence != 42 || ev.CreatedAt.IsZero() || !ev.IsInvoiceEvent() || ev.IsForwardEvent() {
		t.Fatalf("ev = %+v", ev)
	}
	d, err := ev.InvoiceData()
	if err != nil || d.Status != InvoiceStatusPaid || d.AmountReceived != "100000" || d.Reference != "order-1001" || d.PaidAt == nil {
		t.Fatalf("data = %+v err = %v", d, err)
	}
}

func TestParseWebhookForwardEvent(t *testing.T) {
	b := []byte(`{"event":"forward.deferred","sequence":7,"created_at":"2026-08-24T12:05:00.000Z","data":{"forward_id":"f1","ticker":"btc","address_in":"in","address_out":"out","txid_in":"tx","txid_out":null,"value":"150000","value_forwarded":"0","fee":"0","confirmations":2,"pending":false,"reason":"uneconomical","detail":"fees too high"}}`)
	ev, err := ParseWebhook(b, sign(b, current, now.Unix()), []string{current}, at(now))
	if err != nil {
		t.Fatal(err)
	}
	if !ev.IsForwardEvent() || ev.IsInvoiceEvent() {
		t.Fatal("family detection wrong")
	}
	d, err := ev.ForwardData()
	if err != nil || d.Value != "150000" || d.Reason != "uneconomical" || d.TxidOut != "" || d.AddressIn != "in" {
		t.Fatalf("data = %+v err=%v", d, err)
	}
}

func TestParseWebhookUnknownEventStillParses(t *testing.T) {
	b := []byte(`{"event":"invoice.something_new","sequence":1,"created_at":"2026-08-24T12:05:00.000Z","data":{}}`)
	ev, err := ParseWebhook(b, sign(b, current, now.Unix()), []string{current}, at(now))
	if err != nil || ev.Event != "invoice.something_new" {
		t.Fatalf("ev=%v err=%v", ev, err)
	}
}

func TestParseWebhookRejections(t *testing.T) {
	cases := map[string]WebhookErrorReason{
		`not json`:               ReasonBadJSON,
		`{"event":`:              ReasonBadJSON,
		`[1,2,3]`:                ReasonBadEnvelope,
		`{"sequence":1}`:         ReasonBadEnvelope,
		`{"event":5}`:            ReasonBadEnvelope,
		`"invoice.paid"`:         ReasonBadEnvelope,
		`{"event":"","data":{}}`: ReasonBadEnvelope,
	}
	for raw, want := range cases {
		b := []byte(raw)
		_, err := ParseWebhook(b, sign(b, current, now.Unix()), []string{current}, at(now))
		if reason(err) != want {
			t.Errorf("%s: got %v, want %s", raw, err, want)
		}
	}
	// Signature failures come first, before any JSON parsing.
	if _, err := ParseWebhook(body, sign(body, "wrong", now.Unix()), []string{current}, at(now)); reason(err) != ReasonNoMatch {
		t.Fatalf("err = %v", err)
	}
}

func TestParseWebhookToleratesLegacyInvoiceShape(t *testing.T) {
	b := []byte(`{"event":"invoice.paid","invoice":{"id":"legacy-1","status":"paid","amount_expected":"5","amount_received":"5","address":"a"}}`)
	ev, err := ParseWebhook(b, sign(b, current, now.Unix()), []string{current}, at(now))
	if err != nil {
		t.Fatal(err)
	}
	d, err := ev.InvoiceData()
	if err != nil || d.ID != "legacy-1" {
		t.Fatalf("d=%+v err=%v", d, err)
	}
}

func TestClientWebhookMethodsUseConfiguredSecrets(t *testing.T) {
	c, err := NewClient("https://api.example.test", "k", WithWebhookSecrets(current, "", previous))
	if err != nil {
		t.Fatal(err)
	}
	h := sign(body, previous, now.Unix())
	if !c.VerifyWebhook(body, h, at(now)) {
		t.Fatal("client should verify with its configured list")
	}
	if c.CheckWebhook(body, h, at(now)) != nil {
		t.Fatal("CheckWebhook should pass")
	}
	if _, err := c.ParseWebhook(body, h, at(now)); err != nil {
		t.Fatal(err)
	}
	bare, _ := NewClient("https://api.example.test", "k")
	if bare.VerifyWebhook(body, h, at(now)) {
		t.Fatal("a client without secrets must not verify")
	}
}

func TestEventsRegistry(t *testing.T) {
	if len(Events) != 16 {
		t.Fatalf("events = %d", len(Events))
	}
	seen := map[string]bool{}
	for _, e := range Events {
		if seen[e] {
			t.Fatalf("duplicate %s", e)
		}
		seen[e] = true
	}
	for _, e := range []string{"invoice.paid", "sweep.sent", "forward.detected", "forward.deferred", "forward.reversed", "forward.failed", "test"} {
		if !seen[e] {
			t.Errorf("missing %s", e)
		}
	}
}
